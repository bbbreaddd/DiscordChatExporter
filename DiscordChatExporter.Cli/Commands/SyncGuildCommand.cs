using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
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

        // The standalone syncguild command has no exclusion config, so it reconciles every channel.
        var (roleCount, emojiCount, stickerCount, scheduledEventCount) = await SyncGuildAsync(
            Discord,
            store,
            GuildId,
            excludedChannelIds: null,
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
        IReadOnlySet<Snowflake>? excludedChannelIds = null,
        CancellationToken cancellationToken = default
    )
    {
        var guild = await discord.GetGuildAsync(guildId, cancellationToken);

        // Onboarding lives at its own endpoint (not part of the guild object), and the bot may
        // lack MANAGE_GUILD to read it -- TryGetGuildOnboardingJsonAsync tolerates that and
        // returns null, which UpsertGuildAsync's COALESCE then treats as "keep whatever we last
        // knew" rather than clobbering a previously-fetched value.
        var onboardingJson = await discord.TryGetGuildOnboardingJsonAsync(
            guildId,
            cancellationToken
        );
        await store.UpsertGuildAsync(
            guild with
            {
                OnboardingJson = onboardingJson,
            },
            cancellationToken
        );

        // Roles/emoji/stickers have no granular delete event worth relying on alone (emoji and
        // stickers have none at all -- Discord only ever sends the full current list -- and even
        // though Discord does send GUILD_ROLE_DELETE, it's simplest to fold it into the same
        // resync as CREATE/UPDATE rather than parse it out separately). So every sync diffs the
        // live list against what the DB still considers active and soft-deletes anything no
        // longer present, in addition to upserting what's still there.
        var deletedAt = DateTimeOffset.UtcNow;

        var activeRoleIds = await store.GetActiveRoleIdsAsync(guildId, cancellationToken);
        var roleCount = 0;
        await foreach (var role in discord.GetGuildRolesAsync(guildId, cancellationToken))
        {
            await store.UpsertRoleAsync(role, guildId, cancellationToken);
            activeRoleIds.Remove(role.Id);
            roleCount++;
        }
        foreach (var removedId in activeRoleIds)
            await store.MarkRoleDeletedAsync(removedId, deletedAt, cancellationToken);

        var activeEmojiIds = await store.GetActiveGuildEmojiIdsAsync(guildId, cancellationToken);
        var emojiCount = 0;
        await foreach (var emoji in discord.GetGuildEmojisAsync(guildId, cancellationToken))
        {
            await store.UpsertGuildEmojiAsync(emoji, guildId, cancellationToken);
            activeEmojiIds.Remove(emoji.Id);
            emojiCount++;
        }
        foreach (var removedId in activeEmojiIds)
            await store.MarkGuildEmojiDeletedAsync(removedId, deletedAt, cancellationToken);

        var activeStickerIds = await store.GetActiveGuildStickerIdsAsync(
            guildId,
            cancellationToken
        );
        var stickerCount = 0;
        await foreach (var sticker in discord.GetGuildStickersAsync(guildId, cancellationToken))
        {
            await store.UpsertGuildStickerAsync(sticker, guildId, cancellationToken);
            activeStickerIds.Remove(sticker.Id);
            stickerCount++;
        }
        foreach (var removedId in activeStickerIds)
            await store.MarkGuildStickerDeletedAsync(removedId, deletedAt, cancellationToken);

        var scheduledEventCount = 0;
        await foreach (
            var scheduledEvent in discord.GetGuildScheduledEventsAsync(guildId, cancellationToken)
        )
        {
            await store.UpsertScheduledEventAsync(scheduledEvent, guildId, cancellationToken);
            scheduledEventCount++;
        }

        // Reconcile the full channel list: upsert every (non-excluded) channel the API returns, and
        // soft-delete any active non-thread channel it no longer returns. This refreshes channel
        // metadata (name/topic/nsfw/position/permission overwrites) that drifts while the watcher is
        // offline, and -- since there is no CHANNEL_CREATE gateway handler -- is the only way a
        // brand-new channel with no messages ever gets recorded before its first message arrives.
        // It also persists categories/forums, whose own id/name/position are stored nowhere else
        // (they're filtered out of every message-export path).
        //
        // Threads are deliberately out of scope: GetGuildChannelsAsync never returns them, so a
        // deletion diff based on it would sweep every thread up as "deleted", and a full thread
        // listing on every sync would be far too expensive. A thread deleted purely during downtime
        // relies on THREAD_DELETE having been live at the time, same as several other live-only
        // signals in this tool. Threads' metadata is refreshed by their own export path instead.
        var activeChannelIds = await store.GetActiveChannelIdsAsync(guildId, cancellationToken);
        await foreach (var channel in discord.GetGuildChannelsAsync(guildId, cancellationToken))
        {
            // An excluded channel is left entirely untouched: not upserted, and dropped from the
            // active set so the deletion diff below doesn't mistake it for a removed channel.
            if (excludedChannelIds?.Contains(channel.Id) == true)
            {
                activeChannelIds.Remove(channel.Id);
                continue;
            }

            await store.UpsertChannelAsync(channel, cancellationToken);
            activeChannelIds.Remove(channel.Id);
        }
        foreach (var removedId in activeChannelIds)
            await store.MarkChannelDeletedAsync(removedId, deletedAt, cancellationToken);

        await store.FlushAsync(cancellationToken);
        return (roleCount, emojiCount, stickerCount, scheduledEventCount);
    }
}
