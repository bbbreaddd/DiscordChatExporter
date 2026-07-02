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
}
