using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Converting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;

namespace DiscordChatExporter.Cli.Commands;

[Command(
    "checkmedia",
    Description = "Checks previously exported JSON chat log(s) for media assets (attached files, "
        + "stickers, embedded images) missing from a local media directory, and optionally "
        + "downloads the missing ones (refreshing expired Discord links when a token is provided). "
        + "Does not convert anything."
)]
public partial class CheckMediaCommand : DiscordCommandBase
{
    [CommandOption(
        "input",
        'i',
        Description = "Path to a JSON export file, or a directory containing JSON export files (searched recursively)."
    )]
    public required IReadOnlyList<string> InputPaths { get; set; }

    [CommandOption(
        "media-dir",
        Description = "Media directory to check against. "
            + "Supports template tokens (e.g. '%G/%T/%C/'), matching the directory used by the original export."
    )]
    public required string AssetsDirPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        set => field = Path.GetFullPath(value);
    }

    [CommandOption(
        "download",
        Description = "Download the missing assets into the media directory, instead of only reporting them. "
            + "Expired Discord CDN links are automatically refreshed when a --token/--token-file is provided. "
            + "Assets hosted on external sites that are no longer reachable cannot be recovered."
    )]
    public bool ShouldDownloadMissing { get; set; }

    [CommandOption(
        "parallel",
        Description = "Limits how many files can be checked (and downloaded) in parallel."
    )]
    public int ParallelLimit { get; set; } = 1;

    [CommandOption(
        "download-parallel",
        Description = "Limits how many assets can be downloaded in parallel within a single file. "
            + "Combined with --parallel, the total concurrency is roughly the product of the two."
    )]
    public int DownloadParallelLimit { get; set; } = 4;

    [CommandOption(
        "retry-failed",
        Description = "Re-attempt assets that a previous run recorded as permanently failed. "
            + "By default these are skipped so repeat runs don't keep retrying the same dead links."
    )]
    public bool ShouldRetryFailed { get; set; }

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        await base.ExecuteAsync(console);

        var cancellationToken = console.RegisterCancellationHandler();

        // A token is only needed to refresh expired Discord links while downloading. Report-only
        // and token-less downloads still work (the latter just can't recover expired CDN links).
        var hasToken = !string.IsNullOrWhiteSpace(Token) || !string.IsNullOrWhiteSpace(TokenFile);
        var discord = ShouldDownloadMissing && hasToken ? Discord : null;

        if (ShouldDownloadMissing && !hasToken)
        {
            using (console.WithForegroundColor(ConsoleColor.DarkYellow))
            {
                await console.Error.WriteLineAsync(
                    "Warning: downloading without a token — expired Discord CDN links cannot be "
                        + "refreshed and will fail. Provide --token/--token-file to recover them."
                );
            }
        }

        // Resolve input files
        var inputFilePaths = new List<string>();
        foreach (var inputPath in InputPaths)
        {
            if (Directory.Exists(inputPath))
            {
                inputFilePaths.AddRange(
                    Directory.EnumerateFiles(inputPath, "*.json", SearchOption.AllDirectories)
                );
            }
            else if (File.Exists(inputPath))
            {
                inputFilePaths.Add(inputPath);
            }
            else
            {
                throw new CommandException($"Input path '{inputPath}' does not exist.");
            }
        }

        if (inputFilePaths.Count <= 0)
        {
            throw new CommandException(
                "No JSON export files found at the specified input path(s)."
            );
        }

        await console.Output.WriteLineAsync(
            $"Checking {inputFilePaths.Count} file(s) for missing media..."
        );

        var totalReferenced = 0;
        var totalMissing = 0;
        var totalSkipped = 0;
        var totalDownloaded = 0;
        var totalFailed = 0;
        var writeLock = new object();

        await Parallel.ForEachAsync(
            inputFilePaths,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, ParallelLimit),
                CancellationToken = cancellationToken,
            },
            async (inputFilePath, innerCancellationToken) =>
            {
                ExportedChat chat;
                try
                {
                    await using var inputStream = File.OpenRead(inputFilePath);
                    using var document = await JsonDocument.ParseAsync(
                        inputStream,
                        cancellationToken: innerCancellationToken
                    );

                    chat = ExportedChatParser.Parse(document.RootElement);
                }
                // A directory of exports can legitimately contain unrelated JSON (e.g. a backup
                // manifest) or a half-written file. Skip anything that isn't a valid export rather
                // than aborting the whole sweep.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    using (console.WithForegroundColor(ConsoleColor.DarkYellow))
                    {
                        lock (writeLock)
                        {
                            console.Error.WriteLine(
                                $"Skipped '{Path.GetFileName(inputFilePath)}': not a valid JSON export."
                            );
                        }
                    }
                    return;
                }

                // Reuse the export request machinery purely to resolve the per-channel media
                // directory from the template (e.g. '%G/%T/%C/'); nothing is written.
                var request = new ExportRequest(
                    chat.Guild,
                    chat.Channel,
                    Directory.GetCurrentDirectory(),
                    AssetsDirPath,
                    ExportFormat.PlainText,
                    chat.After,
                    chat.Before,
                    PartitionLimit.Null,
                    MessageFilter.Null,
                    false,
                    false,
                    false,
                    false,
                    null,
                    false
                );

                var result = await MediaInspector.InspectAsync(
                    chat,
                    request.AssetsDirPath,
                    ShouldDownloadMissing,
                    discord,
                    DownloadParallelLimit,
                    ShouldRetryFailed,
                    innerCancellationToken
                );

                Interlocked.Add(ref totalReferenced, result.ReferencedCount);
                Interlocked.Add(ref totalMissing, result.MissingCount);
                Interlocked.Add(ref totalSkipped, result.SkippedCount);
                Interlocked.Add(ref totalDownloaded, result.DownloadedCount);
                Interlocked.Add(ref totalFailed, result.FailedCount);

                if (result.MissingCount > 0)
                {
                    string suffix;
                    if (ShouldDownloadMissing)
                    {
                        suffix =
                            $" (downloaded {result.DownloadedCount}, failed {result.FailedCount}";
                        // Known-dead links skipped this run, to explain why some missing assets
                        // weren't downloaded.
                        suffix +=
                            result.SkippedCount > 0
                                ? $", skipped {result.SkippedCount} known-dead)"
                                : ")";
                    }
                    else
                    {
                        suffix =
                            result.SkippedCount > 0 ? $" ({result.SkippedCount} known-dead)" : "";
                    }

                    lock (writeLock)
                    {
                        console.Output.WriteLine(
                            $"{Path.GetFileName(inputFilePath)}: {result.MissingCount} missing of {result.ReferencedCount}{suffix}"
                        );
                    }
                }
            }
        );

        await console.Output.WriteLineAsync();
        using (console.WithForegroundColor(ConsoleColor.White))
        {
            if (totalMissing <= 0)
            {
                await console.Output.WriteLineAsync(
                    $"All {totalReferenced} referenced media asset(s) are present in the cache."
                );
            }
            else if (ShouldDownloadMissing)
            {
                var skippedNote =
                    totalSkipped > 0
                        ? $", skipped {totalSkipped} known-dead (use --retry-failed to re-attempt)"
                        : "";

                await console.Output.WriteLineAsync(
                    $"{totalMissing} of {totalReferenced} media asset(s) were missing — "
                        + $"downloaded {totalDownloaded}, failed {totalFailed}{skippedNote}."
                );
            }
            else
            {
                var skippedNote =
                    totalSkipped > 0
                        ? $" ({totalSkipped} already known-dead from a previous run)"
                        : "";

                await console.Output.WriteLineAsync(
                    $"{totalMissing} of {totalReferenced} media asset(s) are missing from the cache{skippedNote}. "
                        + "Re-run with --download to fetch them."
                );
            }
        }
    }
}
