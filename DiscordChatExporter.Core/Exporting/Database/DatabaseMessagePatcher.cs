using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Database;

// Refreshes a single message's row (e.g. to reflect a new reaction, edit, or pin change) in a
// consolidated SQLite database. Unlike MessagePatcher (the JSON/byte-splice equivalent), there is
// no index/checkpoint/byte-range bookkeeping to worry about: every message is upserted by its
// (globally unique) id, so this works whether or not the specific channel has ever had a full
// export run against this database before -- but the database file itself must already exist
// (see the File.Exists check below), so a typo'd or wrong --output path fails clearly instead of
// silently creating a brand new, near-empty database.
public static class DatabaseMessagePatcher
{
    public static async ValueTask<MessagePatchResult> PatchMessageAsync(
        ExportRequest request,
        DiscordClient discord,
        string databaseFilePath,
        Snowflake messageId,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(databaseFilePath))
        {
            return new MessagePatchResult(
                false,
                $"Database file '{databaseFilePath}' does not exist. "
                    + "patch-message expects a database already created by a prior "
                    + "'export --format Db' or 'todatabase' run -- check the --output path."
            );
        }

        await using var store = await SqliteExportStore.OpenAsync(
            databaseFilePath,
            request.ShouldDownloadAssets ? request.AssetsDirPath : null,
            cancellationToken
        );

        return await PatchMessageAsync(request, discord, store, messageId, cancellationToken);
    }

    public static async ValueTask<MessagePatchResult> PatchMessageAsync(
        ExportRequest request,
        DiscordClient discord,
        SqliteExportStore store,
        Snowflake messageId,
        CancellationToken cancellationToken = default
    )
    {
        var message = await discord.TryGetMessageAsync(
            request.Channel.Id,
            messageId,
            cancellationToken
        );
        if (message is null)
            return new MessagePatchResult(false, "Message no longer exists (deleted).");

        await store.UpsertGuildAsync(request.Guild, cancellationToken);
        await store.UpsertChannelAsync(request.Channel, cancellationToken);

        var context = new ExportContext(discord, request);
        await context.PopulateChannelsAndRolesAsync(cancellationToken);

        foreach (var role in context.Roles.Values)
            await store.UpsertRoleAsync(role, request.Guild.Id, cancellationToken);

        foreach (var user in message.GetReferencedUsers())
        {
            await context.PopulateMemberAsync(user, cancellationToken);
            var member = context.TryGetMember(user.Id);

            await store.UpsertUserAsync(
                user,
                member,
                context.GetUserRoles(user.Id),
                cancellationToken
            );
        }

        var enrichedMessage = await ChannelExporter.EnrichReactionsWithUsersAsync(
            discord,
            request.Channel.Id,
            message,
            cancellationToken
        );

        await store.UpsertMessageAsync(request.Channel.Id, enrichedMessage, cancellationToken);
        await store.FlushAsync(cancellationToken);

        return new MessagePatchResult(true, "Patched successfully.");
    }
}
