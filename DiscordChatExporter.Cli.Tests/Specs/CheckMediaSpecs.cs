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

    [Fact]
    public async Task I_can_save_the_failure_ledger_without_leaving_stray_temp_files_behind()
    {
        // Arrange. sample-export.json references this attachment URL; pre-seeding it into the
        // ledger as already-dead means --download won't attempt any network request for it (no
        // token is provided either), but the ledger still gets rewritten from the current
        // reference set -- exercising the save path (now a process-unique temp file + atomic
        // rename) without needing to mock any HTTP/Discord calls.
        const string deadUrl =
            "https://cdn.discordapp.com/attachments/1063903591533404161/1100000000000000010/image.png";

        using var inputDir = TempDirectory.Create();
        using var mediaDir = TempDirectory.Create();
        File.Copy(SampleExportFilePath, Path.Combine(inputDir.Path, "sample-export.json"));

        var ledgerPath = Path.Combine(mediaDir.Path, ".checkmedia-failures.txt");
        await File.WriteAllTextAsync(ledgerPath, deadUrl);

        using var console = new FakeConsole();

        // Act
        await new CheckMediaCommand
        {
            InputPaths = [inputDir.Path],
            AssetsDirPath = mediaDir.Path,
            ShouldDownloadMissing = true,
        }.ExecuteAsync(console);

        // Assert: the ledger survived the rewrite (still references the same dead URL) and no
        // leftover ".tmp" file from the save was left behind in the media directory.
        File.Exists(ledgerPath).Should().BeTrue();
        (await File.ReadAllTextAsync(ledgerPath)).Should().Contain(deadUrl);

        Directory
            .GetFiles(mediaDir.Path, "*.tmp", SearchOption.AllDirectories)
            .Should()
            .BeEmpty("the save should never leave its temp file behind");
    }
}
