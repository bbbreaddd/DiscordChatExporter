using System;
using System.IO;
using System.Linq;
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
        content.Should().NotContain("highlight.min.js");
        content.Should().NotContain("lottie.min.js");
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
    public async Task I_can_skip_an_unchanged_JSON_export_with_a_template_output_path()
    {
        // Arrange
        using var inputDir = TempDirectory.Create();
        using var outputDir = TempDirectory.Create();

        var inputFilePath = Path.Combine(inputDir.Path, "watcher-input.json");
        await File.WriteAllTextAsync(
            inputFilePath,
            await File.ReadAllTextAsync(SampleExportFilePath)
        );

        var outputPath =
            outputDir.Path
            + Path.DirectorySeparatorChar
            + "%G"
            + Path.DirectorySeparatorChar
            + "%T"
            + Path.DirectorySeparatorChar
            + "%C"
            + Path.DirectorySeparatorChar;

        await new ConvertCommand
        {
            InputPaths = [inputFilePath],
            OutputPath = outputPath,
            ExportFormat = ExportFormat.PlainText,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        var convertedFilePath = Path.Combine(
            outputDir.Path,
            "Test Guild",
            "Text Channels",
            "general",
            "Test Guild - Text Channels - general [1063903591533404161].txt"
        );

        await File.WriteAllTextAsync(convertedFilePath, "already converted");
        File.SetLastWriteTimeUtc(convertedFilePath, DateTime.UtcNow.AddMinutes(1));

        // Act
        await new ConvertCommand
        {
            InputPaths = [inputFilePath],
            OutputPath = outputPath,
            ExportFormat = ExportFormat.PlainText,
            ShouldSkipUnchanged = true,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        var content = await File.ReadAllTextAsync(convertedFilePath);

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

    [Fact]
    public async Task I_can_convert_a_JSON_export_to_the_html_format_with_partitioning_and_pagination()
    {
        // Arrange
        using var dir = TempDirectory.Create();
        var filePath = Path.Combine(dir.Path, "output.html");

        // Act
        await new ConvertCommand
        {
            InputPaths = [SampleExportFilePath],
            OutputPath = filePath,
            ExportFormat = ExportFormat.HtmlDark,
            PartitionLimit = PartitionLimit.Parse("1"),
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        // Assert
        Directory.EnumerateFiles(dir.Path, "output*").Should().HaveCount(3);

        var part1 = await File.ReadAllTextAsync(Path.Combine(dir.Path, "output.html"));
        var part2 = await File.ReadAllTextAsync(Path.Combine(dir.Path, "output [part 2].html"));
        var part3 = await File.ReadAllTextAsync(Path.Combine(dir.Path, "output [part 3].html"));

        part1.Should().Contain("class=\"chatlog__pagination\"");
        part1.Should().Contain("Page 1 of 3");
        part1.Should().Contain("href=\"output%20%5Bpart%202%5D.html\"");

        part2.Should().Contain("class=\"chatlog__pagination\"");
        part2.Should().Contain("Page 2 of 3");
        part2.Should().Contain("href=\"output.html\"");
        part2.Should().Contain("href=\"output%20%5Bpart%203%5D.html\"");

        part3.Should().Contain("class=\"chatlog__pagination\"");
        part3.Should().Contain("Page 3 of 3");
        part3.Should().Contain("href=\"output%20%5Bpart%202%5D.html\"");
    }

    [Fact]
    public async Task I_can_convert_a_JSON_export_to_html_with_shared_assets()
    {
        // Arrange
        using var dir = TempDirectory.Create();
        var filePath = Path.Combine(dir.Path, "output.html");

        // Act
        await new ConvertCommand
        {
            InputPaths = [SampleExportFilePath],
            OutputPath = filePath,
            ExportFormat = ExportFormat.HtmlDark,
            PartitionLimit = PartitionLimit.Parse("1"),
            ShouldUseHtmlSharedAssets = true,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        // Assert
        File.Exists(Path.Combine(dir.Path, "_dce", "html-dark.css")).Should().BeTrue();
        File.Exists(Path.Combine(dir.Path, "_dce", "html.js")).Should().BeTrue();
        File.Exists(Path.Combine(dir.Path, "_dce", "icons.svg")).Should().BeTrue();

        var html = await File.ReadAllTextAsync(filePath);
        var allHtml = string.Join(
            "\n",
            Directory.EnumerateFiles(dir.Path, "*.html").Select(File.ReadAllText)
        );
        html.Should().Contain("_dce/html-dark.css");
        html.Should().Contain("_dce/html.js");
        allHtml.Should().Contain("_dce/icons.svg#");
    }

    [Fact]
    public async Task I_can_convert_a_JSON_export_to_html_using_rebased_cached_asset_paths()
    {
        // Arrange
        using var inputDir = TempDirectory.Create();
        using var outputDir = TempDirectory.Create();

        var json = await File.ReadAllTextAsync(SampleExportFilePath);
        json = json.Replace(
            "\"iconUrl\": \"https://cdn.discordapp.com/embed/avatars/0.png\"",
            "\"iconUrl\": \"https://cdn.discordapp.com/embed/avatars/0.png\",\n    \"iconLocalPath\": \"media/guild.png\""
        );
        json = json.Replace(
            "\"avatarUrl\": \"https://cdn.discordapp.com/embed/avatars/1.png\"",
            "\"avatarUrl\": \"https://cdn.discordapp.com/embed/avatars/1.png\",\n        \"avatarLocalPath\": \"media/alice.png\""
        );
        json = json.Replace(
            "\"url\": \"https://cdn.discordapp.com/attachments/1063903591533404161/1100000000000000010/image.png\"",
            "\"url\": \"https://cdn.discordapp.com/attachments/1063903591533404161/1100000000000000010/image.png\",\n          \"localPath\": \"media/image.png\""
        );

        var inputFilePath = Path.Combine(inputDir.Path, "sample-export.json");
        await File.WriteAllTextAsync(inputFilePath, json);

        var htmlDirPath = Path.Combine(outputDir.Path, "html");
        Directory.CreateDirectory(htmlDirPath);
        var outputFilePath = Path.Combine(htmlDirPath, "output.html");

        // Act
        await new ConvertCommand
        {
            InputPaths = [inputFilePath],
            OutputPath = outputFilePath,
            ExportFormat = ExportFormat.HtmlDark,
            ShouldDownloadAssets = true,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        var content = await File.ReadAllTextAsync(outputFilePath);

        // Assert
        content.Should().Contain("media/alice.png");
        content.Should().Contain("media/image.png");
        content.Should().Contain("media/guild.png");
    }

    [Fact]
    public async Task I_can_convert_a_partitioned_JSON_export_and_merge_them_correctly()
    {
        // Arrange
        using var tempDir = TempDirectory.Create();

        // Write two partition files: general.json and general [part 2].json
        var part1Path = Path.Combine(tempDir.Path, "general.json");
        var part2Path = Path.Combine(tempDir.Path, "general [part 2].json");

        var baseJson = await File.ReadAllTextAsync(SampleExportFilePath);

        await File.WriteAllTextAsync(part1Path, baseJson);
        await File.WriteAllTextAsync(part2Path, baseJson);

        var outputPath = Path.Combine(tempDir.Path, "output.html");

        // Act
        await new ConvertCommand
        {
            InputPaths = [part1Path, part2Path],
            OutputPath = outputPath,
            ExportFormat = ExportFormat.HtmlDark,
            PartitionLimit = PartitionLimit.Parse("3"),
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        // Assert
        Directory.EnumerateFiles(tempDir.Path, "output*").Should().HaveCount(2);

        var html1 = await File.ReadAllTextAsync(Path.Combine(tempDir.Path, "output.html"));
        var html2 = await File.ReadAllTextAsync(Path.Combine(tempDir.Path, "output [part 2].html"));

        html1.Should().Contain("class=\"chatlog__pagination\"");
        html1.Should().Contain("Page 1 of 2");

        html2.Should().Contain("class=\"chatlog__pagination\"");
        html2.Should().Contain("Page 2 of 2");
    }

    [Fact]
    public async Task I_cannot_use_html_shared_assets_with_a_non_html_format()
    {
        var act = async () =>
            await new ConvertCommand
            {
                InputPaths = [SampleExportFilePath],
                OutputPath = TempFile.Create().Path,
                ExportFormat = ExportFormat.PlainText,
                ShouldUseHtmlSharedAssets = true,
            }.ExecuteAsync(new FakeConsole());

        await act.Should().ThrowAsync<CommandException>();
    }
}
