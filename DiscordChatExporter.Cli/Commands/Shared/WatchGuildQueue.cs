using System;
using System.Collections.Generic;
using System.Linq;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;

namespace DiscordChatExporter.Cli.Commands.Shared;

public abstract record QueueItem;

// A message already fully materialized from a gateway payload (MESSAGE_CREATE), to be written
// straight to the database with no Discord round-trip. Attempt tracks retry count so a
// persistently failing upsert is eventually dropped instead of looping forever.
public record UpsertMessageItem(Snowflake ChannelId, Message Message, int Attempt = 0) : QueueItem;

public record PatchMessageItem(Snowflake ChannelId, Snowflake MessageId, string Reason) : QueueItem;

public record PollVoteItem(
    Snowflake ChannelId,
    Snowflake MessageId,
    int AnswerId,
    Snowflake UserId,
    bool IsAdded,
    string Reason
) : QueueItem;

public record MarkDeletedItem(Snowflake ChannelId, IReadOnlyList<Snowflake> MessageIds) : QueueItem;

// CHANNEL_DELETE/THREAD_DELETE. Channels/categories/forums also get a diff-based safety net in
// SyncGuildAsync (for anything missed while the watcher was offline), but a thread has no cheap
// "list everything" call to diff against, so this direct id-based mark is the only mechanism for
// those.
public record MarkChannelDeletedItem(Snowflake ChannelId) : QueueItem;

// THREAD_MEMBERS_UPDATE. Kept separate from a full ChannelExporter re-export (which also
// refreshes the thread member list wholesale) since a join/leave is much cheaper to apply
// directly from the gateway payload than to trigger a debounced channel export for.
public record ThreadMembersUpdateItem(
    Snowflake ChannelId,
    IReadOnlyList<ThreadMember> AddedMembers,
    IReadOnlyList<Snowflake> RemovedMemberIds
) : QueueItem;

public record SyncGuildItem(string Reason) : QueueItem;

public record ExportChannelItem(
    Snowflake ChannelId,
    DateTimeOffset? FirstTimestamp,
    DateTimeOffset FirstQueuedAt,
    bool IsCatchup,
    bool ForceFullScan
) : QueueItem;

public class WatchGuildQueue
{
    private readonly object _lock = new();

    private readonly TimeSpan _channelDebounce;
    private readonly TimeSpan _channelMaxWait;
    private readonly TimeSpan _patchDebounce;
    private readonly TimeSpan _deleteDebounce;
    private readonly TimeSpan _guildSyncDebounce;

    // Live message writes are processed ahead of everything else and without debounce: they are
    // cheap (a local upsert, no Discord call) and are the latency-critical path (a new message
    // should land in the database within a poll tick, not after a debounce window).
    private readonly Queue<UpsertMessageItem> _pendingMessages = new();
    private readonly Queue<PollVoteItem> _pendingPollVotes = new();
    private readonly Queue<MarkChannelDeletedItem> _pendingChannelDeletes = new();
    private readonly Queue<ThreadMembersUpdateItem> _pendingThreadMemberUpdates = new();

    private readonly Dictionary<Snowflake, PendingExport> _pendingExports = new();
    private readonly Dictionary<string, PendingPatch> _pendingPatches = new();
    private readonly Dictionary<Snowflake, PendingDelete> _pendingDeletes = new();
    private DateTimeOffset? _guildSyncDue;
    private string? _guildSyncReason;

    private readonly Dictionary<Snowflake, int> _channelFailures = new();
    private readonly Dictionary<string, int> _patchFailures = new();
    private readonly Dictionary<Snowflake, int> _deleteFailures = new();
    private readonly Dictionary<Snowflake, int> _channelDeleteFailures = new();
    private readonly Dictionary<Snowflake, int> _threadMemberUpdateFailures = new();
    private int _guildSyncFailures;

    public WatchGuildQueue(
        TimeSpan? channelDebounce = null,
        TimeSpan? channelMaxWait = null,
        TimeSpan? patchDebounce = null,
        TimeSpan? deleteDebounce = null,
        TimeSpan? guildSyncDebounce = null
    )
    {
        // Channel exports are now only a background maintenance pass (cursor advance, gap-fill,
        // author/role enrichment) -- live messages are captured instantly by the direct-upsert
        // path -- so they run infrequently to avoid competing with those live writes on the
        // single pump thread.
        _channelDebounce = channelDebounce ?? TimeSpan.FromMinutes(5);
        _channelMaxWait = channelMaxWait ?? TimeSpan.FromMinutes(15);
        _patchDebounce = patchDebounce ?? TimeSpan.FromSeconds(2);
        _deleteDebounce = deleteDebounce ?? TimeSpan.FromSeconds(2);
        _guildSyncDebounce = guildSyncDebounce ?? TimeSpan.FromSeconds(30);
    }

    public int PendingMessagesCount
    {
        get
        {
            lock (_lock)
                return _pendingMessages.Count;
        }
    }

    public int PendingChannelsCount
    {
        get
        {
            lock (_lock)
                return _pendingExports.Count;
        }
    }

    public int PendingPatchesCount
    {
        get
        {
            lock (_lock)
                return _pendingPatches.Count;
        }
    }

    public int PendingPollVotesCount
    {
        get
        {
            lock (_lock)
                return _pendingPollVotes.Count;
        }
    }

    public int PendingDeleteChannelsCount
    {
        get
        {
            lock (_lock)
                return _pendingDeletes.Count;
        }
    }

    public int PendingDeleteMessagesCount
    {
        get
        {
            lock (_lock)
                return _pendingDeletes.Values.Sum(d => d.MessageIds.Count);
        }
    }

    public bool IsGuildSyncPending
    {
        get
        {
            lock (_lock)
                return _guildSyncDue.HasValue;
        }
    }

    public void EnqueueMessageUpsert(Snowflake channelId, Message message)
    {
        lock (_lock)
        {
            _pendingMessages.Enqueue(new UpsertMessageItem(channelId, message));
        }
    }

    public void EnqueueChannelExport(
        Snowflake channelId,
        DateTimeOffset? timestamp = null,
        bool isCatchup = false,
        bool forceFullScan = false
    )
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_pendingExports.TryGetValue(channelId, out var existing))
            {
                var finalCatchup = existing.IsCatchup || isCatchup;
                var finalForceFullScan = existing.ForceFullScan || forceFullScan;

                DateTimeOffset finalDue;
                if (finalCatchup || finalForceFullScan)
                {
                    finalDue = now;
                }
                else
                {
                    finalDue = now + _channelDebounce;
                    if (finalDue > existing.FirstQueuedAt + _channelMaxWait)
                    {
                        finalDue = existing.FirstQueuedAt + _channelMaxWait;
                    }
                }

                DateTimeOffset? finalFirstTimestamp = existing.FirstTimestamp;
                if (timestamp.HasValue)
                {
                    if (finalFirstTimestamp is null || timestamp.Value < finalFirstTimestamp.Value)
                    {
                        finalFirstTimestamp = timestamp;
                    }
                }

                _pendingExports[channelId] = existing with
                {
                    Due = finalDue,
                    FirstTimestamp = finalFirstTimestamp,
                    IsCatchup = finalCatchup,
                    ForceFullScan = finalForceFullScan,
                };
            }
            else
            {
                DateTimeOffset due;
                if (isCatchup || forceFullScan)
                {
                    due = now;
                }
                else
                {
                    due = now + _channelDebounce;
                }

                _pendingExports[channelId] = new PendingExport(
                    channelId,
                    due,
                    timestamp,
                    now,
                    isCatchup,
                    forceFullScan
                );
            }
        }
    }

    public void EnqueuePatch(Snowflake channelId, Snowflake messageId, string reason)
    {
        lock (_lock)
        {
            var key = $"{channelId}:{messageId}";
            var now = DateTimeOffset.UtcNow;
            _pendingPatches[key] = new PendingPatch(
                channelId,
                messageId,
                reason,
                now + _patchDebounce
            );
        }
    }

    public void EnqueuePollVote(
        Snowflake channelId,
        Snowflake messageId,
        int answerId,
        Snowflake userId,
        bool isAdded,
        string reason
    )
    {
        lock (_lock)
        {
            _pendingPollVotes.Enqueue(
                new PollVoteItem(channelId, messageId, answerId, userId, isAdded, reason)
            );
        }
    }

    public void EnqueueDelete(Snowflake channelId, Snowflake messageId)
    {
        EnqueueDelete(channelId, new[] { messageId });
    }

    public void EnqueueDelete(Snowflake channelId, IEnumerable<Snowflake> messageIds)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_pendingDeletes.TryGetValue(channelId, out var existing))
            {
                foreach (var id in messageIds)
                {
                    existing.MessageIds.Add(id);
                }
                existing.Due = now + _deleteDebounce;
            }
            else
            {
                var set = new HashSet<Snowflake>(messageIds);
                _pendingDeletes[channelId] = new PendingDelete(
                    channelId,
                    set,
                    now + _deleteDebounce
                );
            }
        }
    }

    public void EnqueueChannelDelete(Snowflake channelId)
    {
        lock (_lock)
        {
            _pendingChannelDeletes.Enqueue(new MarkChannelDeletedItem(channelId));
        }
    }

    public void EnqueueThreadMembersUpdate(
        Snowflake channelId,
        IReadOnlyList<ThreadMember> addedMembers,
        IReadOnlyList<Snowflake> removedMemberIds
    )
    {
        lock (_lock)
        {
            _pendingThreadMemberUpdates.Enqueue(
                new ThreadMembersUpdateItem(channelId, addedMembers, removedMemberIds)
            );
        }
    }

    public void EnqueueGuildSync(string reason)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            _guildSyncDue = now + _guildSyncDebounce;
            _guildSyncReason = reason;
        }
    }

    public QueueItem? TryDequeue()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;

            // Live message writes take precedence over everything else: they are the
            // latency-critical path and each one is a fast local upsert.
            if (_pendingMessages.Count > 0)
                return _pendingMessages.Dequeue();

            if (_pendingPollVotes.Count > 0)
                return _pendingPollVotes.Dequeue();

            if (_pendingChannelDeletes.Count > 0)
                return _pendingChannelDeletes.Dequeue();

            if (_pendingThreadMemberUpdates.Count > 0)
                return _pendingThreadMemberUpdates.Dequeue();

            var duePatch = _pendingPatches
                .Where(p => p.Value.Due <= now)
                .OrderBy(p => p.Value.Due)
                .FirstOrDefault();

            if (duePatch.Key is not null)
            {
                _pendingPatches.Remove(duePatch.Key);
                return new PatchMessageItem(
                    duePatch.Value.ChannelId,
                    duePatch.Value.MessageId,
                    duePatch.Value.Reason
                );
            }

            var dueDelete = _pendingDeletes
                .Where(d => d.Value.Due <= now)
                .OrderBy(d => d.Value.Due)
                .FirstOrDefault();

            if (dueDelete.Key != default(Snowflake))
            {
                _pendingDeletes.Remove(dueDelete.Key);
                return new MarkDeletedItem(
                    dueDelete.Value.ChannelId,
                    dueDelete.Value.MessageIds.ToList()
                );
            }

            if (_guildSyncDue.HasValue && _guildSyncDue.Value <= now)
            {
                var reason = _guildSyncReason ?? "unknown";
                _guildSyncDue = null;
                _guildSyncReason = null;
                return new SyncGuildItem(reason);
            }

            var dueExport = _pendingExports
                .Where(e => e.Value.Due <= now)
                .OrderBy(e => e.Value.Due)
                .FirstOrDefault();

            if (dueExport.Key != default(Snowflake))
            {
                _pendingExports.Remove(dueExport.Key);
                return new ExportChannelItem(
                    dueExport.Value.ChannelId,
                    dueExport.Value.FirstTimestamp,
                    dueExport.Value.FirstQueuedAt,
                    dueExport.Value.IsCatchup,
                    dueExport.Value.ForceFullScan
                );
            }

            return null;
        }
    }

    public void ReportSuccess(QueueItem item)
    {
        lock (_lock)
        {
            if (item is ExportChannelItem export)
            {
                _channelFailures.Remove(export.ChannelId);
            }
            else if (item is PatchMessageItem patch)
            {
                _patchFailures.Remove($"{patch.ChannelId}:{patch.MessageId}");
            }
            else if (item is MarkDeletedItem delete)
            {
                _deleteFailures.Remove(delete.ChannelId);
            }
            else if (item is SyncGuildItem)
            {
                _guildSyncFailures = 0;
            }
            else if (item is MarkChannelDeletedItem channelDelete)
            {
                _channelDeleteFailures.Remove(channelDelete.ChannelId);
            }
            else if (item is ThreadMembersUpdateItem threadUpdate)
            {
                _threadMemberUpdateFailures.Remove(threadUpdate.ChannelId);
            }
        }
    }

    public bool ReportFailure(QueueItem item)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (item is UpsertMessageItem message)
            {
                // Re-enqueue at the front-of-line priority a couple of times; a message upsert
                // that keeps failing (e.g. a channel that can't be resolved) is eventually
                // dropped rather than looped on forever -- the debounced channel export that runs
                // alongside every MESSAGE_CREATE is the backstop that will recapture it.
                if (message.Attempt >= 2)
                    return false;

                _pendingMessages.Enqueue(message with { Attempt = message.Attempt + 1 });
                return true;
            }
            if (item is ExportChannelItem export)
            {
                _channelFailures.TryGetValue(export.ChannelId, out var count);
                count++;
                _channelFailures[export.ChannelId] = count;
                if (count >= 5)
                {
                    _channelFailures.Remove(export.ChannelId);
                    return false;
                }

                var due = now + TimeSpan.FromSeconds(60 * count);
                _pendingExports[export.ChannelId] = new PendingExport(
                    export.ChannelId,
                    due,
                    export.FirstTimestamp,
                    export.FirstQueuedAt,
                    export.IsCatchup,
                    export.ForceFullScan
                );
                return true;
            }
            else if (item is PatchMessageItem patch)
            {
                var key = $"{patch.ChannelId}:{patch.MessageId}";
                _patchFailures.TryGetValue(key, out var count);
                count++;
                _patchFailures[key] = count;
                if (count >= 3)
                {
                    _patchFailures.Remove(key);
                    return false;
                }

                var due = now + TimeSpan.FromSeconds(30 * count);
                _pendingPatches[key] = new PendingPatch(
                    patch.ChannelId,
                    patch.MessageId,
                    patch.Reason,
                    due
                );
                return true;
            }
            else if (item is MarkDeletedItem delete)
            {
                _deleteFailures.TryGetValue(delete.ChannelId, out var count);
                count++;
                _deleteFailures[delete.ChannelId] = count;
                if (count >= 3)
                {
                    _deleteFailures.Remove(delete.ChannelId);
                    return false;
                }

                var due = now + TimeSpan.FromSeconds(30 * count);
                var set = new HashSet<Snowflake>(delete.MessageIds);
                _pendingDeletes[delete.ChannelId] = new PendingDelete(delete.ChannelId, set, due);
                return true;
            }
            else if (item is SyncGuildItem sync)
            {
                _guildSyncFailures++;
                if (_guildSyncFailures >= 5)
                {
                    _guildSyncFailures = 0;
                    return false;
                }

                _guildSyncDue = now + TimeSpan.FromSeconds(60 * _guildSyncFailures);
                _guildSyncReason = sync.Reason;
                return true;
            }
            else if (item is MarkChannelDeletedItem channelDelete)
            {
                _channelDeleteFailures.TryGetValue(channelDelete.ChannelId, out var count);
                count++;
                _channelDeleteFailures[channelDelete.ChannelId] = count;
                if (count >= 3)
                {
                    _channelDeleteFailures.Remove(channelDelete.ChannelId);
                    return false;
                }
                _pendingChannelDeletes.Enqueue(channelDelete);
                return true;
            }
            else if (item is ThreadMembersUpdateItem threadUpdate)
            {
                _threadMemberUpdateFailures.TryGetValue(threadUpdate.ChannelId, out var count);
                count++;
                _threadMemberUpdateFailures[threadUpdate.ChannelId] = count;
                if (count >= 3)
                {
                    _threadMemberUpdateFailures.Remove(threadUpdate.ChannelId);
                    return false;
                }
                _pendingThreadMemberUpdates.Enqueue(threadUpdate);
                return true;
            }
            return false;
        }
    }

    private record PendingExport(
        Snowflake ChannelId,
        DateTimeOffset Due,
        DateTimeOffset? FirstTimestamp,
        DateTimeOffset FirstQueuedAt,
        bool IsCatchup,
        bool ForceFullScan
    );

    private record PendingPatch(
        Snowflake ChannelId,
        Snowflake MessageId,
        string Reason,
        DateTimeOffset Due
    );

    private class PendingDelete(
        Snowflake ChannelId,
        HashSet<Snowflake> MessageIds,
        DateTimeOffset Due
    )
    {
        public Snowflake ChannelId { get; } = ChannelId;
        public HashSet<Snowflake> MessageIds { get; } = MessageIds;
        public DateTimeOffset Due { get; set; } = Due;
    }
}
