using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Database;

// Refreshes a single message's row (e.g. to reflect a new reaction, edit, or pin change) in a
// consolidated SQLite database. Unlike MessagePatcher (the JSON/byte-splice equivalent), there is
// no index/checkpoint/byte-range bookkeeping to worry about: every message is upserted by its
// (globally unique) id, so this works whether or not the channel has ever had a full export run
// against this database before.
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
        var message = await discord.TryGetMessageAsync(
            request.Channel.Id,
            messageId,
            cancellationToken
        );
        if (message is null)
            return new MessagePatchResult(false, "Message no longer exists (deleted).");

        await using var store = await SqliteExportStore.OpenAsync(
            databaseFilePath,
            cancellationToken
        );

        await store.UpsertGuildAsync(request.Guild, cancellationToken);
        await store.UpsertChannelAsync(request.Channel, cancellationToken);

        var context = new ExportContext(discord, request);
        await context.PopulateChannelsAndRolesAsync(cancellationToken);

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

        await store.UpsertMessageAsync(request.Channel.Id, message, cancellationToken);
        await store.FlushAsync(cancellationToken);

        return new MessagePatchResult(true, "Patched successfully.");
    }
}
