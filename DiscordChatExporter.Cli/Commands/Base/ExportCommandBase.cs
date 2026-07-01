using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli;
using DiscordChatExporter.Cli.Commands.Converters;
using DiscordChatExporter.Cli.Commands.Shared;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Database;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using Gress;
using Spectre.Console;

namespace DiscordChatExporter.Cli.Commands.Base;

public abstract class ExportCommandBase : DiscordCommandBase
{
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

    [CommandOption("format", 'f', Description = "Export format.")]
    public ExportFormat ExportFormat { get; set; } = ExportFormat.HtmlDark;

    [CommandOption(
        "after",
        Description = "Only include messages sent after this date or message ID."
    )]
    public Snowflake? After { get; set; }

    [CommandOption(
        "before",
        Description = "Only include messages sent before this date or message ID."
    )]
    public Snowflake? Before { get; set; }

    [CommandOption(
        "partition",
        'p',
        Description = "Split the output into partitions, each limited to the specified "
            + "number of messages (e.g. '100') or file size (e.g. '10mb')."
    )]
    public PartitionLimit PartitionLimit { get; set; } = PartitionLimit.Null;

    [CommandOption(
        "include-threads",
        Description = "Which types of threads should be included.",
        Converter = typeof(ThreadInclusionModeInputConverter)
    )]
    public ThreadInclusionMode ThreadInclusionMode { get; set; } = ThreadInclusionMode.None;

    [CommandOption(
        "filter",
        Description = "Only include messages that satisfy this filter. "
            + "See the documentation for more info."
    )]
    public MessageFilter MessageFilter { get; set; } = MessageFilter.Null;

    [CommandOption(
        "parallel",
        Description = "Limits how many channels can be exported in parallel."
    )]
    public int ParallelLimit { get; set; } = 1;

    [CommandOption(
        "reverse",
        Description = "Export messages in reverse chronological order (newest first)."
    )]
    public bool IsReverseMessageOrder { get; set; }

    [CommandOption(
        "markdown",
        Description = "Process markdown, mentions, and other special tokens."
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
    public bool ShouldReuseAssets { get; set; } = false;

    [CommandOption(
        "cache-media",
        Description = "Download assets to the media directory, but keep the original (remote) URLs "
            + "in the export instead of replacing them with local paths. "
            + "Useful for warming a media cache ahead of a later 'convert' run pointed at the same "
            + "--media-dir, without making this export's URLs depend on local file paths. "
            + "Cannot be combined with --media."
    )]
    public bool ShouldCacheAssetsOnly { get; set; } = false;

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
        "dateformat",
        Description = "This option doesn't do anything. Kept for backwards compatibility."
    )]
    public string DateFormat { get; set; } = "MM/dd/yyyy h:mm tt";

    [CommandOption(
        "locale",
        Description = "Locale to use when formatting dates and numbers. "
            + "If not specified, the default system locale will be used."
    )]
    public string? Locale { get; set; }

    [CommandOption("utc", Description = "Normalize all timestamps to UTC+0.")]
    public bool IsUtcNormalizationEnabled { get; set; } = false;

    [CommandOption(
        "html-shared-assets",
        Description = "Write shared CSS/JS/icon files into a local _dce directory and have HTML output reference them instead of inlining them into every page. Only valid for HTML formats."
    )]
    public bool ShouldUseHtmlSharedAssets { get; set; } = false;

    [CommandOption(
        "compact",
        Description = "Minify HTML file size by shortening CSS class names and omitting unused metadata."
    )]
    public bool IsCompact { get; set; } = false;

    [CommandOption(
        "incremental",
        Description = "Append new messages to an existing JSON export file instead of overwriting it."
    )]
    public bool IsIncremental { get; set; } = false;

    [CommandOption(
        "strict",
        Description = "Treat any per-channel export error as a fatal failure. "
            + "By default the command succeeds as long as at least one channel exported successfully."
    )]
    public bool IsStrict { get; set; } = false;

    [CommandOption(
        "fuck-russia",
        EnvironmentVariable = "FUCK_RUSSIA",
        Description = "Don't print the Support Ukraine message to the console.",
        // Use a converter to accept '1' as 'true' to reuse the existing environment variable
        Converter = typeof(TruthyBooleanInputConverter)
    )]
    public bool IsUkraineSupportMessageDisabled { get; set; } = false;

    [field: AllowNull, MaybeNull]
    protected ChannelExporter Exporter => field ??= new ChannelExporter(Discord);

    protected async ValueTask ExportAsync(IConsole console, IReadOnlyList<Channel> channels)
    {
        var cancellationToken = console.RegisterCancellationHandlerWithSignals();

        // --media and --cache-media are mutually exclusive ways of triggering asset downloads
        if (ShouldDownloadAssets && ShouldCacheAssetsOnly)
        {
            throw new CommandException(
                "Options --media and --cache-media cannot be used together."
            );
        }

        var isDownloadingAssets = ShouldDownloadAssets || ShouldCacheAssetsOnly;

        // Asset reuse can only be enabled if the download assets option is set
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/425
        if (ShouldReuseAssets && !isDownloadingAssets)
        {
            throw new CommandException(
                "Option --reuse-media cannot be used without --media or --cache-media."
            );
        }

        // Assets directory can only be specified if the download assets option is set
        if (!string.IsNullOrWhiteSpace(AssetsDirPath) && !isDownloadingAssets)
        {
            throw new CommandException(
                "Option --media-dir cannot be used without --media or --cache-media."
            );
        }

        if (IsIncremental && ExportFormat != ExportFormat.Json)
        {
            throw new CommandException(
                "Option --incremental can only be used with JSON format. "
                    + "Use the convert command to get other formats."
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

        if (ExportFormat == ExportFormat.Db)
        {
            if (PartitionLimit != PartitionLimit.Null)
            {
                throw new CommandException(
                    "Option --partition cannot be used with the 'db' format."
                );
            }

            if (ShouldDownloadAssets || ShouldCacheAssetsOnly)
            {
                throw new CommandException(
                    "Options --media and --cache-media have no effect with the 'db' format "
                        + "and cannot be used with it."
                );
            }
        }

        ExportManifest? manifest = null;
        string? manifestDir = null;
        if (IsIncremental)
        {
            manifestDir =
                Directory.Exists(OutputPath) || Path.EndsInDirectorySeparator(OutputPath)
                    ? OutputPath
                    : Path.GetDirectoryName(OutputPath) ?? Directory.GetCurrentDirectory();
            manifest = await ExportManifest.LoadAsync(manifestDir);

            // Pre-flight: verify the manifest directory is writable before spending hours
            // on the export only to discover at the end that progress can't be saved.
            if (await manifest.SaveAsync(manifestDir) is { } preflightError)
            {
                using (console.WithForegroundColor(ConsoleColor.Yellow))
                {
                    await console.Error.WriteLineAsync(
                        $"Warning: cannot write the incremental export manifest to '{manifestDir}'."
                    );
                    await console.Error.WriteLineAsync($"Reason: {preflightError.Message}");
                    await console.Error.WriteLineAsync(
                        "Without a working manifest, all channels will be fully re-exported on the next run."
                    );
                }

                await console.Error.WriteLineAsync();

                if (console.IsInputRedirected)
                {
                    throw new CommandException(
                        "Aborting: running non-interactively and the manifest cannot be saved. "
                            + "Fix the manifest directory permissions or available storage and try again."
                    );
                }

                await console.Error.WriteAsync(
                    "Continue without manifest tracking this run? [y/N]: "
                );
                await console.Error.FlushAsync();
                var response = await console.Input.ReadLineAsync();
                await console.Error.WriteLineAsync();
                if (!string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CommandException(
                        "Aborting. Fix the manifest directory permissions or available storage and try again."
                    );
                }
            }
        }

        // Make sure the user does not try to export multiple channels into one file.
        // With thread inclusion enabled or multiple input channels we know there will be
        // more than one output, so the output path must be a directory or template.
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/799
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/917
        var mightExportMultiple =
            channels.Count > 1 || ThreadInclusionMode != ThreadInclusionMode.None;
        var isValidOutputPath =
            // A database is always one consolidated file, regardless of channel count
            ExportFormat == ExportFormat.Db
            // Anything is valid when we know there's at most one channel
            || !mightExportMultiple
            // When using template tokens, assume the user knows what they're doing
            || OutputPath.Contains('%')
            // Otherwise, require an existing directory or an unambiguous directory path
            || Directory.Exists(OutputPath)
            || Path.EndsInDirectorySeparator(OutputPath);

        if (!isValidOutputPath)
        {
            throw new CommandException(
                "Attempted to export multiple channels, but the output path is neither a directory nor a template. "
                    + "If the provided output path is meant to be treated as a directory, make sure it ends with a slash. "
                    + $"Provided output path: '{OutputPath}'."
            );
        }

        // Build a streaming channel sequence that yields regular channels first, then
        // discovers and yields threads lazily as the API returns them.
        // This replaces the previous collect-all-threads-then-export pattern:
        // Parallel.ForEachAsync accepts IAsyncEnumerable<T> and starts exporting each
        // channel/thread as soon as it appears, rather than waiting for all 17k threads
        // to be held in memory before any export begins.
        var fetchedThreadsCount = 0;

        async IAsyncEnumerable<Channel> GetAllChannelsAsync()
        {
            // Non-forum regular channels are available immediately
            foreach (var ch in channels)
            {
                if (ch.Kind != ChannelKind.GuildForum)
                    yield return ch;
            }

            // Threads are discovered lazily, one API page at a time.
            // Drive the enumerator manually so we can catch errors from MoveNextAsync without
            // a try-catch around a yield (which C# forbids). A failure during thread
            // enumeration (auth error, 5xx after all retries, etc.) logs a warning and stops
            // yielding threads rather than propagating an exception through the async source,
            // which would cancel all in-progress channel exports already being processed in
            // parallel.
            if (ThreadInclusionMode != ThreadInclusionMode.None)
            {
                var threadSource = Discord.GetChannelThreadsAsync(
                    channels,
                    ThreadInclusionMode == ThreadInclusionMode.All,
                    Before,
                    After,
                    manifest,
                    cancellationToken
                );

                await using var enumerator = threadSource.GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // always propagate cancellation
                    }
                    catch (Exception ex)
                    {
                        using (console.WithForegroundColor(ConsoleColor.Yellow))
                        {
                            await console.Error.WriteLineAsync(
                                $"Warning: thread enumeration failed — threads will be skipped this run: {ex.Message}"
                            );
                        }
                        break;
                    }

                    if (!hasNext)
                        break;

                    var thread = enumerator.Current;
                    // Forums cannot be exported directly; their threads are already yielded
                    if (thread.Kind != ChannelKind.GuildForum)
                        yield return thread;

                    fetchedThreadsCount++;
                }
            }
        }

        // Export
        var errorsByChannel = new ConcurrentDictionary<Channel, string>();
        var warningsByChannel = new ConcurrentDictionary<Channel, string>();
        var totalChannelCount = 0;

        // A database export shares one consolidated store across every channel, opened once
        // up front and flushed/closed once the whole run finishes (successfully or not).
        var databaseStore =
            ExportFormat == ExportFormat.Db
                ? await SqliteExportStore.OpenAsync(OutputPath, cancellationToken)
                : null;

        try
        {
            await console.Output.WriteLineAsync("Exporting channels...");
            await console
                .CreateProgressTicker()
                .HideCompleted(
                    // When exporting multiple channels in parallel, hide the completed tasks
                    // because it gets hard to visually parse them as they complete out of order.
                    // https://github.com/Tyrrrz/DiscordChatExporter/issues/1124
                    ParallelLimit > 1
                )
                .StartAsync(async ctx =>
                {
                    await Parallel.ForEachAsync(
                        GetAllChannelsAsync(),
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Math.Max(1, ParallelLimit),
                            CancellationToken = cancellationToken,
                        },
                        async (channel, innerCancellationToken) =>
                        {
                            Interlocked.Increment(ref totalChannelCount);
                            try
                            {
                                await ctx.StartTaskAsync(
                                    Markup.Escape(channel.GetHierarchicalName()),
                                    async progress =>
                                    {
                                        var guild = await Discord.GetGuildAsync(
                                            channel.GuildId,
                                            innerCancellationToken
                                        );

                                        var request = new ExportRequest(
                                            guild,
                                            channel,
                                            OutputPath,
                                            AssetsDirPath,
                                            ExportFormat,
                                            After,
                                            Before,
                                            PartitionLimit,
                                            MessageFilter,
                                            IsReverseMessageOrder,
                                            ShouldFormatMarkdown,
                                            ShouldDownloadAssets,
                                            ShouldReuseAssets,
                                            Locale,
                                            IsUtcNormalizationEnabled,
                                            IsIncremental,
                                            ShouldCacheAssetsOnly,
                                            shouldUseHtmlSharedAssets: ShouldUseHtmlSharedAssets,
                                            isCompact: IsCompact
                                        );

                                        if (databaseStore is not null)
                                        {
                                            await Exporter.ExportChannelAsync(
                                                request,
                                                databaseStore,
                                                progress.ToExportProgress(
                                                    Markup.Escape(channel.GetHierarchicalName())
                                                ),
                                                innerCancellationToken
                                            );
                                        }
                                        else
                                        {
                                            await Exporter.ExportChannelAsync(
                                                request,
                                                manifest,
                                                progress.ToExportProgress(
                                                    Markup.Escape(channel.GetHierarchicalName())
                                                ),
                                                innerCancellationToken
                                            );
                                        }
                                    }
                                );
                            }
                            catch (ChannelEmptyException ex)
                            {
                                warningsByChannel[channel] = ex.Message;
                            }
                            catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                            {
                                errorsByChannel[channel] = ex.Message;
                            }
                        }
                    );
                });
        }
        finally
        {
            if (databaseStore is not null)
                await databaseStore.DisposeAsync();
        }

        if (ThreadInclusionMode != ThreadInclusionMode.None)
            await console.Output.WriteLineAsync($"Fetched {fetchedThreadsCount} thread(s).");

        if (manifest is not null && manifestDir is not null)
        {
            if (await manifest.SaveAsync(manifestDir) is not null)
            {
                using (console.WithForegroundColor(ConsoleColor.Yellow))
                {
                    await console.Error.WriteLineAsync(
                        "Warning: final manifest save failed (storage may have filled during the run). "
                            + "The next run will re-export all channels from scratch instead of resuming."
                    );
                }
            }
        }

        // Print the result
        using (console.WithForegroundColor(ConsoleColor.White))
        {
            await console.Output.WriteLineAsync(
                $"Successfully exported {totalChannelCount - errorsByChannel.Count} channel(s)."
            );
        }

        // Print warnings
        if (warningsByChannel.Any())
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Yellow))
            {
                await console.Error.WriteLineAsync(
                    "Warnings reported for the following channel(s):"
                );
            }

            foreach (var (channel, message) in warningsByChannel)
            {
                await console.Error.WriteAsync($"{channel.GetHierarchicalName()}: ");
                using (console.WithForegroundColor(ConsoleColor.Yellow))
                    await console.Error.WriteLineAsync(message);
            }

            await console.Error.WriteLineAsync();
        }

        // Print errors
        if (errorsByChannel.Any())
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Red))
            {
                await console.Error.WriteLineAsync("Failed to export the following channel(s):");
            }

            foreach (var (channel, message) in errorsByChannel)
            {
                await console.Error.WriteAsync($"{channel.GetHierarchicalName()}: ");
                using (console.WithForegroundColor(ConsoleColor.Red))
                    await console.Error.WriteLineAsync(message);
            }

            await console.Error.WriteLineAsync();
        }

        // With --strict, any per-channel error is fatal. Without it, only fail if every
        // channel failed (the historical default, kept for backwards compatibility).
        if (IsStrict ? errorsByChannel.Count > 0 : errorsByChannel.Count >= totalChannelCount)
            throw new CommandException("Export failed.");
    }

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        if (IsIncremental && ExportFormat != ExportFormat.Json)
        {
            throw new CommandException(
                "Option --incremental can only be used with JSON format. "
                    + "Use the convert command to get other formats."
            );
        }

        // Support Ukraine callout
        if (!IsUkraineSupportMessageDisabled)
        {
            console.Output.WriteLine(
                "┌────────────────────────────────────────────────────────────────────┐"
            );
            console.Output.WriteLine(
                "│   Thank you for supporting Ukraine <3                              │"
            );
            console.Output.WriteLine(
                "│                                                                    │"
            );
            console.Output.WriteLine(
                "│   As Russia wages a genocidal war against my country,              │"
            );
            console.Output.WriteLine(
                "│   I'm grateful to everyone who continues to                        │"
            );
            console.Output.WriteLine(
                "│   stand with Ukraine in our fight for freedom.                     │"
            );
            console.Output.WriteLine(
                "│                                                                    │"
            );
            console.Output.WriteLine(
                "│   Learn more: https://tyrrrz.me/ukraine                            │"
            );
            console.Output.WriteLine(
                "└────────────────────────────────────────────────────────────────────┘"
            );
            console.Output.WriteLine("");
        }

        await base.ExecuteAsync(console);
    }
}
