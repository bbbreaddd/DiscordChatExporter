using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class IncrementalJsonAppenderSpecs
{
    private static JsonObject BuildMessage(string id, string content) =>
        new()
        {
            ["id"] = id,
            ["type"] = "Default",
            ["timestamp"] = "2023-06-15T12:00:00+00:00",
            ["timestampEdited"] = null,
            ["callEndedTimestamp"] = null,
            ["isPinned"] = false,
            ["content"] = content,
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

    private static string BuildExportJson(
        string guildName,
        string channelName,
        string categoryName,
        string channelId,
        params (string Id, string Content)[] messages
    )
    {
        var root = new JsonObject
        {
            ["guild"] = new JsonObject
            {
                ["id"] = "100",
                ["name"] = guildName,
                ["iconUrl"] = "https://cdn.discordapp.com/embed/avatars/0.png",
            },
            ["channel"] = new JsonObject
            {
                ["id"] = channelId,
                ["type"] = "GuildTextChat",
                ["categoryId"] = "200",
                ["category"] = categoryName,
                ["name"] = channelName,
                ["topic"] = "Some topic",
                ["iconUrl"] = null,
            },
            ["dateRange"] = new JsonObject { ["after"] = null, ["before"] = null },
            ["exportedAt"] = "2023-06-15T13:00:00+00:00",
            ["messages"] = new JsonArray(
                messages.Select(m => (JsonNode)BuildMessage(m.Id, m.Content)).ToArray()
            ),
            ["messageCount"] = messages.Length,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    [Fact]
    public async Task Merging_zero_new_messages_still_refreshes_the_header_from_the_fresh_export()
    {
        // Arrange
        using var existingFile = TempFile.Create();
        using var freshTempFile = TempFile.Create();
        using var mergedFile = TempFile.Create();

        await File.WriteAllTextAsync(
            existingFile.Path,
            BuildExportJson(
                "Old Guild",
                "old-channel",
                "Old Category",
                "555",
                ("100", "first message"),
                ("101", "second message")
            )
        );

        // Same channel ID, but guild/category/channel were renamed and there's nothing new to
        // fetch -- this is the "rename only" scenario the merge needs to handle.
        await File.WriteAllTextAsync(
            freshTempFile.Path,
            BuildExportJson("New Guild", "new-channel", "New Category", "555")
        );

        // Act
        await IncrementalJsonAppender.MergeAsync(
            existingFile.Path,
            freshTempFile.Path,
            mergedFile.Path
        );

        // Assert
        var mergedText = await File.ReadAllTextAsync(mergedFile.Path);
        mergedText.Should().Contain("first message");
        mergedText.Should().Contain("second message");

        using var document = JsonDocument.Parse(mergedText);
        var root = document.RootElement;

        root.GetProperty("guild").GetProperty("name").GetString().Should().Be("New Guild");
        root.GetProperty("channel").GetProperty("name").GetString().Should().Be("new-channel");
        root.GetProperty("channel").GetProperty("category").GetString().Should().Be("New Category");
        root.GetProperty("messageCount").GetInt64().Should().Be(2);
        root.GetProperty("messages").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Merging_new_messages_with_renamed_metadata_keeps_old_and_new_messages_in_order()
    {
        // Arrange
        using var existingFile = TempFile.Create();
        using var freshTempFile = TempFile.Create();
        using var mergedFile = TempFile.Create();

        await File.WriteAllTextAsync(
            existingFile.Path,
            BuildExportJson(
                "Old Guild",
                "old-channel",
                "Old Category",
                "555",
                ("100", "first message"),
                ("101", "second message")
            )
        );

        await File.WriteAllTextAsync(
            freshTempFile.Path,
            BuildExportJson(
                "New Guild",
                "new-channel",
                "New Category",
                "555",
                ("102", "third message")
            )
        );

        // Act
        await IncrementalJsonAppender.MergeAsync(
            existingFile.Path,
            freshTempFile.Path,
            mergedFile.Path
        );

        // Assert
        var mergedText = await File.ReadAllTextAsync(mergedFile.Path);

        var firstIndex = mergedText.IndexOf("first message", System.StringComparison.Ordinal);
        var secondIndex = mergedText.IndexOf("second message", System.StringComparison.Ordinal);
        var thirdIndex = mergedText.IndexOf("third message", System.StringComparison.Ordinal);

        firstIndex.Should().BeGreaterThan(-1);
        secondIndex.Should().BeGreaterThan(firstIndex);
        thirdIndex.Should().BeGreaterThan(secondIndex);

        using var document = JsonDocument.Parse(mergedText);
        var root = document.RootElement;

        root.GetProperty("guild").GetProperty("name").GetString().Should().Be("New Guild");
        root.GetProperty("channel").GetProperty("name").GetString().Should().Be("new-channel");
        root.GetProperty("channel").GetProperty("category").GetString().Should().Be("New Category");
        root.GetProperty("messageCount").GetInt64().Should().Be(3);
        root.GetProperty("messages").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task I_can_get_the_last_message_id_ignoring_nested_ids_like_the_author_id()
    {
        // Arrange
        using var file = TempFile.Create();

        // Every message's author has id "900" (see BuildMessage), which sits at a deeper
        // nesting level right after each message's own id -- a naive last-occurrence search
        // would wrongly return "900" instead of the last message's own id.
        await File.WriteAllTextAsync(
            file.Path,
            BuildExportJson(
                "Guild",
                "channel",
                "Category",
                "555",
                ("100", "first message"),
                ("101", "second message"),
                ("102", "third message")
            )
        );

        // Act
        var lastMessageId = IncrementalJsonAppender.TryGetLastMessageId(file.Path);

        // Assert
        lastMessageId.Should().Be("102");
    }

    [Fact]
    public void Getting_the_last_message_id_of_an_empty_export_returns_null()
    {
        // Arrange
        using var file = TempFile.Create();
        File.WriteAllText(file.Path, BuildExportJson("Guild", "channel", "Category", "555"));

        // Act & assert
        IncrementalJsonAppender.TryGetLastMessageId(file.Path).Should().BeNull();
    }

    [Fact]
    public void I_can_parse_the_header_and_find_the_messages_array_of_a_file_that_starts_with_a_UTF8_BOM()
    {
        // Arrange: a file re-saved once by a BOM-emitting external tool. DiscordChatExporter
        // itself never writes one, but a file that picked one up shouldn't permanently break
        // every subsequent incremental run for that channel.
        using var file = TempFile.Create();

        var json = BuildExportJson(
            "Guild",
            "channel",
            "Category",
            "555",
            ("100", "first message"),
            ("101", "second message")
        );
        var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF };
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(file.Path, [.. bomBytes, .. jsonBytes]);

        // Act
        using var header = IncrementalJsonAppender.ParseHeader(file.Path);
        var arrayOpenOffset = IncrementalJsonAppender.FindMessagesArrayOpenOffset(file.Path);
        var lastMessageId = IncrementalJsonAppender.TryGetLastMessageId(file.Path);

        // Assert
        header
            .RootElement.GetProperty("channel")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("channel");

        // The offset must point at the actual '[' in the *original* (BOM-prefixed) file, not at
        // an offset that's off by the BOM's length.
        var fileBytes = File.ReadAllBytes(file.Path);
        fileBytes[arrayOpenOffset].Should().Be((byte)'[');

        lastMessageId.Should().Be("101");
    }

    // Independently finds the byte offset of a message's own '{' (the same position
    // JsonMessageWriter's checkpoint capture records), using a fresh Utf8JsonReader pass rather
    // than any of the production code being tested here -- so this is ground truth, not a
    // circular check. Reads from the file's actual root (unlike the production scanners, which
    // seek past the header first), so message objects sit one level deeper: depth 2 for the
    // object itself, depth 3 for its own "id" property.
    private static long FindMessageStartOffset(string filePath, string messageId)
    {
        var bytes = File.ReadAllBytes(filePath);
        var reader = new Utf8JsonReader(bytes);

        long? currentObjectStart = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 2)
                currentObjectStart = reader.TokenStartIndex;

            if (
                reader.TokenType == JsonTokenType.PropertyName
                && reader.CurrentDepth == 3
                && reader.ValueTextEquals("id"u8)
            )
            {
                reader.Read();
                if (reader.GetString() == messageId)
                    return currentObjectStart!.Value;
            }
        }

        throw new InvalidOperationException($"Message '{messageId}' not found in '{filePath}'.");
    }

    [Fact]
    public async Task Merged_checkpoints_land_at_the_correct_byte_offset_in_the_merged_file()
    {
        // Arrange: an "existing" partition with a persisted checkpoint at message 100, and a
        // "new" temp file (simulating a streaming-append fetch) with its own checkpoint at 200,
        // captured relative to that file's own start (as JsonMessageWriter would).
        using var existingFile = TempFile.Create();
        using var newTempFile = TempFile.Create();
        using var mergedFile = TempFile.Create();

        var existingJson = BuildExportJson(
            "Guild",
            "channel",
            "Category",
            "555",
            ("100", "msg 100"),
            ("101", "msg 101")
        );
        await File.WriteAllTextAsync(existingFile.Path, existingJson);

        var newJson = BuildExportJson(
            "Guild",
            "channel",
            "Category",
            "555",
            ("200", "msg 200"),
            ("201", "msg 201")
        );
        await File.WriteAllTextAsync(newTempFile.Path, newJson);

        var existingCheckpointOffset = FindMessageStartOffset(existingFile.Path, "100");
        var newCheckpointOffset = FindMessageStartOffset(newTempFile.Path, "200");

        var existingIndexEntry = new PartitionIndexEntry
        {
            Index = 0,
            MinMessageId = "100",
            MaxMessageId = "101",
            Checkpoints =
            [
                new CheckpointEntry { MessageId = "100", ByteOffset = existingCheckpointOffset },
            ],
        };

        // Act
        await IncrementalJsonAppender.MergeAsync(
            existingFile.Path,
            newTempFile.Path,
            mergedFile.Path
        );

        var merged = IncrementalJsonAppender.BuildMergedCheckpoints(
            existingFile.Path,
            newTempFile.Path,
            existingIndexEntry,
            partitionIndex: 0,
            newCheckpoints: [("200", newCheckpointOffset)],
            newMaxMessageId: "201"
        );

        // Assert
        merged.Should().NotBeNull();
        merged!.MinMessageId.Should().Be("100");
        merged.MaxMessageId.Should().Be("201");
        merged.Checkpoints.Should().HaveCount(2);

        // The real, independent test: seek to each computed offset in the *actual merged file*
        // and confirm it lands exactly on the expected message's own '{'.
        var mergedBytes = await File.ReadAllBytesAsync(mergedFile.Path);
        foreach (var checkpoint in merged.Checkpoints)
        {
            mergedBytes[checkpoint.ByteOffset]
                .Should()
                .Be(
                    (byte)'{',
                    $"checkpoint for message {checkpoint.MessageId} should point at '{{'"
                );

            var reader = new Utf8JsonReader(mergedBytes.AsSpan((int)checkpoint.ByteOffset));
            reader.Read(); // StartObject
            reader.Read(); // PropertyName "id"
            reader.Read(); // the id's string value
            reader.GetString().Should().Be(checkpoint.MessageId);
        }
    }
}
