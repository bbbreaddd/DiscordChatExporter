using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting;

internal static class HtmlSharedAssets
{
    public static string GetStyleFileName(string themeName) =>
        string.Equals(themeName, "Dark", System.StringComparison.OrdinalIgnoreCase)
            ? "html-dark.css"
            : "html-light.css";

    public static string GetStyleFileName(ExportFormat format) =>
        format == ExportFormat.HtmlLight ? "html-light.css" : "html-dark.css";

    public static string ScriptsFileName => "html.js";

    public static string IconsFileName => "icons.svg";

    // The rendered content is fully deterministic for a given format/theme, so concurrently
    // exported/converted channels sharing this directory (--parallel N>1) can race here safely:
    // each writer renders identical content to its own uniquely-named temp file and atomically
    // renames it into place, so the loser's rename just overwrites with byte-identical content
    // instead of risking a truncated/corrupted shared file from two writers racing on the same
    // path (the previous check-then-write pattern had no such guarantee).
    private static async ValueTask WriteIfMissingAsync(
        string filePath,
        Func<CancellationToken, Task<string>> renderContentAsync,
        CancellationToken cancellationToken
    )
    {
        if (File.Exists(filePath))
            return;

        var tempFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        var content = await renderContentAsync(cancellationToken);
        await File.WriteAllTextAsync(tempFilePath, content, cancellationToken);
        File.Move(tempFilePath, filePath, true);
    }

    public static async ValueTask WriteAsync(
        ExportContext context,
        string themeName,
        CancellationToken cancellationToken = default
    )
    {
        var dirPath = context.Request.GetHtmlSharedAssetsDirPath();
        Directory.CreateDirectory(dirPath);

        await WriteIfMissingAsync(
            Path.Combine(dirPath, GetStyleFileName(themeName)),
            async ct =>
                context.MinifyCss(
                    await new HtmlStyleTemplate
                    {
                        Context = context,
                        ThemeName = themeName,
                        ResolveFontUrls = false,
                    }.RenderAsync(ct)
                ),
            cancellationToken
        );

        await WriteIfMissingAsync(
            Path.Combine(dirPath, ScriptsFileName),
            ct => new HtmlScriptTemplate { Context = context }.RenderAsync(ct),
            cancellationToken
        );

        await WriteIfMissingAsync(
            Path.Combine(dirPath, IconsFileName),
            async ct =>
                "<svg xmlns=\"http://www.w3.org/2000/svg\"><defs>"
                + await new HtmlIconsTemplate().RenderAsync(ct)
                + "</defs></svg>",
            cancellationToken
        );
    }
}
