using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord.Data;

namespace DiscordChatExporter.Core.Exporting;

internal partial class MessageExporter(ExportContext context, string? outputFilePathOverride = null)
    : IAsyncDisposable
{
    private int _partitionIndex;
    private MessageWriter? _writer;
    private string? _activeFilePath;
    private string? _activeTempFilePath;
    private readonly System.Collections.Generic.List<HtmlPageFeatures> _htmlPageFeaturesByPartition =
    [];

    private bool _abandoned;
    private bool _abandonOverwrite;

    public long MessagesExported { get; private set; }

    // Marks the in-progress partition as aborted rather than complete. Call this from a 'catch'
    // around ExportMessageAsync before rethrowing, so disposal (whether from a normal 'await
    // using' unwind or an exception unwind) repairs the partial write instead of finalizing it
    // as-is. Without this, a mid-message exception (a transient API failure, a markdown error, a
    // disk hiccup) would still flow through the normal postamble-write-and-rename path and
    // produce a structurally "complete" file over what's actually a truncated/malformed trailing
    // message -- indistinguishable from a real success.
    //
    // 'allowOverwrite' controls whether the repaired partial is allowed to replace an
    // already-existing file at the final path. Pass true only when the output is always fully
    // regenerable from some other source of truth (e.g. 'convert' re-reading JSON input) -- in
    // that case overwriting with the best partial we got matches the normal happy-path semantics
    // (which already always overwrites) and a future run simply regenerates the rest. Leave it
    // false (default) when the final path may hold previously-good, non-regenerable data (e.g. a
    // live incremental export rewriting from an existing file): never destroy that with a
    // smaller partial: just leave it untouched and retry next time.
    public void Abandon(bool allowOverwrite = false)
    {
        _abandoned = true;
        _abandonOverwrite = allowOverwrite;
    }

    private async ValueTask<MessageWriter> InitializeWriterAsync(
        CancellationToken cancellationToken = default
    )
    {
        // Ensure that the partition limit has not been reached
        if (
            _writer is not null
            && context.Request.PartitionLimit.IsReached(
                _writer.MessagesWritten,
                _writer.BytesWritten
            )
        )
        {
            await UninitializeWriterAsync(cancellationToken);
            _partitionIndex++;
        }

        // Writer is still valid, return
        if (_writer is not null)
            return _writer;

        Directory.CreateDirectory(context.Request.OutputDirPath);
        var basePath = outputFilePathOverride ?? context.Request.OutputFilePath;
        var filePath = GetPartitionFilePath(basePath, _partitionIndex);
        var tempFilePath = filePath + ".tmp";

        _activeFilePath = filePath;
        _activeTempFilePath = tempFilePath;

        var writer = CreateMessageWriter(tempFilePath, context.Request.Format, context);
        await writer.WritePreambleAsync(cancellationToken);

        return _writer = writer;
    }

    private async ValueTask UninitializeWriterAsync(CancellationToken cancellationToken = default)
    {
        if (_writer is not null)
        {
            var filePath = _activeFilePath;
            var tempFilePath = _activeTempFilePath;

            if (_abandoned)
            {
                // Don't trust the writer to produce a valid postamble over data that may have
                // been interrupted mid-message: just release the file handle. The bytes already
                // flushed to tempFilePath are repaired below using the same scan-and-truncate
                // logic crash recovery uses for a real process kill, so a cooperative abandon and
                // an actual hard kill converge on one tested code path instead of two.
                await _writer.DisposeAsync();
                _writer = null;
                _activeFilePath = null;
                _activeTempFilePath = null;

                if (tempFilePath is not null && filePath is not null && File.Exists(tempFilePath))
                {
                    // Best-effort: if nothing salvageable survived, or the final path already
                    // holds data we're not allowed to overwrite, this just leaves tempFilePath
                    // in place exactly as a real crash would -- the next run's crash recovery
                    // (or, for 'convert', a plain retry) picks it up from there.
                    _ = await CrashRecovery.TryRepairAndPromoteAsync(
                        tempFilePath,
                        filePath,
                        cancellationToken,
                        _abandonOverwrite
                    );
                }

                return;
            }

            try
            {
                if (_writer is HtmlMessageWriter htmlWriter)
                    _htmlPageFeaturesByPartition.Add(htmlWriter.PageFeatures);

                await _writer.WritePostambleAsync(cancellationToken);
            }
            // Writer must be disposed, even if it fails to write the postamble
            finally
            {
                await _writer.DisposeAsync();
                _writer = null;
                _activeFilePath = null;
                _activeTempFilePath = null;
            }

            if (tempFilePath is not null && filePath is not null && File.Exists(tempFilePath))
            {
                File.Move(tempFilePath, filePath, true);
            }
        }
    }

    public async ValueTask ExportMessageAsync(
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        var writer = await InitializeWriterAsync(cancellationToken);
        await writer.WriteMessageAsync(message, cancellationToken);
        MessagesExported++;
    }

    public async ValueTask DisposeAsync()
    {
        if (_abandoned)
        {
            // An empty placeholder would be just as misleading as a corrupt one, and pagination
            // post-processing expects a clean set of finalized partitions -- skip both and just
            // let UninitializeWriterAsync repair/leave whatever was in flight.
            await UninitializeWriterAsync();
            return;
        }

        // If not messages were written, force the creation of an empty file
        if (MessagesExported <= 0)
            _ = await InitializeWriterAsync();

        await UninitializeWriterAsync();

        // Add pagination to split HTML files
        await PostProcessHtmlPaginationAsync();
    }

    private async ValueTask PostProcessHtmlPaginationAsync()
    {
        if (
            context.Request.Format != ExportFormat.HtmlDark
            && context.Request.Format != ExportFormat.HtmlLight
        )
            return;

        if (context.Request.ShouldUseHtmlSharedAssets)
        {
            await HtmlSharedAssets.WriteAsync(context, GetThemeName(context.Request.Format));
        }

        var basePathToUse = outputFilePathOverride ?? context.Request.OutputFilePath;

        if (_partitionIndex <= 0)
        {
            // Only 1 partition exists. Clean up the pagination comments.
            var path = GetPartitionFilePath(basePathToUse, 0);
            if (File.Exists(path))
            {
                var pageFeatures =
                    _htmlPageFeaturesByPartition.Count > 0
                        ? _htmlPageFeaturesByPartition[0]
                        : HtmlPageFeatures.None;

                await ReplacePlaceholdersAsync(
                    path,
                    await BuildHtmlReplacementsAsync(pageFeatures, "", "")
                );
            }
            return;
        }

        var totalParts = _partitionIndex + 1;

        for (var j = 0; j < totalParts; j++)
        {
            var path = GetPartitionFilePath(basePathToUse, j);
            if (!File.Exists(path))
                continue;

            var prevLink =
                j > 0
                    ? $"<a class=\"chatlog__pagination-link\" href=\"{Uri.EscapeDataString(Path.GetFileName(GetPartitionFilePath(basePathToUse, j - 1)))}\">Previous Page</a>"
                    : "<span class=\"chatlog__pagination-link chatlog__pagination-link--disabled\">Previous Page</span>";

            var nextLink =
                j < _partitionIndex
                    ? $"<a class=\"chatlog__pagination-link\" href=\"{Uri.EscapeDataString(Path.GetFileName(GetPartitionFilePath(basePathToUse, j + 1)))}\">Next Page</a>"
                    : "<span class=\"chatlog__pagination-link chatlog__pagination-link--disabled\">Next Page</span>";

            var pageInfo =
                $"<span class=\"chatlog__pagination-current\">Page {j + 1} of {totalParts}</span>";

            var paginationHtml =
                $@"
<div class=""chatlog__pagination"">
    {prevLink}
    {pageInfo}
    {nextLink}
</div>
";

            var pageFeatures =
                j < _htmlPageFeaturesByPartition.Count
                    ? _htmlPageFeaturesByPartition[j]
                    : HtmlPageFeatures.None;

            await ReplacePlaceholdersAsync(
                path,
                await BuildHtmlReplacementsAsync(pageFeatures, paginationHtml, paginationHtml)
            );
        }
    }

    private static string GetThemeName(ExportFormat format) =>
        format == ExportFormat.HtmlLight ? "Light" : "Dark";

    private async ValueTask<string> ResolveHtmlHeadAssetUrlAsync(string url) =>
        await context.ResolveAssetUrlAsync(url);

    private async ValueTask<System.Collections.Generic.Dictionary<
        string,
        string
    >> BuildHtmlReplacementsAsync(
        HtmlPageFeatures pageFeatures,
        string headerHtml,
        string footerHtml
    )
    {
        var replacements = new System.Collections.Generic.Dictionary<string, string>(
            System.StringComparer.Ordinal
        )
        {
            ["<!--dce-pagination-header-->"] = headerHtml,
            ["<!--dce-pagination-footer-->"] = footerHtml,
            ["<!--dce-highlight-style-->"] = pageFeatures.HasFlag(HtmlPageFeatures.HighlightCode)
                ? $"""<link rel="stylesheet" href="{await ResolveHtmlHeadAssetUrlAsync($"https://cdnjs.cloudflare.com/ajax/libs/highlight.js/9.15.6/styles/solarized-{GetThemeName(context.Request.Format).ToLowerInvariant()}.min.css")}">"""
                : "",
            ["<!--dce-highlight-script-->"] = pageFeatures.HasFlag(HtmlPageFeatures.HighlightCode)
                ? $"""<script src="{await ResolveHtmlHeadAssetUrlAsync("https://cdnjs.cloudflare.com/ajax/libs/highlight.js/9.15.6/highlight.min.js")}"></script>"""
                : "",
            ["<!--dce-lottie-script-->"] = pageFeatures.HasFlag(HtmlPageFeatures.LottieStickers)
                ? $"""<script src="{await ResolveHtmlHeadAssetUrlAsync("https://cdnjs.cloudflare.com/ajax/libs/lottie-web/5.8.1/lottie.min.js")}"></script>"""
                : "",
        };

        return replacements;
    }

    private static async ValueTask ReplacePlaceholdersAsync(
        string filePath,
        System.Collections.Generic.IReadOnlyDictionary<string, string> replacements
    )
    {
        var tempPath = filePath + ".post.tmp";
        try
        {
            using (var reader = new StreamReader(filePath))
            using (var writer = new StreamWriter(tempPath))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    if (replacements.TryGetValue(line.Trim(), out var replacement))
                    {
                        await writer.WriteLineAsync(replacement);
                    }
                    else
                    {
                        await writer.WriteLineAsync(line);
                    }
                }
            }

            File.Move(tempPath, filePath, true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Ignore
                }
            }
            throw;
        }
    }
}

internal partial class MessageExporter
{
    internal static string GetPartitionFilePath(string baseFilePath, int partitionIndex)
    {
        // First partition, don't change the file name
        if (partitionIndex <= 0)
            return baseFilePath;

        // Inject partition index into the file name
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(baseFilePath);
        var fileExt = Path.GetExtension(baseFilePath);
        var fileName = $"{fileNameWithoutExt} [part {partitionIndex + 1}]{fileExt}";
        var dirPath = Path.GetDirectoryName(baseFilePath);

        return !string.IsNullOrWhiteSpace(dirPath) ? Path.Combine(dirPath, fileName) : fileName;
    }

    private static MessageWriter CreateMessageWriter(
        string filePath,
        ExportFormat format,
        ExportContext context
    ) =>
        format switch
        {
            ExportFormat.PlainText => new PlainTextMessageWriter(File.Create(filePath), context),
            ExportFormat.Csv => new CsvMessageWriter(File.Create(filePath), context),
            ExportFormat.HtmlDark => new HtmlMessageWriter(File.Create(filePath), context, "Dark"),
            ExportFormat.HtmlLight => new HtmlMessageWriter(
                File.Create(filePath),
                context,
                "Light"
            ),
            ExportFormat.Json => new JsonMessageWriter(File.Create(filePath), context),
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                $"Unknown export format '{format}'."
            ),
        };
}
