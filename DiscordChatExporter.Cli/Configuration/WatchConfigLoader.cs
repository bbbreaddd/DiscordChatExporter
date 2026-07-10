using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CliFx;
using Cronos;
using DiscordChatExporter.Core.Discord;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DiscordChatExporter.Cli.Configuration;

// Loads the watcher YAML into a WatchConfig. Uses YamlDotNet's representation-model DOM (concrete
// node types, no reflection) rather than object deserialization, so it stays trim-safe under the
// CLI's PublishTrimmed build, and so every value can be validated with a precise, line-of-sight
// error message (the config is meant to be hand-written, so bad keys/types should fail loudly).
public static class WatchConfigLoader
{
    public static WatchConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new CommandException($"Config file '{path}' does not exist.");

        var yaml = new YamlStream();
        try
        {
            using var reader = new StreamReader(path);
            yaml.Load(reader);
        }
        catch (Exception ex)
        {
            throw new CommandException($"Failed to parse YAML config '{path}': {ex.Message}");
        }

        if (yaml.Documents.Count == 0)
            throw new CommandException($"Config file '{path}' is empty.");

        var root = AsMap(yaml.Documents[0].RootNode, "<root>");
        RequireKnownKeys(
            root,
            "<root>",
            ["token", "token-file", "respect-rate-limits", "notifications", "defaults", "servers"]
        );

        var defaults = GetMap(root, "defaults");
        if (defaults is not null)
            RequireKnownKeys(
                defaults,
                "defaults",
                ["media", "data", "behavior", "exclude", "backup", "full-scan"]
            );

        var serversSeq = GetSequence(root, "servers");
        if (serversSeq is null || serversSeq.Children.Count == 0)
            throw new CommandException("Config must define at least one entry under 'servers'.");

        var servers = new List<ServerConfig>();
        for (var i = 0; i < serversSeq.Children.Count; i++)
        {
            var srv = AsMap(serversSeq.Children[i], $"servers[{i}]");
            RequireKnownKeys(
                srv,
                $"servers[{i}]",
                [
                    "name",
                    "id",
                    "enabled",
                    "output",
                    "media",
                    "data",
                    "behavior",
                    "exclude",
                    "backup",
                    "full-scan",
                ]
            );
            servers.Add(ParseServer(srv, defaults, i));
        }

        // Cross-server conflicts.
        foreach (
            var dup in servers
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
        )
            throw new CommandException(
                $"Duplicate server name '{dup.First().Name}' -- server names must be unique "
                    + "(they're how --server selects one)."
            );

        foreach (var dup in servers.GroupBy(s => s.Id).Where(g => g.Count() > 1))
            throw new CommandException(
                $"Duplicate server id '{dup.Key}' -- it appears on {dup.Count()} server entries."
            );

        foreach (
            var dup in servers
                .GroupBy(s => s.Output, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
        )
            throw new CommandException(
                $"Multiple servers write to the same output '{dup.Key}' -- each server needs its "
                    + "own database file."
            );

        return new WatchConfig
        {
            Token = GetScalar(root, "token"),
            TokenFile = GetScalar(root, "token-file"),
            RespectRateLimits = GetBool(root, "respect-rate-limits") ?? true,
            Notifications = ParseNotifications(GetMap(root, "notifications")),
            Servers = servers,
        };
    }

    private static ServerConfig ParseServer(YamlMappingNode srv, YamlMappingNode? defaults, int i)
    {
        var name =
            GetScalar(srv, "name")
            ?? throw new CommandException($"servers[{i}] is missing required key 'name'.");

        var idText =
            GetScalar(srv, "id")
            ?? throw new CommandException($"server '{name}' is missing required key 'id'.");
        var id =
            Snowflake.TryParse(idText)
            ?? throw new CommandException($"server '{name}' has an invalid id '{idText}'.");

        var output =
            GetScalar(srv, "output")
            ?? throw new CommandException($"server '{name}' is missing required key 'output'.");
        output = Substitute(output, name, id);

        var media = ParseMedia(GetMap(defaults, "media"), GetMap(srv, "media"), name, id);
        var data = ParseData(GetMap(defaults, "data"), GetMap(srv, "data"));
        var behavior = ParseBehavior(GetMap(defaults, "behavior"), GetMap(srv, "behavior"));
        var exclude = ParseExclude(GetMap(defaults, "exclude"), GetMap(srv, "exclude"));
        var backup = ParseBackup(GetMap(defaults, "backup"), GetMap(srv, "backup"), name, id);
        var fullScan = ParseFullScan(GetMap(defaults, "full-scan"), GetMap(srv, "full-scan"), name);

        // Hard conflict: fetching reactor lists only makes sense if reactions are stored at all.
        if (data.Reactors && !data.Reactions)
            throw new CommandException(
                $"server '{name}': data.reactors requires data.reactions -- reactor lists can't be "
                    + "stored while reactions are disabled."
            );

        return new ServerConfig
        {
            Name = name,
            Id = id,
            Enabled = GetBool(srv, "enabled") ?? true,
            Output = output,
            Media = media,
            Data = data,
            Behavior = behavior,
            Exclude = exclude,
            Backup = backup,
            FullScan = fullScan,
        };
    }

    // Non-fatal contradictions worth surfacing at startup: the config loads fine, but a setting is
    // being silently overridden or does nothing. Reported per selected server by WatchCommand.
    public static IReadOnlyList<string> CollectWarnings(ServerConfig server)
    {
        var warnings = new List<string>();

        if (server.Media.Enabled && server.Media.Assets.Count == 0)
            warnings.Add("media is enabled but 'assets' is empty -- no media will be downloaded.");

        if (server.Data.Threads && server.Exclude.Threads)
            warnings.Add(
                "data.threads is on but exclude.threads is also on -- no threads will be captured."
            );

        if (!server.Behavior.CatchUp && !server.Behavior.ScanMissing)
            warnings.Add(
                "neither catch-up nor scan-missing is enabled -- only messages arriving live will "
                    + "be captured (no backfill of history or gaps)."
            );

        return warnings;
    }

    private static MediaConfig ParseMedia(
        YamlMappingNode? def,
        YamlMappingNode? srv,
        string name,
        Snowflake id
    )
    {
        RequireKnownKeys(def, "defaults.media", ["enabled", "dir", "assets"]);
        RequireKnownKeys(srv, "media", ["enabled", "dir", "assets"]);

        var assets = GetStringList(srv, "assets") ?? GetStringList(def, "assets");
        if (assets is not null)
        {
            foreach (var asset in assets)
            {
                if (!MediaConfig.AllAssets.Contains(asset))
                    throw new CommandException(
                        $"Unknown media asset kind '{asset}'. Valid kinds: {string.Join(", ", MediaConfig.AllAssets)}."
                    );
            }
        }

        var dir = GetScalar(srv, "dir") ?? GetScalar(def, "dir");

        return new MediaConfig
        {
            Enabled = GetBool(srv, "enabled") ?? GetBool(def, "enabled") ?? true,
            Dir = dir is not null ? Substitute(dir, name, id) : null,
            Assets = assets ?? MediaConfig.AllAssets,
        };
    }

    private static DataConfig ParseData(YamlMappingNode? def, YamlMappingNode? srv)
    {
        string[] allowed =
        [
            "reactions",
            "reactors",
            "embeds",
            "stickers",
            "polls",
            "threads",
            "pins",
            "members",
            "guild-catalog",
        ];
        RequireKnownKeys(def, "defaults.data", allowed);
        RequireKnownKeys(srv, "data", allowed);

        bool Pick(string key, bool fallback) => GetBool(srv, key) ?? GetBool(def, key) ?? fallback;

        return new DataConfig
        {
            Reactions = Pick("reactions", true),
            Reactors = Pick("reactors", false),
            Embeds = Pick("embeds", true),
            Stickers = Pick("stickers", true),
            Polls = Pick("polls", true),
            Threads = Pick("threads", true),
            Pins = Pick("pins", true),
            Members = Pick("members", true),
            GuildCatalog = Pick("guild-catalog", true),
        };
    }

    private static BehaviorConfig ParseBehavior(YamlMappingNode? def, YamlMappingNode? srv)
    {
        string[] allowed = ["catch-up", "scan-missing", "catch-up-parallel", "retry-failed"];
        RequireKnownKeys(def, "defaults.behavior", allowed);
        RequireKnownKeys(srv, "behavior", allowed);

        var parallel = GetInt(srv, "catch-up-parallel") ?? GetInt(def, "catch-up-parallel") ?? 10;
        if (parallel < 1)
            throw new CommandException("'catch-up-parallel' must be at least 1.");

        return new BehaviorConfig
        {
            CatchUp = GetBool(srv, "catch-up") ?? GetBool(def, "catch-up") ?? true,
            ScanMissing = GetBool(srv, "scan-missing") ?? GetBool(def, "scan-missing") ?? false,
            CatchUpParallel = parallel,
            RetryFailed = GetBool(srv, "retry-failed") ?? GetBool(def, "retry-failed") ?? false,
        };
    }

    private static ExcludeConfig ParseExclude(YamlMappingNode? def, YamlMappingNode? srv)
    {
        string[] allowed = ["channels", "categories", "nsfw", "threads"];
        RequireKnownKeys(def, "defaults.exclude", allowed);
        RequireKnownKeys(srv, "exclude", allowed);

        // Lists replace (rather than concatenate) when overridden per server -- predictable, and a
        // server that wants "no exclusions" can set an empty list to clear an inherited default.
        var channels = Find(srv, "channels") is not null
            ? GetSnowflakeList(srv, "channels")
            : GetSnowflakeList(def, "channels");
        var categories = Find(srv, "categories") is not null
            ? GetSnowflakeList(srv, "categories")
            : GetSnowflakeList(def, "categories");

        return new ExcludeConfig
        {
            Channels = channels,
            Categories = categories,
            Nsfw = GetBool(srv, "nsfw") ?? GetBool(def, "nsfw") ?? false,
            Threads = GetBool(srv, "threads") ?? GetBool(def, "threads") ?? false,
        };
    }

    private static BackupConfig ParseBackup(
        YamlMappingNode? def,
        YamlMappingNode? srv,
        string name,
        Snowflake id
    )
    {
        string[] allowed =
        [
            "enabled",
            "schedule",
            "dir",
            "keep-daily",
            "keep-weekly",
            "compress",
            "integrity-check",
        ];
        RequireKnownKeys(def, "defaults.backup", allowed);
        RequireKnownKeys(srv, "backup", allowed);

        var enabled = GetBool(srv, "enabled") ?? GetBool(def, "enabled") ?? false;

        var schedule = GetScalar(srv, "schedule") ?? GetScalar(def, "schedule") ?? "30 5 * * *";
        ValidateCron(schedule, $"server '{name}' backup.schedule");

        var dir = GetScalar(srv, "dir") ?? GetScalar(def, "dir");
        if (enabled && string.IsNullOrWhiteSpace(dir))
            throw new CommandException(
                $"server '{name}': backup.dir is required when backup is enabled."
            );

        var keepDaily = GetInt(srv, "keep-daily") ?? GetInt(def, "keep-daily") ?? 7;
        var keepWeekly = GetInt(srv, "keep-weekly") ?? GetInt(def, "keep-weekly") ?? 4;
        if (keepDaily < 0 || keepWeekly < 0)
            throw new CommandException(
                $"server '{name}': backup.keep-daily / keep-weekly must be >= 0."
            );

        return new BackupConfig
        {
            Enabled = enabled,
            Schedule = schedule,
            Dir = dir is not null ? Substitute(dir, name, id) : null,
            KeepDaily = keepDaily,
            KeepWeekly = keepWeekly,
            Compress = GetBool(srv, "compress") ?? GetBool(def, "compress") ?? false,
            IntegrityCheck =
                GetBool(srv, "integrity-check") ?? GetBool(def, "integrity-check") ?? true,
        };
    }

    private static FullScanConfig ParseFullScan(
        YamlMappingNode? def,
        YamlMappingNode? srv,
        string name
    )
    {
        string[] allowed =
        [
            "enabled",
            "schedule",
            "channels",
            "categories",
            "exclude-channels",
            "exclude-categories",
            "include-threads",
            "after",
            "before",
            "max-channels",
            "enrich-reactors",
            "media",
        ];
        RequireKnownKeys(def, "defaults.full-scan", allowed);
        RequireKnownKeys(srv, "full-scan", allowed);

        var schedule = GetScalar(srv, "schedule") ?? GetScalar(def, "schedule") ?? "0 4 * * 0";
        ValidateCron(schedule, $"server '{name}' full-scan.schedule");

        var after = GetScalar(srv, "after") ?? GetScalar(def, "after");
        var before = GetScalar(srv, "before") ?? GetScalar(def, "before");
        ValidateWindow(after, $"server '{name}' full-scan.after");
        ValidateWindow(before, $"server '{name}' full-scan.before");

        var maxChannels = GetInt(srv, "max-channels") ?? GetInt(def, "max-channels");
        if (maxChannels is < 1)
            throw new CommandException(
                $"server '{name}': full-scan.max-channels must be at least 1."
            );

        // Lists replace (rather than concatenate) when overridden per server -- matches ParseExclude.
        IReadOnlyList<Snowflake> ListOf(string key) =>
            Find(srv, key) is not null ? GetSnowflakeList(srv, key) : GetSnowflakeList(def, key);

        return new FullScanConfig
        {
            Enabled = GetBool(srv, "enabled") ?? GetBool(def, "enabled") ?? false,
            Schedule = schedule,
            Channels = ListOf("channels"),
            Categories = ListOf("categories"),
            ExcludeChannels = ListOf("exclude-channels"),
            ExcludeCategories = ListOf("exclude-categories"),
            IncludeThreads =
                GetBool(srv, "include-threads") ?? GetBool(def, "include-threads") ?? true,
            After = after,
            Before = before,
            MaxChannels = maxChannels,
            EnrichReactors =
                GetBool(srv, "enrich-reactors") ?? GetBool(def, "enrich-reactors") ?? true,
            Media = GetBool(srv, "media") ?? GetBool(def, "media") ?? true,
        };
    }

    private static NotificationConfig ParseNotifications(YamlMappingNode? map)
    {
        RequireKnownKeys(map, "notifications", ["enabled", "urls", "on"]);

        var on = GetMap(map, "on");
        RequireKnownKeys(
            on,
            "notifications.on",
            [
                "fatal-close",
                "connection-down",
                "backup-failure",
                "backup-success",
                "full-scan-complete",
            ]
        );

        var enabled = GetBool(map, "enabled") ?? false;
        var urls = GetStringList(map, "urls") ?? [];
        if (enabled && urls.Count == 0)
            throw new CommandException(
                "notifications are enabled but 'urls' is empty -- add at least one Apprise URL."
            );

        return new NotificationConfig
        {
            Enabled = enabled,
            Urls = urls,
            OnFatalClose = GetBool(on, "fatal-close") ?? true,
            OnConnectionDown = GetBool(on, "connection-down") ?? true,
            OnBackupFailure = GetBool(on, "backup-failure") ?? true,
            OnBackupSuccess = GetBool(on, "backup-success") ?? false,
            OnFullScanComplete = GetBool(on, "full-scan-complete") ?? true,
        };
    }

    private static void ValidateCron(string schedule, string ctx)
    {
        try
        {
            CronExpression.Parse(schedule);
        }
        catch (Exception ex)
        {
            throw new CommandException(
                $"{ctx}: invalid cron expression '{schedule}' ({ex.Message})."
            );
        }
    }

    private static void ValidateWindow(string? value, string ctx)
    {
        if (value is null)
            return;
        try
        {
            TimeWindow.Resolve(value, DateTimeOffset.UtcNow);
        }
        catch (FormatException ex)
        {
            throw new CommandException($"{ctx}: {ex.Message}");
        }
    }

    // ---- DOM helpers ----

    private static YamlMappingNode AsMap(YamlNode node, string ctx) =>
        node as YamlMappingNode
        ?? throw new CommandException($"'{ctx}' must be a mapping (key: value block).");

    private static YamlNode? Find(YamlMappingNode? map, string key)
    {
        if (map is null)
            return null;
        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode s && s.Value == key)
                return entry.Value;
        }
        return null;
    }

    private static string? GetScalar(YamlMappingNode? map, string key)
    {
        if (Find(map, key) is not YamlScalarNode s)
            return null;

        // Treat an explicit YAML null (`key: null`, `key: ~`, or `key:` with no value) the same as
        // an absent key. Otherwise YamlDotNet hands back the literal text "null", which downstream
        // parsers reject -- e.g. full-scan after/before (TimeWindow) and max-channels (GetInt) all
        // ship `null` as their documented "unset" default. Only plain (unquoted) scalars count, so
        // a deliberately quoted "null" is still preserved as the string.
        if (
            s.Style == ScalarStyle.Plain
            && s.Value is null or "" or "~" or "null" or "Null" or "NULL"
        )
            return null;

        return s.Value;
    }

    private static bool? GetBool(YamlMappingNode? map, string key)
    {
        var s = GetScalar(map, key);
        if (s is null)
            return null;

        return s.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new CommandException(
                $"Key '{key}' must be a boolean (true/false), got '{s}'."
            ),
        };
    }

    private static int? GetInt(YamlMappingNode? map, string key)
    {
        var s = GetScalar(map, key);
        if (s is null)
            return null;

        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new CommandException($"Key '{key}' must be an integer, got '{s}'.");
    }

    private static YamlMappingNode? GetMap(YamlMappingNode? map, string key) =>
        Find(map, key) as YamlMappingNode;

    private static YamlSequenceNode? GetSequence(YamlMappingNode? map, string key) =>
        Find(map, key) as YamlSequenceNode;

    private static IReadOnlyList<string>? GetStringList(YamlMappingNode? map, string key)
    {
        var seq = GetSequence(map, key);
        if (seq is null)
            return null;

        return seq
            .Children.OfType<YamlScalarNode>()
            .Select(n => n.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToArray();
    }

    private static IReadOnlyList<Snowflake> GetSnowflakeList(YamlMappingNode? map, string key)
    {
        var list = GetStringList(map, key);
        if (list is null)
            return [];

        return list.Select(v =>
                Snowflake.TryParse(v)
                ?? throw new CommandException($"Key '{key}' contains an invalid id '{v}'.")
            )
            .ToArray();
    }

    private static void RequireKnownKeys(YamlMappingNode? map, string ctx, string[] allowed)
    {
        if (map is null)
            return;

        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode s && !allowed.Contains(s.Value))
                throw new CommandException(
                    $"Unknown key '{s.Value}' in '{ctx}'. Allowed keys: {string.Join(", ", allowed)}."
                );
        }
    }

    private static string Substitute(string value, string name, Snowflake id) =>
        value.Replace("%SERVER%", name).Replace("%NAME%", name).Replace("%ID%", id.ToString());
}
