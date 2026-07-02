using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    // Ordered, one-way steps applied to bring an existing database file up to the current
    // schema. Each entry's SQL must be safe to run against whatever a database left at the
    // previous version actually looks like (i.e. purely additive: new tables/columns/indexes/
    // triggers, never a destructive rewrite), since production files may be sitting at any
    // version already applied to them. Append new (version, sql) pairs here as the schema grows;
    // never edit or reorder an existing entry.
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, Schema.V1),
        (2, Schema.V2),
        (3, Schema.V3),
        (4, Schema.V4),
    ];

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        long userVersion;
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version;";
            userVersion = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        foreach (var (version, sql) in Migrations)
        {
            if (userVersion >= version)
                continue;

            // Wrapped in an explicit transaction (DDL is fully transactional in SQLite) so a
            // crash partway through a multi-statement migration rolls back entirely, leaving
            // user_version untouched. Without this, a kill between two ALTER TABLE statements
            // would leave some columns already added but user_version still at the old value,
            // and every subsequent run would re-attempt the same statements and fail with
            // "duplicate column name", permanently bricking the database file.
            await using var transaction = (SqliteTransaction)
                await _connection.BeginTransactionAsync(cancellationToken);

            await using (var migration = _connection.CreateCommand())
            {
                migration.Transaction = transaction;
                migration.CommandText = sql + $"\nPRAGMA user_version = {version};";
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
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

    private async ValueTask RollbackCoreAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
            return;

        await _transaction.RollbackAsync(cancellationToken);
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

    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await RollbackCoreAsync(cancellationToken);
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

            var permissionOverwritesJson =
                channel.PermissionOverwrites.Count > 0
                    ? DatabaseJson.SerializePermissionOverwrites(
                        channel
                            .PermissionOverwrites.Select(DatabaseJson.MapPermissionOverwrite)
                            .ToArray()
                    )
                    : null;

            await using var command = CreateCommand(
                """
                INSERT INTO channel (
                    id, guild_id, kind, category_id, category, parent_category_id,
                    parent_category, name, topic, position, is_archived, nsfw,
                    slowmode_seconds, bitrate, user_limit, permission_overwrites_json
                ) VALUES (
                    $id, $guildId, $kind, $categoryId, $category, $parentCategoryId,
                    $parentCategory, $name, $topic, $position, $isArchived, $nsfw,
                    $slowmodeSeconds, $bitrate, $userLimit, $permissionOverwritesJson
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
                    is_archived = excluded.is_archived,
                    nsfw = excluded.nsfw,
                    slowmode_seconds = excluded.slowmode_seconds,
                    bitrate = excluded.bitrate,
                    user_limit = excluded.user_limit,
                    permission_overwrites_json = excluded.permission_overwrites_json;
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
            command.Parameters.AddWithValue("$nsfw", channel.IsNsfw ? 1 : 0);
            command.Parameters.AddWithValue("$slowmodeSeconds", OrNull(channel.SlowmodeSeconds));
            command.Parameters.AddWithValue("$bitrate", OrNull(channel.Bitrate));
            command.Parameters.AddWithValue("$userLimit", OrNull(channel.UserLimit));
            command.Parameters.AddWithValue(
                "$permissionOverwritesJson",
                OrNull(permissionOverwritesJson)
            );
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
        Snowflake? lastExportAfter,
        Snowflake? lastExportBefore,
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
                    last_exported_at = $lastExportedAt,
                    last_export_after = $lastExportAfter,
                    last_export_before = $lastExportBefore
                WHERE id = $id;
                """
            );
            command.Parameters.AddWithValue("$lastMessageId", ToDbId(lastMessageId));
            command.Parameters.AddWithValue("$isArchived", isArchived ? 1 : 0);
            command.Parameters.AddWithValue(
                "$lastExportedAt",
                lastExportedAt.ToString("O", CultureInfo.InvariantCulture)
            );
            command.Parameters.AddWithValue("$lastExportAfter", ToDbId(lastExportAfter));
            command.Parameters.AddWithValue("$lastExportBefore", ToDbId(lastExportBefore));
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
        Snowflake? LastMessageId,
        Snowflake? LastExportAfter,
        Snowflake? LastExportBefore
    );

    // Cheap existence probe used by the live watcher's direct-upsert fast path: a message row
    // has a foreign key to channel(id), so the channel must exist before its messages can be
    // inserted. Callers cache the result, so this runs at most once per channel per session.
    public async ValueTask<bool> ChannelExistsAsync(
        Snowflake channelId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                "SELECT 1 FROM channel WHERE id = $id LIMIT 1;"
            );
            command.Parameters.AddWithValue("$id", ToDbId(channelId));
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
        finally
        {
            _lock.Release();
        }
    }

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
                       parent_category, last_message_id, last_export_after, last_export_before
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
                reader.IsDBNull(7) ? null : FromDbId(reader.GetInt64(7)),
                reader.IsDBNull(8) ? null : FromDbId(reader.GetInt64(8)),
                reader.IsDBNull(9) ? null : FromDbId(reader.GetInt64(9))
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

    // Used by the live-export path, which resolves roles off an ExportContext and passes the
    // whole Member through (rather than two loose nickname/avatar strings) so joined_at/
    // premium_since/communication_disabled_until/pending are available at the same call site.
    public ValueTask UpsertUserAsync(
        User user,
        Member? member,
        IReadOnlyList<Role> roles,
        CancellationToken cancellationToken = default
    ) => UpsertUserCoreAsync(user.Id, DatabaseJson.MapUser(user, member, roles), cancellationToken);

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
                    id, is_bot, discriminator, name, display_name, color, avatar_url, roles_json,
                    joined_at, premium_since, communication_disabled_until, pending
                ) VALUES (
                    $id, $isBot, $discriminator, $name, $displayName, $color, $avatarUrl, $rolesJson,
                    $joinedAt, $premiumSince, $communicationDisabledUntil, $pending
                )
                ON CONFLICT(id) DO UPDATE SET
                    is_bot = excluded.is_bot,
                    discriminator = excluded.discriminator,
                    name = excluded.name,
                    display_name = excluded.display_name,
                    color = excluded.color,
                    avatar_url = excluded.avatar_url,
                    roles_json = excluded.roles_json,
                    joined_at = excluded.joined_at,
                    premium_since = excluded.premium_since,
                    communication_disabled_until = excluded.communication_disabled_until,
                    pending = excluded.pending;
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
            command.Parameters.AddWithValue(
                "$joinedAt",
                OrNull(dto.JoinedAt?.ToString("O", CultureInfo.InvariantCulture))
            );
            command.Parameters.AddWithValue(
                "$premiumSince",
                OrNull(dto.PremiumSince?.ToString("O", CultureInfo.InvariantCulture))
            );
            command.Parameters.AddWithValue(
                "$communicationDisabledUntil",
                OrNull(dto.CommunicationDisabledUntil?.ToString("O", CultureInfo.InvariantCulture))
            );
            command.Parameters.AddWithValue("$pending", dto.Pending ? 1 : 0);
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

        var pollJson = message.Poll is { } poll
            ? JsonSerializer.Serialize(
                DatabaseJson.MapPoll(poll),
                DatabaseJsonContext.Default.PollDto
            )
            : null;

        var componentsJson =
            message.Components.Count > 0
                ? DatabaseJson.SerializeComponents(
                    message.Components.Select(DatabaseJson.MapComponent).ToArray()
                )
                : null;

        // deleted_at is deliberately absent from this INSERT/UPDATE: a normal upsert must never
        // clear a soft-delete marker if a stale re-export happens to touch the row later. Only
        // MarkMessageDeletedAsync ever sets it.
        await using (
            var command = CreateCommand(
                """
                INSERT INTO message (
                    id, channel_id, author_id, kind, timestamp, edited_timestamp,
                    call_ended_timestamp, is_pinned, content, reference_json,
                    forwarded_message_json, interaction_json, embeds_json, stickers_json,
                    inline_emojis_json, webhook_id, poll_json, components_json
                ) VALUES (
                    $id, $channelId, $authorId, $kind, $timestamp, $editedTimestamp,
                    $callEndedTimestamp, $isPinned, $content, $referenceJson,
                    $forwardedMessageJson, $interactionJson, $embedsJson, $stickersJson,
                    $inlineEmojisJson, $webhookId, $pollJson, $componentsJson
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
                    inline_emojis_json = excluded.inline_emojis_json,
                    webhook_id = excluded.webhook_id,
                    poll_json = excluded.poll_json,
                    components_json = excluded.components_json;
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
            command.Parameters.AddWithValue("$webhookId", ToDbId(message.WebhookId));
            command.Parameters.AddWithValue("$pollJson", OrNull(pollJson));
            command.Parameters.AddWithValue("$componentsJson", OrNull(componentsJson));
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

    // Marks a message as deleted without contacting Discord -- by the time this is called the
    // message is already gone, so there's nothing left to fetch. The row and its content are
    // preserved; only deleted_at is set (and only if it wasn't already), which is also what
    // stops a later stale re-export/patch from ever reviving it (see UpsertMessageCoreAsync,
    // which never touches this column).
    // Returns true if a row was actually updated -- false if no message with this id exists in
    // this channel, or it was already marked deleted. Callers should surface the false case
    // rather than assuming success, since it usually means a wrong channel/message id was passed.
    public async ValueTask<bool> MarkMessageDeletedAsync(
        Snowflake channelId,
        Snowflake messageId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                UPDATE message SET deleted_at = $deletedAt
                WHERE id = $id AND channel_id = $channelId AND deleted_at IS NULL;
                """
            );
            command.Parameters.AddWithValue(
                "$deletedAt",
                deletedAt.ToString("O", CultureInfo.InvariantCulture)
            );
            command.Parameters.AddWithValue("$id", ToDbId(messageId));
            command.Parameters.AddWithValue("$channelId", ToDbId(channelId));
            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);

            return rowsAffected > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask UpsertRoleAsync(
        Role role,
        Snowflake guildId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO role (
                    id, guild_id, name, color, position, permissions, hoist, mentionable,
                    icon_url, unicode_emoji, managed
                ) VALUES (
                    $id, $guildId, $name, $color, $position, $permissions, $hoist, $mentionable,
                    $iconUrl, $unicodeEmoji, $managed
                )
                ON CONFLICT(id) DO UPDATE SET
                    guild_id = excluded.guild_id,
                    name = excluded.name,
                    color = excluded.color,
                    position = excluded.position,
                    permissions = excluded.permissions,
                    hoist = excluded.hoist,
                    mentionable = excluded.mentionable,
                    icon_url = excluded.icon_url,
                    unicode_emoji = excluded.unicode_emoji,
                    managed = excluded.managed;
                """
            );
            command.Parameters.AddWithValue("$id", ToDbId(role.Id));
            command.Parameters.AddWithValue("$guildId", ToDbId(guildId));
            command.Parameters.AddWithValue("$name", role.Name);
            command.Parameters.AddWithValue(
                "$color",
                OrNull(role.Color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null)
            );
            command.Parameters.AddWithValue("$position", role.Position);
            command.Parameters.AddWithValue(
                "$permissions",
                role.Permissions.ToString(CultureInfo.InvariantCulture)
            );
            command.Parameters.AddWithValue("$hoist", role.Hoist ? 1 : 0);
            command.Parameters.AddWithValue("$mentionable", role.Mentionable ? 1 : 0);
            command.Parameters.AddWithValue("$iconUrl", OrNull(role.IconUrl));
            command.Parameters.AddWithValue("$unicodeEmoji", OrNull(role.UnicodeEmoji));
            command.Parameters.AddWithValue("$managed", role.Managed ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask UpsertGuildEmojiAsync(
        GuildEmoji emoji,
        Snowflake guildId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO guild_emoji (
                    id, guild_id, name, is_animated, image_url, creator_id, is_available, is_managed
                ) VALUES (
                    $id, $guildId, $name, $isAnimated, $imageUrl, $creatorId, $isAvailable, $isManaged
                )
                ON CONFLICT(id) DO UPDATE SET
                    guild_id = excluded.guild_id,
                    name = excluded.name,
                    is_animated = excluded.is_animated,
                    image_url = excluded.image_url,
                    creator_id = excluded.creator_id,
                    is_available = excluded.is_available,
                    is_managed = excluded.is_managed;
                """
            );
            command.Parameters.AddWithValue("$id", ToDbId(emoji.Id));
            command.Parameters.AddWithValue("$guildId", ToDbId(guildId));
            command.Parameters.AddWithValue("$name", emoji.Name);
            command.Parameters.AddWithValue("$isAnimated", emoji.IsAnimated ? 1 : 0);
            command.Parameters.AddWithValue("$imageUrl", emoji.ImageUrl);
            command.Parameters.AddWithValue("$creatorId", ToDbId(emoji.CreatorId));
            command.Parameters.AddWithValue("$isAvailable", emoji.IsAvailable ? 1 : 0);
            command.Parameters.AddWithValue("$isManaged", emoji.IsManaged ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask UpsertGuildStickerAsync(
        GuildSticker sticker,
        Snowflake guildId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO guild_sticker (
                    id, guild_id, name, description, tags, format, source_url, creator_id, is_available
                ) VALUES (
                    $id, $guildId, $name, $description, $tags, $format, $sourceUrl, $creatorId, $isAvailable
                )
                ON CONFLICT(id) DO UPDATE SET
                    guild_id = excluded.guild_id,
                    name = excluded.name,
                    description = excluded.description,
                    tags = excluded.tags,
                    format = excluded.format,
                    source_url = excluded.source_url,
                    creator_id = excluded.creator_id,
                    is_available = excluded.is_available;
                """
            );
            command.Parameters.AddWithValue("$id", ToDbId(sticker.Id));
            command.Parameters.AddWithValue("$guildId", ToDbId(guildId));
            command.Parameters.AddWithValue("$name", sticker.Name);
            command.Parameters.AddWithValue("$description", OrNull(sticker.Description));
            command.Parameters.AddWithValue("$tags", OrNull(sticker.Tags));
            command.Parameters.AddWithValue("$format", sticker.Format.ToString());
            command.Parameters.AddWithValue("$sourceUrl", sticker.SourceUrl);
            command.Parameters.AddWithValue("$creatorId", ToDbId(sticker.CreatorId));
            command.Parameters.AddWithValue("$isAvailable", sticker.IsAvailable ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask UpsertScheduledEventAsync(
        ScheduledEvent scheduledEvent,
        Snowflake guildId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO scheduled_event (
                    id, guild_id, channel_id, creator_id, name, description, start_time,
                    end_time, status, entity_type, location, cover_image_url, user_count
                ) VALUES (
                    $id, $guildId, $channelId, $creatorId, $name, $description, $startTime,
                    $endTime, $status, $entityType, $location, $coverImageUrl, $userCount
                )
                ON CONFLICT(id) DO UPDATE SET
                    guild_id = excluded.guild_id,
                    channel_id = excluded.channel_id,
                    creator_id = excluded.creator_id,
                    name = excluded.name,
                    description = excluded.description,
                    start_time = excluded.start_time,
                    end_time = excluded.end_time,
                    status = excluded.status,
                    entity_type = excluded.entity_type,
                    location = excluded.location,
                    cover_image_url = excluded.cover_image_url,
                    user_count = excluded.user_count;
                """
            );
            command.Parameters.AddWithValue("$id", ToDbId(scheduledEvent.Id));
            command.Parameters.AddWithValue("$guildId", ToDbId(guildId));
            command.Parameters.AddWithValue("$channelId", ToDbId(scheduledEvent.ChannelId));
            command.Parameters.AddWithValue("$creatorId", ToDbId(scheduledEvent.CreatorId));
            command.Parameters.AddWithValue("$name", scheduledEvent.Name);
            command.Parameters.AddWithValue("$description", OrNull(scheduledEvent.Description));
            command.Parameters.AddWithValue(
                "$startTime",
                scheduledEvent.StartTime.ToString("O", CultureInfo.InvariantCulture)
            );
            command.Parameters.AddWithValue(
                "$endTime",
                OrNull(scheduledEvent.EndTime?.ToString("O", CultureInfo.InvariantCulture))
            );
            command.Parameters.AddWithValue("$status", scheduledEvent.Status.ToString());
            command.Parameters.AddWithValue("$entityType", scheduledEvent.EntityType.ToString());
            command.Parameters.AddWithValue("$location", OrNull(scheduledEvent.Location));
            command.Parameters.AddWithValue("$coverImageUrl", OrNull(scheduledEvent.CoverImageUrl));
            command.Parameters.AddWithValue("$userCount", OrNull(scheduledEvent.UserCount));
            await command.ExecuteNonQueryAsync(cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await RollbackCoreAsync(CancellationToken.None);
        }
        finally
        {
            _lock.Release();
        }

        await _connection.DisposeAsync();
        _lock.Dispose();
    }
}
