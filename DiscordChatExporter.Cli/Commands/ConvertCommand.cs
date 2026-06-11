using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Converting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
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
        "markdown",
        Description = "Process markdown, mentions, and other special tokens. "
            + "Has no effect if the input was exported with this option disabled."
    )]
    public bool ShouldFormatMarkdown { get; set; } = true;

    [CommandOption(
        "media",
        Description = "Download assets referenced by the export (user avatars, attached files, embedded images, etc.)."
    )]
    public bool ShouldDownloadAssets { get; set; }

    [CommandOption(
        "reuse-media",
        Description = "Reuse previously downloaded assets to avoid redundant requests."
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

        // Make sure the user does not try to convert multiple files into one file.
        // Output path must either be a directory or contain template tokens for this to work.
        var isValidOutputPath =
            // Anything is valid when converting a single file
            inputFilePaths.Count <= 1
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

        // Convert
        var errorsByFile = new ConcurrentDictionary<string, string>();

        await console.Output.WriteLineAsync($"Converting {inputFilePaths.Count} file(s)...");
        await console
            .CreateProgressTicker()
            .HideCompleted(
                // When converting multiple files in parallel, hide the completed tasks
                // because it gets hard to visually parse them as they complete out of order.
                ParallelLimit > 1
            )
            .StartAsync(async ctx =>
            {
                await Parallel.ForEachAsync(
                    inputFilePaths,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, ParallelLimit),
                        CancellationToken = cancellationToken,
                    },
                    async (inputFilePath, innerCancellationToken) =>
                    {
                        try
                        {
                            await ctx.StartTaskAsync(
                                Markup.Escape(Path.GetFileName(inputFilePath)),
                                async progress =>
                                {
                                    await using var inputStream = File.OpenRead(inputFilePath);
                                    using var document = await JsonDocument.ParseAsync(
                                        inputStream,
                                        cancellationToken: innerCancellationToken
                                    );

                                    var chat = ExportedChatParser.Parse(document.RootElement);

                                    var request = new ExportRequest(
                                        chat.Guild,
                                        chat.Channel,
                                        OutputPath,
                                        AssetsDirPath,
                                        ExportFormat,
                                        chat.After,
                                        chat.Before,
                                        PartitionLimit,
                                        MessageFilter,
                                        false,
                                        ShouldFormatMarkdown,
                                        ShouldDownloadAssets,
                                        ShouldReuseAssets,
                                        Locale,
                                        IsUtcNormalizationEnabled
                                    );

                                    await new ChatConverter().ConvertAsync(
                                        chat,
                                        request,
                                        progress.ToPercentageBased(),
                                        innerCancellationToken
                                    );
                                }
                            );
                        }
                        catch (Exception ex)
                        {
                            errorsByFile[inputFilePath] = ex.Message;
                        }
                    }
                );
            });

        // Print the result
        using (console.WithForegroundColor(ConsoleColor.White))
        {
            await console.Output.WriteLineAsync(
                $"Successfully converted {inputFilePaths.Count - errorsByFile.Count} file(s)."
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

        // Fail the command only if ALL files failed to convert.
        // If only some files failed, it's okay.
        if (errorsByFile.Count >= inputFilePaths.Count)
            throw new CommandException("Conversion failed.");
    }
}
