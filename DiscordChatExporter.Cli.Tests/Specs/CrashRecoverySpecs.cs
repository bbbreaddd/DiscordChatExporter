using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class CrashRecoverySpecs
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
                // A nested array + object, so the scanner has to correctly track depth and not
                // mistake an inner closing brace/bracket for the end of a message.
                ["roles"] = new JsonArray(new JsonObject { ["id"] = "1", ["name"] = "role" }),
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
    public async Task Repairs_a_temp_truncated_in_the_middle_of_a_message_keeping_the_complete_ones()
    {
        // Arrange: a full export, then chop it off mid-way through the last message to simulate a
        // process killed while streaming messages to disk.
        var full = BuildExportJson(
            ("100", "first message"),
            ("101", "second message"),
            ("102", "third message — gets cut off")
        );
        var thirdStart = full.IndexOf("third message", System.StringComparison.Ordinal);
        var truncated = full[..(thirdStart + 5)]; // cut in the middle of the third message

        using var tempFile = TempFile.Create();
        await File.WriteAllTextAsync(tempFile.Path, truncated);
        var finalPath = tempFile.Path + ".final.json";

        // Act
        var ok = await CrashRecovery.TryRepairAndPromoteAsync(tempFile.Path, finalPath);

        // Assert
        try
        {
            ok.Should().BeTrue();
            File.Exists(tempFile.Path).Should().BeFalse("the temp is moved onto the final path");
            File.Exists(finalPath).Should().BeTrue();

            var repaired = await File.ReadAllTextAsync(finalPath);
            using var document = JsonDocument.Parse(repaired); // must be valid JSON again
            var root = document.RootElement;

            root.GetProperty("messages").GetArrayLength().Should().Be(2);
            root.GetProperty("messageCount").GetInt64().Should().Be(2);
            repaired.Should().Contain("first message");
            repaired.Should().Contain("second message");
            repaired.Should().NotContain("gets cut off");
        }
        finally
        {
            if (File.Exists(finalPath))
                File.Delete(finalPath);
        }
    }

    [Fact]
    public async Task Leaves_an_already_complete_temp_valid_after_repair()
    {
        // A temp that happens to be fully written (postamble present) should survive repair intact.
        var full = BuildExportJson(("100", "first message"), ("101", "second message"));

        using var tempFile = TempFile.Create();
        await File.WriteAllTextAsync(tempFile.Path, full);
        var finalPath = tempFile.Path + ".final.json";

        var ok = await CrashRecovery.TryRepairAndPromoteAsync(tempFile.Path, finalPath);

        try
        {
            ok.Should().BeTrue();
            var repaired = await File.ReadAllTextAsync(finalPath);
            using var document = JsonDocument.Parse(repaired);
            document.RootElement.GetProperty("messages").GetArrayLength().Should().Be(2);
            document.RootElement.GetProperty("messageCount").GetInt64().Should().Be(2);
        }
        finally
        {
            if (File.Exists(finalPath))
                File.Delete(finalPath);
        }
    }

    [Fact]
    public async Task Refuses_to_promote_a_temp_with_no_complete_messages()
    {
        // Crash right after the preamble: header + '[' written, but not a single full message.
        var full = BuildExportJson(("100", "first message"));
        var firstStart = full.IndexOf("\"id\": \"100\"", System.StringComparison.Ordinal);
        var truncated = full[..firstStart]; // keeps the header and the opening '[', drops messages

        using var tempFile = TempFile.Create();
        await File.WriteAllTextAsync(tempFile.Path, truncated);
        var finalPath = tempFile.Path + ".final.json";

        var ok = await CrashRecovery.TryRepairAndPromoteAsync(tempFile.Path, finalPath);

        ok.Should().BeFalse();
        File.Exists(finalPath).Should().BeFalse("nothing salvageable should ever be promoted");
    }

    [Fact]
    public async Task Refuses_to_repair_a_temp_with_no_recognizable_header()
    {
        // Crash before the header was even flushed — pure garbage, nothing to do.
        using var tempFile = TempFile.Create();
        await File.WriteAllTextAsync(tempFile.Path, "{ \"guild\": { \"id\": \"100\"");
        var finalPath = tempFile.Path + ".final.json";

        var ok = await CrashRecovery.TryRepairAndPromoteAsync(tempFile.Path, finalPath);

        ok.Should().BeFalse();
        File.Exists(finalPath).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteSavedMerge_merges_appendTempPath_into_existing_output()
    {
        // Arrange: existing output has messages A+B; appendTempPath has new messages C+D
        // (simulating a crash after the fetch completed but before the merge ran).
        var existingJson = BuildExportJson(("100", "msg A"), ("101", "msg B"));
        var newJson = BuildExportJson(("102", "msg C"), ("103", "msg D"));

        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var outputFilePath = Path.Combine(dir, "export.json");
            var appendTempPath = outputFilePath + ".new.tmp";

            await File.WriteAllTextAsync(outputFilePath, existingJson);
            await File.WriteAllTextAsync(appendTempPath, newJson);

            // Act
            var ok = await CrashRecovery.TryCompleteSavedMergeAsync(
                appendTempPath,
                outputFilePath,
                manifest: null,
                channelId: "555",
                baseOutputDirPath: dir,
                cancellationToken: default
            );

            // Assert: merge succeeded and the output file now has all 4 messages
            ok.Should().BeTrue();
            File.Exists(outputFilePath).Should().BeTrue();

            var merged = await File.ReadAllTextAsync(outputFilePath);
            using var doc = JsonDocument.Parse(merged);
            var root = doc.RootElement;
            root.GetProperty("messageCount").GetInt64().Should().Be(4);
            root.GetProperty("messages").GetArrayLength().Should().Be(4);
            merged.Should().Contain("msg A");
            merged.Should().Contain("msg B");
            merged.Should().Contain("msg C");
            merged.Should().Contain("msg D");

            // appendTempPath is left for the caller (RecoverAsync) to delete
            File.Exists(appendTempPath).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteSavedMerge_promotes_overflow_partitions()
    {
        // Arrange: existing output has one partition (messages A+B). The fetch produced enough
        // messages to overflow into a second partition: appendTempPath has C+D, and the overflow
        // temp (appendTempPath [part 2]) has E+F.
        var existingJson = BuildExportJson(("100", "msg A"), ("101", "msg B"));
        var appendJson = BuildExportJson(("102", "msg C"), ("103", "msg D"));
        var overflowJson = BuildExportJson(("104", "msg E"), ("105", "msg F"));

        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var outputFilePath = Path.Combine(dir, "export.json");
            var appendTempPath = outputFilePath + ".new.tmp";
            // Overflow partition name matches MessageExporter.GetPartitionFilePath(appendTempPath, 1)
            var overflowTempPath = Path.Combine(dir, "export.json.new [part 2].tmp");

            await File.WriteAllTextAsync(outputFilePath, existingJson);
            await File.WriteAllTextAsync(appendTempPath, appendJson);
            await File.WriteAllTextAsync(overflowTempPath, overflowJson);

            // Act
            var ok = await CrashRecovery.TryCompleteSavedMergeAsync(
                appendTempPath,
                outputFilePath,
                manifest: null,
                channelId: "555",
                baseOutputDirPath: dir,
                cancellationToken: default
            );

            // Assert: overflow partition was promoted to a real partition file
            ok.Should().BeTrue();
            var expectedPartition2 = Path.Combine(dir, "export [part 2].json");
            File.Exists(expectedPartition2).Should().BeTrue("overflow temp should be promoted");
            File.Exists(overflowTempPath)
                .Should()
                .BeFalse("overflow temp should be gone after promotion");

            using var doc2 = JsonDocument.Parse(await File.ReadAllTextAsync(expectedPartition2));
            doc2.RootElement.GetProperty("messageCount").GetInt64().Should().Be(2);
            (await File.ReadAllTextAsync(expectedPartition2)).Should().Contain("msg E");
            (await File.ReadAllTextAsync(expectedPartition2)).Should().Contain("msg F");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteSavedMerge_does_not_duplicate_messages_when_replayed_after_the_merge_already_committed()
    {
        // Arrange: simulates a crash that happens *after* a previous TryCompleteSavedMergeAsync
        // call already merged appendTempPath's messages into the output, but before it (or its
        // caller) got around to deleting appendTempPath -- so this method runs again on the next
        // recovery pass with the exact same appendTempPath still on disk. The streaming-append
        // merge has no id-based dedup, so a naive replay would duplicate every message in it.
        var alreadyMergedJson = BuildExportJson(
            ("100", "msg A"),
            ("101", "msg B"),
            ("102", "msg C"),
            ("103", "msg D")
        );
        var appendJson = BuildExportJson(("102", "msg C"), ("103", "msg D"));

        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var outputFilePath = Path.Combine(dir, "export.json");
            var appendTempPath = outputFilePath + ".new.tmp";

            await File.WriteAllTextAsync(outputFilePath, alreadyMergedJson);
            await File.WriteAllTextAsync(appendTempPath, appendJson);

            // Act
            var ok = await CrashRecovery.TryCompleteSavedMergeAsync(
                appendTempPath,
                outputFilePath,
                manifest: null,
                channelId: "555",
                baseOutputDirPath: dir,
                cancellationToken: default
            );

            // Assert: still exactly 4 messages, not 6
            ok.Should().BeTrue();

            var merged = await File.ReadAllTextAsync(outputFilePath);
            using var doc = JsonDocument.Parse(merged);
            var root = doc.RootElement;
            root.GetProperty("messageCount").GetInt64().Should().Be(4);
            root.GetProperty("messages").GetArrayLength().Should().Be(4);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
