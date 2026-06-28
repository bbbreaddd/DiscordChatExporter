using System.IO;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Utils;

namespace DiscordChatExporter.Core.Exporting;

[System.Flags]
internal enum HtmlPageFeatures
{
    None = 0,
    HighlightCode = 1 << 0,
    LottieStickers = 1 << 1,
}

internal static class HtmlExport
{
    public static string GetMessageElementId(Snowflake messageId) => $"m-{messageId}";

    public static string GetMessageElementId(string messageId) => $"m-{messageId}";

    public static string GetMessageFragment(Snowflake messageId) =>
        "#" + GetMessageElementId(messageId);

    public static string GetMessageFragment(string messageId) =>
        "#" + GetMessageElementId(messageId);

    public static string GetSharedAssetsDirPath(string outputDirPath) =>
        Path.Combine(outputDirPath, "_dce");

    public static string GetSharedAssetUrl(string fileName) =>
        Url.EncodeFilePath(Path.Combine("_dce", fileName));
}
