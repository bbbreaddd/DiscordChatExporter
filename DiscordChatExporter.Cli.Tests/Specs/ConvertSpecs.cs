using System;
using System.IO;
using System.Threading.Tasks;
using CliFx;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands;
using DiscordChatExporter.Cli.Tests.Utils;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ConvertSpecs
{
    private static readonly string SampleExportFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "Data",
        "sample-export.json"
    );

    private static readonly string SampleThreadExportFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "Data",
        "sample-export-thread.json"
    );

    [Fact]
    public async Task I_can_convert_a_JSON_export_to_the_plain_text_format()
    {
        // Arrange
        using var file = TempFile.Create();

        // Act
        await new ConvertCommand
        {
            InputPaths = [SampleExportFilePath],
            OutputPath = file.Path,
            ExportFormat = ExportFormat.PlainText,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        var content = await File.ReadAllTextAsync(file.Path);

        // Assert
        content.Should().Contain("Guild: Test Guild");
        content.Should().Contain("Channel: Text Channels / general");
        content.Should().Contain("Topic: Test channel for conversion");
        content.Should().Contain("Hello world, this is a test message.");
        content.Should().Contain("Hey @Bobby, check this out!");
        content.Should().Contain("{Embed}");
        content.Should().Contain("Example Embed");
        content.Should().Contain("Field Name");
        content.Should().Contain("Field Value");
        content.Should().Contain("{Reactions}");
        content.Should().Contain("👍");
        content.Should().Contain("Changed the channel name: off-topic");
        content.Should().Contain("Exported 3 message(s)");
    }

    [Fact]
    public async Task I_can_convert_a_JSON_export_to_the_csv_format()
    {
        // Arrange
        using var file = TempFile.Create();

        // Act
        await new ConvertCommand
        {
            InputPaths = [SampleExportFilePath],
            OutputPath = file.Path,
            ExportFormat = ExportFormat.Csv,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        var lines = await File.ReadAllLinesAsync(file.Path);

        // Assert
        lines[0].Should().Be("AuthorID,Author,Date,Content,Attachments,Reactions");
        lines.Should().Contain(l => l.Contains("Hello world, this is a test message."));
        lines.Should().Contain(l => l.Contains("Changed the channel name: off-topic"));
    }

    [Fact]
    public async Task I_can_convert_a_JSON_export_to_the_html_format()
    {
        // Arrange
        using var file = TempFile.Create();

        // Act
        await new ConvertCommand
        {
            InputPaths = [SampleExportFilePath],
            OutputPath = file.Path,
            ExportFormat = ExportFormat.HtmlDark,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        var content = await File.ReadAllTextAsync(file.Path);
        var document = Html.Parse(content);

        // Assert
        document.QuerySelectorAll("[data-message-id]").Should().HaveCount(3);

        content.Should().Contain("Test Guild");
        content.Should().Contain("Alice");
        content.Should().Contain("Hello world, this is a test message.");
        content.Should().Contain("Bobby");
        content.Should().Contain("Example Embed");
        content.Should().Contain("Field Name");
        content.Should().Contain("Field Value");
        content.Should().Contain("image.png");
        content.Should().Contain("👍");
        content.Should().Contain("changed the channel name: ");
        content.Should().Contain("off-topic");
    }

    [Fact]
    public async Task I_cannot_convert_a_JSON_export_to_the_JSON_format()
    {
        // Act & assert
        var act = async () =>
            await new ConvertCommand
            {
                InputPaths = [SampleExportFilePath],
                OutputPath = TempFile.Create().Path,
                ExportFormat = ExportFormat.Json,
            }.ExecuteAsync(new FakeConsole());

        await act.Should().ThrowAsync<CommandException>();
    }

    [Fact]
    public async Task I_can_convert_all_JSON_exports_in_a_directory()
    {
        // Arrange
        using var inputDir = TempDirectory.Create();
        using var outputDir = TempDirectory.Create();

        var sampleJson = await File.ReadAllTextAsync(SampleExportFilePath);

        // The output file name is derived from the guild/channel, so the second export
        // needs a different channel id/name to avoid being written to the same file.
        var sampleJson2 = sampleJson
            .Replace("\"id\": \"1063903591533404161\"", "\"id\": \"1063903591533404165\"")
            .Replace("\"name\": \"general\"", "\"name\": \"off-topic\"");

        await File.WriteAllTextAsync(Path.Combine(inputDir.Path, "export-1.json"), sampleJson);
        await File.WriteAllTextAsync(Path.Combine(inputDir.Path, "export-2.json"), sampleJson2);

        // Act
        await new ConvertCommand
        {
            InputPaths = [inputDir.Path],
            OutputPath = outputDir.Path + Path.DirectorySeparatorChar,
            ExportFormat = ExportFormat.PlainText,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        var outputFilePaths = Directory.GetFiles(outputDir.Path, "*", SearchOption.AllDirectories);

        // Assert
        outputFilePaths.Should().HaveCount(2);

        foreach (var outputFilePath in outputFilePaths)
        {
            var content = await File.ReadAllTextAsync(outputFilePath);
            content.Should().Contain("Hello world, this is a test message.");
        }
    }

    [Fact]
    public async Task I_can_skip_an_unchanged_JSON_export()
    {
        // Arrange
        using var file = TempFile.Create();

        await new ConvertCommand
        {
            InputPaths = [SampleExportFilePath],
            OutputPath = file.Path,
            ExportFormat = ExportFormat.PlainText,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        await File.WriteAllTextAsync(file.Path, "already converted");

        // Act
        await new ConvertCommand
        {
            InputPaths = [SampleExportFilePath],
            OutputPath = file.Path,
            ExportFormat = ExportFormat.PlainText,
            ShouldSkipUnchanged = true,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        var content = await File.ReadAllTextAsync(file.Path);

        // Assert
        content.Should().Be("already converted");
    }

    [Fact]
    public async Task I_can_convert_a_JSON_export_with_a_message_filter()
    {
        // Arrange
        using var file = TempFile.Create();

        // Act
        await new ConvertCommand
        {
            InputPaths = [SampleExportFilePath],
            OutputPath = file.Path,
            ExportFormat = ExportFormat.PlainText,
            MessageFilter = MessageFilter.Parse("has:embed"),
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        var content = await File.ReadAllTextAsync(file.Path);

        // Assert
        content.Should().NotContain("Hello world, this is a test message.");
        content.Should().Contain("Hey @Bobby, check this out!");
        content.Should().Contain("Exported 1 message(s)");
    }

    [Fact]
    public async Task I_can_convert_a_JSON_export_of_a_thread_with_its_full_category_hierarchy()
    {
        // Arrange
        using var file = TempFile.Create();

        // Act
        await new ConvertCommand
        {
            InputPaths = [SampleThreadExportFilePath],
            OutputPath = file.Path,
            ExportFormat = ExportFormat.PlainText,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        var content = await File.ReadAllTextAsync(file.Path);

        // Assert
        content.Should().Contain("Channel: Text Channels / general / thread-topic");
        content.Should().Contain("This is a thread message.");
    }
}
