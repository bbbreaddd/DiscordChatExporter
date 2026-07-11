using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Database;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class DatabaseMediaSpecs
{
    private static string GetMediaRelativePath(string assetKind, string url)
    {
        var fileName = ExportAssetDownloader.GetFileNameFromUrl(url);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var hashStart = nameWithoutExtension.LastIndexOf('-') + 1;
        var hash = hashStart > 0 ? nameWithoutExtension[hashStart..] : nameWithoutExtension;

        if (hash.Length < 4)
            hash = hash.PadRight(4, '0');

        return Path.Combine(assetKind, hash[..2], hash[2..4], fileName);
    }

    private static void CreateCachedMedia(string mediaDirPath, string assetKind, string url)
    {
        var path = Path.Combine(mediaDirPath, GetMediaRelativePath(assetKind, url));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "cached");
    }

    private static async Task<long> ExecuteScalarLongAsync(string dbPath, string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()
        );
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> ExecuteScalarStringAsync(string dbPath, string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()
        );
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync()) as string;
    }

    private static Message ParseMessage(string attachmentUrl) =>
        Message.Parse(
            JsonDocument
                .Parse(
                    $$"""
                    {
                      "id": "10",
                      "type": 0,
                      "content": "file",
                      "channel_id": "300",
                      "author": {
                        "id": "1",
                        "username": "alice",
                        "discriminator": "0000",
                        "avatar": null
                      },
                      "attachments": [
                        {
                          "id": "20",
                          "url": "{{attachmentUrl}}",
                          "filename": "image.png",
                          "size": 6
                        }
                      ],
                      "embeds": [],
                      "pinned": false,
                      "timestamp": "2023-06-15T12:00:00+00:00"
                    }
                    """
                )
                .RootElement
        );

    private static Message ParseComponentMessage() =>
        Message.Parse(
            JsonDocument
                .Parse(
                    """
                    {
                      "id": "11",
                      "type": 0,
                      "content": "",
                      "channel_id": "300",
                      "author": {
                        "id": "1",
                        "username": "alice",
                        "discriminator": "0000",
                        "avatar": null
                      },
                      "attachments": [],
                      "embeds": [],
                      "pinned": false,
                      "timestamp": "2023-06-15T12:00:00+00:00",
                      "components": [
                        {
                          "type": 10,
                          "content": "rich text the summary parser does not model"
                        }
                      ]
                    }
                    """
                )
                .RootElement
        );

    private static async ValueTask SeedMessageParentsAsync(SqliteExportStore store)
    {
        var guild = new Guild(new Snowflake(100), "Guild", "https://example.com/icon.png");
        var channel = new Channel(
            new Snowflake(300),
            ChannelKind.GuildTextChat,
            guild.Id,
            null,
            "general",
            0,
            null,
            null,
            false,
            new Snowflake(10),
            []
        );
        var user = new User(
            new Snowflake(1),
            false,
            0,
            "alice",
            "alice",
            "https://example.com/avatar.png"
        );

        await store.UpsertGuildAsync(guild);
        await store.UpsertChannelAsync(channel);
        await store.UpsertUserAsync(user, null, Array.Empty<Role>());
    }

    [Fact]
    public async Task Db_stores_message_timestamps_as_epoch_ms_and_normalizes_the_reference()
    {
        using var db = TempFile.Create();

        var message = Message.Parse(
            JsonDocument
                .Parse(
                    """
                    {
                      "id": "50",
                      "type": 0,
                      "content": "reply body",
                      "channel_id": "300",
                      "author": {
                        "id": "1",
                        "username": "alice",
                        "discriminator": "0000",
                        "avatar": null
                      },
                      "attachments": [],
                      "embeds": [],
                      "pinned": false,
                      "timestamp": "2023-06-15T12:00:00+00:00",
                      "edited_timestamp": "2023-06-15T12:05:00+00:00",
                      "message_reference": {
                        "type": 0,
                        "message_id": "40",
                        "channel_id": "300",
                        "guild_id": "100"
                      }
                    }
                    """
                )
                .RootElement
        );

        await using (var store = await SqliteExportStore.OpenAsync(db.Path))
        {
            await SeedMessageParentsAsync(store);
            await store.UpsertMessageAsync(new Snowflake(300), message);
            await store.FlushAsync();
        }

        // V16 stores timestamps as INTEGER epoch-ms (matching DateTimeOffset.ToUnixTimeMilliseconds).
        var expectedTs = new DateTimeOffset(
            2023,
            6,
            15,
            12,
            0,
            0,
            TimeSpan.Zero
        ).ToUnixTimeMilliseconds();
        var expectedEdited = new DateTimeOffset(
            2023,
            6,
            15,
            12,
            5,
            0,
            TimeSpan.Zero
        ).ToUnixTimeMilliseconds();
        (await ExecuteScalarLongAsync(db.Path, "SELECT timestamp FROM message WHERE id = 50;"))
            .Should()
            .Be(expectedTs);
        (
            await ExecuteScalarLongAsync(
                db.Path,
                "SELECT edited_timestamp FROM message WHERE id = 50;"
            )
        )
            .Should()
            .Be(expectedEdited);

        // reference_json was normalized into the ref_* columns.
        (await ExecuteScalarStringAsync(db.Path, "SELECT ref_type FROM message WHERE id = 50;"))
            .Should()
            .Be("Default");
        (await ExecuteScalarLongAsync(db.Path, "SELECT ref_message_id FROM message WHERE id = 50;"))
            .Should()
            .Be(40);
        (await ExecuteScalarLongAsync(db.Path, "SELECT ref_channel_id FROM message WHERE id = 50;"))
            .Should()
            .Be(300);
        (await ExecuteScalarLongAsync(db.Path, "SELECT ref_guild_id FROM message WHERE id = 50;"))
            .Should()
            .Be(100);
    }

    [Fact]
    public async Task Db_vacuum_reclaims_space_and_leaves_a_valid_database()
    {
        using var db = TempFile.Create();

        await using (var store = await SqliteExportStore.OpenAsync(db.Path))
        {
            await SeedMessageParentsAsync(store);
            // Deliberately not flushed first: VacuumAsync must commit the pending write batch
            // itself, since VACUUM cannot run inside a transaction.
            await store.VacuumAsync(incremental: false);
            // Incremental is a no-op unless the DB is in auto_vacuum=INCREMENTAL mode, but it
            // must still run cleanly.
            await store.VacuumAsync(incremental: true);
        }

        // The file is still a valid, current-schema database and the seeded rows survived.
        (await ExecuteScalarLongAsync(db.Path, "PRAGMA user_version;"))
            .Should()
            .Be(16);
        (await ExecuteScalarLongAsync(db.Path, "SELECT COUNT(*) FROM guild WHERE id = 100;"))
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task Db_tracks_guild_boost_status_and_daily_member_count()
    {
        using var db = TempFile.Create();

        var guild = Guild.Parse(
            JsonDocument
                .Parse(
                    """
                    {
                      "id": "100",
                      "name": "Guild",
                      "icon": null,
                      "banner": null,
                      "premium_tier": 2,
                      "premium_subscription_count": 7,
                      "approximate_member_count": 1234
                    }
                    """
                )
                .RootElement
        );

        await using (var store = await SqliteExportStore.OpenAsync(db.Path))
        {
            await store.UpsertGuildAsync(guild);
            await store.UpsertGuildAsync(guild with { ApproximateMemberCount = 1235 });
            await store.FlushAsync();
        }

        (await ExecuteScalarLongAsync(db.Path, "SELECT premium_tier FROM guild WHERE id = 100;"))
            .Should()
            .Be(2);
        (
            await ExecuteScalarLongAsync(
                db.Path,
                "SELECT premium_subscription_count FROM guild WHERE id = 100;"
            )
        )
            .Should()
            .Be(7);
        (
            await ExecuteScalarLongAsync(
                db.Path,
                "SELECT approximate_member_count FROM guild WHERE id = 100;"
            )
        )
            .Should()
            .Be(1235);
        (
            await ExecuteScalarLongAsync(
                db.Path,
                "SELECT COUNT(*) FROM guild_member_count_snapshot WHERE guild_id = 100;"
            )
        )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task Db_media_downloads_are_saved_under_sharded_paths()
    {
        using var db = TempFile.Create();
        using var mediaDir = TempDirectory.Create();
        var attachmentUrl = "https://cdn.discordapp.com/attachments/1/20/image.png?ex=1&is=2&hm=3";
        var expectedLocalPath = GetMediaRelativePath("attachments", attachmentUrl);
        CreateCachedMedia(mediaDir.Path, "attachments", attachmentUrl);

        await using (var store = await SqliteExportStore.OpenAsync(db.Path, mediaDir.Path, default))
        {
            await SeedMessageParentsAsync(store);
            await store.UpsertMessageAsync(new Snowflake(300), ParseMessage(attachmentUrl));
            await store.FlushAsync();
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = db.Path }.ToString()
        );
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT local_path FROM media_asset WHERE asset_kind = 'attachments';";

        (await command.ExecuteScalarAsync()).Should().Be(expectedLocalPath);
    }

    [Fact]
    public async Task Db_media_keeps_history_for_changed_guild_icons()
    {
        using var db = TempFile.Create();
        using var mediaDir = TempDirectory.Create();
        var firstUrl = "https://cdn.discordapp.com/icons/100/first.png?size=512";
        var secondUrl = "https://cdn.discordapp.com/icons/100/second.png?size=512";
        CreateCachedMedia(mediaDir.Path, "guild-icons", firstUrl);
        CreateCachedMedia(mediaDir.Path, "guild-icons", secondUrl);

        await using (var store = await SqliteExportStore.OpenAsync(db.Path, mediaDir.Path, default))
        {
            await store.UpsertGuildAsync(new Guild(new Snowflake(100), "Guild", firstUrl));
            await store.UpsertGuildAsync(new Guild(new Snowflake(100), "Guild", secondUrl));
            await store.FlushAsync();
        }

        (await ExecuteScalarLongAsync(db.Path, "SELECT COUNT(*) FROM media_asset;")).Should().Be(2);
        (
            await ExecuteScalarLongAsync(
                db.Path,
                "SELECT COUNT(*) FROM media_asset WHERE is_current = 1 AND source_url LIKE '%second.png%';"
            )
        )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task Db_media_does_not_store_fallback_avatar_as_guild_icon()
    {
        using var db = TempFile.Create();
        using var mediaDir = TempDirectory.Create();

        await using (var store = await SqliteExportStore.OpenAsync(db.Path, mediaDir.Path, default))
        {
            await store.UpsertGuildAsync(
                new Guild(
                    new Snowflake(100),
                    "Guild",
                    "https://cdn.discordapp.com/embed/avatars/0.png"
                )
            );
            await store.FlushAsync();
        }

        (await ExecuteScalarLongAsync(db.Path, "SELECT COUNT(*) FROM media_asset;")).Should().Be(0);
    }

    [Fact]
    public async Task Db_media_replaces_current_only_assets_without_history()
    {
        using var db = TempFile.Create();
        using var mediaDir = TempDirectory.Create();
        var firstUrl = "https://cdn.discordapp.com/attachments/1/20/image.png?ex=1&is=2&hm=3";
        var secondUrl = "https://cdn.discordapp.com/attachments/1/20/image.png?ex=4&is=5&hm=6";
        CreateCachedMedia(mediaDir.Path, "attachments", firstUrl);
        CreateCachedMedia(mediaDir.Path, "attachments", secondUrl);

        await using (var store = await SqliteExportStore.OpenAsync(db.Path, mediaDir.Path, default))
        {
            await SeedMessageParentsAsync(store);
            await store.UpsertMessageAsync(new Snowflake(300), ParseMessage(firstUrl));
            await store.UpsertMessageAsync(new Snowflake(300), ParseMessage(secondUrl));
            await store.FlushAsync();
        }

        (await ExecuteScalarLongAsync(db.Path, "SELECT COUNT(*) FROM media_asset;")).Should().Be(1);
    }

    [Fact]
    public async Task Db_records_poll_vote_events()
    {
        using var db = TempFile.Create();

        await using (var store = await SqliteExportStore.OpenAsync(db.Path))
        {
            await store.InsertPollVoteEventAsync(
                new Snowflake(300),
                new Snowflake(10),
                3,
                new Snowflake(1),
                true
            );
            await store.FlushAsync();
        }

        (
            await ExecuteScalarLongAsync(
                db.Path,
                "SELECT COUNT(*) FROM poll_vote_event WHERE message_id = 10 AND answer_id = 3 AND user_id = 1 AND is_added = 1;"
            )
        )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task Db_skips_a_media_url_previously_ledgered_as_permanently_gone()
    {
        using var db = TempFile.Create();
        using var mediaDir = TempDirectory.Create();

        // This URL is never cached and points at a real Discord CDN host that this test never
        // actually reaches -- if the ledger gate below did not short-circuit before the network
        // call, this test would hang for minutes retrying a connection failure instead of failing
        // fast, since HTTP connection failures are treated as retryable by Http.ResiliencePipeline.
        var attachmentUrl = "https://cdn.discordapp.com/attachments/1/20/image.png?ex=1&is=2&hm=3";

        await using var store = await SqliteExportStore.OpenAsync(db.Path, mediaDir.Path, default);
        await store.RecordMediaFailureAsync(ExportAssetDownloader.NormalizeUrl(attachmentUrl), 404);

        await SeedMessageParentsAsync(store);
        await store.UpsertMessageAsync(new Snowflake(300), ParseMessage(attachmentUrl));
        await store.FlushAsync();

        (await ExecuteScalarLongAsync(db.Path, "SELECT COUNT(*) FROM media_asset;")).Should().Be(0);
        (await ExecuteScalarLongAsync(db.Path, "SELECT COUNT(*) FROM media_download_failure;"))
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task Db_media_ledger_matches_urls_by_normalized_form_and_tracks_attempt_count()
    {
        using var db = TempFile.Create();
        using var mediaDir = TempDirectory.Create();

        var firstSignature = "https://cdn.discordapp.com/attachments/1/20/image.png?ex=1&is=2&hm=3";
        var reSignedSameAsset =
            "https://cdn.discordapp.com/attachments/1/20/image.png?ex=9&is=8&hm=7";

        await using var store = await SqliteExportStore.OpenAsync(db.Path, mediaDir.Path, default);

        (await store.IsMediaLedgeredAsync(ExportAssetDownloader.NormalizeUrl(firstSignature)))
            .Should()
            .BeFalse();

        await store.RecordMediaFailureAsync(
            ExportAssetDownloader.NormalizeUrl(firstSignature),
            404
        );

        // A freshly re-signed link for the same asset must still match the ledgered entry --
        // otherwise every gateway reconnect would re-attempt (and re-fail) the same dead link.
        (await store.IsMediaLedgeredAsync(ExportAssetDownloader.NormalizeUrl(reSignedSameAsset)))
            .Should()
            .BeTrue();

        await store.RecordMediaFailureAsync(
            ExportAssetDownloader.NormalizeUrl(reSignedSameAsset),
            410
        );
        await store.FlushAsync();

        (await ExecuteScalarLongAsync(db.Path, "SELECT attempts FROM media_download_failure;"))
            .Should()
            .Be(2);
        (await ExecuteScalarLongAsync(db.Path, "SELECT status_code FROM media_download_failure;"))
            .Should()
            .Be(410);
    }

    [Fact]
    public async Task Db_preserves_raw_component_payloads()
    {
        using var db = TempFile.Create();

        await using (var store = await SqliteExportStore.OpenAsync(db.Path))
        {
            await SeedMessageParentsAsync(store);
            await store.UpsertMessageAsync(new Snowflake(300), ParseComponentMessage());
            await store.FlushAsync();
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = db.Path }.ToString()
        );
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT components_raw_json FROM message WHERE id = 11;";

        ((string)(await command.ExecuteScalarAsync())!)
            .Should()
            .Contain("rich text the summary parser does not model");
    }
}
