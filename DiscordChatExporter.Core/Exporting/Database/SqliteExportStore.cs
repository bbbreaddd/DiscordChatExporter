using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using Microsoft.Data.Sqlite;

namespace DiscordChatExporter.Core.Exporting.Database;

// Owns a single writer connection to a consolidated SQLite database that holds messages from
// (potentially) many channels across many guilds. Message rows are keyed by their (globally
// unique) Discord snowflake, so every write is a plain upsert -- there is no byte-level merge,
// temp-file, or crash-recovery machinery to replicate from the JSON export path; a transaction
// that never committed is simply retried by the caller on the next run.
public sealed class SqliteExportStore : IAsyncDisposable
{
    // Kept modest so that a single failed statement (e.g. a constraint violation from malformed
    // input) only rolls back a small, cheaply-redone batch instead of hours of prior work.
    private const int BatchSize = 2000;

    private readonly SqliteConnection _connection;

    // All public members serialize through this so the single underlying connection (and its
    // at-most-one open transaction) is never touched from two threads at once -- callers may
    // export multiple channels concurrently, but writes to the shared database must not.
    private readonly SemaphoreSlim _lock = new(1, 1);

    private SqliteTransaction? _transaction;
    private int _pendingCount;

    private SqliteExportStore(SqliteConnection connection) => _connection = connection;

    public static async Task<SqliteExportStore> OpenAsync(
        string databaseFilePath,
        CancellationToken cancellationToken = default
    )
    {
        var directoryPath = Path.GetDirectoryName(databaseFilePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFilePath,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 30000;
                """;
            await pragmas.ExecuteNonQueryAsync(cancellationToken);
        }

        var store = new SqliteExportStore(connection);
        await store.MigrateAsync(cancellationToken);

        return store;
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        long userVersion;
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version;";
            userVersion = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        // Schema is fixed at version 1 for now; bump this and branch here when it changes.
        if (userVersion >= 1)
            return;

        await using var migration = _connection.CreateCommand();
        migration.CommandText = Schema.CreateStatements + "\nPRAGMA user_version = 1;";
        await migration.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteCommand CreateCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        return command;
    }

    private async ValueTask EnsureTransactionAsync(CancellationToken cancellationToken)
    {
        _transaction ??= (SqliteTransaction)
            await _connection.BeginTransactionAsync(cancellationToken);
    }

    private async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
            return;

        await _transaction.CommitAsync(cancellationToken);
        await _transaction.DisposeAsync();
        _transaction = null;
        _pendingCount = 0;
    }

    private async ValueTask MaybeAutoFlushAsync(CancellationToken cancellationToken)
    {
        _pendingCount++;
        if (_pendingCount >= BatchSize)
            await FlushCoreAsync(cancellationToken);
    }

    // Commits whatever writes are currently pending. Safe to call at any time, including when
    // nothing is pending. Callers should flush after finishing a logical unit of work (a whole
    // channel, or a whole file) so a crash doesn't lose more than one partial batch.
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await FlushCoreAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static long ToDbId(Snowflake snowflake) => (long)snowflake.Value;

    private static object ToDbId(Snowflake? snowflake) =>
        snowflake is { } s ? (long)s.Value : DBNull.Value;

    private static Snowflake FromDbId(long value) => new((ulong)value);

    private static object OrNull(string? value) => (object?)value ?? DBNull.Value;

    private static object OrNull(int? value) => (object?)value ?? DBNull.Value;

    public async ValueTask UpsertGuildAsync(
        Guild guild,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO guild (id, name, icon_url)
                VALUES ($id, $name, $iconUrl)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    icon_url = excluded.icon_url;
                """
            );
            command.Parameters.AddWithValue("$id", ToDbId(guild.Id));
            command.Parameters.AddWithValue("$name", guild.Name);
            command.Parameters.AddWithValue("$iconUrl", OrNull(guild.IconUrl));
            await command.ExecuteNonQueryAsync(cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask UpsertChannelAsync(
        Channel channel,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO channel (
                    id, guild_id, kind, category_id, category, parent_category_id,
                    parent_category, name, topic, position, is_archived
                ) VALUES (
                    $id, $guildId, $kind, $categoryId, $category, $parentCategoryId,
                    $parentCategory, $name, $topic, $position, $isArchived
                )
                ON CONFLICT(id) DO UPDATE SET
                    guild_id = excluded.guild_id,
                    kind = excluded.kind,
                    category_id = excluded.category_id,
                    category = excluded.category,
                    parent_category_id = excluded.parent_category_id,
                    parent_category = excluded.parent_category,
                    name = excluded.name,
                    topic = excluded.topic,
                    position = excluded.position,
                    is_archived = excluded.is_archived;
                """
            );
            command.Parameters.AddWithValue("$id", ToDbId(channel.Id));
            command.Parameters.AddWithValue("$guildId", ToDbId(channel.GuildId));
            command.Parameters.AddWithValue("$kind", channel.Kind.ToString());
            command.Parameters.AddWithValue("$categoryId", ToDbId(channel.Parent?.Id));
            command.Parameters.AddWithValue("$category", OrNull(channel.Parent?.Name));
            command.Parameters.AddWithValue(
                "$parentCategoryId",
                ToDbId(channel.Parent?.Parent?.Id)
            );
            command.Parameters.AddWithValue(
                "$parentCategory",
                OrNull(channel.Parent?.Parent?.Name)
            );
            command.Parameters.AddWithValue("$name", channel.Name);
            command.Parameters.AddWithValue("$topic", OrNull(channel.Topic));
            command.Parameters.AddWithValue("$position", OrNull(channel.Position));
            command.Parameters.AddWithValue("$isArchived", channel.IsArchived ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask UpdateChannelExportStateAsync(
        Snowflake channelId,
        Snowflake? lastMessageId,
        bool isArchived,
        DateTimeOffset lastExportedAt,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                UPDATE channel
                SET last_message_id = $lastMessageId,
                    is_archived = $isArchived,
                    last_exported_at = $lastExportedAt
                WHERE id = $id;
                """
            );
            command.Parameters.AddWithValue("$lastMessageId", ToDbId(lastMessageId));
            command.Parameters.AddWithValue("$isArchived", isArchived ? 1 : 0);
            command.Parameters.AddWithValue(
                "$lastExportedAt",
                lastExportedAt.ToString("O", CultureInfo.InvariantCulture)
            );
            command.Parameters.AddWithValue("$id", ToDbId(channelId));
            await command.ExecuteNonQueryAsync(cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<Snowflake?> GetMaxMessageIdAsync(
        Snowflake channelId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                "SELECT MAX(id) FROM message WHERE channel_id = $channelId;"
            );
            command.Parameters.AddWithValue("$channelId", ToDbId(channelId));
            var result = await command.ExecuteScalarAsync(cancellationToken);

            return result is long value ? FromDbId(value) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<DateTimeOffset?> GetChannelLastExportedAtAsync(
        Snowflake channelId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                "SELECT last_exported_at FROM channel WHERE id = $channelId;"
            );
            command.Parameters.AddWithValue("$channelId", ToDbId(channelId));
            var result = await command.ExecuteScalarAsync(cancellationToken);

            return result is string text
                ? DateTimeOffset.Parse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                )
                : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Snapshot of a channel's stored metadata, used by the live-export path to decide whether a
    // channel can be skipped (mirrors ChannelExporter.HeaderMatchesRequest for the JSON format).
    public sealed record ChannelState(
        ChannelKind Kind,
        string Name,
        string? Topic,
        Snowflake? CategoryId,
        string? Category,
        Snowflake? ParentCategoryId,
        string? ParentCategory,
        Snowflake? LastMessageId
    );

    public async ValueTask<ChannelState?> GetChannelStateAsync(
        Snowflake channelId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                """
                SELECT kind, name, topic, category_id, category, parent_category_id,
                       parent_category, last_message_id
                FROM channel
                WHERE id = $channelId;
                """
            );
            command.Parameters.AddWithValue("$channelId", ToDbId(channelId));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new ChannelState(
                Enum.Parse<ChannelKind>(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : FromDbId(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : FromDbId(reader.GetInt64(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : FromDbId(reader.GetInt64(7))
            );
        }
        finally
        {
            _lock.Release();
        }
    }

    // Used by the offline JSON importer (BuildDatabaseCommand).
    public ValueTask UpsertUserAsync(
        User user,
        Member? member,
        IReadOnlyDictionary<Snowflake, Role> roles,
        CancellationToken cancellationToken = default
    ) => UpsertUserCoreAsync(user.Id, DatabaseJson.MapUser(user, member, roles), cancellationToken);

    // Used by the live-export path, which resolves roles/nickname/avatar off an ExportContext.
    public ValueTask UpsertUserAsync(
        User user,
        IReadOnlyList<Role> roles,
        string? nickname,
        string? avatarUrl,
        CancellationToken cancellationToken = default
    ) =>
        UpsertUserCoreAsync(
            user.Id,
            DatabaseJson.MapUser(user, roles, nickname, avatarUrl),
            cancellationToken
        );

    private async ValueTask UpsertUserCoreAsync(
        Snowflake userId,
        UserDto dto,
        CancellationToken cancellationToken
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO "user" (
                    id, is_bot, discriminator, name, display_name, color, avatar_url, roles_json
                ) VALUES (
                    $id, $isBot, $discriminator, $name, $displayName, $color, $avatarUrl, $rolesJson
                )
                ON CONFLICT(id) DO UPDATE SET
                    is_bot = excluded.is_bot,
                    discriminator = excluded.discriminator,
                    name = excluded.name,
                    display_name = excluded.display_name,
                    color = excluded.color,
                    avatar_url = excluded.avatar_url,
                    roles_json = excluded.roles_json;
                """
            );
            command.Parameters.AddWithValue("$id", ToDbId(userId));
            command.Parameters.AddWithValue("$isBot", dto.IsBot ? 1 : 0);
            command.Parameters.AddWithValue("$discriminator", dto.Discriminator);
            command.Parameters.AddWithValue("$name", dto.Name);
            command.Parameters.AddWithValue("$displayName", OrNull(dto.Nickname));
            command.Parameters.AddWithValue("$color", OrNull(dto.Color));
            command.Parameters.AddWithValue("$avatarUrl", dto.AvatarUrl);
            command.Parameters.AddWithValue("$rolesJson", DatabaseJson.SerializeRoles(dto.Roles));
            await command.ExecuteNonQueryAsync(cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask UpsertMessageAsync(
        Snowflake channelId,
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);
            await UpsertMessageCoreAsync(channelId, message, cancellationToken);
            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async ValueTask UpsertMessageCoreAsync(
        Snowflake channelId,
        Message message,
        CancellationToken cancellationToken
    )
    {
        var messageIdDb = ToDbId(message.Id);

        var embedsJson =
            message.Embeds.Count > 0
                ? DatabaseJson.SerializeEmbeds(
                    message.Embeds.Select(DatabaseJson.MapEmbed).ToArray()
                )
                : null;

        var stickersJson =
            message.Stickers.Count > 0
                ? DatabaseJson.SerializeStickers(
                    message.Stickers.Select(DatabaseJson.MapSticker).ToArray()
                )
                : null;

        var inlineEmojis = DatabaseJson.ExtractInlineEmojis(message.Content);
        var inlineEmojisJson =
            inlineEmojis.Count > 0 ? DatabaseJson.SerializeInlineEmojis(inlineEmojis) : null;

        await using (
            var command = CreateCommand(
                """
                INSERT INTO message (
                    id, channel_id, author_id, kind, timestamp, edited_timestamp,
                    call_ended_timestamp, is_pinned, content, reference_json,
                    forwarded_message_json, interaction_json, embeds_json, stickers_json,
                    inline_emojis_json
                ) VALUES (
                    $id, $channelId, $authorId, $kind, $timestamp, $editedTimestamp,
                    $callEndedTimestamp, $isPinned, $content, $referenceJson,
                    $forwardedMessageJson, $interactionJson, $embedsJson, $stickersJson,
                    $inlineEmojisJson
                )
                ON CONFLICT(id) DO UPDATE SET
                    channel_id = excluded.channel_id,
                    author_id = excluded.author_id,
                    kind = excluded.kind,
                    timestamp = excluded.timestamp,
                    edited_timestamp = excluded.edited_timestamp,
                    call_ended_timestamp = excluded.call_ended_timestamp,
                    is_pinned = excluded.is_pinned,
                    content = excluded.content,
                    reference_json = excluded.reference_json,
                    forwarded_message_json = excluded.forwarded_message_json,
                    interaction_json = excluded.interaction_json,
                    embeds_json = excluded.embeds_json,
                    stickers_json = excluded.stickers_json,
                    inline_emojis_json = excluded.inline_emojis_json;
                """
            )
        )
        {
            command.Parameters.AddWithValue("$id", messageIdDb);
            command.Parameters.AddWithValue("$channelId", ToDbId(channelId));
            command.Parameters.AddWithValue("$authorId", ToDbId(message.Author.Id));
            command.Parameters.AddWithValue("$kind", message.Kind.ToString());
            command.Parameters.AddWithValue(
                "$timestamp",
                message.Timestamp.ToString("O", CultureInfo.InvariantCulture)
            );
            command.Parameters.AddWithValue(
                "$editedTimestamp",
                OrNull(message.EditedTimestamp?.ToString("O", CultureInfo.InvariantCulture))
            );
            command.Parameters.AddWithValue(
                "$callEndedTimestamp",
                OrNull(message.CallEndedTimestamp?.ToString("O", CultureInfo.InvariantCulture))
            );
            command.Parameters.AddWithValue("$isPinned", message.IsPinned ? 1 : 0);
            command.Parameters.AddWithValue("$content", message.Content);
            command.Parameters.AddWithValue(
                "$referenceJson",
                DatabaseJson.ToDbParam(
                    message.Reference is { } reference ? DatabaseJson.MapReference(reference) : null
                )
            );
            command.Parameters.AddWithValue(
                "$forwardedMessageJson",
                DatabaseJson.ToDbParam(
                    message.ForwardedMessage is { } forwarded
                        ? DatabaseJson.MapForwardedMessage(forwarded)
                        : null
                )
            );
            command.Parameters.AddWithValue(
                "$interactionJson",
                DatabaseJson.ToDbParam(
                    message.Interaction is { } interaction
                        ? DatabaseJson.MapInteraction(interaction)
                        : null
                )
            );
            command.Parameters.AddWithValue("$embedsJson", OrNull(embedsJson));
            command.Parameters.AddWithValue("$stickersJson", OrNull(stickersJson));
            command.Parameters.AddWithValue("$inlineEmojisJson", OrNull(inlineEmojisJson));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Child rows are always fully replaced rather than diffed, so re-upserting a message
        // (e.g. because a reaction changed) can never leave stale rows behind.
        await using (
            var deleteAttachments = CreateCommand("DELETE FROM attachment WHERE message_id = $id;")
        )
        {
            deleteAttachments.Parameters.AddWithValue("$id", messageIdDb);
            await deleteAttachments.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var attachment in message.Attachments)
        {
            await using var insertAttachment = CreateCommand(
                """
                INSERT INTO attachment (id, message_id, url, file_name, file_size_bytes)
                VALUES ($id, $messageId, $url, $fileName, $fileSizeBytes)
                ON CONFLICT(id) DO UPDATE SET
                    message_id = excluded.message_id,
                    url = excluded.url,
                    file_name = excluded.file_name,
                    file_size_bytes = excluded.file_size_bytes;
                """
            );
            insertAttachment.Parameters.AddWithValue("$id", ToDbId(attachment.Id));
            insertAttachment.Parameters.AddWithValue("$messageId", messageIdDb);
            insertAttachment.Parameters.AddWithValue("$url", attachment.Url);
            insertAttachment.Parameters.AddWithValue("$fileName", attachment.FileName);
            insertAttachment.Parameters.AddWithValue(
                "$fileSizeBytes",
                attachment.FileSize.TotalBytes
            );
            await insertAttachment.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (
            var deleteReactions = CreateCommand("DELETE FROM reaction WHERE message_id = $id;")
        )
        {
            deleteReactions.Parameters.AddWithValue("$id", messageIdDb);
            await deleteReactions.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var reaction in message.Reactions)
        {
            await using var insertReaction = CreateCommand(
                """
                INSERT INTO reaction (
                    message_id, emoji_id, emoji_name, emoji_code, emoji_is_animated,
                    emoji_image_url, count, users_json
                ) VALUES (
                    $messageId, $emojiId, $emojiName, $emojiCode, $emojiIsAnimated,
                    $emojiImageUrl, $count, $usersJson
                );
                """
            );
            insertReaction.Parameters.AddWithValue("$messageId", messageIdDb);
            insertReaction.Parameters.AddWithValue("$emojiId", ToDbId(reaction.Emoji.Id));
            insertReaction.Parameters.AddWithValue("$emojiName", reaction.Emoji.Name);
            insertReaction.Parameters.AddWithValue("$emojiCode", reaction.Emoji.Code);
            insertReaction.Parameters.AddWithValue(
                "$emojiIsAnimated",
                reaction.Emoji.IsAnimated ? 1 : 0
            );
            insertReaction.Parameters.AddWithValue(
                "$emojiImageUrl",
                OrNull(reaction.Emoji.ImageUrl)
            );
            insertReaction.Parameters.AddWithValue("$count", reaction.Count);
            insertReaction.Parameters.AddWithValue(
                "$usersJson",
                reaction.Users is { } users
                    ? DatabaseJson.SerializeReactionUsers(
                        users.Select(DatabaseJson.MapReactionUser).ToArray()
                    )
                    : (object)DBNull.Value
            );
            await insertReaction.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (
            var deleteMentions = CreateCommand(
                "DELETE FROM message_mention WHERE message_id = $id;"
            )
        )
        {
            deleteMentions.Parameters.AddWithValue("$id", messageIdDb);
            await deleteMentions.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var mentioned in message.MentionedUsers)
        {
            await using var insertMention = CreateCommand(
                "INSERT OR IGNORE INTO message_mention (message_id, user_id) VALUES ($messageId, $userId);"
            );
            insertMention.Parameters.AddWithValue("$messageId", messageIdDb);
            insertMention.Parameters.AddWithValue("$userId", ToDbId(mentioned.Id));
            await insertMention.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await FlushCoreAsync(CancellationToken.None);
        }
        finally
        {
            _lock.Release();
        }

        await _connection.DisposeAsync();
        _lock.Dispose();
    }
}
