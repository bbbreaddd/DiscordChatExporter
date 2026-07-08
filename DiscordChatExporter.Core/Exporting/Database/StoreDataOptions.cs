using System.Collections.Generic;

namespace DiscordChatExporter.Core.Exporting.Database;

// Optional per-store data-capture toggles, threaded in from the watcher config. The default
// (all-on, every media kind) reproduces the historical behavior, so the export/sync/patch paths
// that don't pass one are unaffected.
public sealed record StoreDataOptions
{
    public static readonly StoreDataOptions Default = new();

    // Null means "all kinds"; otherwise only these asset kinds are downloaded (the strings match
    // the assetKind values passed to TryDownloadMediaAsync, e.g. "attachments", "emojis").
    public IReadOnlySet<string>? MediaAssetKinds { get; init; }

    public bool CaptureReactions { get; init; } = true;
    public bool CaptureEmbeds { get; init; } = true;
    public bool CaptureStickers { get; init; } = true;
    public bool CapturePolls { get; init; } = true;

    public bool AllowsMediaKind(string assetKind) =>
        MediaAssetKinds is null || MediaAssetKinds.Contains(assetKind);
}
