using System;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Cli.Commands.Shared;
using DiscordChatExporter.Cli.Configuration;

namespace DiscordChatExporter.Cli.Commands;

[Command(
    "watch",
    Description = "Watches a guild defined in a YAML config file, exporting live gateway events "
        + "(and optional catch-up) into a SQLite database. The config replaces the long list of "
        + "watchguild flags and adds per-data-type, media, and exclusion control. The config may "
        + "list several servers; one is watched per run (select with --server)."
)]
public partial class WatchCommand : DiscordCommandBase
{
    [CommandOption("config", 'c', Description = "Path to the watcher YAML config file.")]
    public required string ConfigPath { get; set; }

    [CommandOption(
        "server",
        's',
        Description = "Which server from the config to watch (by name or id). Required only when "
            + "more than one server is enabled."
    )]
    public string? ServerSelector { get; set; }

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var config = WatchConfigLoader.Load(ConfigPath);
        var server = SelectServer(config);

        foreach (var warning in WatchConfigLoader.CollectWarnings(server))
        {
            using (console.WithForegroundColor(ConsoleColor.Yellow))
                await console.Error.WriteLineAsync($"[watch] Warning ({server.Name}): {warning}");
        }

        var settings = new WatchSettings
        {
            DownloadMedia = server.Media.Enabled,
            MediaDir = server.Media.Dir,
            MediaAssets = server.Media.Assets,
            RetryFailedMedia = server.Behavior.RetryFailed,
            CatchUp = server.Behavior.CatchUp,
            ScanMissing = server.Behavior.ScanMissing,
            CatchUpParallel = server.Behavior.CatchUpParallel,
            EnrichReactors = server.Data.Reactors,
            CaptureReactions = server.Data.Reactions,
            CaptureEmbeds = server.Data.Embeds,
            CaptureStickers = server.Data.Stickers,
            CapturePolls = server.Data.Polls,
            CaptureThreads = server.Data.Threads,
            CapturePins = server.Data.Pins,
            CaptureMembers = server.Data.Members,
            SyncGuildCatalog = server.Data.GuildCatalog,
            ExcludeChannels = server.Exclude.Channels.ToHashSet(),
            ExcludeCategories = server.Exclude.Categories.ToHashSet(),
            ExcludeNsfw = server.Exclude.Nsfw,
            ExcludeThreads = server.Exclude.Threads,
            Backup = server.Backup,
            FullScan = server.FullScan,
            Vacuum = server.Vacuum,
            Notifications = config.Notifications,
        };

        // Delegate to the existing single-guild watcher: the token/output/guild come from the
        // config (falling back to this command's own --token/--token-file if the config omits
        // them), and SettingsOverride feeds it everything else so it never reads its own CLI flags.
        var watch = new WatchGuildCommand
        {
            GuildId = server.Id,
            OutputPath = server.Output,
            Token = config.Token ?? Token,
            TokenFile = config.TokenFile ?? TokenFile,
            ShouldRespectRateLimits = config.RespectRateLimits,
            SettingsOverride = settings,
        };

        await console.Output.WriteLineAsync(
            $"[watch] Watching '{server.Name}' ({server.Id}) into '{server.Output}'."
        );

        await watch.ExecuteAsync(console);
    }

    private ServerConfig SelectServer(WatchConfig config)
    {
        // Explicit --server wins and may target any listed server, enabled or not.
        if (!string.IsNullOrWhiteSpace(ServerSelector))
        {
            return config.Servers.FirstOrDefault(s =>
                    string.Equals(s.Name, ServerSelector, StringComparison.OrdinalIgnoreCase)
                    || s.Id.ToString() == ServerSelector
                )
                ?? throw new CommandException(
                    $"No server named or with id '{ServerSelector}' is defined in the config."
                );
        }

        var enabled = config.Servers.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0)
            throw new CommandException(
                "No servers are enabled in the config. Set 'enabled: true' on one, or pass --server."
            );

        if (enabled.Count > 1)
            throw new CommandException(
                $"{enabled.Count} servers are enabled, but only one can be watched per run "
                    + "(simultaneous multi-server watching is not supported yet). Pass "
                    + "--server <name|id> to pick one, or enable only a single server."
            );

        return enabled[0];
    }
}
