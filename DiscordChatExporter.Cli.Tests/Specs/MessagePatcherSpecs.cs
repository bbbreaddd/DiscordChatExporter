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

public class MessagePatcherSpecs
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

    private static string BuildExportJson(params (string Id, string Content)[] messages)
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
                messages.Select(m => (JsonNode)BuildMessage(m.Id, m.Content)).ToArray()
            ),
            ["messageCount"] = messages.Length,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    [Fact]
    public async Task FindMessageByteRange_finds_a_message_when_scanning_from_the_true_start()
    {
        // Arrange
        using var file = TempFile.Create();
        await File.WriteAllTextAsync(
            file.Path,
            BuildExportJson(("100", "first"), ("101", "second"), ("102", "third"))
        );

        // Act: checkpointOffset=0 means "no usable checkpoint, scan from the array's real start".
        var range = MessagePatcher.FindMessageByteRange(file.Path, 0, Snowflake.Parse("101"));

        // Assert
        range.Should().NotBeNull();
        var bytes = await File.ReadAllBytesAsync(file.Path);
        bytes[range!.Value.Start].Should().Be((byte)'{');

        var slice = System.Text.Encoding.UTF8.GetString(
            bytes,
            (int)range.Value.Start,
            (int)(range.Value.End - range.Value.Start)
        );
        slice.Should().Contain("\"id\": \"101\"");
        slice.Should().Contain("second");
        slice.Should().NotContain("first");
        slice.Should().NotContain("third");
    }

    [Fact]
    public async Task FindMessageByteRange_finds_a_message_when_seeking_directly_to_a_checkpoint()
    {
        // Arrange: this is the case that requires the synthetic '[' prepend trick -- seeking
        // straight to a mid-array message's '{', skipping the real array bracket entirely.
        using var file = TempFile.Create();
        await File.WriteAllTextAsync(
            file.Path,
            BuildExportJson(("100", "first"), ("101", "second"), ("102", "third"))
        );

        var checkpointOffset = FindMessageStartOffset(file.Path, "101");

        // Act: seek straight to message 101's checkpoint, looking for message 102 (the sibling
        // right after it).
        var range = MessagePatcher.FindMessageByteRange(
            file.Path,
            checkpointOffset,
            Snowflake.Parse("102")
        );

        // Assert
        range.Should().NotBeNull();
        var bytes = await File.ReadAllBytesAsync(file.Path);
        var slice = System.Text.Encoding.UTF8.GetString(
            bytes,
            (int)range!.Value.Start,
            (int)(range.Value.End - range.Value.Start)
        );
        slice.Should().Contain("\"id\": \"102\"");
        slice.Should().Contain("third");
    }

    [Fact]
    public async Task FindMessageByteRange_returns_null_for_an_id_that_does_not_exist()
    {
        using var file = TempFile.Create();
        await File.WriteAllTextAsync(file.Path, BuildExportJson(("100", "first")));

        MessagePatcher.FindMessageByteRange(file.Path, 0, Snowflake.Parse("999")).Should().BeNull();
    }

    [Fact]
    public void ReindentAsNestedMessage_shifts_every_line_but_the_first_by_two_levels()
    {
        // Arrange: JsonMessageWriter renders a message as if it were a standalone top-level
        // value (2-space indent for its own properties), but spliced into the real file it
        // actually sits 2 levels deep (root -> messages array -> this object), where its
        // properties should be indented 6 spaces to match every sibling message around it.
        // Discovered by diffing a real patch against production data: the JSON stayed valid
        // either way (whitespace doesn't affect parsing), but the indentation visibly didn't
        // match its neighbors without this.
        var rendered = System.Text.Encoding.UTF8.GetBytes(
            "{\n  \"id\": \"123\",\n  \"nested\": {\n    \"x\": 1\n  }\n}"
        );

        // Act
        var reindented = MessagePatcher.ReindentAsNestedMessage(rendered);

        // Assert
        var text = System.Text.Encoding.UTF8.GetString(reindented);
        text.Should()
            .Be("{\n      \"id\": \"123\",\n      \"nested\": {\n        \"x\": 1\n      }\n    }");
    }

    [Fact]
    public async Task Splicing_the_first_message_of_many_leaves_every_other_message_byte_for_byte_untouched()
    {
        // Arrange: patch the *first* message of a multi-message export -- the worst case, since
        // everything after it has to be rewritten.
        using var file = TempFile.Create();
        var originalMessages = Enumerable
            .Range(1, 50)
            .Select(i => ((100 + i).ToString(), $"message {i}"))
            .ToArray();
        await File.WriteAllTextAsync(file.Path, BuildExportJson(originalMessages));

        var targetId = originalMessages[0].Item1;
        var range = MessagePatcher.FindMessageByteRange(file.Path, 0, Snowflake.Parse(targetId));
        range.Should().NotBeNull();

        var newMessageBytes = System.Text.Encoding.UTF8.GetBytes(
            $$"""{ "id": "{{targetId}}", "content": "EDITED CONTENT WITH A REACTION" }"""
        );

        // Act
        await MessagePatcher.SplicePartitionAsync(
            file.Path,
            range!.Value.Start,
            range.Value.End,
            newMessageBytes
        );

        // Assert: the whole file is still valid, well-formed JSON...
        var resultText = await File.ReadAllTextAsync(file.Path);
        using var document = JsonDocument.Parse(resultText);
        var messagesArray = document.RootElement.GetProperty("messages");
        messagesArray.GetArrayLength().Should().Be(originalMessages.Length);

        // ...the target message reflects the new content...
        messagesArray[0].GetProperty("id").GetString().Should().Be(targetId);
        messagesArray[0]
            .GetProperty("content")
            .GetString()
            .Should()
            .Be("EDITED CONTENT WITH A REACTION");

        // ...and every other message is completely unchanged.
        for (var i = 1; i < originalMessages.Length; i++)
        {
            messagesArray[i].GetProperty("id").GetString().Should().Be(originalMessages[i].Item1);
            messagesArray[i]
                .GetProperty("content")
                .GetString()
                .Should()
                .Be(originalMessages[i].Item2);
        }
    }

    // Manual benchmark, not a CI assertion (timing-based asserts are flaky) -- run explicitly by
    // temporarily removing the Skip below, or with
    // `dotnet test --filter "FullyQualifiedName~Benchmark_index_vs_full_scan"` after doing so, to
    // see actual numbers. Demonstrates the case the whole feature exists for: finding a message
    // that sits deep in a large partition (worst case for a naive scan-from-start) is O(index
    // lookup + small local scan) with the index, versus O(file size) without one. Last measured:
    // 50,000 messages, target near the end -- full scan 289ms, with index 3ms (~97x).
    [Fact(Skip = "Manual benchmark -- run explicitly, not part of normal CI")]
    public async Task Benchmark_index_vs_full_scan()
    {
        using var dir = TempDirectory.Create();
        var baseFilePath = Path.Combine(dir.Path, "export.json");

        const int messageCount = 50_000;
        var messageIds = Enumerable
            .Range(1, messageCount)
            .Select(i => (1_000_000 + i).ToString())
            .ToArray();
        await File.WriteAllTextAsync(
            baseFilePath,
            BuildExportJson(messageIds.Select(id => (id, $"message content for {id}")).ToArray())
        );

        // Target a message near the very end -- the worst case for a naive forward scan, and
        // exactly the case an index (jumping close via a checkpoint) is meant to fix.
        var targetId = messageIds[^10];

        var index = await MessageIndex.LoadOrBuildAsync(baseFilePath);
        var partition = index.FindPartitionForMessage(Snowflake.Parse(targetId));
        partition.Should().NotBeNull();
        var checkpointOffset = MessageIndex.FindNearestCheckpointBefore(
            partition!,
            Snowflake.Parse(targetId)
        );

        var swNoIndex = System.Diagnostics.Stopwatch.StartNew();
        var rangeNoIndex = MessagePatcher.FindMessageByteRange(
            baseFilePath,
            0,
            Snowflake.Parse(targetId)
        );
        swNoIndex.Stop();

        var swWithIndex = System.Diagnostics.Stopwatch.StartNew();
        var rangeWithIndex = MessagePatcher.FindMessageByteRange(
            baseFilePath,
            checkpointOffset,
            Snowflake.Parse(targetId)
        );
        swWithIndex.Stop();

        rangeNoIndex.Should().Be(rangeWithIndex);

        System.Console.WriteLine(
            $"[benchmark] {messageCount} messages, target near the end -- "
                + $"full scan: {swNoIndex.Elapsed.TotalMilliseconds:F2}ms, "
                + $"with index: {swWithIndex.Elapsed.TotalMilliseconds:F2}ms"
        );
    }

    // Independently finds the byte offset of a message's own '{' (ground truth, not using any of
    // the code under test), matching JsonMessageWriter's checkpoint convention. Reads from the
    // file's actual root (unlike the production scanners, which seek past the header first), so
    // message objects sit one level deeper: depth 2 for the object itself, depth 3 for its "id".
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

        throw new System.InvalidOperationException($"Message '{messageId}' not found.");
    }
}
