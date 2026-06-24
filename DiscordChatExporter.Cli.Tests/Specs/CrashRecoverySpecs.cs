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
}
