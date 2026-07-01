using System;

namespace DiscordChatExporter.Core.Exporting;

public enum ExportFormat
{
    PlainText,
    HtmlDark,
    HtmlLight,
    Csv,
    Json,
    Db,
}

public static class ExportFormatExtensions
{
    extension(ExportFormat format)
    {
        public string GetFileExtension() =>
            format switch
            {
                ExportFormat.PlainText => "txt",
                ExportFormat.HtmlDark => "html",
                ExportFormat.HtmlLight => "html",
                ExportFormat.Csv => "csv",
                ExportFormat.Json => "json",
                ExportFormat.Db => "db",
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            };

        public string GetDisplayName() =>
            format switch
            {
                ExportFormat.PlainText => "TXT",
                ExportFormat.HtmlDark => "HTML (Dark)",
                ExportFormat.HtmlLight => "HTML (Light)",
                ExportFormat.Csv => "CSV",
                ExportFormat.Json => "JSON",
                ExportFormat.Db => "SQLite Database",
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            };
    }
}
