using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class MessageIndexSpecs
{
    private static JsonObject BuildMessage(string id) =>
        new()
        {
            ["id"] = id,
            ["type"] = "Default",
            ["timestamp"] = "2023-06-15T12:00:00+00:00",
            ["timestampEdited"] = null,
            ["callEndedTimestamp"] = null,
            ["isPinned"] = false,
            ["content"] = $"message {id}",
            ["author"] = new JsonObject
            {
                ["id"] = "900",
                ["name"] = "alice",
                ["discriminator"] = "0000",
                ["nickname"] = "Alice",
                ["color"] = null,
                ["isBot"] = false,
                ["roles"] = new JsonArray(),
                ["avatarUrl"] = "https://cdn.discordapp.com/embed/avatars/1.png",
            },
            ["attachments"] = new JsonArray(),
            ["embeds"] = new JsonArray(),
            ["stickers"] = new JsonArray(),
            ["reactions"] = new JsonArray(),
            ["mentions"] = new JsonArray(),
        };

    private static string BuildExportJson(params string[] messageIds)
    {
        var root = new JsonObject
        {
            ["guild"] = new JsonObject
            {
                ["id"] = "100",
                ["name"] = "Guild",
                ["iconUrl"] = "https://cdn.discordapp.com/embed/avatars/0.png",
            },
            ["channel"] = new JsonObject
            {
                ["id"] = "555",
                ["type"] = "GuildTextChat",
                ["categoryId"] = "200",
                ["category"] = "Category",
                ["name"] = "channel",
                ["topic"] = "Some topic",
                ["iconUrl"] = null,
            },
            ["dateRange"] = new JsonObject { ["after"] = null, ["before"] = null },
            ["exportedAt"] = "2023-06-15T13:00:00+00:00",
            ["messages"] = new JsonArray(
                messageIds.Select(id => (JsonNode)BuildMessage(id)).ToArray()
            ),
            ["messageCount"] = messageIds.Length,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    [Fact]
    public async Task I_can_save_and_load_an_index_round_trip()
    {
        // Arrange
        using var file = TempFile.Create();
        var index = new MessageIndex
        {
            Partitions =
            [
                new PartitionIndexEntry
                {
                    Index = 0,
                    MinMessageId = "100",
                    MaxMessageId = "500",
                    Checkpoints = [new CheckpointEntry { MessageId = "100", ByteOffset = 42 }],
                },
            ],
        };

        // Act
        var saveError = await index.SaveAsync(file.Path);
        var loaded = await MessageIndex.LoadAsync(file.Path);

        // Assert
        saveError.Should().BeNull();
        loaded.Partitions.Should().HaveCount(1);
        loaded.Partitions[0].MinMessageId.Should().Be("100");
        loaded.Partitions[0].MaxMessageId.Should().Be("500");
        loaded.Partitions[0].Checkpoints.Should().ContainSingle(c => c.ByteOffset == 42);
    }

    [Fact]
    public async Task Loading_a_missing_index_file_returns_an_empty_index()
    {
        var index = await MessageIndex.LoadAsync(
            "/nonexistent/path/that/does/not/exist.index.json"
        );
        index.Partitions.Should().BeEmpty();
    }

    [Fact]
    public void FindPartitionForMessage_returns_null_when_the_id_is_outside_every_range()
    {
        var index = new MessageIndex
        {
            Partitions =
            [
                new PartitionIndexEntry
                {
                    Index = 0,
                    MinMessageId = "1000",
                    MaxMessageId = "2000",
                },
            ],
        };

        index.FindPartitionForMessage(Snowflake.Parse("500")).Should().BeNull();
        index.FindPartitionForMessage(Snowflake.Parse("2500")).Should().BeNull();
        index.FindPartitionForMessage(Snowflake.Parse("1500")).Should().NotBeNull();
    }

    [Fact]
    public void FindNearestCheckpointBefore_returns_zero_when_the_partition_has_no_checkpoints()
    {
        var partition = new PartitionIndexEntry { Index = 0 };

        MessageIndex
            .FindNearestCheckpointBefore(partition, Snowflake.Parse("12345"))
            .Should()
            .Be(0);
    }

    [Fact]
    public void FindNearestCheckpointBefore_picks_the_closest_checkpoint_at_or_before_the_target()
    {
        var partition = new PartitionIndexEntry
        {
            Index = 0,
            Checkpoints =
            [
                new CheckpointEntry { MessageId = "100", ByteOffset = 10 },
                new CheckpointEntry { MessageId = "600", ByteOffset = 6000 },
                new CheckpointEntry { MessageId = "1100", ByteOffset = 12000 },
            ],
        };

        MessageIndex
            .FindNearestCheckpointBefore(partition, Snowflake.Parse("50"))
            .Should()
            .Be(0, "the target is before every checkpoint");
        MessageIndex
            .FindNearestCheckpointBefore(partition, Snowflake.Parse("650"))
            .Should()
            .Be(6000);
        MessageIndex
            .FindNearestCheckpointBefore(partition, Snowflake.Parse("999999"))
            .Should()
            .Be(12000, "the target is after every checkpoint -- use the last one");
    }

    [Fact]
    public async Task LoadOrBuildAsync_lazily_builds_an_accurate_index_for_a_partition_with_no_persisted_index()
    {
        // Arrange: a plain export file with no ".index.json" sidecar at all -- simulating an
        // archive from before this feature existed.
        using var dir = TempDirectory.Create();
        var baseFilePath = Path.Combine(dir.Path, "export.json");

        var messageIds = Enumerable.Range(1, 1200).Select(i => (1000 + i).ToString()).ToArray();
        await File.WriteAllTextAsync(baseFilePath, BuildExportJson(messageIds));

        // Act
        var index = await MessageIndex.LoadOrBuildAsync(baseFilePath);

        // Assert
        index.Partitions.Should().ContainSingle();
        var partition = index.Partitions[0];
        partition.MinMessageId.Should().Be(messageIds[0]);
        partition.MaxMessageId.Should().Be(messageIds[^1]);
        // 1200 messages, checkpoint every 500 (at #1, #501, #1001) => 3 checkpoints.
        partition.Checkpoints.Should().HaveCount(3);

        // The index should now be persisted, so a second call doesn't need to rescan.
        File.Exists(MessageIndex.GetIndexFilePath(baseFilePath)).Should().BeTrue();

        // Ground truth: each checkpoint should point at the exact byte where that message's
        // object starts in the real file.
        var fileBytes = await File.ReadAllBytesAsync(baseFilePath);
        foreach (var checkpoint in partition.Checkpoints)
        {
            fileBytes[checkpoint.ByteOffset].Should().Be((byte)'{');

            var reader = new Utf8JsonReader(fileBytes.AsSpan((int)checkpoint.ByteOffset));
            reader.Read(); // StartObject
            reader.Read(); // PropertyName "id"
            reader.Read(); // the id's string value
            reader.GetString().Should().Be(checkpoint.MessageId);
        }
    }
}
