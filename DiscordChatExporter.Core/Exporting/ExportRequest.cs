using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using DiscordChatExporter.Core.Utils;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

public partial class ExportRequest
{
    public Guild Guild { get; }

    public Channel Channel { get; }

    public string OutputFilePath { get; private set; }

    public string OutputDirPath { get; private set; }

    public string AssetsDirPath { get; private set; }

    public string BaseOutputDirPath { get; }

    // Stored so RedirectOutputFilePath can recompute AssetsDirPath when it was derived (not
    // explicitly provided). Null when the user passed --media-dir explicitly.
    private readonly string? _assetsDirTemplate;

    // The deepest directory that is guaranteed to not move even if the output path template
    // (e.g. "%G/%T/%C/") expands differently due to a guild/category/channel rename. Used to
    // recursively look for a pre-existing output file for the same channel ID under a stale
    // name/directory, since with template directories the rename can move the file into an
    // entirely different folder, not just give it a different name within the same folder.
    internal string OutputSearchRootDirPath { get; }

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

    // When set, asset resolution is purely local: cached files are referenced, but anything not
    // already on disk keeps its original remote URL instead of being downloaded. Used by 'convert'.
    public bool IsOfflineAssetMode { get; }

    public string? Locale { get; }

    public CultureInfo? CultureInfo { get; }

    public bool IsUtcNormalizationEnabled { get; }

    public bool IsIncremental { get; }

    public bool ShouldUseHtmlSharedAssets { get; }

    public bool IsCompact { get; }

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
        bool shouldCacheAssetsOnly = false,
        bool isOfflineAssetMode = false,
        bool shouldUseHtmlSharedAssets = false,
        bool isCompact = false
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
        IsOfflineAssetMode = isOfflineAssetMode;
        Locale = locale;
        IsUtcNormalizationEnabled = isUtcNormalizationEnabled;
        IsIncremental = isIncremental;
        ShouldUseHtmlSharedAssets = shouldUseHtmlSharedAssets;
        IsCompact = isCompact;

        BaseOutputDirPath =
            Directory.Exists(outputPath) || Path.EndsInDirectorySeparator(outputPath)
                ? outputPath
                : Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();

        OutputSearchRootDirPath = GetOutputSearchRootDirPath(outputPath);

        OutputFilePath = GetOutputBaseFilePath(Guild, Channel, outputPath, Format, After, Before);

        OutputDirPath = Path.GetDirectoryName(OutputFilePath)!;

        _assetsDirTemplate = !string.IsNullOrWhiteSpace(assetsDirPath) ? assetsDirPath : null;
        AssetsDirPath = _assetsDirTemplate is not null
            ? FormatPath(_assetsDirTemplate, Guild, Channel, After, Before)
            : $"{OutputFilePath}_Files{Path.DirectorySeparatorChar}";

        CultureInfo = Locale?.Pipe(CultureInfo.GetCultureInfo);
    }

    // Re-points this request at a pre-existing export file for the same channel, found under
    // a different name (e.g. because the channel or one of its parent categories was renamed
    // on Discord since the last export). This is the fallback used when that old file can't be
    // renamed to the freshly-computed path (e.g. because something else already occupies it);
    // it keeps writing to the file the channel's history is already in, instead of starting a
    // new, disconnected file under the freshly-computed name.
    public void RedirectOutputFilePath(string existingOutputFilePath)
    {
        OutputFilePath = existingOutputFilePath;
        OutputDirPath = Path.GetDirectoryName(OutputFilePath)!;
        // When AssetsDirPath was derived from OutputFilePath (no explicit --media-dir), keep it
        // in sync so cached assets from the existing export are still found via --reuse-media.
        if (_assetsDirTemplate is null)
            AssetsDirPath = $"{OutputFilePath}_Files{Path.DirectorySeparatorChar}";
    }

    public string GetHtmlSharedAssetsDirPath() => HtmlExport.GetSharedAssetsDirPath(OutputDirPath);

    public string GetHtmlSharedAssetUrl(string fileName) => HtmlExport.GetSharedAssetUrl(fileName);
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
        if (before is not null)
        {
            buffer
                .Append(' ')
                .Append('(')
                .Append($"before {before.Value.ToDate():yyyy-MM-dd}")
                .Append(')');
        }

        // File extension
        buffer.Append('.').Append(format.GetFileExtension());

        return Path.EscapeFileName(buffer.ToString());
    }

    // Finds the deepest directory in the (unexpanded) output path that cannot be affected by
    // template substitution. If the path has no template tokens, this is just its directory
    // portion. Otherwise, only the part of the path before the first token is stable across
    // renames, so we walk back from there to the nearest directory separator.
    private static string GetOutputSearchRootDirPath(string outputPath)
    {
        var templateTokenIndex = outputPath.IndexOf('%');
        if (templateTokenIndex < 0)
        {
            return Directory.Exists(outputPath) || Path.EndsInDirectorySeparator(outputPath)
                ? outputPath
                : Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        }

        var stablePrefix = outputPath[..templateTokenIndex];
        var lastSeparatorIndex = stablePrefix.LastIndexOfAny([
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar,
        ]);

        return lastSeparatorIndex >= 0
            ? stablePrefix[..(lastSeparatorIndex + 1)]
            : Path.GetPathRoot(outputPath) ?? Directory.GetCurrentDirectory();
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
