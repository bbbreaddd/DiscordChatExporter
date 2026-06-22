using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

public partial class ExportRequest
{
    public Guild Guild { get; }

    public Channel Channel { get; }

    public string OutputFilePath { get; private set; }

    public string OutputDirPath { get; private set; }

    public string AssetsDirPath { get; }

    public string BaseOutputDirPath { get; }

    public ExportFormat Format { get; }

    public Snowflake? After { get; }

    public Snowflake? Before { get; }

    public PartitionLimit PartitionLimit { get; }

    public MessageFilter MessageFilter { get; }

    public bool IsReverseMessageOrder { get; }

    public bool ShouldFormatMarkdown { get; }

    public bool ShouldDownloadAssets { get; }

    public bool ShouldReuseAssets { get; }

    public bool ShouldCacheAssetsOnly { get; }

    public string? Locale { get; }

    public CultureInfo? CultureInfo { get; }

    public bool IsUtcNormalizationEnabled { get; }

    public bool IsIncremental { get; }

    public ExportRequest(
        Guild guild,
        Channel channel,
        string outputPath,
        string? assetsDirPath,
        ExportFormat format,
        Snowflake? after,
        Snowflake? before,
        PartitionLimit partitionLimit,
        MessageFilter messageFilter,
        bool isReverseMessageOrder,
        bool shouldFormatMarkdown,
        bool shouldDownloadAssets,
        bool shouldReuseAssets,
        string? locale,
        bool isUtcNormalizationEnabled,
        bool isIncremental = false,
        bool shouldCacheAssetsOnly = false
    )
    {
        Guild = guild;
        Channel = channel;
        Format = format;
        After = after;
        Before = before;
        PartitionLimit = partitionLimit;
        MessageFilter = messageFilter;
        IsReverseMessageOrder = isReverseMessageOrder;
        ShouldFormatMarkdown = shouldFormatMarkdown;
        ShouldDownloadAssets = shouldDownloadAssets;
        ShouldReuseAssets = shouldReuseAssets;
        ShouldCacheAssetsOnly = shouldCacheAssetsOnly;
        Locale = locale;
        IsUtcNormalizationEnabled = isUtcNormalizationEnabled;
        IsIncremental = isIncremental;

        BaseOutputDirPath =
            Directory.Exists(outputPath) || Path.EndsInDirectorySeparator(outputPath)
                ? outputPath
                : Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();

        OutputFilePath = GetOutputBaseFilePath(Guild, Channel, outputPath, Format, After, Before);

        OutputDirPath = Path.GetDirectoryName(OutputFilePath)!;

        AssetsDirPath = !string.IsNullOrWhiteSpace(assetsDirPath)
            ? FormatPath(assetsDirPath, Guild, Channel, After, Before)
            : $"{OutputFilePath}_Files{Path.DirectorySeparatorChar}";

        CultureInfo = Locale?.Pipe(CultureInfo.GetCultureInfo);
    }

    // Re-points this request at a pre-existing export file for the same channel, found under
    // a different name (e.g. because the channel or one of its parent categories was renamed
    // on Discord since the last export). This is the fallback used when that old file can't be
    // renamed to the freshly-computed path (e.g. because something else already occupies it);
    // it keeps writing to the file the channel's history is already in, instead of starting a
    // new, disconnected file under the freshly-computed name.
    internal void RedirectOutputFilePath(string existingOutputFilePath)
    {
        OutputFilePath = existingOutputFilePath;
        OutputDirPath = Path.GetDirectoryName(OutputFilePath)!;
    }
}

public partial class ExportRequest
{
    public static string GetDefaultOutputFileName(
        Guild guild,
        Channel channel,
        ExportFormat format,
        Snowflake? after = null,
        Snowflake? before = null
    )
    {
        var buffer = new StringBuilder();

        // Guild name
        buffer.Append(guild.Name);

        // Parent name
        if (channel.Parent is not null)
            buffer.Append(" - ").Append(channel.Parent.Name);

        // Channel name and ID
        buffer
            .Append(" - ")
            .Append(channel.Name)
            .Append(' ')
            .Append('[')
            .Append(channel.Id)
            .Append(']');

        // Date range
        if (after is not null || before is not null)
        {
            buffer.Append(' ').Append('(');

            // Both 'after' and 'before' are set
            if (after is not null && before is not null)
            {
                buffer.Append(
                    $"{after.Value.ToDate():yyyy-MM-dd} to {before.Value.ToDate():yyyy-MM-dd}"
                );
            }
            // Only 'after' is set
            else if (after is not null)
            {
                buffer.Append($"after {after.Value.ToDate():yyyy-MM-dd}");
            }
            // Only 'before' is set
            else if (before is not null)
            {
                buffer.Append($"before {before.Value.ToDate():yyyy-MM-dd}");
            }

            buffer.Append(')');
        }

        // File extension
        buffer.Append('.').Append(format.GetFileExtension());

        return Path.EscapeFileName(buffer.ToString());
    }

    private static string FormatPath(
        string path,
        Guild guild,
        Channel channel,
        Snowflake? after,
        Snowflake? before
    ) =>
        Regex.Replace(
            path,
            "%.",
            m =>
                Path.EscapeFileName(
                    m.Value switch
                    {
                        "%g" => guild.Id.ToString(),
                        "%G" => guild.Name,

                        "%t" => channel.Parent?.Id.ToString() ?? "",
                        "%T" => channel.Parent?.Name ?? "",

                        "%c" => channel.Id.ToString(),
                        "%C" => channel.Name,

                        "%p" => channel.Position?.ToString(CultureInfo.InvariantCulture) ?? "0",
                        "%P" => channel.Parent?.Position?.ToString(CultureInfo.InvariantCulture)
                            ?? "0",

                        "%a" => after?.ToDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                            ?? "",
                        "%b" => before
                            ?.ToDate()
                            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                            ?? "",
                        "%d" => DateTimeOffset.Now.ToString(
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture
                        ),

                        "%%" => "%",
                        _ => m.Value,
                    }
                )
        );

    private static string GetOutputBaseFilePath(
        Guild guild,
        Channel channel,
        string outputPath,
        ExportFormat format,
        Snowflake? after = null,
        Snowflake? before = null
    )
    {
        var actualOutputPath = FormatPath(outputPath, guild, channel, after, before);

        // Output is a directory
        if (
            Directory.Exists(actualOutputPath)
            || string.IsNullOrWhiteSpace(Path.GetExtension(actualOutputPath))
        )
        {
            var fileName = GetDefaultOutputFileName(guild, channel, format, after, before);
            return Path.Combine(actualOutputPath, fileName);
        }

        // Output is a file
        return actualOutputPath;
    }
}
