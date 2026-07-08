using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CliFx;
using DiscordChatExporter.Core.Discord;
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
            ["token", "token-file", "respect-rate-limits", "defaults", "servers"]
        );

        var defaults = GetMap(root, "defaults");
        if (defaults is not null)
            RequireKnownKeys(defaults, "defaults", ["media", "data", "behavior", "exclude"]);

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
                ["name", "id", "enabled", "output", "media", "data", "behavior", "exclude"]
            );
            servers.Add(ParseServer(srv, defaults, i));
        }

        return new WatchConfig
        {
            Token = GetScalar(root, "token"),
            TokenFile = GetScalar(root, "token-file"),
            RespectRateLimits = GetBool(root, "respect-rate-limits") ?? true,
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

        return new ServerConfig
        {
            Name = name,
            Id = id,
            Enabled = GetBool(srv, "enabled") ?? true,
            Output = output,
            Media = ParseMedia(GetMap(defaults, "media"), GetMap(srv, "media"), name, id),
            Data = ParseData(GetMap(defaults, "data"), GetMap(srv, "data")),
            Behavior = ParseBehavior(GetMap(defaults, "behavior"), GetMap(srv, "behavior")),
            Exclude = ParseExclude(GetMap(defaults, "exclude"), GetMap(srv, "exclude")),
        };
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

    private static string? GetScalar(YamlMappingNode? map, string key) =>
        Find(map, key) is YamlScalarNode s ? s.Value : null;

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
