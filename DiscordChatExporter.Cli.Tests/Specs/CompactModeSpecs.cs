using System;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class CompactModeSpecs
{
    private static Guild CreateGuild(string name = "Test Guild") =>
        new(Snowflake.Parse("100"), name, "https://cdn.discordapp.com/embed/avatars/0.png");

    private static Channel CreateChannel(Guild guild, string channelName = "general")
    {
        return new Channel(
            Snowflake.Parse("300"),
            ChannelKind.GuildTextChat,
            guild.Id,
            null,
            channelName,
            null,
            null,
            "Some topic",
            false,
            Snowflake.Parse("400")
        );
    }

    private static ExportRequest CreateRequest(bool isCompact) =>
        new(
            CreateGuild(),
            CreateChannel(CreateGuild()),
            "output_path",
            null,
            ExportFormat.HtmlDark,
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
            isCompact: isCompact
        );

    [Fact]
    public void ExportContext_ClassMap_Should_Not_Have_Collisions()
    {
        // Arrange
        var context = new ExportContext(null, CreateRequest(true));

        // Act
        // Verify individual minified values that were previously colliding
        context.GetClass("chatlog__reply-avatar").Should().Be("rav");
        context.GetClass("chatlog__reactions").Should().Be("ra");
        context.GetClass("chatlog__reply-content").Should().Be("rpc");
        context.GetClass("chatlog__reaction").Should().Be("rc");
        context.GetClass("chatlog__embed-field--inline").Should().Be("efi");
        context.GetClass("chatlog__embed-footer-icon").Should().Be("effi");

        // Verify no duplicate values in the mapping
        var field = typeof(ExportContext).GetField(
            "ClassMap",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        field.Should().NotBeNull();

        var classMap = (System.Collections.Generic.Dictionary<string, string>)field.GetValue(null)!;
        var minifiedNames = new System.Collections.Generic.HashSet<string>();
        foreach (var kvp in classMap)
        {
            minifiedNames
                .Add(kvp.Value)
                .Should()
                .BeTrue($"Duplicate minified class name: {kvp.Value} for key: {kvp.Key}");
        }
    }

    [Fact]
    public void ExportContext_EscapeCssString_Should_Escape_Special_Characters()
    {
        // Arrange
        var context = new ExportContext(null, CreateRequest(true));

        // Act & Assert
        context.EscapeCssString("normal-url").Should().Be("normal-url");
        context.EscapeCssString("url-with-'quote'").Should().Be("url-with-\\27 quote\\27 ");
        context.EscapeCssString("url-with-\\backslash").Should().Be("url-with-\\5c backslash");
        context.EscapeCssString("url-with-(parens)").Should().Be("url-with-\\28 parens\\29 ");
        context.EscapeCssString("url-with-</style>").Should().Be("url-with-\\3c /style\\3e ");
    }
}
