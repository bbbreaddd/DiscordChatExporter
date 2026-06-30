using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Converting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using DiscordChatExporter.Core.Utils;
using Gress;
using Spectre.Console;

namespace DiscordChatExporter.Cli.Commands;

[Command(
    "convert",
    Description = "Converts previously exported JSON chat log(s) into another format, without contacting Discord."
)]
public partial class ConvertCommand : ICommand
{
    [CommandOption(
        "input",
        'i',
        Description = "Path to a JSON export file, or a directory containing JSON export files (searched recursively)."
    )]
    public required IReadOnlyList<string> InputPaths { get; set; }

    [CommandOption(
        "output",
        'o',
        Description = "Output file or directory path. "
            + "If a directory is specified, file names will be generated automatically based on the channel names and export parameters. "
            + "Directory paths must end with a slash to avoid ambiguity. "
            + "Supports template tokens, see the documentation for more info."
    )]
    public string OutputPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = Path.GetFullPath(value);
    } = Directory.GetCurrentDirectory();

    [CommandOption(
        "format",
        'f',
        Description = "Output format. Converting to JSON is not supported, as the input is already JSON."
    )]
    public ExportFormat ExportFormat { get; set; } = ExportFormat.HtmlDark;

    [CommandOption(
        "partition",
        'p',
        Description = "Split the output into partitions, each limited to the specified "
            + "number of messages (e.g. '100') or file size (e.g. '10mb')."
    )]
    public PartitionLimit PartitionLimit { get; set; } = PartitionLimit.Null;

    [CommandOption(
        "filter",
        Description = "Only include messages that satisfy this filter. "
            + "See the documentation for more info."
    )]
    public MessageFilter MessageFilter { get; set; } = MessageFilter.Null;

    [CommandOption("parallel", Description = "Limits how many files can be converted in parallel.")]
    public int ParallelLimit { get; set; } = 1;

    [CommandOption(
        "skip-unchanged",
        Description = "Skip files whose existing output is newer than the input JSON export."
    )]
    public bool ShouldSkipUnchanged { get; set; }

    [CommandOption(
        "markdown",
        Description = "Process markdown, mentions, and other special tokens. "
            + "Has no effect if the input was exported with this option disabled."
    )]
    public bool ShouldFormatMarkdown { get; set; } = true;

    [CommandOption(
        "media",
        Description = "Reference locally cached assets (user avatars, attached files, embedded images, etc.) from the media directory. "
            + "Convert never contacts Discord: any asset not already cached keeps its original (remote) URL. "
            + "Use 'exportguild --cache-media' (or 'checkmedia --download') to populate the cache beforehand."
    )]
    public bool ShouldDownloadAssets { get; set; }

    [CommandOption(
        "reuse-media",
        Description = "Accepted for backwards compatibility; convert always reuses cached assets and never downloads, so this is implied by --media."
    )]
    public bool ShouldReuseAssets { get; set; }

    [CommandOption(
        "media-dir",
        Description = "Download assets to this directory. "
            + "If not specified, the asset directory path will be derived from the output path."
    )]
    public string? AssetsDirPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = value is not null ? Path.GetFullPath(value) : null;
    }

    [CommandOption(
        "locale",
        Description = "Locale to use when formatting dates and numbers. "
            + "If not specified, the default system locale will be used."
    )]
    public string? Locale { get; set; }

    [CommandOption("utc", Description = "Normalize all timestamps to UTC+0.")]
    public bool IsUtcNormalizationEnabled { get; set; }

    [CommandOption(
        "html-shared-assets",
        Description = "Write shared CSS/JS/icon files into a local _dce directory and have HTML output reference them instead of inlining them into every page. Only valid for HTML formats."
    )]
    public bool ShouldUseHtmlSharedAssets { get; set; }

    [CommandOption(
        "compact",
        'c',
        Description = "Minify HTML file size by shortening CSS class names and omitting unused metadata."
    )]
    public bool IsCompact { get; set; }

    [CommandOption(
        "strict",
        Description = "Treat any per-file conversion error as a fatal failure. "
            + "By default the command succeeds as long as at least one file converted successfully."
    )]
    public bool IsStrict { get; set; }

    public async ValueTask ExecuteAsync(IConsole console)
    {
        var cancellationToken = console.RegisterCancellationHandler();

        if (ExportFormat == ExportFormat.Json)
        {
            throw new CommandException(
                "Cannot convert to JSON, because the input is already a JSON export. "
                    + "Choose a different output format."
            );
        }

        if (
            ShouldUseHtmlSharedAssets
            && ExportFormat is not ExportFormat.HtmlDark and not ExportFormat.HtmlLight
        )
        {
            throw new CommandException(
                "Option --html-shared-assets can only be used with HTML formats."
            );
        }

        if (IsCompact && ExportFormat is not ExportFormat.HtmlDark and not ExportFormat.HtmlLight)
        {
            throw new CommandException("Option --compact can only be used with HTML formats.");
        }

        // Asset reuse can only be enabled if the download assets option is set
        if (ShouldReuseAssets && !ShouldDownloadAssets)
            throw new CommandException("Option --reuse-media cannot be used without --media.");

        // Assets directory can only be specified if the download assets option is set
        if (!string.IsNullOrWhiteSpace(AssetsDirPath) && !ShouldDownloadAssets)
            throw new CommandException("Option --media-dir cannot be used without --media.");

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

        inputFilePaths.RemoveAll(path =>
            string.Equals(
                Path.GetFileName(path),
                ".discord_backup_manifest.json",
                StringComparison.OrdinalIgnoreCase
            )
        );

        if (inputFilePaths.Count <= 0)
        {
            throw new CommandException(
                "No JSON export files found at the specified input path(s)."
            );
        }

        static int GetPartitionIndex(string filePath)
        {
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
            var match = System.Text.RegularExpressions.Regex.Match(
                fileNameWithoutExt,
                @"\[part\s*(\d+)\]$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );

            return match.Success ? int.Parse(match.Groups[1].Value) - 1 : 0;
        }

        static string GetBaseFileNameKey(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath) ?? "";
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

            var baseName = System.Text.RegularExpressions.Regex.Replace(
                fileNameWithoutExt,
                @"\s*\[part\s*\d+\]$",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );

            return Path.Combine(dir, baseName);
        }

        var groupedInputFiles = inputFilePaths
            .GroupBy(GetBaseFileNameKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(GetPartitionIndex).ToList())
            .ToList();

        // Make sure the user does not try to convert multiple files into one file.
        // Output path must either be a directory or contain template tokens for this to work.
        var isValidOutputPath =
            // Anything is valid when converting a single channel
            groupedInputFiles.Count <= 1
            // When using template tokens, assume the user knows what they're doing
            || OutputPath.Contains('%')
            // Otherwise, require an existing directory or an unambiguous directory path
            || Directory.Exists(OutputPath)
            || Path.EndsInDirectorySeparator(OutputPath);

        if (!isValidOutputPath)
        {
            throw new CommandException(
                "Attempted to convert multiple files, but the output path is neither a directory nor a template. "
                    + "If the provided output path is meant to be treated as a directory, make sure it ends with a slash. "
                    + $"Provided output path: '{OutputPath}'."
            );
        }

        var errorsByFile = new ConcurrentDictionary<string, string>();
        var skippedFilePaths = new ConcurrentBag<string>();
        var isSingleExplicitOutputFile =
            groupedInputFiles.Count == 1
            && !OutputPath.Contains('%')
            && !Directory.Exists(OutputPath)
            && !Path.EndsInDirectorySeparator(OutputPath);

        ExportRequest CreateRequest(
            Guild guild,
            Channel channel,
            Snowflake? after,
            Snowflake? before
        ) =>
            new(
                guild,
                channel,
                OutputPath,
                AssetsDirPath,
                ExportFormat,
                after,
                before,
                PartitionLimit,
                MessageFilter,
                false,
                ShouldFormatMarkdown,
                ShouldDownloadAssets,
                ShouldReuseAssets,
                Locale,
                IsUtcNormalizationEnabled,
                // Convert is an offline operation: reference cached assets, but never contact Discord.
                isOfflineAssetMode: true,
                shouldUseHtmlSharedAssets: ShouldUseHtmlSharedAssets,
                isCompact: IsCompact
            );

        bool TryGetKnownOutputFilePath(string inputFilePath, out string outputFilePath)
        {
            if (isSingleExplicitOutputFile)
            {
                outputFilePath = OutputPath;
                return true;
            }

            outputFilePath = "";
            return false;
        }

        static bool IsOutputNewerThanInput(
            IReadOnlyList<string> inputFilePaths,
            string outputFilePath
        )
        {
            if (!File.Exists(outputFilePath))
                return false;

            // Use the most recently modified input partition as the reference. Only checking
            // the first partition misses the case where new messages were appended to a later
            // partition while earlier ones stayed unchanged.
            var inputWriteTime = inputFilePaths.Max(File.GetLastWriteTimeUtc);

            for (var index = 0; ; index++)
            {
                var partitionFilePath = GetPartitionFilePath(outputFilePath, index);

                if (!File.Exists(partitionFilePath))
                    return index > 0;

                if (File.GetLastWriteTimeUtc(partitionFilePath) < inputWriteTime)
                    return false;
            }
        }

        static string GetPartitionFilePath(string baseFilePath, int partitionIndex)
        {
            if (partitionIndex <= 0)
                return baseFilePath;

            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(baseFilePath);
            var fileExt = Path.GetExtension(baseFilePath);
            var fileName = $"{fileNameWithoutExt} [part {partitionIndex + 1}]{fileExt}";
            var dirPath = Path.GetDirectoryName(baseFilePath);

            return !string.IsNullOrWhiteSpace(dirPath) ? Path.Combine(dirPath, fileName) : fileName;
        }

        // Converts a group of partitioned files representing a single channel.
        async ValueTask<bool> ConvertFileGroupAsync(
            IReadOnlyList<string> groupFilePaths,
            IProgress<Percentage>? progress,
            CancellationToken innerCancellationToken
        )
        {
            var firstFilePath = groupFilePaths[0];

            if (
                ShouldSkipUnchanged
                && TryGetKnownOutputFilePath(firstFilePath, out var knownOutputFilePath)
                && IsOutputNewerThanInput(groupFilePaths, knownOutputFilePath)
            )
            {
                return false;
            }

            var (guild, channel, after, before) = ExportedChatParser.ParseMetadata(firstFilePath);
            var request = CreateRequest(guild, channel, after, before);

            if (
                ShouldSkipUnchanged
                && IsOutputNewerThanInput(groupFilePaths, request.OutputFilePath)
            )
            {
                return false;
            }

            Func<string, Func<string, string>?> getRebaseLocalAssetPath = filePath =>
            {
                Func<string, string>? rebaseLocalAssetPath = null;
                if (ShouldDownloadAssets)
                {
                    rebaseLocalAssetPath = relativeLocalPath =>
                    {
                        var inputDirPath =
                            Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();
                        var absolutePath = Path.GetFullPath(
                            Path.Combine(inputDirPath, relativeLocalPath)
                        );
                        var rebasedPath = Path.GetRelativePath(request.OutputDirPath, absolutePath);

                        return request.Format is ExportFormat.HtmlDark or ExportFormat.HtmlLight
                            ? Url.EncodeFilePath(rebasedPath)
                            : rebasedPath;
                    };
                }
                return rebaseLocalAssetPath;
            };

            await new ChatConverter().ConvertStreamingAsync(
                groupFilePaths,
                request,
                getRebaseLocalAssetPath,
                progress,
                innerCancellationToken
            );

            return true;
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, ParallelLimit),
            CancellationToken = cancellationToken,
        };

        await console.Output.WriteLineAsync($"Converting {groupedInputFiles.Count} channel(s)...");

        if (console.IsOutputRedirected)
        {
            var completed = 0;
            var writeLock = new object();

            await Parallel.ForEachAsync(
                groupedInputFiles,
                parallelOptions,
                async (fileGroup, innerCancellationToken) =>
                {
                    var converted = true;
                    try
                    {
                        converted = await ConvertFileGroupAsync(
                            fileGroup,
                            null,
                            innerCancellationToken
                        );
                        if (!converted)
                            skippedFilePaths.Add(fileGroup[0]);
                    }
                    catch (Exception ex)
                    {
                        errorsByFile[fileGroup[0]] = ex.Message;
                    }

                    var index = Interlocked.Increment(ref completed);
                    if (converted || errorsByFile.ContainsKey(fileGroup[0]))
                    {
                        lock (writeLock)
                        {
                            console.Output.WriteLine(
                                $"[{index}/{groupedInputFiles.Count}] {Path.GetFileName(GetBaseFileNameKey(fileGroup[0]))}"
                            );
                        }
                    }
                }
            );
        }
        else
        {
            await console
                .CreateProgressTicker()
                .HideCompleted(ParallelLimit > 1)
                .StartAsync(async ctx =>
                {
                    await Parallel.ForEachAsync(
                        groupedInputFiles,
                        parallelOptions,
                        async (fileGroup, innerCancellationToken) =>
                        {
                            try
                            {
                                var converted = true;
                                await ctx.StartTaskAsync(
                                    Markup.Escape(
                                        Path.GetFileName(GetBaseFileNameKey(fileGroup[0]))
                                    ),
                                    async progress =>
                                        converted = await ConvertFileGroupAsync(
                                            fileGroup,
                                            progress.ToPercentageBased(),
                                            innerCancellationToken
                                        )
                                );

                                if (!converted)
                                    skippedFilePaths.Add(fileGroup[0]);
                            }
                            catch (Exception ex)
                            {
                                errorsByFile[fileGroup[0]] = ex.Message;
                            }
                        }
                    );
                });
        }

        // Print the result
        using (console.WithForegroundColor(ConsoleColor.White))
        {
            await console.Output.WriteLineAsync(
                $"Successfully converted {groupedInputFiles.Count - errorsByFile.Count - skippedFilePaths.Count} channel(s)."
            );

            if (!skippedFilePaths.IsEmpty)
                await console.Output.WriteLineAsync(
                    $"Skipped {skippedFilePaths.Count} unchanged channel(s)."
                );
        }

        // Print errors
        if (errorsByFile.Any())
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Red))
            {
                await console.Error.WriteLineAsync("Failed to convert the following file(s):");
            }

            foreach (var (filePath, message) in errorsByFile)
            {
                await console.Error.WriteAsync($"{filePath}: ");
                using (console.WithForegroundColor(ConsoleColor.Red))
                    await console.Error.WriteLineAsync(message);
            }

            await console.Error.WriteLineAsync();
        }

        // With --strict, any per-file error is fatal. Without it, only fail if every
        // file failed (the historical default, kept for backwards compatibility).
        if (IsStrict ? errorsByFile.Count > 0 : errorsByFile.Count >= groupedInputFiles.Count)
            throw new CommandException("Conversion failed.");
    }
}
