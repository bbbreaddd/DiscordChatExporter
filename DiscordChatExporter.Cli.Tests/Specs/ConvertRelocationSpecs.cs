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

// Covers the rename-aware relocation that ConvertCommand inherits from
// ExistingOutputRelocator: a converted output's path is derived from the guild/category/
// channel's current name, so a rename moves the computed path -- even into a different
// directory entirely, when the output uses a template like "%G/%T/%C/". These tests make sure
// the previous output follows the rename instead of being left behind as a stale duplicate.
public class ConvertRelocationSpecs
{
    private static readonly string SampleExportFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "Data",
        "sample-export.json"
    );

    [Fact]
    public async Task Converting_renamed_metadata_relocates_the_previous_output_under_a_template_path()
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
        File.Exists(oldFilePath).Should().BeFalse();
        File.Exists(newFilePath).Should().BeTrue();

        var content = await File.ReadAllTextAsync(newFilePath);
        content.Should().Contain("Hello world, this is a test message.");
    }

    [Fact]
    public async Task Converting_renamed_metadata_falls_back_to_the_old_path_when_the_fresh_path_is_occupied()
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

        // The channel gets renamed, and -- independently -- something unrelated already
        // occupies one of the *other* partitions at the freshly-computed location (even though
        // partition 0 itself is still free). The whole channel must stay together, so this
        // alone should be enough to veto the relocation.
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

        // Assert: nothing was ever created under the fresh name, the occupying file is left
        // untouched, and the converted output stays on the old path (now reflecting the
        // renamed metadata) instead of being abandoned or split into a second history.
        File.Exists(newBasePath).Should().BeFalse();

        var occupyingContent = await File.ReadAllTextAsync(newPart2Path);
        occupyingContent.Should().Be("PRE-EXISTING UNRELATED FILE");

        File.Exists(oldBasePath).Should().BeTrue();
        var oldContent = await File.ReadAllTextAsync(oldBasePath);
        oldContent.Should().Contain("Channel: Text Channels / general-renamed");
    }
}
