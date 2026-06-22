using System;
using Gress;

namespace DiscordChatExporter.Core.Exporting;

public readonly record struct ExportProgress(
    Percentage Percentage,
    DateTimeOffset? CurrentTimestamp = null
);
