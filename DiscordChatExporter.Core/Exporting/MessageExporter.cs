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

    public long MessagesExported { get; private set; }

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

            try
            {
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

        var basePathToUse = outputFilePathOverride ?? context.Request.OutputFilePath;

        if (_partitionIndex <= 0)
        {
            // Only 1 partition exists. Clean up the pagination comments.
            var path = GetPartitionFilePath(basePathToUse, 0);
            if (File.Exists(path))
            {
                await ReplacePlaceholdersAsync(path, "", "");
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

            await ReplacePlaceholdersAsync(path, paginationHtml, paginationHtml);
        }
    }

    private static async ValueTask ReplacePlaceholdersAsync(
        string filePath,
        string headerHtml,
        string footerHtml
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
                    if (line.Contains("<!--dce-pagination-header-->", StringComparison.Ordinal))
                    {
                        await writer.WriteLineAsync(headerHtml);
                    }
                    else if (
                        line.Contains("<!--dce-pagination-footer-->", StringComparison.Ordinal)
                    )
                    {
                        await writer.WriteLineAsync(footerHtml);
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
