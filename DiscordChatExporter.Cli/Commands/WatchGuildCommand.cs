using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Cli.Commands.Shared;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Database;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;

namespace DiscordChatExporter.Cli.Commands;

[Command(
    "watchguild",
    Description = "Monitors a Discord guild using the Gateway API and exports events to a database."
)]
public partial class WatchGuildCommand : DiscordCommandBase
{
    [CommandOption("guild", 'g', Description = "Server ID.")]
    public required Snowflake GuildId { get; set; }

    [CommandOption("output", 'o', Description = "Path to the SQLite database file.")]
    public required string OutputPath
    {
        get;
        set => field = Path.GetFullPath(value);
    }

    [CommandOption("format", 'f', Description = "Export format. Only 'Db' is supported.")]
    public ExportFormat ExportFormat { get; set; } = ExportFormat.Db;

    [CommandOption(
        "media",
        Description = "Download Discord media referenced by the database export."
    )]
    public bool ShouldDownloadAssets { get; set; }

    [CommandOption(
        "media-dir",
        Description = "Download media to this directory. If not specified, the media directory will be derived from the output path."
    )]
    public string? AssetsDirPath
    {
        get;
        set => field = value is not null ? Path.GetFullPath(value) : null;
    }

    [CommandOption(
        "catch-up",
        Description = "Queue all guild channels/threads for export after connection."
    )]
    public bool CatchUp { get; set; }

    [CommandOption(
        "scan-missing",
        Description = "Queue all guild channels/threads with a full DB rescan."
    )]
    public bool ScanMissing { get; set; }

    [CommandOption(
        "retry-failed",
        Description = "Retry media URLs previously recorded as permanently gone (404/410), "
            + "instead of skipping them. By default such URLs are only attempted once."
    )]
    public bool RetryFailedMedia { get; set; }

    [CommandOption(
        "enrich-reactors",
        Description = "Fetch the full reactor list during catch-up/scan-missing channel exports "
            + "(one paginated request per unique emoji per message). Off by default: live reactions "
            + "already record reactors from the gateway, so this only affects historical backfill, "
            + "where it can multiply the request count several-fold."
    )]
    public bool EnrichReactors { get; set; } = false;

    [CommandOption(
        "catch-up-parallel",
        Description = "How many channel/thread exports (catch-up, scan-missing, or a debounced "
            + "regular re-export) may run concurrently. The message-history endpoint is rate "
            + "limited per-channel independently (measured: ~5 burst then ~2 requests/s each), so "
            + "raising this multiplies catch-up throughput up to Discord's global ~50 req/s cap. "
            + "~10-12 is a good balance; much higher yields little until you hit the global limit. "
            + "Live messages, patches, deletes, and guild syncs are unaffected: they always run "
            + "immediately on the main loop regardless of this setting, since they're cheap/local."
    )]
    public int CatchUpParallel { get; set; } = 10;

    private readonly Dictionary<Snowflake, HashSet<Snowflake>> _knownPinnedIds = new();

    // Guards against re-running a full guild rescan on every gateway READY. A flapping
    // connection (several non-resumable reconnects in quick succession) each raises a READY,
    // but each rescan lists every channel/thread and re-enqueues them -- redundant within
    // seconds of the last one (the queue dedups by channel id, so it's wasteful, not unsafe).
    private readonly object _catchUpLock = new();
    private DateTimeOffset _lastCatchUpAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan CatchUpMinInterval = TimeSpan.FromSeconds(60);

    // Channels already known to exist in the database, so the direct-upsert path can skip the
    // existence check (and any resolve-and-upsert of a brand-new channel) after the first message
    // for a channel. Only ever touched from the single-threaded pump loop.
    private readonly HashSet<Snowflake> _knownChannelIds = new();

    // Cached guild role catalog, used to resolve the role IDs in a live message's member block
    // into full Role objects for the user row. Fetched once (one REST call per session), then
    // invalidated whenever a guild sync runs -- role create/update/delete all funnel through
    // EnqueueGuildSync. Only ever touched from the single-threaded pump loop.
    private IReadOnlyDictionary<Snowflake, Role>? _guildRoles;

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        await base.ExecuteAsync(console);

        if (ExportFormat != ExportFormat.Db)
            throw new CommandException("Option --format only supports 'Db' for watchguild.");

        if (!string.IsNullOrWhiteSpace(AssetsDirPath) && !ShouldDownloadAssets)
            throw new CommandException("Option --media-dir cannot be used without --media.");

        // Single-instance guard, scoped per-database. Two watchers (or a watcher plus a separate
        // `export --format Db` run) writing to the same file would each open a writer connection;
        // SQLite's WAL permits only one writer, so the loser blocks for busy_timeout and then
        // fails. An exclusive OS file lock (FileShare.None -> flock on Unix) is self-releasing on
        // process death, so unlike a bare PID file there's no staleness bookkeeping to get wrong.
        var lockFilePath = OutputPath + ".watch.lock";
        FileStream instanceLock;
        try
        {
            instanceLock = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
        }
        catch (IOException)
        {
            throw new CommandException(
                $"Another watchguild instance appears to already be running for '{OutputPath}' "
                    + $"(lock file '{lockFilePath}' is held). Only one watcher may write to a "
                    + "database at a time."
            );
        }

        using var instanceLockHandle = instanceLock;

        var cancellationToken = console.RegisterCancellationHandlerWithSignals();

        var firstToken = Discord.Tokens[0];
        var queue = new WatchGuildQueue(maxConcurrentExports: Math.Max(1, CatchUpParallel));

        await console.Output.WriteLineAsync(
            $"Starting watchguild for guild {GuildId} into '{OutputPath}'..."
        );

        await using var store = await SqliteExportStore.OpenAsync(
            OutputPath,
            ShouldDownloadAssets ? AssetsDirPath ?? $"{OutputPath}_Files" : null,
            RetryFailedMedia,
            cancellationToken
        );

        var gatewayClient = new GatewayClient(firstToken);

        gatewayClient.LogMessage += msg =>
        {
            lock (console)
            {
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [gateway] {msg}"
                );
            }
        };

        gatewayClient.ErrorOccurred += ex =>
        {
            lock (console)
            {
                console.Error.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [gateway-error] {ex.Message}"
                );
            }
        };

        gatewayClient.ConnectionDown += failureCount =>
        {
            lock (console)
            {
                console.Error.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [gateway-down] Gateway has failed "
                        + $"to (re)connect {failureCount} times in a row. Still retrying..."
                );
            }
        };

        gatewayClient.ConnectionRestored += failureCount =>
        {
            lock (console)
            {
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [gateway] Connection restored "
                        + $"after {failureCount} failed attempt(s)."
                );
            }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        gatewayClient.FatalCloseOccurred += () =>
        {
            lock (console)
            {
                console.Error.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [gateway-fatal] Fatal close occurred. Stopping..."
                );
            }
            cts.Cancel();
        };

        gatewayClient.DispatchReceived += (eventType, data) =>
        {
            try
            {
                if (eventType == "READY")
                {
                    var skipRescan = false;
                    if (CatchUp || ScanMissing)
                    {
                        lock (_catchUpLock)
                        {
                            var nowRescan = DateTimeOffset.UtcNow;
                            if (nowRescan - _lastCatchUpAt < CatchUpMinInterval)
                                skipRescan = true;
                            else
                                _lastCatchUpAt = nowRescan;
                        }

                        if (skipRescan)
                        {
                            lock (console)
                                console.Output.WriteLine(
                                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [catch-up] Skipping rescan -- one already started within the last {CatchUpMinInterval.TotalSeconds:F0}s."
                                );
                        }
                    }

                    if (CatchUp && !skipRescan)
                    {
                        _ = Task.Run(
                            async () =>
                            {
                                try
                                {
                                    lock (console)
                                        console.Output.WriteLine(
                                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [catch-up] Listing channels and threads..."
                                        );
                                    var channels = new List<Channel>();
                                    await foreach (
                                        var ch in Discord.GetGuildChannelsAsync(GuildId, cts.Token)
                                    )
                                    {
                                        if (!ch.IsCategory && ch.Kind != ChannelKind.GuildForum)
                                            channels.Add(ch);
                                    }
                                    await foreach (
                                        var th in Discord.GetGuildThreadsAsync(
                                            GuildId,
                                            includeArchived: true,
                                            cancellationToken: cts.Token
                                        )
                                    )
                                    {
                                        if (th.Kind != ChannelKind.GuildForum)
                                            channels.Add(th);
                                    }
                                    foreach (var ch in channels)
                                    {
                                        queue.EnqueueChannelExport(ch.Id, isCatchup: true);
                                    }
                                    lock (console)
                                        console.Output.WriteLine(
                                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [catch-up] Queued {channels.Count} channels/threads for export."
                                        );
                                }
                                catch (Exception ex)
                                {
                                    lock (console)
                                        console.Error.WriteLine(
                                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [catch-up-error] {ex.Message}"
                                        );
                                }
                            },
                            cts.Token
                        );
                    }

                    if (ScanMissing && !skipRescan)
                    {
                        _ = Task.Run(
                            async () =>
                            {
                                try
                                {
                                    lock (console)
                                        console.Output.WriteLine(
                                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [scan-missing] Listing channels and threads for full rescan..."
                                        );
                                    var channels = new List<Channel>();
                                    await foreach (
                                        var ch in Discord.GetGuildChannelsAsync(GuildId, cts.Token)
                                    )
                                    {
                                        if (!ch.IsCategory && ch.Kind != ChannelKind.GuildForum)
                                            channels.Add(ch);
                                    }
                                    await foreach (
                                        var th in Discord.GetGuildThreadsAsync(
                                            GuildId,
                                            includeArchived: true,
                                            cancellationToken: cts.Token
                                        )
                                    )
                                    {
                                        if (th.Kind != ChannelKind.GuildForum)
                                            channels.Add(th);
                                    }
                                    foreach (var ch in channels)
                                    {
                                        queue.EnqueueChannelExport(ch.Id, forceFullScan: true);
                                    }
                                    lock (console)
                                        console.Output.WriteLine(
                                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [scan-missing] Queued {channels.Count} channels/threads for full rescan."
                                        );
                                }
                                catch (Exception ex)
                                {
                                    lock (console)
                                        console.Error.WriteLine(
                                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [scan-missing-error] {ex.Message}"
                                        );
                                }
                            },
                            cts.Token
                        );
                    }
                    return ValueTask.CompletedTask;
                }

                if (
                    data.TryGetProperty("guild_id", out var gProp)
                    && gProp.ValueKind == JsonValueKind.String
                )
                {
                    if (gProp.GetString() != GuildId.ToString())
                        return ValueTask.CompletedTask;
                }
                else
                {
                    if (eventType != "RESUMED")
                        return ValueTask.CompletedTask;
                }

                switch (eventType)
                {
                    case "MESSAGE_CREATE":
                        {
                            var channelId = Snowflake.Parse(
                                data.GetProperty("channel_id").GetString()!
                            );
                            var timestamp = DateTimeOffset.Parse(
                                data.GetProperty("timestamp").GetString()!
                            );

                            // Fast path: a MESSAGE_CREATE payload is always the complete message,
                            // so write it straight to the database now (sub-second, no Discord
                            // round-trip). If parsing ever fails, fall through to the export below.
                            try
                            {
                                var message = Message.Parse(data);

                                // The payload embeds the author's guild member block (nick, roles,
                                // join date, ...) under "member" -- capture it so the author is
                                // stored complete without a REST member fetch.
                                Member? authorMember = null;
                                if (
                                    data.TryGetProperty("member", out var memberJson)
                                    && memberJson.ValueKind == JsonValueKind.Object
                                )
                                {
                                    authorMember = Member.ParseFromMessage(
                                        memberJson,
                                        message.Author,
                                        GuildId
                                    );
                                }

                                queue.EnqueueMessageUpsert(channelId, message, authorMember);

                                lock (console)
                                    console.Output.WriteLine(
                                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [message] channel={channelId} author={message.Author.FullName}: {DescribeMessageContent(message)}"
                                    );
                            }
                            catch (Exception ex)
                            {
                                lock (console)
                                    console.Error.WriteLine(
                                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [dispatch-error] Failed to parse MESSAGE_CREATE payload; relying on channel export: {ex.Message}"
                                    );
                            }

                            // Also queue the debounced channel export: it advances the stored
                            // cursor, backfills anything missed during a gateway gap, and enriches
                            // author roles/members that the raw gateway payload doesn't carry.
                            queue.EnqueueChannelExport(channelId, timestamp);
                        }
                        break;

                    case "MESSAGE_UPDATE":
                        {
                            var channelId = Snowflake.Parse(
                                data.GetProperty("channel_id").GetString()!
                            );
                            var messageId = Snowflake.Parse(data.GetProperty("id").GetString()!);
                            queue.EnqueuePatch(channelId, messageId, eventType);
                        }
                        break;

                    case "MESSAGE_DELETE":
                        {
                            var channelId = Snowflake.Parse(
                                data.GetProperty("channel_id").GetString()!
                            );
                            var messageId = Snowflake.Parse(data.GetProperty("id").GetString()!);
                            queue.EnqueueDelete(channelId, messageId);
                        }
                        break;

                    case "MESSAGE_DELETE_BULK":
                        {
                            var channelId = Snowflake.Parse(
                                data.GetProperty("channel_id").GetString()!
                            );
                            var ids = data.GetProperty("ids")
                                .EnumerateArray()
                                .Select(x => Snowflake.Parse(x.GetString()!));
                            queue.EnqueueDelete(channelId, ids);
                        }
                        break;

                    case "GUILD_EMOJIS_UPDATE":
                    case "GUILD_STICKERS_UPDATE":
                    case "GUILD_SCHEDULED_EVENT_CREATE":
                    case "GUILD_SCHEDULED_EVENT_UPDATE":
                    case "GUILD_SCHEDULED_EVENT_DELETE":
                    // Roles have no per-item CREATE/UPDATE parsing here because SyncGuildAsync
                    // already re-upserts every current role on each sync -- including deletion:
                    // it diffs the live role list against the DB and soft-deletes anything
                    // missing, so GUILD_ROLE_DELETE doesn't need its own direct-mark handler
                    // either.
                    case "GUILD_ROLE_CREATE":
                    case "GUILD_ROLE_UPDATE":
                    case "GUILD_ROLE_DELETE":
                        queue.EnqueueGuildSync(eventType);
                        break;

                    case "CHANNEL_DELETE":
                    case "THREAD_DELETE":
                        {
                            var channelId = Snowflake.Parse(data.GetProperty("id").GetString()!);
                            queue.EnqueueChannelDelete(channelId);
                        }
                        break;

                    case "THREAD_MEMBERS_UPDATE":
                        {
                            var channelId = Snowflake.Parse(data.GetProperty("id").GetString()!);

                            var addedMembers = data.TryGetProperty(
                                "added_members",
                                out var addedProp
                            )
                                ? addedProp.EnumerateArray().Select(ThreadMember.Parse).ToArray()
                                : [];

                            var removedMemberIds = data.TryGetProperty(
                                "removed_member_ids",
                                out var removedProp
                            )
                                ? removedProp
                                    .EnumerateArray()
                                    .Select(x => Snowflake.Parse(x.GetString()!))
                                    .ToArray()
                                : [];

                            queue.EnqueueThreadMembersUpdate(
                                channelId,
                                addedMembers,
                                removedMemberIds
                            );
                        }
                        break;

                    case "MESSAGE_REACTION_ADD":
                    case "MESSAGE_REACTION_REMOVE":
                    case "MESSAGE_REACTION_REMOVE_ALL":
                    case "MESSAGE_REACTION_REMOVE_EMOJI":
                        {
                            var channelId = Snowflake.Parse(
                                data.GetProperty("channel_id").GetString()!
                            );
                            var messageId = Snowflake.Parse(
                                data.GetProperty("message_id").GetString()!
                            );
                            queue.EnqueuePatch(channelId, messageId, eventType);
                        }
                        break;

                    case "MESSAGE_POLL_VOTE_ADD":
                    case "MESSAGE_POLL_VOTE_REMOVE":
                        {
                            var channelId = Snowflake.Parse(
                                data.GetProperty("channel_id").GetString()!
                            );
                            var messageId = Snowflake.Parse(
                                data.GetProperty("message_id").GetString()!
                            );
                            var answerId = data.GetProperty("answer_id").GetInt32();
                            var userId = Snowflake.Parse(data.GetProperty("user_id").GetString()!);
                            queue.EnqueuePollVote(
                                channelId,
                                messageId,
                                answerId,
                                userId,
                                eventType == "MESSAGE_POLL_VOTE_ADD",
                                eventType
                            );
                        }
                        break;

                    case "CHANNEL_PINS_UPDATE":
                        {
                            var channelId = Snowflake.Parse(
                                data.GetProperty("channel_id").GetString()!
                            );
                            _ = Task.Run(
                                async () =>
                                {
                                    try
                                    {
                                        var newPins = await Discord.GetPinnedMessagesAsync(
                                            channelId,
                                            cts.Token
                                        );
                                        var newPinIds = new HashSet<Snowflake>(
                                            newPins.Select(p => p.Id)
                                        );

                                        lock (_knownPinnedIds)
                                        {
                                            if (
                                                _knownPinnedIds.TryGetValue(
                                                    channelId,
                                                    out var oldPinIds
                                                )
                                            )
                                            {
                                                var changed = new HashSet<Snowflake>();
                                                foreach (var id in newPinIds)
                                                    if (!oldPinIds.Contains(id))
                                                        changed.Add(id);
                                                foreach (var id in oldPinIds)
                                                    if (!newPinIds.Contains(id))
                                                        changed.Add(id);

                                                foreach (var id in changed)
                                                    queue.EnqueuePatch(channelId, id, eventType);
                                            }
                                            // First pins update seen for this channel this
                                            // session: just record the current set as the
                                            // baseline. Refreshing every currently-pinned message
                                            // here would be a burst of patches on channels with
                                            // many pins (which can stall the queue); only act on
                                            // subsequent diffs against this baseline.
                                            _knownPinnedIds[channelId] = newPinIds;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        lock (console)
                                            console.Error.WriteLine(
                                                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pins-error] {ex.Message}"
                                            );
                                    }
                                },
                                cts.Token
                            );
                        }
                        break;

                    case "THREAD_CREATE":
                        {
                            var threadId = Snowflake.Parse(data.GetProperty("id").GetString()!);
                            var timestamp =
                                data.TryGetProperty("thread_metadata", out var meta)
                                && meta.TryGetProperty("create_timestamp", out var ct)
                                    ? DateTimeOffset.Parse(ct.GetString()!)
                                    : DateTimeOffset.UtcNow;
                            queue.EnqueueChannelExport(threadId, timestamp);
                        }
                        break;

                    case "THREAD_UPDATE":
                        {
                            var channelId = Snowflake.Parse(data.GetProperty("id").GetString()!);
                            queue.EnqueueChannelExport(channelId, null);
                        }
                        break;

                    case "CHANNEL_UPDATE":
                        {
                            var channelId = Snowflake.Parse(data.GetProperty("id").GetString()!);
                            var isCategory =
                                data.TryGetProperty("type", out var typeProp)
                                && typeProp.GetInt32() == 4;
                            if (isCategory)
                            {
                                _ = Task.Run(
                                    async () =>
                                    {
                                        try
                                        {
                                            lock (console)
                                                console.Output.WriteLine(
                                                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [gateway] Category updated: {channelId}. Enqueueing child channels..."
                                                );
                                            await foreach (
                                                var ch in Discord.GetGuildChannelsAsync(
                                                    GuildId,
                                                    cts.Token
                                                )
                                            )
                                            {
                                                if (ch.Parent?.Id == channelId)
                                                {
                                                    queue.EnqueueChannelExport(ch.Id, null);
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            lock (console)
                                                console.Error.WriteLine(
                                                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [gateway-error] Category update child enumeration failed: {ex.Message}"
                                                );
                                        }
                                    },
                                    cts.Token
                                );
                            }
                            else
                            {
                                queue.EnqueueChannelExport(channelId, null);
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                lock (console)
                    console.Error.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [dispatch-error] {ex.Message}"
                    );
            }

            return ValueTask.CompletedTask;
        };

        var gatewayTask = Task.Run(() => gatewayClient.RunAsync(cts.Token), cts.Token);

        // Processes one item and reports the outcome back to the queue. Shared between the
        // sequential inline path (everything except channel exports) and the concurrent
        // background path (channel exports only, see the loop below) so both go through
        // identical error handling/retry/logging. Cancellation is deliberately left to
        // propagate out uncaught -- what it means differs by caller (break the main loop vs.
        // let a background task end quietly), so each call site handles it itself.
        async Task ProcessAndReportAsync(QueueItem item, CancellationToken itemToken)
        {
            try
            {
                await ProcessQueueItemAsync(item, store, console, itemToken);
                queue.ReportSuccess(item);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Flush (not rollback): with channel exports now running concurrently, other
                // channels may have writes pending in this same shared store, and a rollback
                // would discard theirs too. Safe to keep whatever's pending instead -- every
                // write is idempotent and a channel's own cursor only advances on full success,
                // so a partial attempt just gets harmlessly re-covered by the next run.
                try
                {
                    await store.FlushAsync(CancellationToken.None);
                }
                catch (Exception flushEx)
                {
                    lock (console)
                        console.Error.WriteLine(
                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [flush-error] Failed to flush pending writes: {flushEx.Message}"
                        );
                }

                lock (console)
                {
                    console.Error.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump-error] Error processing queue item: {ex.Message}"
                    );
                }

                var retrying = queue.ReportFailure(item);
                if (retrying)
                {
                    lock (console)
                        console.Output.WriteLine(
                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Re-queued item for retry."
                        );
                }
                else
                {
                    lock (console)
                        console.Error.WriteLine(
                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Gave up on queue item after maximum retries."
                        );
                }
            }
        }

        var lastStatusLog = DateTimeOffset.UtcNow;

        // Channel exports (catch-up backlog, scan-missing, debounced regular re-exports) run
        // concurrently in the background -- WatchGuildQueue caps how many are in flight at once
        // (--catch-up-parallel), so this list never grows past that bound. Everything else
        // (live messages, patches, deletes, poll votes, guild sync) stays on the sequential
        // inline path below: they're already cheap/local and don't benefit from concurrency,
        // and some (e.g. the live-message fast path's _knownChannelIds cache) assume a single
        // caller.
        var inFlightExportTasks = new List<Task>();

        while (!cts.Token.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow - lastStatusLog >= TimeSpan.FromSeconds(60))
            {
                lock (console)
                {
                    console.Output.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [status] queue status: "
                            + $"messages={queue.PendingMessagesCount}, channels={queue.PendingChannelsCount}, "
                            + $"inFlightExports={queue.InFlightExportsCount}, patches={queue.PendingPatchesCount}, "
                            + $"pollVotes={queue.PendingPollVotesCount}, "
                            + $"deleteChannels={queue.PendingDeleteChannelsCount}, deleteMessages={queue.PendingDeleteMessagesCount}, "
                            + $"guildSync={(queue.IsGuildSyncPending ? "pending" : "idle")}"
                    );
                }
                lastStatusLog = DateTimeOffset.UtcNow;
            }

            inFlightExportTasks.RemoveAll(t => t.IsCompleted);

            var item = queue.TryDequeue();
            if (item is ExportChannelItem)
            {
                inFlightExportTasks.Add(
                    Task.Run(
                        async () =>
                        {
                            try
                            {
                                await ProcessAndReportAsync(item, cts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                // Shutting down -- let it end quietly, nothing more to do.
                            }
                        },
                        cts.Token
                    )
                );
            }
            else if (item is not null)
            {
                try
                {
                    await ProcessAndReportAsync(item, cts.Token);
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
                {
                    break;
                }
            }
            else
            {
                try
                {
                    // Short idle tick so a freshly-arrived live message is picked up within ~100ms
                    // rather than waiting out a long poll interval.
                    await Task.Delay(100, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // Drain in-flight exports before the store gets disposed out from under them. Failures
        // were already logged inside ProcessAndReportAsync; WhenAll here is just to wait, not to
        // observe results.
        try
        {
            await Task.WhenAll(inFlightExportTasks);
        }
        catch { }

        try
        {
            await gatewayTask;
        }
        catch (OperationCanceledException) { }
    }

    // Renders a single-line, log-friendly preview of a message for the live [message] feed --
    // truncated and stripped of newlines so one incoming message is always one log line.
    private static string DescribeMessageContent(Message message)
    {
        const int maxLength = 200;

        // A "Forward" carries no content/attachments/embeds/stickers of its own -- the actual
        // forwarded text lives in the nested snapshot instead, so fall back to that.
        var content = message.Content;
        if (string.IsNullOrWhiteSpace(content))
            content = message.ForwardedMessage?.Content ?? "";

        content = content.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (content.Length > maxLength)
            content = content[..maxLength] + "…";

        if (!string.IsNullOrWhiteSpace(content))
            return message.IsForwarded ? $"[forwarded] {content}" : content;

        var attachmentCount =
            message.Attachments.Count + (message.ForwardedMessage?.Attachments.Count ?? 0);
        var embedCount = message.Embeds.Count + (message.ForwardedMessage?.Embeds.Count ?? 0);
        var stickerCount = message.Stickers.Count + (message.ForwardedMessage?.Stickers.Count ?? 0);

        var parts = new List<string>();
        if (attachmentCount > 0)
            parts.Add($"{attachmentCount} attachment(s)");
        if (embedCount > 0)
            parts.Add($"{embedCount} embed(s)");
        if (stickerCount > 0)
            parts.Add($"{stickerCount} sticker(s)");
        if (message.Poll is not null)
            parts.Add("poll");
        if (message.Components.Count > 0)
            parts.Add("components");
        if (message.IsForwarded)
            parts.Add("forwarded");

        return parts.Count > 0 ? $"[{string.Join(", ", parts)}]" : "[empty]";
    }

    // Ensures the channel row (and its guild) exist before a message's foreign key needs them.
    // Results are cached, so this costs at most one existence probe -- and, for a brand-new
    // channel/thread, one Discord fetch -- per channel per session.
    // Lazily fetches (and caches) the guild role catalog for resolving live message authors' role
    // IDs. Costs one REST call the first time, then nothing until a guild sync invalidates it.
    // Only called from the single-threaded pump loop, so the null-check/assign needs no locking.
    private async ValueTask<IReadOnlyDictionary<Snowflake, Role>> GetGuildRolesAsync(
        CancellationToken cancellationToken
    )
    {
        if (_guildRoles is null)
        {
            var map = new Dictionary<Snowflake, Role>();
            await foreach (var role in Discord.GetGuildRolesAsync(GuildId, cancellationToken))
                map[role.Id] = role;
            _guildRoles = map;
        }

        return _guildRoles;
    }

    private async ValueTask EnsureChannelExistsAsync(
        Snowflake channelId,
        SqliteExportStore store,
        CancellationToken cancellationToken
    )
    {
        if (_knownChannelIds.Contains(channelId))
            return;

        if (await store.ChannelExistsAsync(channelId, cancellationToken))
        {
            _knownChannelIds.Add(channelId);
            return;
        }

        var channel = await Discord.GetChannelAsync(channelId, cancellationToken);
        var guild = await Discord.GetGuildAsync(channel.GuildId, cancellationToken);
        await store.UpsertGuildAsync(guild, cancellationToken);
        await store.UpsertChannelAsync(channel, cancellationToken);
        _knownChannelIds.Add(channelId);
    }

    private async ValueTask<ExportRequest> CreateExportRequestAsync(
        Snowflake channelId,
        bool forceFullScan,
        CancellationToken cancellationToken,
        Snowflake? after = null
    )
    {
        var channel = await Discord.GetChannelAsync(channelId, cancellationToken);
        var guild = await Discord.GetGuildAsync(channel.GuildId, cancellationToken);
        return new ExportRequest(
            guild,
            channel,
            OutputPath,
            null,
            ExportFormat.Db,
            after,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            false,
            true,
            false,
            false,
            null,
            false,
            forceFullScan: forceFullScan,
            enrichReactors: EnrichReactors
        );
    }

    private async ValueTask ProcessQueueItemAsync(
        QueueItem item,
        SqliteExportStore store,
        IConsole console,
        CancellationToken cancellationToken
    )
    {
        async ValueTask<MessagePatchResult> PatchMessageAsync(
            Snowflake channelId,
            Snowflake messageId
        )
        {
            var req = await CreateExportRequestAsync(
                channelId,
                forceFullScan: false,
                cancellationToken
            );
            return await DatabaseMessagePatcher.PatchMessageAsync(
                req,
                Discord,
                store,
                messageId,
                cancellationToken
            );
        }

        if (item is UpsertMessageItem upsert)
        {
            // Hot path: write the already-materialized gateway message straight to the database.
            // Intentionally not logged per-message -- on a busy guild that would flood the log;
            // the periodic [status] line reports throughput instead.
            await EnsureChannelExistsAsync(upsert.ChannelId, store, cancellationToken);

            // Resolve the author's role IDs (from the gateway member block) into full Role objects
            // for the user row -- everything else the author needs (nick, join date, ...) is already
            // in the member block, so no REST member fetch is required.
            IReadOnlyList<Role> authorRoles = Array.Empty<Role>();
            if (upsert.AuthorMember is { } authorMember)
            {
                var roleMap = await GetGuildRolesAsync(cancellationToken);
                authorRoles = authorMember
                    .RoleIds.Select(roleMap.GetValueOrDefault)
                    .Where(r => r is not null)
                    .Select(r => r!)
                    .OrderByDescending(r => r.Position)
                    .ToArray();
            }

            var authorId = upsert.Message.Author.Id;
            foreach (var user in upsert.Message.GetReferencedUsers())
            {
                // Only the author's member block rides along in the payload; mentioned/referenced
                // users are upserted without member info (the store preserves any richer row a
                // prior real sighting stored, rather than clobbering it -- see UpsertUserCoreAsync).
                if (upsert.AuthorMember is not null && user.Id == authorId)
                    await store.UpsertUserAsync(
                        user,
                        upsert.AuthorMember,
                        authorRoles,
                        cancellationToken
                    );
                else
                    await store.UpsertUserAsync(user, null, Array.Empty<Role>(), cancellationToken);
            }

            await store.UpsertMessageAsync(upsert.ChannelId, upsert.Message, cancellationToken);

            // Advance the stored cursor from this live message so the debounced re-export finds
            // nothing new and skips re-fetching recent messages just to re-enrich them over REST.
            await store.AdvanceChannelCursorAsync(
                upsert.ChannelId,
                upsert.Message.Id,
                cancellationToken
            );
            await store.FlushAsync(cancellationToken);
            return;
        }

        if (item is PatchMessageItem patch)
        {
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Patching message {patch.MessageId} in channel {patch.ChannelId} ({patch.Reason})..."
                );
            var result = await PatchMessageAsync(patch.ChannelId, patch.MessageId);
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Patch result: {result.Reason}"
                );
        }
        else if (item is PollVoteItem vote)
        {
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Recording poll vote message={vote.MessageId} answer={vote.AnswerId} user={vote.UserId} ({vote.Reason})..."
                );

            await store.InsertPollVoteEventAsync(
                vote.ChannelId,
                vote.MessageId,
                vote.AnswerId,
                vote.UserId,
                vote.IsAdded,
                cancellationToken
            );
            await store.FlushAsync(cancellationToken);

            var result = await PatchMessageAsync(vote.ChannelId, vote.MessageId);
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Poll vote recorded; patch result: {result.Reason}"
                );
        }
        else if (item is MarkDeletedItem delete)
        {
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Marking {delete.MessageIds.Count} messages deleted in channel {delete.ChannelId}..."
                );
            foreach (var messageId in delete.MessageIds)
            {
                await store.MarkMessageDeletedAsync(
                    delete.ChannelId,
                    messageId,
                    DateTimeOffset.UtcNow,
                    cancellationToken
                );
            }
            await store.FlushAsync(cancellationToken);
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Marked messages deleted."
                );
        }
        else if (item is MarkChannelDeletedItem channelDelete)
        {
            var wasMarked = await store.MarkChannelDeletedAsync(
                channelDelete.ChannelId,
                DateTimeOffset.UtcNow,
                cancellationToken
            );
            await store.FlushAsync(cancellationToken);
            if (wasMarked)
                lock (console)
                    console.Output.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Marked channel {channelDelete.ChannelId} deleted."
                    );
        }
        else if (item is ThreadMembersUpdateItem threadUpdate)
        {
            if (threadUpdate.AddedMembers.Count > 0)
                await store.AddThreadMembersAsync(
                    threadUpdate.ChannelId,
                    threadUpdate.AddedMembers,
                    cancellationToken
                );
            if (threadUpdate.RemovedMemberIds.Count > 0)
                await store.RemoveThreadMembersAsync(
                    threadUpdate.ChannelId,
                    threadUpdate.RemovedMemberIds,
                    cancellationToken
                );
            await store.FlushAsync(cancellationToken);
        }
        else if (item is SyncGuildItem sync)
        {
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Syncing guild catalog ({sync.Reason})..."
                );
            var (roleCount, emojiCount, stickerCount, scheduledEventCount) =
                await SyncGuildCommand.SyncGuildAsync(Discord, store, GuildId, cancellationToken);
            // Roles may have changed (create/update/delete all funnel through here) -- drop the
            // cached catalog so the next live message resolves author roles against fresh data.
            _guildRoles = null;
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Synced guild catalog: {roleCount} roles, {emojiCount} emojis, {stickerCount} stickers, {scheduledEventCount} events."
                );
        }
        else if (item is ExportChannelItem export)
        {
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Exporting channel {export.ChannelId} (IsCatchup={export.IsCatchup}, ForceFullScan={export.ForceFullScan})..."
                );
            try
            {
                // To prevent backfilling the entire channel history for a brand-new/empty DB,
                // we set `after` to the first timestamp we observed during this run.
                // However, if the database already has a stored cursor, we leave `after` null
                // so ChannelExporter resumes from the stored cursor (LastMessageId) and correctly
                // fills any gap that arrived while disconnected.
                Snowflake? after = null;
                if (!export.IsCatchup && !export.ForceFullScan)
                {
                    var storedState = await store.GetChannelStateAsync(
                        export.ChannelId,
                        cancellationToken
                    );
                    if (storedState is null && export.FirstTimestamp.HasValue)
                    {
                        after = Snowflake.FromDate(export.FirstTimestamp.Value);
                    }
                }

                var req = await CreateExportRequestAsync(
                    export.ChannelId,
                    forceFullScan: export.ForceFullScan,
                    cancellationToken,
                    after
                );
                var exporter = new ChannelExporter(Discord);
                await exporter.ExportChannelAsync(req, store, null, cancellationToken);
                lock (console)
                    console.Output.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Exported channel {export.ChannelId}."
                    );
            }
            catch (ChannelEmptyException)
            {
                lock (console)
                    console.Output.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Channel {export.ChannelId} is empty or has no messages in the specified range."
                    );
            }
        }
    }
}
