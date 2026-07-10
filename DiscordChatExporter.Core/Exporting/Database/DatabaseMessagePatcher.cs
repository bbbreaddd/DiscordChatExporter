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
        // --- Phase 1: all network I/O, before any write opens a transaction ---
        // Every write below runs inside the store's single shared WAL transaction, which is held
        // until FlushAsync. Doing the (slow, rate-limit-prone) network calls first keeps that
        // transaction -- and thus the database write lock -- from staying open across them, so a
        // concurrent writer process (e.g. a manual exportguild/fillgaps on the same file) doesn't
        // block on the lock and time out with SQLITE_BUSY.
        var message = await discord.TryGetMessageAsync(
            request.Channel.Id,
            messageId,
            cancellationToken
        );
        if (message is null)
            return new MessagePatchResult(false, "Message no longer exists (deleted).");

        var context = new ExportContext(discord, request);
        await context.PopulateChannelsAndRolesAsync(cancellationToken);

        foreach (var user in message.GetReferencedUsers())
            await context.PopulateMemberAsync(user, cancellationToken);

        var enrichedMessage = await ChannelExporter.EnrichReactionsWithUsersAsync(
            discord,
            request.Channel.Id,
            message,
            cancellationToken
        );

        // --- Phase 2: writes only (no network), then flush ---
        // Ordered to satisfy the foreign keys (foreign_keys = ON): guild -> channel -> role ->
        // user -> message. The member/role lookups below just read the context populated above.
        await store.UpsertGuildAsync(request.Guild, cancellationToken);
        await store.UpsertChannelAsync(request.Channel, cancellationToken);

        foreach (var role in context.Roles.Values)
            await store.UpsertRoleAsync(role, request.Guild.Id, cancellationToken);

        foreach (var user in message.GetReferencedUsers())
        {
            var member = context.TryGetMember(user.Id);

            await store.UpsertUserAsync(
                user,
                member,
                context.GetUserRoles(user.Id),
                cancellationToken
            );
        }

        await store.UpsertMessageAsync(request.Channel.Id, enrichedMessage, cancellationToken);
        await store.FlushAsync(cancellationToken);

        return new MessagePatchResult(true, "Patched successfully.");
    }
}
