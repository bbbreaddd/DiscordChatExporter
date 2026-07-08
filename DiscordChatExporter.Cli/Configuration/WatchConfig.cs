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
