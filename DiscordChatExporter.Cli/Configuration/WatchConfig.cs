using System.Collections.Generic;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Cli.Configuration;

// Strongly-typed model of the watcher YAML config. A `defaults:` block supplies values inherited by
// every server; each server may override any subset of them (merged field-by-field, see
// WatchConfigLoader). Everything has a sensible default so a minimal config -- a token and one
// server with an id + output -- just works.
public record WatchConfig
{
    public string? Token { get; init; }
    public string? TokenFile { get; init; }
    public bool RespectRateLimits { get; init; } = true;

    // Process-wide notification config (one bot process watches one server), so it lives at the top
    // level alongside the token rather than per-server.
    public NotificationConfig Notifications { get; init; } = new();
    public IReadOnlyList<ServerConfig> Servers { get; init; } = [];
}

public record ServerConfig
{
    public required string Name { get; init; }
    public required Snowflake Id { get; init; }
    public bool Enabled { get; init; } = true;
    public required string Output { get; init; }
    public MediaConfig Media { get; init; } = new();
    public DataConfig Data { get; init; } = new();
    public BehaviorConfig Behavior { get; init; } = new();
    public ExcludeConfig Exclude { get; init; } = new();
    public BackupConfig Backup { get; init; } = new();
    public FullScanConfig FullScan { get; init; } = new();
    public VacuumConfig Vacuum { get; init; } = new();
}

// Scheduled database VACUUM to reclaim free space. Runs in-process on the shared writer connection
// (see SqliteExportStore.VacuumAsync), so it serializes with live capture rather than racing it.
// Off by default: a full VACUUM briefly pauses live writes (they queue in memory) while it rewrites
// the file, and needs transient free disk roughly equal to the database size. Incremental mode is
// far lighter but only returns freelist pages and only when the DB is in auto_vacuum=INCREMENTAL.
public record VacuumConfig
{
    public bool Enabled { get; init; }

    // Standard 5-field cron, evaluated in the container timezone. Default weekly, Sunday 06:00 --
    // after the 05:30 backup, so the weekly copy captures the pre-vacuum state.
    public string Schedule { get; init; } = "0 6 * * 0";

    // false = full VACUUM (rewrite + defragment); true = PRAGMA incremental_vacuum (freelist only).
    public bool Incremental { get; init; }
}

// Scheduled online backup of this server's database. Runs on a cron schedule inside the watcher via a
// separate read connection (so it never blocks live capture); see DatabaseBackupService.
public record BackupConfig
{
    public bool Enabled { get; init; }

    // Standard 5-field cron (minute hour day month day-of-week), evaluated in the container timezone.
    // Default 05:30 daily -- deliberately not 04:00, to avoid overlapping a router/network reboot.
    public string Schedule { get; init; } = "30 5 * * *";
    public string? Dir { get; init; }
    public int KeepDaily { get; init; } = 7;
    public int KeepWeekly { get; init; } = 4;
    public bool Compress { get; init; }
    public bool IntegrityCheck { get; init; } = true;
}

// Scheduled periodic full re-scan (force-full-scan) of some/all channels, to reconcile changes catch-up
// can't see (edits/reactions on already-stored messages). Runs in-process through the shared store, so
// it serializes cleanly with live capture (no cross-process "database is locked").
public record FullScanConfig
{
    public bool Enabled { get; init; }
    public string Schedule { get; init; } = "0 4 * * 0";

    // Scope. Empty `Channels`/`Categories` means "everything in scope"; the exclude lists and the
    // server-level ExcludeConfig both still apply.
    public IReadOnlyList<Snowflake> Channels { get; init; } = [];
    public IReadOnlyList<Snowflake> Categories { get; init; } = [];
    public IReadOnlyList<Snowflake> ExcludeChannels { get; init; } = [];
    public IReadOnlyList<Snowflake> ExcludeCategories { get; init; } = [];
    public bool IncludeThreads { get; init; } = true;

    // Limits. After/Before are raw strings resolved at run time -- either an absolute date
    // (ISO-8601) or a relative duration like "30d"/"12h"/"4w" (subtracted from "now" each run).
    public string? After { get; init; }
    public string? Before { get; init; }
    public int? MaxChannels { get; init; }
    public bool EnrichReactors { get; init; } = true;

    // When false, the run skips media downloads (rows are still reconciled). Note this suppresses
    // media store-wide while the scan drains, live capture included -- see
    // SqliteExportStore.SuppressMediaDownloads. Full-scan exports run at the watcher's shared export
    // concurrency (`behavior.catch-up-parallel`); there is no separate per-scan parallelism knob.
    public bool Media { get; init; } = true;
}

// How the watcher notifies the operator, via the bundled Apprise CLI (see AppriseNotifier). `Urls` are
// Apprise service URLs (discord://, tgram://, ...). The On* flags gate which events fire a notification.
public record NotificationConfig
{
    public bool Enabled { get; init; }
    public IReadOnlyList<string> Urls { get; init; } = [];
    public bool OnFatalClose { get; init; } = true;
    public bool OnConnectionDown { get; init; } = true;
    public bool OnBackupFailure { get; init; } = true;
    public bool OnBackupSuccess { get; init; }
    public bool OnFullScanComplete { get; init; } = true;
}

public record MediaConfig
{
    // The full set of downloadable asset kinds, matching the ownerKind/assetKind strings used by
    // SqliteExportStore.TryDownloadMediaAsync. `assets:` selects a subset of these.
    public static readonly IReadOnlyList<string> AllAssets =
    [
        "attachments",
        "avatars",
        "user-banners",
        "emojis",
        "stickers",
        "guild-icons",
        "guild-banners",
        "role-icons",
    ];

    public bool Enabled { get; init; } = true;
    public string? Dir { get; init; }
    public IReadOnlyList<string> Assets { get; init; } = AllAssets;
}

public record DataConfig
{
    public bool Reactions { get; init; } = true;
    public bool Reactors { get; init; }
    public bool Embeds { get; init; } = true;
    public bool Stickers { get; init; } = true;
    public bool Polls { get; init; } = true;
    public bool Threads { get; init; } = true;
    public bool Pins { get; init; } = true;
    public bool Members { get; init; } = true;
    public bool GuildCatalog { get; init; } = true;
}

public record BehaviorConfig
{
    public bool CatchUp { get; init; } = true;
    public bool ScanMissing { get; init; }
    public int CatchUpParallel { get; init; } = 10;
    public bool RetryFailed { get; init; }
}

public record ExcludeConfig
{
    public IReadOnlyList<Snowflake> Channels { get; init; } = [];
    public IReadOnlyList<Snowflake> Categories { get; init; } = [];
    public bool Nsfw { get; init; }
    public bool Threads { get; init; }
}
