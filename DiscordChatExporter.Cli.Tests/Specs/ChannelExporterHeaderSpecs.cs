using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

// Covers ChannelExporter.HeaderMatchesRequest, the check that backs the incremental
// manifest-based skip: a channel whose LastMessageId hasn't moved should still be re-exported
// if its (or one of its parent categories') name has drifted from what's recorded in the
// existing file's header.
public class ChannelExporterHeaderSpecs
{
    private static Guild CreateGuild(string name = "Test Guild") =>
        new(Snowflake.Parse("100"), name, "https://cdn.discordapp.com/embed/avatars/0.png");

    private static Channel CreateChannel(
        Guild guild,
        string channelName = "general",
        string categoryName = "Text Channels"
    )
    {
        var category = new Channel(
            Snowflake.Parse("200"),
            ChannelKind.GuildCategory,
            guild.Id,
            null,
            categoryName,
            null,
            null,
            null,
            false,
            null
        );

        return new Channel(
            Snowflake.Parse("300"),
            ChannelKind.GuildTextChat,
            guild.Id,
            category,
            channelName,
            null,
            null,
            "Some topic",
            false,
            Snowflake.Parse("400")
        );
    }

    private static string BuildHeaderJson(Guild guild, Channel channel)
    {
        var root = new JsonObject
        {
            ["guild"] = new JsonObject
            {
                ["id"] = guild.Id.ToString(),
                ["name"] = guild.Name,
                ["iconUrl"] = guild.IconUrl,
            },
            ["channel"] = new JsonObject
            {
                ["id"] = channel.Id.ToString(),
                ["type"] = channel.Kind.ToString(),
                ["categoryId"] = channel.Parent?.Id.ToString(),
                ["category"] = channel.Parent?.Name,
                ["parentCategoryId"] = channel.Parent?.Parent?.Id.ToString(),
                ["parentCategory"] = channel.Parent?.Parent?.Name,
                ["name"] = channel.Name,
                ["topic"] = channel.Topic,
                ["iconUrl"] = channel.IconUrl,
            },
            ["dateRange"] = new JsonObject { ["after"] = null, ["before"] = null },
            ["exportedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["messages"] = new JsonArray(),
            ["messageCount"] = 0,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static ExportRequest CreateRequest(
        Guild guild,
        Channel channel,
        string outputDirPath
    ) =>
        new(
            guild,
            channel,
            outputDirPath,
            null,
            ExportFormat.Json,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            false,
            true,
            false,
            false,
            null,
            false,
            true
        );

    [Fact]
    public async Task Header_matches_request_when_nothing_has_changed()
    {
        // Arrange
        using var file = TempFile.Create();
        using var outputDir = TempDirectory.Create();

        var guild = CreateGuild();
        var channel = CreateChannel(guild);

        await File.WriteAllTextAsync(file.Path, BuildHeaderJson(guild, channel));

        var request = CreateRequest(guild, channel, outputDir.Path);

        // Act & assert
        ChannelExporter.HeaderMatchesRequest(file.Path, request).Should().BeTrue();
    }

    [Fact]
    public async Task Header_does_not_match_request_after_the_channel_was_renamed()
    {
        // Arrange
        using var file = TempFile.Create();
        using var outputDir = TempDirectory.Create();

        var guild = CreateGuild();
        var staleChannel = CreateChannel(guild, channelName: "general");

        await File.WriteAllTextAsync(file.Path, BuildHeaderJson(guild, staleChannel));

        // Same channel ID and same LastMessageId, but the channel's name has drifted since the
        // header was written.
        var renamedChannel = CreateChannel(guild, channelName: "general-renamed");
        var request = CreateRequest(guild, renamedChannel, outputDir.Path);

        // Act & assert
        ChannelExporter.HeaderMatchesRequest(file.Path, request).Should().BeFalse();
    }

    [Fact]
    public async Task Header_does_not_match_request_after_the_parent_category_was_renamed()
    {
        // Arrange
        using var file = TempFile.Create();
        using var outputDir = TempDirectory.Create();

        var guild = CreateGuild();
        var staleChannel = CreateChannel(guild, categoryName: "Text Channels");

        await File.WriteAllTextAsync(file.Path, BuildHeaderJson(guild, staleChannel));

        // Same channel/category IDs and same LastMessageId, but the category was renamed.
        var renamedChannel = CreateChannel(guild, categoryName: "Archive");
        var request = CreateRequest(guild, renamedChannel, outputDir.Path);

        // Act & assert
        ChannelExporter.HeaderMatchesRequest(file.Path, request).Should().BeFalse();
    }

    [Fact]
    public async Task Header_does_not_match_request_after_the_guild_was_renamed()
    {
        // Arrange
        using var file = TempFile.Create();
        using var outputDir = TempDirectory.Create();

        var staleGuild = CreateGuild("Old Guild Name");
        var channel = CreateChannel(staleGuild);

        await File.WriteAllTextAsync(file.Path, BuildHeaderJson(staleGuild, channel));

        var renamedGuild = CreateGuild("New Guild Name");
        var request = CreateRequest(renamedGuild, channel, outputDir.Path);

        // Act & assert
        ChannelExporter.HeaderMatchesRequest(file.Path, request).Should().BeFalse();
    }
}
