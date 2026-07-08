using System.Collections.Generic;
using DiscordChatExporter.Cli.Configuration;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Cli.Commands.Shared;

// The tunable behavior of a single guild watch, decoupled from where it came from: WatchGuildCommand
// builds one from its CLI options, WatchCommand builds one from a server entry in the YAML config.
// GuildId / OutputPath / token stay on the command itself; this carries everything else. Defaults
// reproduce the historical watchguild behavior (all data captured, nothing excluded).
public record WatchSettings
{
    // Media
    public bool DownloadMedia { get; init; }
    public string? MediaDir { get; init; }
    public IReadOnlyList<string> MediaAssets { get; init; } = MediaConfig.AllAssets;
    public bool RetryFailedMedia { get; init; }

    // Behavior / tuning
    public bool CatchUp { get; init; }
    public bool ScanMissing { get; init; }
    public int CatchUpParallel { get; init; } = 10;
    public bool EnrichReactors { get; init; }

    // Data-type toggles
    public bool CaptureReactions { get; init; } = true;
    public bool CaptureEmbeds { get; init; } = true;
    public bool CaptureStickers { get; init; } = true;
    public bool CapturePolls { get; init; } = true;
    public bool CaptureThreads { get; init; } = true;
    public bool CapturePins { get; init; } = true;
    public bool CaptureMembers { get; init; } = true;
    public bool SyncGuildCatalog { get; init; } = true;

    // Exclusions
    public IReadOnlySet<Snowflake> ExcludeChannels { get; init; } = new HashSet<Snowflake>();
    public IReadOnlySet<Snowflake> ExcludeCategories { get; init; } = new HashSet<Snowflake>();
    public bool ExcludeNsfw { get; init; }
    public bool ExcludeThreads { get; init; }
}
