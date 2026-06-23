using System;
using System.IO;
using System.Threading.Tasks;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

// Convert outputs are derived artifacts, not the canonical incremental history. A rename in the
// input metadata should produce a new output at the newly-computed path, not relocate or merge
// an older converted file for the same channel.
public class ConvertRelocationSpecs
{
    private static readonly string SampleExportFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "Data",
        "sample-export.json"
    );

    [Fact]
    public async Task Converting_renamed_metadata_keeps_the_previous_output_and_writes_a_new_one()
    {
        // Arrange
        using var outputDir = TempDirectory.Create();
        using var renamedInput = TempFile.Create();

        var templateOutputPath =
            outputDir.Path
            + Path.DirectorySeparatorChar
            + "%G"
            + Path.DirectorySeparatorChar
            + "%T"
            + Path.DirectorySeparatorChar
            + "%C"
            + Path.DirectorySeparatorChar;

        var oldFilePath = Path.Combine(
            outputDir.Path,
            "Test Guild",
            "Text Channels",
            "general",
            "Test Guild - Text Channels - general [1063903591533404161].txt"
        );

        var newFilePath = Path.Combine(
            outputDir.Path,
            "Test Guild",
            "Text Channels",
            "general-renamed",
            "Test Guild - Text Channels - general-renamed [1063903591533404161].txt"
        );

        // First conversion establishes the "previous" output under the original name.
        await new ConvertCommand
        {
            InputPaths = [SampleExportFilePath],
            OutputPath = templateOutputPath,
            ExportFormat = ExportFormat.PlainText,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        File.Exists(oldFilePath).Should().BeTrue();

        // The channel was renamed on Discord (same channel ID), so re-converting now computes
        // a different path -- in a different directory, since the template includes %C.
        var sampleJson = await File.ReadAllTextAsync(SampleExportFilePath);
        var renamedJson = sampleJson.Replace(
            "\"name\": \"general\"",
            "\"name\": \"general-renamed\""
        );
        await File.WriteAllTextAsync(renamedInput.Path, renamedJson);

        // Act
        await new ConvertCommand
        {
            InputPaths = [renamedInput.Path],
            OutputPath = templateOutputPath,
            ExportFormat = ExportFormat.PlainText,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        // Assert
        File.Exists(oldFilePath).Should().BeTrue();
        File.Exists(newFilePath).Should().BeTrue();

        var oldContent = await File.ReadAllTextAsync(oldFilePath);
        var content = await File.ReadAllTextAsync(newFilePath);
        oldContent.Should().Contain("Channel: Text Channels / general");
        oldContent.Should().Contain("Hello world, this is a test message.");
        oldContent.Should().NotContain("general-renamed");
        content.Should().Contain("Hello world, this is a test message.");
        content.Should().Contain("Channel: Text Channels / general-renamed");
    }

    [Fact]
    public async Task Converting_the_same_channel_with_a_different_date_range_keeps_both_outputs()
    {
        // Arrange
        using var outputDir = TempDirectory.Create();
        using var rangedInput = TempFile.Create();

        await new ConvertCommand
        {
            InputPaths = [SampleExportFilePath],
            OutputPath = outputDir.Path + Path.DirectorySeparatorChar,
            ExportFormat = ExportFormat.PlainText,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        var sampleJson = await File.ReadAllTextAsync(SampleExportFilePath);
        var rangedJson = sampleJson.Replace(
            "\"after\": null",
            "\"after\": \"2023-06-13T00:00:00+00:00\""
        );
        await File.WriteAllTextAsync(rangedInput.Path, rangedJson);

        var fullExportPath = Path.Combine(
            outputDir.Path,
            "Test Guild - Text Channels - general [1063903591533404161].txt"
        );

        // Act
        await new ConvertCommand
        {
            InputPaths = [rangedInput.Path],
            OutputPath = outputDir.Path + Path.DirectorySeparatorChar,
            ExportFormat = ExportFormat.PlainText,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        // Assert
        File.Exists(fullExportPath).Should().BeTrue();
        Directory
            .GetFiles(outputDir.Path, "*.txt", SearchOption.TopDirectoryOnly)
            .Should()
            .Contain(path => Path.GetFileName(path).Contains(" (after ", StringComparison.Ordinal))
            .And.HaveCount(2);
    }

    [Fact]
    public async Task Converting_renamed_metadata_keeps_existing_outputs_untouched_when_the_fresh_path_is_occupied()
    {
        // Arrange
        using var outputDir = TempDirectory.Create();
        using var renamedInput = TempFile.Create();

        var templateOutputPath =
            outputDir.Path
            + Path.DirectorySeparatorChar
            + "%G"
            + Path.DirectorySeparatorChar
            + "%T"
            + Path.DirectorySeparatorChar
            + "%C"
            + Path.DirectorySeparatorChar;

        // One message per partition, so the 3-message sample produces 3 partition files. The
        // relocator only finds an "occupied" destination once it actually looks for one -- it's
        // gated on the *base* (partition 0) path being free, so the collision needs to show up
        // on a later partition for the fallback to be exercised at all.
        var partitionLimit = PartitionLimit.Parse("1");

        var oldBasePath = Path.Combine(
            outputDir.Path,
            "Test Guild",
            "Text Channels",
            "general",
            "Test Guild - Text Channels - general [1063903591533404161].txt"
        );
        var oldPart3Path = Path.Combine(
            outputDir.Path,
            "Test Guild",
            "Text Channels",
            "general",
            "Test Guild - Text Channels - general [1063903591533404161] [part 3].txt"
        );
        var oldPart2Path = Path.Combine(
            outputDir.Path,
            "Test Guild",
            "Text Channels",
            "general",
            "Test Guild - Text Channels - general [1063903591533404161] [part 2].txt"
        );

        var newBasePath = Path.Combine(
            outputDir.Path,
            "Test Guild",
            "Text Channels",
            "general-renamed",
            "Test Guild - Text Channels - general-renamed [1063903591533404161].txt"
        );
        var newPart3Path = Path.Combine(
            outputDir.Path,
            "Test Guild",
            "Text Channels",
            "general-renamed",
            "Test Guild - Text Channels - general-renamed [1063903591533404161] [part 3].txt"
        );
        var newPart2Path = Path.Combine(
            outputDir.Path,
            "Test Guild",
            "Text Channels",
            "general-renamed",
            "Test Guild - Text Channels - general-renamed [1063903591533404161] [part 2].txt"
        );

        // First conversion establishes 3 partitions under the original name.
        await new ConvertCommand
        {
            InputPaths = [SampleExportFilePath],
            OutputPath = templateOutputPath,
            ExportFormat = ExportFormat.PlainText,
            PartitionLimit = partitionLimit,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        File.Exists(oldBasePath).Should().BeTrue();
        File.Exists(oldPart2Path).Should().BeTrue();

        // The channel gets renamed, and -- independently -- something already occupies one of
        // the future partitions at the freshly-computed location. Since convert no longer
        // relocates prior output, the old files stay put and the new conversion proceeds on
        // the fresh path.
        Directory.CreateDirectory(Path.GetDirectoryName(newPart2Path)!);
        await File.WriteAllTextAsync(newPart2Path, "PRE-EXISTING UNRELATED FILE");

        var sampleJson = await File.ReadAllTextAsync(SampleExportFilePath);
        var renamedJson = sampleJson.Replace(
            "\"name\": \"general\"",
            "\"name\": \"general-renamed\""
        );
        await File.WriteAllTextAsync(renamedInput.Path, renamedJson);

        // Act
        await new ConvertCommand
        {
            InputPaths = [renamedInput.Path],
            OutputPath = templateOutputPath,
            ExportFormat = ExportFormat.PlainText,
            PartitionLimit = partitionLimit,
            Locale = "en-US",
            IsUtcNormalizationEnabled = true,
        }.ExecuteAsync(new FakeConsole());

        // Assert: the old converted output remains intact while the new conversion writes its
        // own outputs under the fresh path.
        File.Exists(newBasePath).Should().BeTrue();
        File.Exists(newPart2Path).Should().BeTrue();
        File.Exists(newPart3Path).Should().BeTrue();

        File.Exists(oldBasePath).Should().BeTrue();
        File.Exists(oldPart3Path).Should().BeTrue();
        var oldContent = await File.ReadAllTextAsync(oldBasePath);
        oldContent.Should().Contain("Channel: Text Channels / general");

        var newContent = await File.ReadAllTextAsync(newBasePath);
        newContent.Should().Contain("Channel: Text Channels / general-renamed");

        var newPart2Content = await File.ReadAllTextAsync(newPart2Path);
        newPart2Content.Should().Contain("Channel: Text Channels / general-renamed");
    }
}
