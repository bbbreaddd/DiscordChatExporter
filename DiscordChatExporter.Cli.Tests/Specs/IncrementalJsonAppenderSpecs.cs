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
}
