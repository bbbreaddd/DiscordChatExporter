using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    private const long MediaDedupeMaxBytes = 50L * 1024 * 1024;

    private readonly SqliteConnection _connection;
    private readonly string? _mediaDirPath;
    private readonly ExportAssetDownloader? _mediaDownloader;
    private readonly bool _retryFailedMedia;
    private readonly StoreDataOptions _dataOptions;

    // All public members serialize through this so the single underlying connection (and its
    // at-most-one open transaction) is never touched from two threads at once -- callers may
    // export multiple channels concurrently, but writes to the shared database must not.
    private readonly SemaphoreSlim _lock = new(1, 1);

    private SqliteTransaction? _transaction;
    private int _pendingCount;

    private SqliteExportStore(
        SqliteConnection connection,
        string? mediaDirPath,
        bool retryFailedMedia,
        StoreDataOptions? dataOptions
    )
    {
        _connection = connection;
        _mediaDirPath = mediaDirPath;
        _retryFailedMedia = retryFailedMedia;
        _dataOptions = dataOptions ?? StoreDataOptions.Default;
        _mediaDownloader = mediaDirPath is not null
            ? new ExportAssetDownloader(mediaDirPath, reuse: true)
            : null;
    }

    public static async Task<SqliteExportStore> OpenAsync(
        string databaseFilePath,
        CancellationToken cancellationToken = default
    ) => await OpenAsync(databaseFilePath, null, false, null, cancellationToken);

    public static async Task<SqliteExportStore> OpenAsync(
        string databaseFilePath,
        string? mediaDirPath,
        CancellationToken cancellationToken
    ) => await OpenAsync(databaseFilePath, mediaDirPath, false, null, cancellationToken);

    public static async Task<SqliteExportStore> OpenAsync(
        string databaseFilePath,
        string? mediaDirPath,
        bool retryFailedMedia,
        CancellationToken cancellationToken
    ) => await OpenAsync(databaseFilePath, mediaDirPath, retryFailedMedia, null, cancellationToken);

    // retryFailedMedia forces re-attempting media URLs already recorded in the
    // media_download_failure ledger (see Schema.V10), instead of skipping them. dataOptions gates
    // which media kinds and message sub-parts are captured (null = capture everything).
    public static async Task<SqliteExportStore> OpenAsync(
        string databaseFilePath,
        string? mediaDirPath,
        bool retryFailedMedia,
        StoreDataOptions? dataOptions,
        CancellationToken cancellationToken
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

        var store = new SqliteExportStore(connection, mediaDirPath, retryFailedMedia, dataOptions);
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
        (5, Schema.V5),
        (6, Schema.V6),
        (7, Schema.V7),
        (8, Schema.V8),
        (9, Schema.V9),
        (10, Schema.V10),
        (11, Schema.V11),
        (12, Schema.V12),
        (13, Schema.V13),
        (14, Schema.V14),
        (15, Schema.V15),
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

    private record PendingMedia(
        string OwnerKind,
        Snowflake OwnerId,
        string AssetKind,
        string SourceUrl,
        string LocalPath,
        string FilePath,
        long SizeBytes,
        string? ContentHash,
        bool KeepHistory
    );

    private static string GetMediaRelativeFilePath(string assetKind, string url)
    {
        var fileName = ExportAssetDownloader.GetFileNameFromUrl(url);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var hashStart = nameWithoutExtension.LastIndexOf('-') + 1;
        var hash = hashStart > 0 ? nameWithoutExtension[hashStart..] : nameWithoutExtension;

        if (hash.Length < 4)
            hash = hash.PadRight(4, '0');

        return Path.Combine(assetKind, hash[..2], hash[2..4], fileName);
    }

    private static bool IsDiscordMediaUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return string.Equals(uri.Host, "cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "media.discordapp.net", StringComparison.OrdinalIgnoreCase);
    }

    private async ValueTask<PendingMedia?> TryDownloadMediaAsync(
        string ownerKind,
        Snowflake ownerId,
        string assetKind,
        string? sourceUrl,
        bool keepHistory,
        CancellationToken cancellationToken
    )
    {
        if (_mediaDownloader is null || _mediaDirPath is null || !IsDiscordMediaUrl(sourceUrl))
            return null;

        // Per-asset-kind gating from the watcher config (e.g. capture attachments but not emojis).
        if (!_dataOptions.AllowsMediaKind(assetKind))
            return null;

        if (
            ownerKind == "guild"
            && assetKind == "guild-icons"
            && sourceUrl!.Contains("/embed/avatars/", StringComparison.OrdinalIgnoreCase)
        )
            return null;

        var normalizedUrl = ExportAssetDownloader.NormalizeUrl(sourceUrl!);

        // Skip the network entirely for a URL already known to be permanently gone, unless the
        // caller explicitly asked to retry (--retry-failed). A file already reused from a local
        // cache (e.g. hardlinked in from a pre-migration media dir) never reaches this check --
        // DownloadWithInfoAsync's own file-existence check below short-circuits first -- so this
        // can't wrongly block a legitimately-cached asset.
        if (!_retryFailedMedia && await IsMediaLedgeredAsync(normalizedUrl, cancellationToken))
            return null;

        try
        {
            var relativeFilePath = GetMediaRelativeFilePath(assetKind, sourceUrl!);
            var result = await _mediaDownloader.DownloadWithInfoAsync(
                sourceUrl!,
                sourceUrl!,
                relativeFilePath,
                MediaDedupeMaxBytes,
                cancellationToken
            );
            if (result is null)
                return null;

            return new PendingMedia(
                ownerKind,
                ownerId,
                assetKind,
                sourceUrl!,
                Path.GetRelativePath(_mediaDirPath, result.FilePath),
                result.FilePath,
                result.SizeBytes,
                result.Sha256Hash,
                keepHistory
            );
        }
        // Only a permanent "gone" response is ledgered. Timeouts, 5xx, and other network errors
        // are left unledgered so they're retried on the next run -- see Schema.V10.
        catch (HttpRequestException ex)
            when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            await RecordMediaFailureAsync(
                normalizedUrl,
                (int)ex.StatusCode.Value,
                cancellationToken
            );
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    // Internal (rather than private) so tests can exercise the ledger directly without needing
    // to trigger a real network failure -- same rationale as GetFileNameFromUrl in
    // ExportAssetDownloader.
    internal async ValueTask<bool> IsMediaLedgeredAsync(
        string urlNormalized,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                "SELECT 1 FROM media_download_failure WHERE url_normalized = $url;"
            );
            command.Parameters.AddWithValue("$url", urlNormalized);
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
        finally
        {
            _lock.Release();
        }
    }

    internal async ValueTask RecordMediaFailureAsync(
        string urlNormalized,
        int statusCode,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            await using var command = CreateCommand(
                """
                INSERT INTO media_download_failure (
                    url_normalized, status_code, first_failed_at, last_attempt_at, attempts
                )
                VALUES ($url, $statusCode, $now, $now, 1)
                ON CONFLICT(url_normalized) DO UPDATE SET
                    status_code = excluded.status_code,
                    last_attempt_at = excluded.last_attempt_at,
                    attempts = attempts + 1;
                """
            );
            command.Parameters.AddWithValue("$url", urlNormalized);
            command.Parameters.AddWithValue("$statusCode", statusCode);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async ValueTask<(long? BlobId, string LocalPath)> ResolveBlobAsync(
        PendingMedia media,
        CancellationToken cancellationToken
    )
    {
        if (media.ContentHash is null || media.SizeBytes > MediaDedupeMaxBytes)
            return (null, media.LocalPath);

        await using (
            var existing = CreateCommand(
                """
                SELECT id, local_path
                FROM media_blob
                WHERE content_hash = $contentHash AND size_bytes = $sizeBytes;
                """
            )
        )
        {
            existing.Parameters.AddWithValue("$contentHash", media.ContentHash);
            existing.Parameters.AddWithValue("$sizeBytes", media.SizeBytes);

            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var existingPath = reader.GetString(1);
                if (!string.Equals(existingPath, media.LocalPath, StringComparison.Ordinal))
                {
                    try
                    {
                        File.Delete(media.FilePath);
                    }
                    catch (IOException) { }
                }

                return (reader.GetInt64(0), existingPath);
            }
        }

        await using var insert = CreateCommand(
            """
            INSERT INTO media_blob (content_hash, size_bytes, local_path, recorded_at)
            VALUES ($contentHash, $sizeBytes, $localPath, $recordedAt)
            RETURNING id;
            """
        );
        insert.Parameters.AddWithValue("$contentHash", media.ContentHash);
        insert.Parameters.AddWithValue("$sizeBytes", media.SizeBytes);
        insert.Parameters.AddWithValue("$localPath", media.LocalPath);
        insert.Parameters.AddWithValue(
            "$recordedAt",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        );

        return ((long)(await insert.ExecuteScalarAsync(cancellationToken))!, media.LocalPath);
    }

    private async ValueTask InsertMediaAsync(
        PendingMedia? media,
        CancellationToken cancellationToken
    )
    {
        if (media is null)
            return;

        var (blobId, localPath) = await ResolveBlobAsync(media, cancellationToken);

        if (!media.KeepHistory)
        {
            await using var delete = CreateCommand(
                """
                DELETE FROM media_asset
                WHERE owner_kind = $ownerKind AND owner_id = $ownerId AND asset_kind = $assetKind;
                """
            );
            delete.Parameters.AddWithValue("$ownerKind", media.OwnerKind);
            delete.Parameters.AddWithValue("$ownerId", ToDbId(media.OwnerId));
            delete.Parameters.AddWithValue("$assetKind", media.AssetKind);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var existing = CreateCommand(
                """
                SELECT source_url
                FROM media_asset
                WHERE owner_kind = $ownerKind
                    AND owner_id = $ownerId
                    AND asset_kind = $assetKind
                    AND is_current = 1;
                """
            );
            existing.Parameters.AddWithValue("$ownerKind", media.OwnerKind);
            existing.Parameters.AddWithValue("$ownerId", ToDbId(media.OwnerId));
            existing.Parameters.AddWithValue("$assetKind", media.AssetKind);

            if (
                await existing.ExecuteScalarAsync(cancellationToken) is string currentUrl
                && string.Equals(currentUrl, media.SourceUrl, StringComparison.Ordinal)
            )
                return;

            await using var expire = CreateCommand(
                """
                UPDATE media_asset
                SET is_current = 0
                WHERE owner_kind = $ownerKind
                    AND owner_id = $ownerId
                    AND asset_kind = $assetKind
                    AND is_current = 1;
                """
            );
            expire.Parameters.AddWithValue("$ownerKind", media.OwnerKind);
            expire.Parameters.AddWithValue("$ownerId", ToDbId(media.OwnerId));
            expire.Parameters.AddWithValue("$assetKind", media.AssetKind);
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = CreateCommand(
            """
            INSERT INTO media_asset (
                owner_kind, owner_id, asset_kind, source_url, local_path, is_current, recorded_at, blob_id
            ) VALUES (
                $ownerKind, $ownerId, $assetKind, $sourceUrl, $localPath, 1, $recordedAt, $blobId
            );
            """
        );
        insert.Parameters.AddWithValue("$ownerKind", media.OwnerKind);
        insert.Parameters.AddWithValue("$ownerId", ToDbId(media.OwnerId));
        insert.Parameters.AddWithValue("$assetKind", media.AssetKind);
        insert.Parameters.AddWithValue("$sourceUrl", media.SourceUrl);
        insert.Parameters.AddWithValue("$localPath", localPath);
        insert.Parameters.AddWithValue(
            "$recordedAt",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        );
        insert.Parameters.AddWithValue("$blobId", (object?)blobId ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask UpsertGuildAsync(
        Guild guild,
        CancellationToken cancellationToken = default
    )
    {
        var iconMedia = await TryDownloadMediaAsync(
            "guild",
            guild.Id,
            "guild-icons",
            guild.IconUrl,
            keepHistory: true,
            cancellationToken
        );
        var bannerMedia = await TryDownloadMediaAsync(
            "guild",
            guild.Id,
            "guild-banners",
            guild.BannerUrl,
            keepHistory: true,
            cancellationToken
        );

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO guild (
                    id, name, icon_url, banner_url,
                    premium_tier, premium_subscription_count, approximate_member_count,
                    verification_level, explicit_content_filter, mfa_level,
                    system_channel_id, rules_channel_id, public_updates_channel_id,
                    afk_channel_id, afk_timeout, preferred_locale, vanity_url_code,
                    features_json, welcome_screen_json, onboarding_json,
                    owner_id, description, splash_url, discovery_splash_url
                )
                VALUES (
                    $id, $name, $iconUrl, $bannerUrl,
                    $premiumTier, $premiumSubscriptionCount, $approximateMemberCount,
                    $verificationLevel, $explicitContentFilter, $mfaLevel,
                    $systemChannelId, $rulesChannelId, $publicUpdatesChannelId,
                    $afkChannelId, $afkTimeout, $preferredLocale, $vanityUrlCode,
                    $featuresJson, $welcomeScreenJson, $onboardingJson,
                    $ownerId, $description, $splashUrl, $discoverySplashUrl
                )
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    icon_url = excluded.icon_url,
                    banner_url = excluded.banner_url,
                    premium_tier = excluded.premium_tier,
                    premium_subscription_count = excluded.premium_subscription_count,
                    approximate_member_count = excluded.approximate_member_count,
                    verification_level = excluded.verification_level,
                    explicit_content_filter = excluded.explicit_content_filter,
                    mfa_level = excluded.mfa_level,
                    system_channel_id = excluded.system_channel_id,
                    rules_channel_id = excluded.rules_channel_id,
                    public_updates_channel_id = excluded.public_updates_channel_id,
                    afk_channel_id = excluded.afk_channel_id,
                    afk_timeout = excluded.afk_timeout,
                    preferred_locale = excluded.preferred_locale,
                    vanity_url_code = excluded.vanity_url_code,
                    features_json = excluded.features_json,
                    welcome_screen_json = excluded.welcome_screen_json,
                    -- onboarding is fetched separately (not part of the guild object itself) and
                    -- may not always be re-supplied, so COALESCE keeps the last known value
                    -- instead of clobbering it with NULL on a call that didn't fetch it.
                    onboarding_json = COALESCE(excluded.onboarding_json, guild.onboarding_json),
                    owner_id = excluded.owner_id,
                    description = excluded.description,
                    splash_url = excluded.splash_url,
                    discovery_splash_url = excluded.discovery_splash_url;
                """
            );
            command.Parameters.AddWithValue("$id", ToDbId(guild.Id));
            command.Parameters.AddWithValue("$name", guild.Name);
            command.Parameters.AddWithValue("$iconUrl", OrNull(guild.IconUrl));
            command.Parameters.AddWithValue("$bannerUrl", OrNull(guild.BannerUrl));
            command.Parameters.AddWithValue("$premiumTier", OrNull(guild.PremiumTier));
            command.Parameters.AddWithValue(
                "$premiumSubscriptionCount",
                OrNull(guild.PremiumSubscriptionCount)
            );
            command.Parameters.AddWithValue(
                "$approximateMemberCount",
                OrNull(guild.ApproximateMemberCount)
            );
            command.Parameters.AddWithValue("$verificationLevel", OrNull(guild.VerificationLevel));
            command.Parameters.AddWithValue(
                "$explicitContentFilter",
                OrNull(guild.ExplicitContentFilter)
            );
            command.Parameters.AddWithValue("$mfaLevel", OrNull(guild.MfaLevel));
            command.Parameters.AddWithValue("$systemChannelId", ToDbId(guild.SystemChannelId));
            command.Parameters.AddWithValue("$rulesChannelId", ToDbId(guild.RulesChannelId));
            command.Parameters.AddWithValue(
                "$publicUpdatesChannelId",
                ToDbId(guild.PublicUpdatesChannelId)
            );
            command.Parameters.AddWithValue("$afkChannelId", ToDbId(guild.AfkChannelId));
            command.Parameters.AddWithValue("$afkTimeout", OrNull(guild.AfkTimeout));
            command.Parameters.AddWithValue("$preferredLocale", OrNull(guild.PreferredLocale));
            command.Parameters.AddWithValue("$vanityUrlCode", OrNull(guild.VanityUrlCode));
            command.Parameters.AddWithValue("$featuresJson", OrNull(guild.FeaturesJson));
            command.Parameters.AddWithValue("$welcomeScreenJson", OrNull(guild.WelcomeScreenJson));
            command.Parameters.AddWithValue("$onboardingJson", OrNull(guild.OnboardingJson));
            command.Parameters.AddWithValue("$ownerId", ToDbId(guild.OwnerId));
            command.Parameters.AddWithValue("$description", OrNull(guild.Description));
            command.Parameters.AddWithValue("$splashUrl", OrNull(guild.SplashUrl));
            command.Parameters.AddWithValue(
                "$discoverySplashUrl",
                OrNull(guild.DiscoverySplashUrl)
            );
            await command.ExecuteNonQueryAsync(cancellationToken);

            if (guild.ApproximateMemberCount is not null)
            {
                await using var snapshot = CreateCommand(
                    """
                    INSERT INTO guild_member_count_snapshot (
                        guild_id, snapshot_date, approximate_member_count, recorded_at
                    )
                    VALUES ($guildId, $snapshotDate, $approximateMemberCount, $recordedAt)
                    ON CONFLICT(guild_id, snapshot_date) DO UPDATE SET
                        approximate_member_count = excluded.approximate_member_count,
                        recorded_at = excluded.recorded_at;
                    """
                );
                snapshot.Parameters.AddWithValue("$guildId", ToDbId(guild.Id));
                snapshot.Parameters.AddWithValue(
                    "$snapshotDate",
                    DateOnly
                        .FromDateTime(DateTime.UtcNow)
                        .ToString("O", CultureInfo.InvariantCulture)
                );
                snapshot.Parameters.AddWithValue(
                    "$approximateMemberCount",
                    guild.ApproximateMemberCount.Value
                );
                snapshot.Parameters.AddWithValue(
                    "$recordedAt",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                );
                await snapshot.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertMediaAsync(iconMedia, cancellationToken);
            await InsertMediaAsync(bannerMedia, cancellationToken);

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
                    slowmode_seconds, bitrate, user_limit, permission_overwrites_json,
                    available_tags_json, applied_tags_json,
                    owner_id, message_count, member_count, total_message_sent,
                    auto_archive_duration, archive_timestamp, is_locked, is_invitable,
                    create_timestamp
                ) VALUES (
                    $id, $guildId, $kind, $categoryId, $category, $parentCategoryId,
                    $parentCategory, $name, $topic, $position, $isArchived, $nsfw,
                    $slowmodeSeconds, $bitrate, $userLimit, $permissionOverwritesJson,
                    $availableTagsJson, $appliedTagsJson,
                    $ownerId, $messageCount, $memberCount, $totalMessageSent,
                    $autoArchiveDuration, $archiveTimestamp, $isLocked, $isInvitable,
                    $createTimestamp
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
                    permission_overwrites_json = excluded.permission_overwrites_json,
                    available_tags_json = excluded.available_tags_json,
                    applied_tags_json = excluded.applied_tags_json,
                    owner_id = excluded.owner_id,
                    message_count = excluded.message_count,
                    member_count = excluded.member_count,
                    total_message_sent = excluded.total_message_sent,
                    auto_archive_duration = excluded.auto_archive_duration,
                    archive_timestamp = excluded.archive_timestamp,
                    is_locked = excluded.is_locked,
                    is_invitable = excluded.is_invitable,
                    create_timestamp = excluded.create_timestamp,
                    -- A successful upsert is proof the channel currently exists, so clear any
                    -- stale soft-delete marker.
                    deleted_at = NULL;
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
            command.Parameters.AddWithValue(
                "$availableTagsJson",
                OrNull(channel.AvailableTagsJson)
            );
            command.Parameters.AddWithValue("$appliedTagsJson", OrNull(channel.AppliedTagsJson));
            command.Parameters.AddWithValue("$ownerId", ToDbId(channel.OwnerId));
            command.Parameters.AddWithValue("$messageCount", OrNull(channel.MessageCount));
            command.Parameters.AddWithValue("$memberCount", OrNull(channel.MemberCount));
            command.Parameters.AddWithValue("$totalMessageSent", OrNull(channel.TotalMessageSent));
            command.Parameters.AddWithValue(
                "$autoArchiveDuration",
                OrNull(channel.AutoArchiveDuration)
            );
            command.Parameters.AddWithValue(
                "$archiveTimestamp",
                OrNull(channel.ArchiveTimestamp?.ToString("O", CultureInfo.InvariantCulture))
            );
            command.Parameters.AddWithValue("$isLocked", channel.IsLocked ? 1 : 0);
            command.Parameters.AddWithValue("$isInvitable", channel.IsInvitable ? 1 : 0);
            command.Parameters.AddWithValue(
                "$createTimestamp",
                OrNull(channel.CreateTimestamp?.ToString("O", CultureInfo.InvariantCulture))
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
        CancellationToken cancellationToken = default,
        // Only passed (non-null) when this completion was a --force-full-scan run, so a normal
        // incremental run never touches (and can't accidentally clear) the force-scan watermark.
        Snowflake? forceScannedMessageId = null
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
                    last_export_before = $lastExportBefore,
                    force_scanned_message_id = COALESCE(
                        $forceScannedMessageId,
                        force_scanned_message_id
                    )
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
            command.Parameters.AddWithValue(
                "$forceScannedMessageId",
                ToDbId(forceScannedMessageId)
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

    // Advances a channel's stored last_message_id straight from a live gateway message, skipping
    // the full export bookkeeping. The guard only ever moves the cursor FORWARD, so a duplicate or
    // out-of-order delivery can't rewind it. Keeping the cursor current between debounced exports
    // means the next debounced re-export's GetMessages(after=cursor) finds nothing new -- so it no
    // longer re-fetches recent messages just to re-enrich their authors/reactors over REST. On a
    // reconnect the gap is still backfilled correctly, because messages that arrived while
    // disconnected have higher ids than this cursor and catch-up resumes from it.
    public async ValueTask AdvanceChannelCursorAsync(
        Snowflake channelId,
        Snowflake messageId,
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
                SET last_message_id = $messageId
                WHERE id = $id
                    AND (last_message_id IS NULL OR $messageId > last_message_id);
                """
            );
            command.Parameters.AddWithValue("$messageId", ToDbId(messageId));
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
        Snowflake? LastExportBefore,
        Snowflake? ForceScannedMessageId
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
                       parent_category, last_message_id, last_export_after, last_export_before,
                       force_scanned_message_id
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
                reader.IsDBNull(9) ? null : FromDbId(reader.GetInt64(9)),
                reader.IsDBNull(10) ? null : FromDbId(reader.GetInt64(10))
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
    ) =>
        UpsertUserCoreAsync(
            user.Id,
            DatabaseJson.MapUser(user, member, roles),
            member is not null,
            cancellationToken
        );

    // Used by the live-export path, which resolves roles off an ExportContext and passes the
    // whole Member through (rather than two loose nickname/avatar strings) so joined_at/
    // premium_since/communication_disabled_until/pending are available at the same call site.
    public ValueTask UpsertUserAsync(
        User user,
        Member? member,
        IReadOnlyList<Role> roles,
        CancellationToken cancellationToken = default
    ) =>
        UpsertUserCoreAsync(
            user.Id,
            DatabaseJson.MapUser(user, member, roles),
            member is not null,
            cancellationToken
        );

    // hasMemberInfo says whether the caller actually had this user's guild-member data (nick,
    // roles, join date, ...). When false (e.g. the live fast-path upserting a *mentioned* user it
    // only saw a bare user object for), the member-derived columns are LEFT AS-IS on an existing
    // row instead of being clobbered with empty/null -- otherwise a mention would wipe out the
    // richer row a prior real member sighting had stored. On a brand-new row there's nothing to
    // preserve, so the (empty) values are inserted as-is either way.
    private async ValueTask UpsertUserCoreAsync(
        Snowflake userId,
        UserDto dto,
        bool hasMemberInfo,
        CancellationToken cancellationToken
    )
    {
        var avatarMedia = await TryDownloadMediaAsync(
            "user",
            userId,
            "avatars",
            dto.AvatarUrl,
            keepHistory: false,
            cancellationToken
        );
        var bannerMedia = await TryDownloadMediaAsync(
            "user",
            userId,
            "user-banners",
            dto.BannerUrl,
            keepHistory: false,
            cancellationToken
        );

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO "user" (
                    id, is_bot, discriminator, name, display_name, color, avatar_url, roles_json,
                    joined_at, premium_since, communication_disabled_until, pending,
                    banner_url, accent_color
                ) VALUES (
                    $id, $isBot, $discriminator, $name, $displayName, $color, $avatarUrl, $rolesJson,
                    $joinedAt, $premiumSince, $communicationDisabledUntil, $pending,
                    $bannerUrl, $accentColor
                )
                ON CONFLICT(id) DO UPDATE SET
                    -- Always-current user-level fields.
                    is_bot = excluded.is_bot,
                    discriminator = excluded.discriminator,
                    name = excluded.name,
                    banner_url = excluded.banner_url,
                    accent_color = excluded.accent_color,
                    -- Member-derived fields: only overwrite when the caller actually had member
                    -- info; otherwise keep whatever a previous real sighting stored.
                    display_name = CASE WHEN $hasMember = 1 THEN excluded.display_name ELSE "user".display_name END,
                    color = CASE WHEN $hasMember = 1 THEN excluded.color ELSE "user".color END,
                    avatar_url = CASE WHEN $hasMember = 1 THEN excluded.avatar_url ELSE "user".avatar_url END,
                    roles_json = CASE WHEN $hasMember = 1 THEN excluded.roles_json ELSE "user".roles_json END,
                    joined_at = CASE WHEN $hasMember = 1 THEN excluded.joined_at ELSE "user".joined_at END,
                    premium_since = CASE WHEN $hasMember = 1 THEN excluded.premium_since ELSE "user".premium_since END,
                    communication_disabled_until = CASE WHEN $hasMember = 1 THEN excluded.communication_disabled_until ELSE "user".communication_disabled_until END,
                    pending = CASE WHEN $hasMember = 1 THEN excluded.pending ELSE "user".pending END;
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
            command.Parameters.AddWithValue("$bannerUrl", OrNull(dto.BannerUrl));
            command.Parameters.AddWithValue("$accentColor", OrNull(dto.AccentColor));
            command.Parameters.AddWithValue("$hasMember", hasMemberInfo ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await InsertMediaAsync(avatarMedia, cancellationToken);
            await InsertMediaAsync(bannerMedia, cancellationToken);

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
        var attachmentMedia = new List<PendingMedia>();
        foreach (var attachment in message.Attachments)
        {
            if (
                await TryDownloadMediaAsync(
                    "attachment",
                    attachment.Id,
                    "attachments",
                    attachment.Url,
                    keepHistory: false,
                    cancellationToken
                ) is
                { } media
            )
            {
                attachmentMedia.Add(media);
            }
        }

        if (message.ForwardedMessage is { } forwarded)
        {
            foreach (var attachment in forwarded.Attachments)
            {
                if (
                    await TryDownloadMediaAsync(
                        "attachment",
                        attachment.Id,
                        "attachments",
                        attachment.Url,
                        keepHistory: false,
                        cancellationToken
                    ) is
                    { } media
                )
                {
                    attachmentMedia.Add(media);
                }
            }
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);
            await UpsertMessageCoreAsync(channelId, message, cancellationToken);
            foreach (var media in attachmentMedia)
                await InsertMediaAsync(media, cancellationToken);
            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask InsertPollVoteEventAsync(
        Snowflake channelId,
        Snowflake messageId,
        int answerId,
        Snowflake userId,
        bool isAdded,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO poll_vote_event (
                    message_id, channel_id, answer_id, user_id, is_added, recorded_at
                ) VALUES (
                    $messageId, $channelId, $answerId, $userId, $isAdded, $recordedAt
                );
                """
            );
            command.Parameters.AddWithValue("$messageId", ToDbId(messageId));
            command.Parameters.AddWithValue("$channelId", ToDbId(channelId));
            command.Parameters.AddWithValue("$answerId", answerId);
            command.Parameters.AddWithValue("$userId", ToDbId(userId));
            command.Parameters.AddWithValue("$isAdded", isAdded ? 1 : 0);
            command.Parameters.AddWithValue(
                "$recordedAt",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            );
            await command.ExecuteNonQueryAsync(cancellationToken);

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
            _dataOptions.CaptureEmbeds && message.Embeds.Count > 0
                ? DatabaseJson.SerializeEmbeds(
                    message.Embeds.Select(DatabaseJson.MapEmbed).ToArray()
                )
                : null;

        var stickersJson =
            _dataOptions.CaptureStickers && message.Stickers.Count > 0
                ? DatabaseJson.SerializeStickers(
                    message.Stickers.Select(DatabaseJson.MapSticker).ToArray()
                )
                : null;

        var inlineEmojis = DatabaseJson.ExtractInlineEmojis(message.Content);
        var inlineEmojisJson =
            inlineEmojis.Count > 0 ? DatabaseJson.SerializeInlineEmojis(inlineEmojis) : null;

        var pollJson =
            _dataOptions.CapturePolls && message.Poll is { } poll
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
                    inline_emojis_json, webhook_id, poll_json, components_json, components_raw_json
                ) VALUES (
                    $id, $channelId, $authorId, $kind, $timestamp, $editedTimestamp,
                    $callEndedTimestamp, $isPinned, $content, $referenceJson,
                    $forwardedMessageJson, $interactionJson, $embedsJson, $stickersJson,
                    $inlineEmojisJson, $webhookId, $pollJson, $componentsJson, $componentsRawJson
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
                    components_json = excluded.components_json,
                    components_raw_json = excluded.components_raw_json;
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
            command.Parameters.AddWithValue(
                "$componentsRawJson",
                OrNull(message.ComponentsRawJson)
            );
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

        // The DELETE above always runs (clears stale rows); the insert is skipped when reactions
        // are disabled, so the message keeps no reaction rows at all.
        var reactions = _dataOptions.CaptureReactions ? message.Reactions : [];
        foreach (var reaction in reactions)
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

        await using (
            var deleteRoleMentions = CreateCommand(
                "DELETE FROM message_role_mention WHERE message_id = $id;"
            )
        )
        {
            deleteRoleMentions.Parameters.AddWithValue("$id", messageIdDb);
            await deleteRoleMentions.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var roleId in message.MentionedRoleIds)
        {
            await using var insertRoleMention = CreateCommand(
                "INSERT OR IGNORE INTO message_role_mention (message_id, role_id) VALUES ($messageId, $roleId);"
            );
            insertRoleMention.Parameters.AddWithValue("$messageId", messageIdDb);
            insertRoleMention.Parameters.AddWithValue("$roleId", ToDbId(roleId));
            await insertRoleMention.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (
            var deleteChannelMentions = CreateCommand(
                "DELETE FROM message_channel_mention WHERE message_id = $id;"
            )
        )
        {
            deleteChannelMentions.Parameters.AddWithValue("$id", messageIdDb);
            await deleteChannelMentions.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var mentionedChannelId in message.MentionedChannelIds)
        {
            await using var insertChannelMention = CreateCommand(
                "INSERT OR IGNORE INTO message_channel_mention (message_id, channel_id) VALUES ($messageId, $channelId);"
            );
            insertChannelMention.Parameters.AddWithValue("$messageId", messageIdDb);
            insertChannelMention.Parameters.AddWithValue("$channelId", ToDbId(mentionedChannelId));
            await insertChannelMention.ExecuteNonQueryAsync(cancellationToken);
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

    // Shared by the soft-delete diff passes in SyncGuildAsync (roles/emoji/stickers don't get a
    // channel-style live existence check, and emoji/stickers have no granular delete event at
    // all -- Discord only ever sends the full current list via GUILD_EMOJIS_UPDATE/
    // GUILD_STICKERS_UPDATE) and by the live GUILD_ROLE_DELETE/CHANNEL_DELETE/THREAD_DELETE
    // gateway handlers in WatchGuildCommand, which already know the specific id and can mark it
    // immediately without waiting for the next full sync.
    private async ValueTask<bool> MarkDeletedAsync(
        string table,
        Snowflake id,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                $"""
                UPDATE {table} SET deleted_at = $deletedAt
                WHERE id = $id AND deleted_at IS NULL;
                """
            );
            command.Parameters.AddWithValue(
                "$deletedAt",
                deletedAt.ToString("O", CultureInfo.InvariantCulture)
            );
            command.Parameters.AddWithValue("$id", ToDbId(id));
            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);

            return rowsAffected > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    // `table` is always one of the four fixed literals below (never user input), so building
    // the SQL via interpolation here is safe -- SQLite has no parameter placeholder for table
    // names.
    public ValueTask<bool> MarkChannelDeletedAsync(
        Snowflake channelId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default
    ) => MarkDeletedAsync("channel", channelId, deletedAt, cancellationToken);

    public ValueTask<bool> MarkRoleDeletedAsync(
        Snowflake roleId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default
    ) => MarkDeletedAsync("role", roleId, deletedAt, cancellationToken);

    public ValueTask<bool> MarkGuildEmojiDeletedAsync(
        Snowflake emojiId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default
    ) => MarkDeletedAsync("guild_emoji", emojiId, deletedAt, cancellationToken);

    public ValueTask<bool> MarkGuildStickerDeletedAsync(
        Snowflake stickerId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default
    ) => MarkDeletedAsync("guild_sticker", stickerId, deletedAt, cancellationToken);

    // Used by SyncGuildAsync's deletion-diff safety net: fetch the ids we currently believe are
    // still active for a guild, so anything missing from a fresh live listing can be marked
    // deleted. `table` and `extraWhere` are always fixed literals from the callers below, never
    // user input.
    private async ValueTask<HashSet<Snowflake>> GetActiveIdsAsync(
        string table,
        Snowflake guildId,
        string? extraWhere,
        CancellationToken cancellationToken
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                $"""
                SELECT id FROM {table}
                WHERE guild_id = $guildId AND deleted_at IS NULL {extraWhere};
                """
            );
            command.Parameters.AddWithValue("$guildId", ToDbId(guildId));

            var ids = new HashSet<Snowflake>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(FromDbId(reader.GetInt64(0)));

            return ids;
        }
        finally
        {
            _lock.Release();
        }
    }

    public ValueTask<HashSet<Snowflake>> GetActiveRoleIdsAsync(
        Snowflake guildId,
        CancellationToken cancellationToken = default
    ) => GetActiveIdsAsync("role", guildId, null, cancellationToken);

    public ValueTask<HashSet<Snowflake>> GetActiveGuildEmojiIdsAsync(
        Snowflake guildId,
        CancellationToken cancellationToken = default
    ) => GetActiveIdsAsync("guild_emoji", guildId, null, cancellationToken);

    public ValueTask<HashSet<Snowflake>> GetActiveGuildStickerIdsAsync(
        Snowflake guildId,
        CancellationToken cancellationToken = default
    ) => GetActiveIdsAsync("guild_sticker", guildId, null, cancellationToken);

    // Every active non-thread channel (categories, forums, and regular text/voice/etc.), matching
    // what GetGuildChannelsAsync returns. Threads are excluded because that endpoint never returns
    // them, so a deletion diff based on it would otherwise sweep every thread up as "deleted".
    // Used by SyncGuildAsync's channel reconciliation as the "still active" baseline to diff the
    // live channel list against.
    public ValueTask<HashSet<Snowflake>> GetActiveChannelIdsAsync(
        Snowflake guildId,
        CancellationToken cancellationToken = default
    ) =>
        GetActiveIdsAsync(
            "channel",
            guildId,
            "AND kind NOT IN ('GuildNewsThread', 'GuildPublicThread', 'GuildPrivateThread')",
            cancellationToken
        );

    // Bulk map of channel id -> stored last_message_id cursor for a guild (only channels that have
    // one). The startup pins reconciliation uses this to decide which channels are "active" (their
    // live last_message_id is ahead of what we last stored) and therefore worth a pin re-fetch.
    public async ValueTask<Dictionary<Snowflake, Snowflake>> GetChannelLastMessageIdsAsync(
        Snowflake guildId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                "SELECT id, last_message_id FROM channel "
                    + "WHERE guild_id = $guildId AND last_message_id IS NOT NULL;"
            );
            command.Parameters.AddWithValue("$guildId", ToDbId(guildId));

            var result = new Dictionary<Snowflake, Snowflake>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result[FromDbId(reader.GetInt64(0))] = FromDbId(reader.GetInt64(1));

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    // The set of message ids currently marked pinned in a channel. The startup pins reconciliation
    // diffs this DB baseline against the live pinned list so it can patch only the messages whose
    // pinned state actually changed while the watcher was offline.
    public async ValueTask<HashSet<Snowflake>> GetPinnedMessageIdsAsync(
        Snowflake channelId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                "SELECT id FROM message "
                    + "WHERE channel_id = $channelId AND is_pinned = 1 AND deleted_at IS NULL;"
            );
            command.Parameters.AddWithValue("$channelId", ToDbId(channelId));

            var ids = new HashSet<Snowflake>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(FromDbId(reader.GetInt64(0)));

            return ids;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Messages whose stored poll has not been finalized but whose expiry has already passed -- i.e.
    // polls that concluded (possibly while the watcher was offline) but whose final results we never
    // captured. The startup poll reconciliation re-fetches each of these once to pull in the
    // finalized counts. Already-finalized and still-open polls are skipped. Backed by the partial
    // index message_poll (Schema.V15) so this doesn't scan the whole message table.
    public async ValueTask<
        IReadOnlyList<(Snowflake ChannelId, Snowflake MessageId)>
    > GetUnfinalizedExpiredPollsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                "SELECT channel_id, id, poll_json FROM message "
                    + "WHERE poll_json IS NOT NULL AND deleted_at IS NULL;"
            );

            var result = new List<(Snowflake, Snowflake)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                PollDto? poll;
                try
                {
                    poll = JsonSerializer.Deserialize(
                        reader.GetString(2),
                        DatabaseJsonContext.Default.PollDto
                    );
                }
                catch (JsonException)
                {
                    // A malformed poll_json row shouldn't abort the whole reconciliation.
                    continue;
                }

                if (poll is null || poll.IsFinalized)
                    continue;
                if (poll.Expiry is not { } expiry || expiry > now)
                    continue;

                result.Add((FromDbId(reader.GetInt64(0)), FromDbId(reader.GetInt64(1))));
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    // A recorded window of channel history that was never captured live (see Schema.V16).
    public sealed record MessageGap(
        long Id,
        Snowflake ChannelId,
        Snowflake AfterMessageId,
        Snowflake BeforeMessageId
    );

    // Records a downtime gap for a channel: the (after, before] range that existed at reconnect but
    // was never captured because catch-up was off. Coalesces with an existing unfilled gap starting
    // at the same cursor -- repeated reconnects during one downtime-off stretch keep the same
    // `after` and only push `before` forward -- so this never accumulates duplicate rows.
    public async ValueTask RecordMessageGapAsync(
        Snowflake channelId,
        Snowflake afterMessageId,
        Snowflake beforeMessageId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO message_gap (
                    channel_id, after_message_id, before_message_id, detected_at
                )
                SELECT $channelId, $after, $before, $detectedAt
                WHERE NOT EXISTS (
                    SELECT 1 FROM message_gap
                    WHERE channel_id = $channelId
                      AND after_message_id = $after
                      AND filled_at IS NULL
                );

                UPDATE message_gap
                SET before_message_id = $before, detected_at = $detectedAt
                WHERE channel_id = $channelId
                  AND after_message_id = $after
                  AND filled_at IS NULL
                  AND before_message_id < $before;
                """
            );
            command.Parameters.AddWithValue("$channelId", ToDbId(channelId));
            command.Parameters.AddWithValue("$after", ToDbId(afterMessageId));
            command.Parameters.AddWithValue("$before", ToDbId(beforeMessageId));
            command.Parameters.AddWithValue(
                "$detectedAt",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            );
            await command.ExecuteNonQueryAsync(cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Unfilled downtime gaps, optionally restricted to one guild. Consumed by the fillgaps command.
    public async ValueTask<IReadOnlyList<MessageGap>> GetUnfilledGapsAsync(
        Snowflake? guildId = null,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(
                guildId is null
                    ? """
                    SELECT id, channel_id, after_message_id, before_message_id
                    FROM message_gap
                    WHERE filled_at IS NULL
                    ORDER BY id;
                    """
                    : """
                    SELECT g.id, g.channel_id, g.after_message_id, g.before_message_id
                    FROM message_gap g
                    JOIN channel c ON c.id = g.channel_id
                    WHERE g.filled_at IS NULL AND c.guild_id = $guildId
                    ORDER BY g.id;
                    """
            );
            if (guildId is { } gid)
                command.Parameters.AddWithValue("$guildId", ToDbId(gid));

            var result = new List<MessageGap>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(
                    new MessageGap(
                        reader.GetInt64(0),
                        FromDbId(reader.GetInt64(1)),
                        FromDbId(reader.GetInt64(2)),
                        FromDbId(reader.GetInt64(3))
                    )
                );

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask MarkGapFilledAsync(
        long gapId,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);
            await using var command = CreateCommand(
                "UPDATE message_gap SET filled_at = $filledAt WHERE id = $id;"
            );
            command.Parameters.AddWithValue(
                "$filledAt",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            );
            command.Parameters.AddWithValue("$id", gapId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Sets just the last_export_after/before bookkeeping on a channel without touching its cursor.
    // fillgaps uses this to undo the export window a bounded backfill leaves behind, so a later
    // catch-up doesn't mistake the channel for one that needs a full rescan.
    public async ValueTask SetChannelExportWindowAsync(
        Snowflake channelId,
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
                SET last_export_after = $after, last_export_before = $before
                WHERE id = $id;
                """
            );
            command.Parameters.AddWithValue("$after", ToDbId(lastExportAfter));
            command.Parameters.AddWithValue("$before", ToDbId(lastExportBefore));
            command.Parameters.AddWithValue("$id", ToDbId(channelId));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async ValueTask UpsertThreadMemberCoreAsync(
        Snowflake channelId,
        ThreadMember member,
        CancellationToken cancellationToken
    )
    {
        await using var command = CreateCommand(
            """
            INSERT INTO thread_member (channel_id, user_id, join_timestamp, flags)
            VALUES ($channelId, $userId, $joinTimestamp, $flags)
            ON CONFLICT(channel_id, user_id) DO UPDATE SET
                join_timestamp = excluded.join_timestamp,
                flags = excluded.flags;
            """
        );
        command.Parameters.AddWithValue("$channelId", ToDbId(channelId));
        command.Parameters.AddWithValue("$userId", ToDbId(member.UserId));
        command.Parameters.AddWithValue(
            "$joinTimestamp",
            member.JoinTimestamp.ToString("O", CultureInfo.InvariantCulture)
        );
        command.Parameters.AddWithValue("$flags", member.Flags);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Full-list replace (delete then re-insert everything currently returned by the API),
    // mirroring the same "no granular per-item event, so diff/replace the whole catalog"
    // approach used for guild emoji/stickers. Called whenever a thread gets (re-)exported, so
    // the list stays fresh on every normal/force-scan/catch-up pass without needing its own
    // separate backfill job.
    public async ValueTask UpsertThreadMembersAsync(
        Snowflake channelId,
        IReadOnlyList<ThreadMember> members,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using (
                var delete = CreateCommand(
                    "DELETE FROM thread_member WHERE channel_id = $channelId;"
                )
            )
            {
                delete.Parameters.AddWithValue("$channelId", ToDbId(channelId));
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var member in members)
                await UpsertThreadMemberCoreAsync(channelId, member, cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Incremental counterparts driven directly by THREAD_MEMBERS_UPDATE's added_members/
    // removed_member_ids, so a live join/leave doesn't require re-fetching the whole thread's
    // member list.
    public async ValueTask AddThreadMembersAsync(
        Snowflake channelId,
        IReadOnlyList<ThreadMember> members,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            foreach (var member in members)
                await UpsertThreadMemberCoreAsync(channelId, member, cancellationToken);

            await MaybeAutoFlushAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask RemoveThreadMembersAsync(
        Snowflake channelId,
        IReadOnlyList<Snowflake> userIds,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            foreach (var userId in userIds)
            {
                await using var command = CreateCommand(
                    "DELETE FROM thread_member WHERE channel_id = $channelId AND user_id = $userId;"
                );
                command.Parameters.AddWithValue("$channelId", ToDbId(channelId));
                command.Parameters.AddWithValue("$userId", ToDbId(userId));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await MaybeAutoFlushAsync(cancellationToken);
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
        var iconMedia = await TryDownloadMediaAsync(
            "role",
            role.Id,
            "role-icons",
            role.IconUrl,
            keepHistory: true,
            cancellationToken
        );

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO role (
                    id, guild_id, name, color, secondary_color, tertiary_color, position,
                    permissions, hoist, mentionable, icon_url, unicode_emoji, managed
                ) VALUES (
                    $id, $guildId, $name, $color, $secondaryColor, $tertiaryColor, $position,
                    $permissions, $hoist, $mentionable, $iconUrl, $unicodeEmoji, $managed
                )
                ON CONFLICT(id) DO UPDATE SET
                    guild_id = excluded.guild_id,
                    name = excluded.name,
                    color = excluded.color,
                    secondary_color = excluded.secondary_color,
                    tertiary_color = excluded.tertiary_color,
                    position = excluded.position,
                    permissions = excluded.permissions,
                    hoist = excluded.hoist,
                    mentionable = excluded.mentionable,
                    icon_url = excluded.icon_url,
                    unicode_emoji = excluded.unicode_emoji,
                    managed = excluded.managed,
                    -- A successful upsert is proof the role currently exists, so clear any
                    -- stale soft-delete marker (e.g. from a diff pass that ran during a
                    -- transient API hiccup).
                    deleted_at = NULL;
                """
            );
            command.Parameters.AddWithValue("$id", ToDbId(role.Id));
            command.Parameters.AddWithValue("$guildId", ToDbId(guildId));
            command.Parameters.AddWithValue("$name", role.Name);
            command.Parameters.AddWithValue(
                "$color",
                OrNull(role.Color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null)
            );
            command.Parameters.AddWithValue(
                "$secondaryColor",
                OrNull(role.SecondaryColor is { } sc ? $"#{sc.R:X2}{sc.G:X2}{sc.B:X2}" : null)
            );
            command.Parameters.AddWithValue(
                "$tertiaryColor",
                OrNull(role.TertiaryColor is { } tc ? $"#{tc.R:X2}{tc.G:X2}{tc.B:X2}" : null)
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

            await InsertMediaAsync(iconMedia, cancellationToken);

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
        var imageMedia = await TryDownloadMediaAsync(
            "guild_emoji",
            emoji.Id,
            "emojis",
            emoji.ImageUrl,
            keepHistory: true,
            cancellationToken
        );

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTransactionAsync(cancellationToken);

            await using var command = CreateCommand(
                """
                INSERT INTO guild_emoji (
                    id, guild_id, name, is_animated, image_url, creator_id, is_available,
                    is_managed, role_ids_json
                ) VALUES (
                    $id, $guildId, $name, $isAnimated, $imageUrl, $creatorId, $isAvailable,
                    $isManaged, $roleIdsJson
                )
                ON CONFLICT(id) DO UPDATE SET
                    guild_id = excluded.guild_id,
                    name = excluded.name,
                    is_animated = excluded.is_animated,
                    image_url = excluded.image_url,
                    creator_id = excluded.creator_id,
                    is_available = excluded.is_available,
                    is_managed = excluded.is_managed,
                    role_ids_json = excluded.role_ids_json,
                    deleted_at = NULL;
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
            command.Parameters.AddWithValue("$roleIdsJson", OrNull(emoji.RoleIdsJson));
            await command.ExecuteNonQueryAsync(cancellationToken);

            await InsertMediaAsync(imageMedia, cancellationToken);

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
        var imageMedia = await TryDownloadMediaAsync(
            "guild_sticker",
            sticker.Id,
            "stickers",
            sticker.SourceUrl,
            keepHistory: true,
            cancellationToken
        );

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
                    is_available = excluded.is_available,
                    deleted_at = NULL;
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

            await InsertMediaAsync(imageMedia, cancellationToken);

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
