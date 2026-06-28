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

    public static async ValueTask WriteAsync(
        ExportContext context,
        string themeName,
        CancellationToken cancellationToken = default
    )
    {
        var dirPath = context.Request.GetHtmlSharedAssetsDirPath();
        Directory.CreateDirectory(dirPath);

        var styleFilePath = Path.Combine(dirPath, GetStyleFileName(themeName));
        if (!File.Exists(styleFilePath))
        {
            var content = await new HtmlStyleTemplate
            {
                Context = context,
                ThemeName = themeName,
                ResolveFontUrls = false,
            }.RenderAsync(cancellationToken);

            await File.WriteAllTextAsync(styleFilePath, content, cancellationToken);
        }

        var scriptsFilePath = Path.Combine(dirPath, ScriptsFileName);
        if (!File.Exists(scriptsFilePath))
        {
            var content = await new HtmlScriptTemplate().RenderAsync(cancellationToken);
            await File.WriteAllTextAsync(scriptsFilePath, content, cancellationToken);
        }

        var iconsFilePath = Path.Combine(dirPath, IconsFileName);
        if (!File.Exists(iconsFilePath))
        {
            var content =
                "<svg xmlns=\"http://www.w3.org/2000/svg\"><defs>"
                + await new HtmlIconsTemplate().RenderAsync(cancellationToken)
                + "</defs></svg>";
            await File.WriteAllTextAsync(iconsFilePath, content, cancellationToken);
        }
    }
}
