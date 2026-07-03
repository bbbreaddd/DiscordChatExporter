using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Database;

namespace DiscordChatExporter.Cli.Commands;

[Command(
    "syncguild",
    Description = "Refreshes a guild's role/emoji/sticker/scheduled-event catalog in a "
        + "consolidated SQLite database, without touching any channels or messages. Cheap and "
        + "fast compared to a channel export -- meant to be called whenever one of those "
        + "catalogs changes (e.g. in response to the corresponding gateway dispatch) rather than "
        + "waiting for unrelated channel activity to trigger a refresh."
)]
public partial class SyncGuildCommand : DiscordCommandBase
{
    [CommandOption("guild", 'g', Description = "Server ID.")]
    public required Snowflake GuildId { get; set; }

    [CommandOption("output", 'o', Description = "Path to the SQLite database file.")]
    public required string OutputPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = Path.GetFullPath(value);
    }

    [CommandOption(
        "format",
        'f',
        Description = "Kept for parity with other database-targeting commands. Only 'Db' is supported."
    )]
    public ExportFormat ExportFormat { get; set; } = ExportFormat.Db;

    [CommandOption("media", Description = "Download Discord guild/catalog media while syncing.")]
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
        "retry-failed",
        Description = "Retry media URLs previously recorded as permanently gone (404/410), "
            + "instead of skipping them. By default such URLs are only attempted once."
    )]
    public bool RetryFailedMedia { get; set; }

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        await base.ExecuteAsync(console);

        if (ExportFormat != ExportFormat.Db)
            throw new CommandException("Option --format only supports 'Db' for syncguild.");

        if (!string.IsNullOrWhiteSpace(AssetsDirPath) && !ShouldDownloadAssets)
            throw new CommandException("Option --media-dir cannot be used without --media.");

        if (!File.Exists(OutputPath))
        {
            throw new CommandException(
                $"Database file '{OutputPath}' does not exist. "
                    + "syncguild expects a database already created by a prior "
                    + "'export --format Db' or 'todatabase' run -- check the --output path."
            );
        }

        var cancellationToken = console.RegisterCancellationHandlerWithSignals();

        var guild = await Discord.GetGuildAsync(GuildId, cancellationToken);

        await using var store = await SqliteExportStore.OpenAsync(
            OutputPath,
            ShouldDownloadAssets ? AssetsDirPath ?? $"{OutputPath}_Files" : null,
            RetryFailedMedia,
            cancellationToken
        );

        var (roleCount, emojiCount, stickerCount, scheduledEventCount) = await SyncGuildAsync(
            Discord,
            store,
            GuildId,
            cancellationToken
        );

        await console.Output.WriteLineAsync(
            $"Synced guild '{guild.Name}' (#{guild.Id}): "
                + $"{roleCount} role(s), {emojiCount} emoji, {stickerCount} sticker(s), "
                + $"{scheduledEventCount} scheduled event(s)."
        );
    }

    public static async ValueTask<(
        int roleCount,
        int emojiCount,
        int stickerCount,
        int scheduledEventCount
    )> SyncGuildAsync(
        DiscordClient discord,
        SqliteExportStore store,
        Snowflake guildId,
        CancellationToken cancellationToken = default
    )
    {
        var guild = await discord.GetGuildAsync(guildId, cancellationToken);
        await store.UpsertGuildAsync(guild, cancellationToken);

        var roleCount = 0;
        await foreach (var role in discord.GetGuildRolesAsync(guildId, cancellationToken))
        {
            await store.UpsertRoleAsync(role, guildId, cancellationToken);
            roleCount++;
        }

        var emojiCount = 0;
        await foreach (var emoji in discord.GetGuildEmojisAsync(guildId, cancellationToken))
        {
            await store.UpsertGuildEmojiAsync(emoji, guildId, cancellationToken);
            emojiCount++;
        }

        var stickerCount = 0;
        await foreach (var sticker in discord.GetGuildStickersAsync(guildId, cancellationToken))
        {
            await store.UpsertGuildStickerAsync(sticker, guildId, cancellationToken);
            stickerCount++;
        }

        var scheduledEventCount = 0;
        await foreach (
            var scheduledEvent in discord.GetGuildScheduledEventsAsync(guildId, cancellationToken)
        )
        {
            await store.UpsertScheduledEventAsync(scheduledEvent, guildId, cancellationToken);
            scheduledEventCount++;
        }

        // Categories are filtered out everywhere channels are exported (they have no messages
        // of their own), so this is the only place their own id/name/position ever gets
        // persisted -- otherwise a category's relative order to its sibling categories is lost,
        // even though each channel's order *within* its category is stored on the channel row.
        await foreach (var channel in discord.GetGuildChannelsAsync(guildId, cancellationToken))
        {
            if (channel.IsCategory)
                await store.UpsertChannelAsync(channel, cancellationToken);
        }

        await store.FlushAsync(cancellationToken);
        return (roleCount, emojiCount, stickerCount, scheduledEventCount);
    }
}
