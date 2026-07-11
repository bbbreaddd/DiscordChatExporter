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
using DiscordChatExporter.Cli.Configuration;
using DiscordChatExporter.Cli.Utils;
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

    // Channels the config excludes from backup. Seeded from the explicit id list; channels matched
    // by category / nsfw / thread rules are added as catch-up classifies them, so their subsequent
    // live events are dropped too. Read from the dispatch thread and written from the background
    // catch-up task, so all access goes through the lock.
    private readonly HashSet<Snowflake> _excludedChannelIds = new();
    private readonly object _excludedLock = new();

    private void MarkChannelExcluded(Snowflake channelId)
    {
        lock (_excludedLock)
            _excludedChannelIds.Add(channelId);
    }

    private bool IsExcludedChannelId(Snowflake channelId)
    {
        lock (_excludedLock)
            return _excludedChannelIds.Contains(channelId);
    }

    // Classifies a freshly-listed channel against the config's exclusion rules (id / category /
    // nsfw / thread). Pure -- reads only the immutable settings.
    private bool IsChannelExcluded(Channel channel)
    {
        if (_settings.ExcludeChannels.Contains(channel.Id))
            return true;
        if (channel.Parent is { } parent && _settings.ExcludeCategories.Contains(parent.Id))
            return true;
        if (_settings.ExcludeNsfw && channel.IsNsfw)
            return true;
        if (_settings.ExcludeThreads && channel.IsThread)
            return true;
        return false;
    }

    // Applies the full-scan block's own scope (its include/exclude channel + category lists) to a
    // listed channel/thread. The server-level exclusions are applied separately via IsChannelExcluded,
    // so a full-scan never re-scans a channel the watcher is configured to skip. Category scoping
    // matches on a channel's parent category; threads (whose parent is their channel) are matched by
    // id, so pair category scoping with include-threads + explicit ids if you need thread coverage.
    private bool InFullScanScope(Channel channel)
    {
        var fs = _settings.FullScan;
        if (fs.Channels.Count > 0 && !fs.Channels.Contains(channel.Id))
            return false;
        if (fs.Categories.Count > 0)
        {
            var categoryId = channel.Parent?.Id;
            if (categoryId is null || !fs.Categories.Contains(categoryId.Value))
                return false;
        }
        if (fs.ExcludeChannels.Contains(channel.Id))
            return false;
        if (channel.Parent is { } parent && fs.ExcludeCategories.Contains(parent.Id))
            return false;
        return true;
    }

    // When set (the `watch --config` path), these replace the CLI options as the source of the
    // watch's behavior. Null on the plain `watchguild` CLI path, where BuildSettingsFromOptions()
    // maps the options instead. GuildId/OutputPath/token are always taken from the command itself.
    internal WatchSettings? SettingsOverride { get; set; }

    private WatchSettings _settings = null!;

    // Set for the duration of a scheduled full-scan run (see RunFullScanAsync), consulted by
    // CreateExportRequestAsync for force-full-scan exports only. Volatile because it is written by
    // the scheduler task and read by the export pump. Null whenever no full-scan is in flight.
    private volatile FullScanProfile? _activeFullScanProfile;

    // A fatal gateway close (revoked token/intents) fires a notification and then cancels; the
    // handler stashes that in-flight notify here so the shutdown path can await delivery before the
    // process exits (the notify runs on CancellationToken.None so the cancel doesn't kill it).
    private Task? _fatalCloseNotifyTask;

    // The inline (pump-thread) live-write handlers no longer flush per item; they set this and the
    // main loop flushes once when the queue drains, batching a burst of live events into a single
    // transaction. Only ever touched on the pump thread (inline processing is awaited sequentially
    // with the drain check), so no synchronization is needed; background channel/gap exports
    // self-flush and never set it.
    private bool _hasPendingInlineWrites;

    // The window + reactor policy a scheduled full-scan applies to its force-full-scan exports.
    private sealed record FullScanProfile(Snowflake? After, Snowflake? Before, bool EnrichReactors);

    private WatchSettings BuildSettingsFromOptions() =>
        new()
        {
            DownloadMedia = ShouldDownloadAssets,
            MediaDir = AssetsDirPath,
            RetryFailedMedia = RetryFailedMedia,
            CatchUp = CatchUp,
            ScanMissing = ScanMissing,
            CatchUpParallel = CatchUpParallel,
            EnrichReactors = EnrichReactors,
            // Data toggles / exclusions keep their permissive defaults on the CLI path -- those
            // knobs are only surfaced through the YAML config, not as watchguild flags.
        };

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        await base.ExecuteAsync(console);

        if (ExportFormat != ExportFormat.Db)
            throw new CommandException("Option --format only supports 'Db' for watchguild.");

        // CLI-only validation: the config path validates its own media block on load.
        if (
            SettingsOverride is null
            && !string.IsNullOrWhiteSpace(AssetsDirPath)
            && !ShouldDownloadAssets
        )
            throw new CommandException("Option --media-dir cannot be used without --media.");

        _settings = SettingsOverride ?? BuildSettingsFromOptions();

        foreach (var excludedId in _settings.ExcludeChannels)
            MarkChannelExcluded(excludedId);

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
        var queue = new WatchGuildQueue(
            maxConcurrentExports: Math.Max(1, _settings.CatchUpParallel)
        );

        await console.Output.WriteLineAsync(
            $"Starting watchguild for guild {GuildId} into '{OutputPath}'..."
        );

        var storeDataOptions = new StoreDataOptions
        {
            MediaAssetKinds = _settings.MediaAssets.ToHashSet(),
            CaptureReactions = _settings.CaptureReactions,
            CaptureEmbeds = _settings.CaptureEmbeds,
            CaptureStickers = _settings.CaptureStickers,
            CapturePolls = _settings.CapturePolls,
        };

        await using var store = await SqliteExportStore.OpenAsync(
            OutputPath,
            _settings.DownloadMedia ? _settings.MediaDir ?? $"{OutputPath}_Files" : null,
            _settings.RetryFailedMedia,
            storeDataOptions,
            cancellationToken
        );

        // Operator notifications (Apprise). Empty target list when notifications are disabled, so
        // HasTargets is false and every notify call is a no-op.
        var notifier = new AppriseNotifier(
            _settings.Notifications.Enabled ? _settings.Notifications.Urls : Array.Empty<string>()
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

            // ConnectionDown is raised exactly once per outage (the client guards it), so this
            // fires at most once per stuck period -- no debouncing needed here. Fire-and-forget on
            // an uncancellable token: the watcher keeps retrying regardless of delivery.
            if (_settings.Notifications.OnConnectionDown && notifier.HasTargets)
                _ = notifier.TryNotifyAsync(
                    "[watchguild] Gateway connection down",
                    $"Guild {GuildId}: the gateway has failed to (re)connect {failureCount} times "
                        + "in a row and is still retrying.",
                    CancellationToken.None
                );
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

            // Kick off the notification BEFORE cancelling -- a fatal close (revoked token/intents,
            // auth failure) stops the watcher for good, so this is the alert the operator most needs.
            // Runs on CancellationToken.None so the cancel below doesn't abort it; the shutdown path
            // awaits _fatalCloseNotifyTask so delivery completes before the process exits.
            if (_settings.Notifications.OnFatalClose && notifier.HasTargets)
                _fatalCloseNotifyTask = notifier.TryNotifyAsync(
                    "[watchguild] Fatal gateway close -- watcher stopping",
                    $"Guild {GuildId}: the gateway reported a fatal close (usually a revoked token or "
                        + "revoked/insufficient intents). The watcher is shutting down and will not "
                        + "reconnect until it is restarted.",
                    CancellationToken.None
                );

            cts.Cancel();
        };

        gatewayClient.DispatchReceived += (eventType, data) =>
        {
            try
            {
                if (eventType == "READY")
                {
                    var skipRescan = false;
                    if (
                        _settings.CatchUp
                        || _settings.ScanMissing
                        || _settings.SyncGuildCatalog
                        || _settings.CapturePins
                        || _settings.CapturePolls
                        // Gap tracking runs whenever nothing else backfills (catch-up/scan off), and
                        // should honor the same reconnect-flap throttle as the other startup work.
                        || (!_settings.CatchUp && !_settings.ScanMissing)
                    )
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

                    if (_settings.SyncGuildCatalog && !skipRescan)
                    {
                        queue.EnqueueGuildSync("startup", immediate: true);
                        lock (console)
                            console.Output.WriteLine(
                                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [catch-up] Queued guild-catalog sync (startup)."
                            );
                    }

                    // One channel-list pass at reconnect that drives two independent reconciliations,
                    // both keyed on the same signal (a channel whose live last_message_id is ahead of
                    // our stored cursor):
                    //   * Gap tracking (only when catch-up/scan-missing are off, so nothing else
                    //     backfills): record the (cursor, live] window of messages that arrived during
                    //     downtime, so the hole is discoverable and `fillgaps` can recover it -- instead
                    //     of the cursor silently jumping past it.
                    //   * Pins: pins aren't covered by catch-up or message history, so diff the live
                    //     pinned set against the DB for active channels and patch what changed (also
                    //     seeds _knownPinnedIds so the first live CHANNEL_PINS_UPDATE won't re-baseline).
                    var trackGaps = !_settings.CatchUp && !_settings.ScanMissing;
                    if ((_settings.CapturePins || trackGaps) && !skipRescan)
                    {
                        _ = Task.Run(
                            async () =>
                            {
                                try
                                {
                                    var storedCursors = await store.GetChannelLastMessageIdsAsync(
                                        GuildId,
                                        cts.Token
                                    );
                                    var reconciled = 0;
                                    var gaps = 0;
                                    await foreach (
                                        var ch in Discord.GetGuildChannelsAsync(GuildId, cts.Token)
                                    )
                                    {
                                        // Categories/forums hold no messages of their own, so no pins.
                                        if (ch.IsCategory || ch.Kind == ChannelKind.GuildForum)
                                            continue;
                                        if (IsChannelExcluded(ch))
                                        {
                                            MarkChannelExcluded(ch.Id);
                                            continue;
                                        }
                                        // "Active" = has messages beyond our stored cursor (a
                                        // never-scanned channel uses cursor 0, so it counts too).
                                        var cursor = storedCursors.GetValueOrDefault(ch.Id);
                                        if (!ch.MayHaveMessagesAfter(cursor))
                                            continue;

                                        // Isolate each channel: a channel the bot can't read (403
                                        // forbidden) or a transient failure must not abort the
                                        // reconciliation of every remaining channel.
                                        try
                                        {
                                            // Gap: only for channels we were actually recording (a
                                            // stored cursor exists). A never-seen channel is missing
                                            // its whole history, which isn't a downtime gap.
                                            if (
                                                trackGaps
                                                && storedCursors.TryGetValue(
                                                    ch.Id,
                                                    out var recordedCursor
                                                )
                                                && ch.LastMessageId is { } liveLast
                                                && liveLast > recordedCursor
                                            )
                                            {
                                                await store.RecordMessageGapAsync(
                                                    ch.Id,
                                                    recordedCursor,
                                                    liveLast,
                                                    cts.Token
                                                );
                                                gaps++;
                                            }

                                            if (_settings.CapturePins)
                                            {
                                                var livePins = await Discord.GetPinnedMessagesAsync(
                                                    ch.Id,
                                                    cts.Token
                                                );
                                                var livePinIds = new HashSet<Snowflake>(
                                                    livePins.Select(p => p.Id)
                                                );
                                                var storedPinIds =
                                                    await store.GetPinnedMessageIdsAsync(
                                                        ch.Id,
                                                        cts.Token
                                                    );

                                                var changed = new HashSet<Snowflake>(livePinIds);
                                                changed.SymmetricExceptWith(storedPinIds);
                                                foreach (var id in changed)
                                                    queue.EnqueuePatch(ch.Id, id, "pins-reconcile");

                                                // Seed the in-memory baseline with the live set so the
                                                // next live pins-update diffs against it instead of
                                                // re-baselining.
                                                lock (_knownPinnedIds)
                                                    _knownPinnedIds[ch.Id] = livePinIds;

                                                if (changed.Count > 0)
                                                    reconciled++;
                                            }
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            throw;
                                        }
                                        catch (Exception ex)
                                        {
                                            lock (console)
                                                console.Error.WriteLine(
                                                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [reconnect-scan] Skipped channel {ch.Id}: {ex.Message}"
                                                );
                                        }
                                    }

                                    // Persist the recorded gaps promptly (they're written straight to
                                    // the store from this background task).
                                    if (gaps > 0)
                                        await store.FlushAsync(cts.Token);

                                    lock (console)
                                    {
                                        if (_settings.CapturePins)
                                            console.Output.WriteLine(
                                                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pins-reconcile] Reconciled pins in {reconciled} channel(s) with changes."
                                            );
                                        if (trackGaps && gaps > 0)
                                            console.Output.WriteLine(
                                                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [gap-tracking] Recorded downtime gaps in {gaps} channel(s). Run `fillgaps` to backfill them."
                                            );
                                    }
                                }
                                catch (Exception ex)
                                {
                                    lock (console)
                                        console.Error.WriteLine(
                                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [reconnect-scan-error] {ex.Message}"
                                        );
                                }
                            },
                            cts.Token
                        );
                    }

                    // A poll that ended while the watcher was offline never had its finalized
                    // results captured -- the stored poll_json still reads is_finalized=false with
                    // stale counts. Re-fetch each such message once to pull in the final tally.
                    // (Only aggregate counts are recoverable; Discord has no post-hoc voter list.)
                    if (_settings.CapturePolls && !skipRescan)
                    {
                        _ = Task.Run(
                            async () =>
                            {
                                try
                                {
                                    var polls = await store.GetUnfinalizedExpiredPollsAsync(
                                        DateTimeOffset.UtcNow,
                                        cts.Token
                                    );
                                    var queued = 0;
                                    foreach (var (channelId, messageId) in polls)
                                    {
                                        if (IsExcludedChannelId(channelId))
                                            continue;
                                        queue.EnqueuePatch(channelId, messageId, "poll-reconcile");
                                        queued++;
                                    }
                                    if (queued > 0)
                                        lock (console)
                                            console.Output.WriteLine(
                                                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [poll-reconcile] Queued {queued} finished poll(s) for a results refresh."
                                            );
                                }
                                catch (Exception ex)
                                {
                                    lock (console)
                                        console.Error.WriteLine(
                                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [poll-reconcile-error] {ex.Message}"
                                        );
                                }
                            },
                            cts.Token
                        );
                    }

                    if (_settings.CatchUp && !skipRescan)
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
                                    if (_settings.CaptureThreads)
                                    {
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
                                    }
                                    // Snapshot every channel's stored cursor once, then record a
                                    // durable gap for the (cursor, liveLast] backlog that piled up
                                    // while the watcher was offline. Recording this BEFORE any
                                    // backfill runs -- and keying the backfill off the snapshotted
                                    // gap rather than the channel's live-advanced last_message_id --
                                    // is what makes catch-up interruption-safe: a live message can
                                    // shove the cursor past history that was never backfilled, so a
                                    // cursor-resuming catch-up would skip that history forever. The
                                    // gap row is the source of truth; an interrupted run just leaves
                                    // it unfilled to retry, never a silent hole.
                                    var storedCursors = await store.GetChannelLastMessageIdsAsync(
                                        GuildId,
                                        cts.Token
                                    );
                                    // Lazily upserted (once, only if there's a gap to record) so the
                                    // channel rows below satisfy their guild_id foreign key on a
                                    // fresh database where the guild-catalog sync hasn't run yet.
                                    Guild? guildRow = null;
                                    var recorded = 0;
                                    foreach (var ch in channels)
                                    {
                                        // Already excluded -- either by a config rule, or marked
                                        // this session because a prior backfill found it unreadable
                                        // (a forbidden/deleted channel). Skipping here stops such a
                                        // channel from being re-recorded and re-probed on every
                                        // reconnect within the session.
                                        if (IsExcludedChannelId(ch.Id))
                                            continue;
                                        if (IsChannelExcluded(ch))
                                        {
                                            MarkChannelExcluded(ch.Id);
                                            continue;
                                        }
                                        // No messages ever -> nothing to catch up.
                                        if (ch.LastMessageId is not { } liveLast)
                                            continue;
                                        var hasCursor = storedCursors.TryGetValue(
                                            ch.Id,
                                            out var cursor
                                        );
                                        // Already level with the live tip -> no backlog.
                                        if (hasCursor && !(cursor < liveLast))
                                            continue;
                                        // Ensure the guild + channel rows exist before recording the
                                        // gap. GetUnfilledGapsAsync filters by guild via a JOIN on
                                        // the channel table, so a gap whose channel row hasn't been
                                        // created yet (a fresh DB, or the startup guild-catalog sync
                                        // still racing on the pump) would be silently invisible to
                                        // the enqueue below -- catch-up would record gaps and back
                                        // up nothing. The guild row is upserted first to satisfy the
                                        // channel's guild_id foreign key. Both are idempotent and
                                        // also capture metadata we need regardless.
                                        if (guildRow is null)
                                        {
                                            guildRow = await Discord.GetGuildAsync(
                                                GuildId,
                                                cts.Token
                                            );
                                            await store.UpsertGuildAsync(guildRow, cts.Token);
                                        }
                                        await store.UpsertChannelAsync(ch, cts.Token);
                                        // Never-scanned channels have no lower bound, so backfill
                                        // from the beginning (after = 0 fetches full history).
                                        var after = hasCursor ? cursor : Snowflake.Zero;
                                        await store.RecordMessageGapAsync(
                                            ch.Id,
                                            after,
                                            liveLast,
                                            cts.Token
                                        );
                                        recorded++;
                                    }
                                    await store.FlushAsync(cts.Token);

                                    // Enqueue a bounded backfill for every unfilled gap -- the ones
                                    // just recorded plus any a previously interrupted catch-up left
                                    // behind. Each runs on the shared export concurrency pool and
                                    // marks its gap filled only on full success.
                                    var gaps = await store.GetUnfilledGapsAsync(GuildId, cts.Token);
                                    var queued = 0;
                                    foreach (var gap in gaps)
                                    {
                                        if (IsExcludedChannelId(gap.ChannelId))
                                        {
                                            // Leftover gap for a now-excluded channel -- resolve it
                                            // instead of leaving it to be re-read (and skipped) on
                                            // every run forever.
                                            await store.MarkGapFilledAsync(gap.Id, cts.Token);
                                            continue;
                                        }
                                        queue.EnqueueGapBackfill(
                                            gap.Id,
                                            gap.ChannelId,
                                            gap.AfterMessageId,
                                            gap.BeforeMessageId
                                        );
                                        queued++;
                                    }
                                    lock (console)
                                        console.Output.WriteLine(
                                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [catch-up] Recorded {recorded} new backlog gap(s); queued {queued} gap backfill(s) (includes any left by a prior interrupted run)."
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

                    if (_settings.ScanMissing && !skipRescan)
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
                                    if (_settings.CaptureThreads)
                                    {
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
                                    }
                                    var queued = 0;
                                    foreach (var ch in channels)
                                    {
                                        if (IsChannelExcluded(ch))
                                        {
                                            MarkChannelExcluded(ch.Id);
                                            continue;
                                        }
                                        queue.EnqueueChannelExport(ch.Id, forceFullScan: true);
                                        queued++;
                                    }
                                    lock (console)
                                        console.Output.WriteLine(
                                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [scan-missing] Queued {queued} channels/threads for full rescan ({channels.Count - queued} excluded)."
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

                // Drop events for excluded channels uniformly. Covers every channel-scoped event
                // that carries "channel_id" (messages, reactions, pins, poll votes, deletes);
                // channel/thread lifecycle events use "id" and are backstopped at export time.
                if (
                    data.TryGetProperty("channel_id", out var scopedChannelProp)
                    && scopedChannelProp.ValueKind == JsonValueKind.String
                    && IsExcludedChannelId(Snowflake.Parse(scopedChannelProp.GetString()!))
                )
                {
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
                                    _settings.CaptureMembers
                                    && data.TryGetProperty("member", out var memberJson)
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
                        if (_settings.SyncGuildCatalog)
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
                            if (!_settings.CapturePolls)
                                break;
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
                            if (!_settings.CapturePins)
                                break;
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
                            if (!_settings.CaptureThreads)
                                break;
                            var threadId = Snowflake.Parse(data.GetProperty("id").GetString()!);
                            if (IsExcludedChannelId(threadId))
                                break;
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
                            if (!_settings.CaptureThreads)
                                break;
                            var channelId = Snowflake.Parse(data.GetProperty("id").GetString()!);
                            if (IsExcludedChannelId(channelId))
                                break;
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

        // ----- Scheduled reliability jobs (backup + full-scan), driven by cron -----

        // A scheduled online backup via a separate read connection (never blocks the writer).
        async Task RunBackupJobAsync(CancellationToken jobToken)
        {
            var cfg = _settings.Backup;
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [backup] Starting scheduled backup to '{cfg.Dir}'..."
                );
            try
            {
                var service = new DatabaseBackupService(
                    OutputPath,
                    cfg.Dir!,
                    cfg.KeepDaily,
                    cfg.KeepWeekly,
                    cfg.Compress,
                    cfg.IntegrityCheck
                );
                var result = await service.RunAsync(jobToken);
                var sizeMb = result.SizeBytes / 1024d / 1024d;

                if (!result.IntegrityOk)
                {
                    lock (console)
                        console.Error.WriteLine(
                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [backup] Backup written to '{result.Path}' but FAILED its integrity check."
                        );
                    if (_settings.Notifications.OnBackupFailure && notifier.HasTargets)
                        await notifier.TryNotifyAsync(
                            "[watchguild] Backup integrity check FAILED",
                            $"Guild {GuildId}: backup '{result.Path}' failed PRAGMA quick_check -- the copy may be corrupt.",
                            jobToken
                        );
                    return;
                }

                lock (console)
                    console.Output.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [backup] Backup ok: '{result.Path}' ({sizeMb:F1} MB); pruned {result.Pruned} old copies."
                    );
                if (_settings.Notifications.OnBackupSuccess && notifier.HasTargets)
                    await notifier.TryNotifyAsync(
                        "[watchguild] Backup complete",
                        $"Guild {GuildId}: backup written to '{result.Path}' ({sizeMb:F1} MB); pruned {result.Pruned} old copies.",
                        jobToken
                    );
            }
            catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (console)
                    console.Error.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [backup] Backup FAILED: {ex.Message}"
                    );
                if (_settings.Notifications.OnBackupFailure && notifier.HasTargets)
                    await notifier.TryNotifyAsync(
                        "[watchguild] Backup FAILED",
                        $"Guild {GuildId}: scheduled backup failed: {ex.Message}",
                        CancellationToken.None
                    );
            }
        }

        // A scheduled VACUUM to reclaim free space, run in-process through the shared store so it
        // serializes with live capture (writes queue in memory for the duration) instead of fighting
        // it for the database lock.
        async Task RunVacuumJobAsync(CancellationToken jobToken)
        {
            var cfg = _settings.Vacuum;
            var mode = cfg.Incremental ? "incremental" : "full";
            long beforeBytes = 0;
            try
            {
                beforeBytes = new FileInfo(OutputPath).Length;
            }
            catch
            {
                // Size is only for the log line; ignore if the file can't be stat'd.
            }

            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [vacuum] Starting {mode} vacuum..."
                );

            var started = DateTimeOffset.UtcNow;
            try
            {
                await store.VacuumAsync(cfg.Incremental, jobToken);

                long afterBytes = beforeBytes;
                try
                {
                    afterBytes = new FileInfo(OutputPath).Length;
                }
                catch
                {
                    // ignored
                }

                var secs = (DateTimeOffset.UtcNow - started).TotalSeconds;
                var afterMb = afterBytes / 1024d / 1024d;
                var reclaimedMb = (beforeBytes - afterBytes) / 1024d / 1024d;
                lock (console)
                    console.Output.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [vacuum] Done in {secs:F0}s: "
                            + $"{afterMb:F1} MB (reclaimed {reclaimedMb:F1} MB)."
                    );
            }
            catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (console)
                    console.Error.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [vacuum] Vacuum FAILED: {ex.Message}"
                    );
            }
        }

        // A scheduled full re-scan of the in-scope channels/threads, to reconcile edits/reactions on
        // already-stored messages that catch-up can't see. Runs in-process through the shared store,
        // so it serializes cleanly with live capture (no cross-process "database is locked").
        async Task RunFullScanJobAsync(CancellationToken jobToken)
        {
            var cfg = _settings.FullScan;
            var now = DateTimeOffset.UtcNow;
            Snowflake? after = cfg.After is not null
                ? Snowflake.FromDate(TimeWindow.Resolve(cfg.After, now))
                : null;
            Snowflake? before = cfg.Before is not null
                ? Snowflake.FromDate(TimeWindow.Resolve(cfg.Before, now))
                : null;

            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [full-scan] Starting scheduled full-scan..."
                );

            _activeFullScanProfile = new FullScanProfile(after, before, cfg.EnrichReactors);
            store.SuppressMediaDownloads = !cfg.Media;
            try
            {
                var channels = new List<Channel>();
                await foreach (var ch in Discord.GetGuildChannelsAsync(GuildId, jobToken))
                {
                    if (!ch.IsCategory && ch.Kind != ChannelKind.GuildForum)
                        channels.Add(ch);
                }
                if (cfg.IncludeThreads)
                {
                    await foreach (
                        var th in Discord.GetGuildThreadsAsync(
                            GuildId,
                            includeArchived: true,
                            cancellationToken: jobToken
                        )
                    )
                    {
                        if (th.Kind != ChannelKind.GuildForum)
                            channels.Add(th);
                    }
                }

                var queued = 0;
                foreach (var ch in channels)
                {
                    if (cfg.MaxChannels is { } max && queued >= max)
                        break;
                    if (IsChannelExcluded(ch))
                    {
                        MarkChannelExcluded(ch.Id);
                        continue;
                    }
                    if (!InFullScanScope(ch))
                        continue;
                    queue.EnqueueChannelExport(ch.Id, forceFullScan: true);
                    queued++;
                }

                lock (console)
                    console.Output.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [full-scan] Queued {queued} channels/threads; waiting for drain..."
                    );

                // Wait for the enqueued exports to drain so the completion notification and the
                // media-flag reset below reflect the true end of the scan. The shared export
                // counters can be nudged up by concurrent live re-exports, which only extends the
                // wait -- harmless.
                while (
                    !jobToken.IsCancellationRequested
                    && (queue.PendingChannelsCount > 0 || queue.InFlightExportsCount > 0)
                )
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), jobToken);
                }

                lock (console)
                    console.Output.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [full-scan] Completed full-scan of {queued} channels/threads."
                    );
                if (_settings.Notifications.OnFullScanComplete && notifier.HasTargets)
                    await notifier.TryNotifyAsync(
                        "[watchguild] Full-scan complete",
                        $"Guild {GuildId}: scheduled full-scan finished ({queued} channels/threads re-scanned).",
                        jobToken
                    );
            }
            finally
            {
                store.SuppressMediaDownloads = false;
                _activeFullScanProfile = null;
            }
        }

        var scheduledJobs = new List<CronScheduler.Job>();
        if (_settings.Backup.Enabled)
            scheduledJobs.Add(
                new CronScheduler.Job("backup", _settings.Backup.Schedule, RunBackupJobAsync)
            );
        if (_settings.FullScan.Enabled)
            scheduledJobs.Add(
                new CronScheduler.Job("full-scan", _settings.FullScan.Schedule, RunFullScanJobAsync)
            );
        if (_settings.Vacuum.Enabled)
            scheduledJobs.Add(
                new CronScheduler.Job("vacuum", _settings.Vacuum.Schedule, RunVacuumJobAsync)
            );

        var scheduler = new CronScheduler(
            scheduledJobs,
            TimeZoneInfo.Local,
            msg =>
            {
                lock (console)
                    console.Output.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [scheduler] {msg}"
                    );
            }
        );
        var schedulerTask = scheduler.HasJobs
            ? Task.Run(() => scheduler.RunAsync(cts.Token), cts.Token)
            : Task.CompletedTask;

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
                            + $"gapBackfills={queue.PendingGapBackfillsCount}, "
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
            // Gap backfills are bounded channel exports and hold an export concurrency slot, so
            // they run on the same background pool as ExportChannelItem rather than blocking the
            // pump (which must stay free for the latency-critical live-message path).
            if (item is ExportChannelItem or GapBackfillItem)
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
                // Queue drained: commit any live writes the inline handlers batched since the last
                // flush, in one transaction. Background channel/gap exports self-flush, so this
                // only commits the inline hot-path writes. Durability tradeoff: on a hard kill an
                // uncommitted batch is lost, but each item's resume cursor commits in the same
                // transaction as its data, so catch-up/gap recording re-covers it on restart.
                if (_hasPendingInlineWrites)
                {
                    try
                    {
                        await store.FlushAsync(cts.Token);
                        _hasPendingInlineWrites = false;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Keep the flag set so the batch is retried on the next drain; a transient
                        // flush failure (e.g. lock contention) must not kill the pump loop.
                        lock (console)
                            console.Error.WriteLine(
                                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [flush-error] Failed to flush batched live writes: {ex.Message}"
                            );
                    }
                }

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

        // Commit any live writes still batched from the last inline drain before the store is
        // disposed, so a graceful shutdown doesn't drop them. Best-effort on CancellationToken.None
        // since cts is already cancelled here.
        if (_hasPendingInlineWrites)
        {
            try
            {
                await store.FlushAsync(CancellationToken.None);
                _hasPendingInlineWrites = false;
            }
            catch (Exception ex)
            {
                lock (console)
                    console.Error.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [flush-error] Failed to flush batched live writes on shutdown: {ex.Message}"
                    );
            }
        }

        try
        {
            await gatewayTask;
        }
        catch (OperationCanceledException) { }

        try
        {
            await schedulerTask;
        }
        catch (OperationCanceledException) { }

        // If a fatal close triggered shutdown, make sure its notification actually went out before
        // the process exits (the notify was started on CancellationToken.None so the cancel didn't
        // abort it, but we still have to wait for it here).
        if (_fatalCloseNotifyTask is not null)
        {
            try
            {
                await _fatalCloseNotifyTask;
            }
            catch { }
        }
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

        Snowflake? before = null;
        var enrichReactors = _settings.EnrichReactors;

        // A scheduled full-scan sets _activeFullScanProfile for the duration of its run and applies
        // its own window + reactor policy -- but only to force-full-scan exports. Live/debounced
        // exports (forceFullScan == false) always keep the watcher's defaults.
        if (forceFullScan && _activeFullScanProfile is { } profile)
        {
            after ??= profile.After;
            before = profile.Before;
            enrichReactors = profile.EnrichReactors;
        }

        return new ExportRequest(
            guild,
            channel,
            OutputPath,
            null,
            ExportFormat.Db,
            after,
            before,
            PartitionLimit.Null,
            MessageFilter.Null,
            false,
            true,
            false,
            false,
            null,
            false,
            forceFullScan: forceFullScan,
            enrichReactors: enrichReactors
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
            _hasPendingInlineWrites = true;
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
            _hasPendingInlineWrites = true;

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
            _hasPendingInlineWrites = true;
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
            _hasPendingInlineWrites = true;
            if (wasMarked)
                lock (console)
                    console.Output.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Marked channel {channelDelete.ChannelId} deleted."
                    );
        }
        else if (item is ThreadMembersUpdateItem threadUpdate)
        {
            // thread_member.channel_id foreign-keys the channel row, so make sure the thread's
            // channel (and its guild) exist before inserting members. Without this, a
            // THREAD_MEMBERS_UPDATE that arrives before the thread has been captured (a fresh DB,
            // or a thread not yet reached by catch-up) fails the FK and the join/leave is lost.
            await EnsureChannelExistsAsync(threadUpdate.ChannelId, store, cancellationToken);
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
            _hasPendingInlineWrites = true;
        }
        else if (item is SyncGuildItem sync)
        {
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Syncing guild catalog ({sync.Reason})..."
                );
            // Snapshot the exclusion set (the background catch-up task mutates it) so SyncGuildAsync
            // leaves excluded channels untouched -- neither upserting them nor marking them deleted.
            HashSet<Snowflake> excludedSnapshot;
            lock (_excludedLock)
                excludedSnapshot = new HashSet<Snowflake>(_excludedChannelIds);
            var (roleCount, emojiCount, stickerCount, scheduledEventCount) =
                await SyncGuildCommand.SyncGuildAsync(
                    Discord,
                    store,
                    GuildId,
                    excludedSnapshot,
                    cancellationToken
                );
            // Roles may have changed (create/update/delete all funnel through here) -- drop the
            // cached catalog so the next live message resolves author roles against fresh data.
            _guildRoles = null;
            _hasPendingInlineWrites = true;
            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Synced guild catalog: {roleCount} roles, {emojiCount} emojis, {stickerCount} stickers, {scheduledEventCount} events."
                );
        }
        else if (item is ExportChannelItem export)
        {
            // Backstop for excluded channels that reached the queue via an id-based lifecycle
            // event (which the channel_id dispatch guard doesn't cover).
            if (IsExcludedChannelId(export.ChannelId))
                return;

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
        else if (item is GapBackfillItem gap)
        {
            if (IsExcludedChannelId(gap.ChannelId))
            {
                // Channel got excluded after this gap was queued (e.g. an earlier overlapping gap's
                // fill found it forbidden and marked it excluded). Resolve the gap rather than
                // leaving it unfilled forever -- an excluded channel isn't being backed up, so its
                // backlog is moot; if access is later restored, a fresh gap is recorded from the
                // unchanged cursor.
                await store.MarkGapFilledAsync(gap.GapId, cancellationToken);
                await store.FlushAsync(cancellationToken);
                return;
            }

            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Backfilling gap in channel {gap.ChannelId} ({gap.AfterMessageId} .. {gap.BeforeMessageId})..."
                );

            // The channel may have been deleted since the gap was recorded -- resolve it so a
            // stale gap for a gone channel doesn't linger forever.
            var channel = await Discord.TryGetChannelAsync(gap.ChannelId, cancellationToken);
            if (channel is null)
            {
                // Forbidden or deleted (transient/rate-limit failures are already retried inside
                // the client, so a null here is effectively permanent for this session). Exclude
                // it so the next reconnect's gap recording skips it instead of re-probing every
                // time; a process restart re-evaluates it once, in case access was since granted.
                MarkChannelExcluded(gap.ChannelId);
                await store.MarkGapFilledAsync(gap.GapId, cancellationToken);
                await store.FlushAsync(cancellationToken);
                lock (console)
                    console.Output.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Gap channel {gap.ChannelId} is unreadable (forbidden/deleted) -- marked filled and excluded for this session."
                    );
                return;
            }

            var guild = await Discord.GetGuildAsync(channel.GuildId, cancellationToken);

            // Snapshot the channel's export window so the bounded backfill doesn't leave its
            // narrow last_export_after/before behind and trick a later debounced re-export into a
            // full rescan. The message cursor (last_message_id) is never regressed by the export
            // -- maxMessageId starts at the stored cursor and only advances.
            var state = await store.GetChannelStateAsync(gap.ChannelId, cancellationToken);

            // A gap's range is (after, before] -- inclusive of before_message_id, which is the
            // channel's last message at record time (the newest downtime message, exactly what must
            // be captured). Discord's `before` query param is EXCLUSIVE, so fetch with before+1 to
            // include that boundary message; otherwise it is silently dropped every time.
            var beforeInclusive = new Snowflake(gap.BeforeMessageId.Value + 1);

            var req = new ExportRequest(
                guild,
                channel,
                OutputPath,
                null,
                ExportFormat.Db,
                gap.AfterMessageId,
                beforeInclusive,
                PartitionLimit.Null,
                MessageFilter.Null,
                false,
                true,
                false,
                false,
                null,
                false,
                enrichReactors: _settings.EnrichReactors
            );
            var exporter = new ChannelExporter(Discord);
            try
            {
                await exporter.ExportChannelAsync(req, store, null, cancellationToken);
            }
            catch (ChannelEmptyException)
            {
                // The range turned out empty (e.g. every message in it was deleted on Discord).
                // Nothing to backfill -- still count the gap resolved.
            }

            await store.SetChannelExportWindowAsync(
                gap.ChannelId,
                state?.LastExportAfter,
                state?.LastExportBefore,
                cancellationToken
            );
            // Advance the cursor to the range's upper bound even when the fetch came back empty.
            // Some channels report a last_message_id pointing at a deleted/unretrievable message,
            // so (after, before] is perpetually empty and the cursor -- left at maxMessageId --
            // would never reach `before`, re-recording the identical gap on every reconnect.
            // Bumping to `before` (monotonic; AdvanceChannelCursorAsync never regresses) closes the
            // gap for good and matches Discord's own notion of the channel's tail.
            await store.AdvanceChannelCursorAsync(
                gap.ChannelId,
                gap.BeforeMessageId,
                cancellationToken
            );
            await store.MarkGapFilledAsync(gap.GapId, cancellationToken);
            await store.FlushAsync(cancellationToken);

            lock (console)
                console.Output.WriteLine(
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [pump] Filled gap in '{channel.Name}' ({gap.AfterMessageId} .. {gap.BeforeMessageId})."
                );
        }
    }
}
