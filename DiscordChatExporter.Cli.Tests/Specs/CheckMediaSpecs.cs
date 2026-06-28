using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands;
using FluentAssertions;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class CheckMediaSpecs
{
    private static readonly string SampleExportFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "Data",
        "sample-export.json"
    );

    private static string ReadStream(Stream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task I_ignore_the_incremental_manifest_when_scanning_a_directory()
    {
        // Arrange
        using var inputDir = TempDirectory.Create();
        using var mediaDir = TempDirectory.Create();
        File.Copy(SampleExportFilePath, Path.Combine(inputDir.Path, "sample-export.json"));
        await File.WriteAllTextAsync(
            Path.Combine(inputDir.Path, ".discord_backup_manifest.json"),
            "{}"
        );

        await using var outputStream = new MemoryStream();
        await using var errorStream = new MemoryStream();
        using var console = new FakeConsole(Stream.Null, outputStream, errorStream);

        // Act
        await new CheckMediaCommand
        {
            InputPaths = [inputDir.Path],
            AssetsDirPath = mediaDir.Path,
        }.ExecuteAsync(console);

        var output = ReadStream(outputStream);
        var error = ReadStream(errorStream);

        // Assert
        output.Should().Contain("Checking 1 file(s) for missing media...");
        error.Should().NotContain(".discord_backup_manifest.json");
        error.Should().NotContain("not a valid JSON export");
    }
}
