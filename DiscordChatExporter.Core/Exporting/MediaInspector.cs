using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting.Converting;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

public record MediaInspectionResult(
    int ReferencedCount,
    int MissingCount,
    // Missing assets that were not attempted this run because a prior run already recorded them as
    // permanently failed (see the failure ledger). In report-only mode this is how many of the
    // missing are known-dead; with --retry-failed it's always zero.
    int SkippedCount,
    int DownloadedCount,
    int FailedCount,
    IReadOnlyList<string> MissingUrls
);

// Inspects a parsed JSON export to find media assets that are not present in a local media
// directory, without converting anything. Optionally downloads the missing assets to warm the
// cache, so that a later offline 'convert' run can reference them all locally.
public static class MediaInspector
{
    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (
            string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        );

    // Upper bound on how long a single asset download (including retries) may run before it's
    // treated as failed. Large enough for legitimate Discord files, small enough that dead hosts
    // don't stall the sweep.
    private static readonly TimeSpan PerAssetTimeout = TimeSpan.FromSeconds(45);

    // Per-channel record of asset URLs that have permanently failed to download (dead external
    // hosts, 404s). Stored alongside the cached files so repeat runs can skip them instead of
    // re-attempting -- and re-failing -- the same hopeless links every time. Lives in the media
    // dir, hidden, and is ignored by the converters (which look up assets by exact file name).
    private const string FailureLedgerFileName = ".checkmedia-failures.txt";

    // Serializes ledger writes per path, in case two same-named channels resolve to one media dir
    // and run concurrently. The common case (one channel per dir) never contends.
    private static readonly AsyncKeyedLocker<string> LedgerLocker = new();

    // The media asset URLs a conversion would reference for this message: attachments, stickers,
    // and embed thumbnails/images. Avatars, emoji and embed icons are intentionally excluded --
    // they're tiny, extremely numerous, and not what "missing media" is usually about. The
    // ProxyUrl-first selection mirrors what the converters resolve, so the computed cache paths
    // line up exactly with what an export/convert would have written.
    private static IEnumerable<string> GetMediaUrls(Message message)
    {
        foreach (var attachment in message.Attachments)
        {
            if (IsHttpUrl(attachment.Url))
                yield return attachment.Url;
        }

        foreach (var sticker in message.Stickers)
        {
            if (IsHttpUrl(sticker.SourceUrl))
                yield return sticker.SourceUrl;
        }

        foreach (var embed in message.Embeds)
        {
            var thumbnailUrl = embed.Thumbnail?.ProxyUrl ?? embed.Thumbnail?.Url;
            if (IsHttpUrl(thumbnailUrl))
                yield return thumbnailUrl!;

            foreach (var image in embed.Images)
            {
                var imageUrl = image.ProxyUrl ?? image.Url;
                if (IsHttpUrl(imageUrl))
                    yield return imageUrl!;
            }
        }
    }

    public static async ValueTask<MediaInspectionResult> InspectAsync(
        ExportedChat chat,
        string assetsDirPath,
        bool download,
        DiscordClient? discord = null,
        int downloadParallelism = 1,
        bool retryFailed = false,
        CancellationToken cancellationToken = default
    )
    {
        // Collapse duplicate references (the same asset is often posted many times) so each
        // distinct asset is checked -- and downloaded -- at most once.
        var urls = chat.Messages.SelectMany(GetMediaUrls).ToHashSet(StringComparer.Ordinal);

        var missingUrls = urls.Where(url => !IsCached(assetsDirPath, url)).ToList();

        // URLs a previous run already gave up on. Unless the caller forces a retry, these are
        // skipped so we don't keep re-attempting hopeless links every run.
        var ledgerPath = Path.Combine(assetsDirPath, FailureLedgerFileName);
        var knownDead = await LoadFailureLedgerAsync(ledgerPath, cancellationToken);

        if (!download)
        {
            // Report-only: surface how many of the missing are already known to be dead.
            var knownDeadMissing = missingUrls.Count(knownDead.Contains);
            return new MediaInspectionResult(
                urls.Count,
                missingUrls.Count,
                knownDeadMissing,
                0,
                0,
                missingUrls
            );
        }

        var candidates = retryFailed
            ? missingUrls
            : missingUrls.Where(url => !knownDead.Contains(url)).ToList();
        var skipped = missingUrls.Count - candidates.Count;

        var downloaded = 0;
        var failed = 0;
        // Links that returned a definitive "gone" response (HTTP 404/410). Only these are recorded
        // in the ledger. Transient failures -- timeouts, connection errors, server errors, or a
        // local network outage (e.g. a nightly router reset) -- are counted but not remembered, so
        // they're retried on the next run instead of being wrongly skipped as dead forever.
        var deadUrls = new ConcurrentBag<string>();

        if (candidates.Count > 0)
        {
            // Discord CDN links are signed and expire after a day or so. Ask Discord for fresh
            // signatures so links that would otherwise 404 become downloadable again. Only
            // Discord-hosted URLs can be refreshed; external ones pass through unchanged.
            var refreshedUrls = discord is not null
                ? await discord.RefreshAttachmentUrlsAsync(candidates, cancellationToken)
                : (IReadOnlyDictionary<string, string>)
                    new Dictionary<string, string>(StringComparer.Ordinal);

            var downloader = new ExportAssetDownloader(assetsDirPath, reuse: true);

            // Downloads within a channel are independent and network-bound, so run several at
            // once. The downloader's per-file lock and concurrent cache keep this safe, and the
            // shared HTTP resilience pipeline still self-throttles on rate limits.
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, downloadParallelism),
                    CancellationToken = cancellationToken,
                },
                async (url, innerCancellationToken) =>
                {
                    // Fetch from the refreshed link when available, but cache under the original
                    // URL so an offline 'convert' (which only knows the original) still finds it.
                    var downloadUrl = refreshedUrls.GetValueOrDefault(url) ?? url;

                    // Cap how long any one asset may take. A dead or blackholed external host can
                    // otherwise stall for the full HTTP timeout on every retry (minutes). The cap
                    // is generous enough for legitimately large Discord files, but turns a hopeless
                    // link into a quick failure instead of a long stall.
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                        innerCancellationToken
                    );
                    timeoutCts.CancelAfter(PerAssetTimeout);

                    try
                    {
                        await downloader.DownloadAsync(url, downloadUrl, timeoutCts.Token);
                        Interlocked.Increment(ref downloaded);
                    }
                    // Genuine user cancellation aborts the whole sweep. Anything else is a per-asset
                    // failure that's recorded so the sweep keeps going -- but only links Discord
                    // reports as actually gone (404/410) are remembered as dead; transient failures
                    // are left out of the ledger so a later run can retry them.
                    catch (OperationCanceledException)
                        when (innerCancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        if (IsPermanentlyGone(ex))
                            deadUrls.Add(url);
                    }
                }
            );
        }

        // Rewrite the ledger with everything still known-dead: this run's confirmed-gone links plus
        // any previously-dead links we skipped that are still referenced and uncached. Rebuilding it
        // from the current reference set prunes entries for assets no longer in the export, and a
        // --retry-failed run that now succeeds (or only fails transiently) drops them entirely.
        var stillDead = new HashSet<string>(deadUrls, StringComparer.Ordinal);
        if (!retryFailed)
        {
            foreach (var url in missingUrls)
            {
                if (knownDead.Contains(url))
                    stillDead.Add(url);
            }
        }
        await SaveFailureLedgerAsync(ledgerPath, stillDead, cancellationToken);

        return new MediaInspectionResult(
            urls.Count,
            missingUrls.Count,
            skipped,
            downloaded,
            failed,
            missingUrls
        );
    }

    // A definitive "the resource is gone" signal -- the only kind of failure worth remembering as
    // permanently dead. A timeout, connection error, network outage, or 5xx could all be temporary,
    // so they're deliberately excluded to avoid cataloguing a still-live asset as dead.
    private static bool IsPermanentlyGone(Exception exception) =>
        exception
            .GetSelfAndDescendants()
            .OfType<HttpRequestException>()
            .Any(ex => ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone);

    private static bool IsFileValid(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsCached(string assetsDirPath, string url) =>
        IsFileValid(Path.Combine(assetsDirPath, ExportAssetDownloader.GetFileNameFromUrl(url)))
        || IsFileValid(
            Path.Combine(assetsDirPath, ExportAssetDownloader.GetLegacyFileNameFromUrl(url))
        );

    private static async ValueTask<IReadOnlySet<string>> LoadFailureLedgerAsync(
        string ledgerPath,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(ledgerPath))
            return new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var lines = await File.ReadAllLinesAsync(ledgerPath, cancellationToken);
            return lines
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrEmpty(line))
                .ToHashSet(StringComparer.Ordinal);
        }
        // A corrupt or unreadable ledger should never break a media check -- just treat it as empty.
        catch (IOException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static async ValueTask SaveFailureLedgerAsync(
        string ledgerPath,
        IReadOnlyCollection<string> deadUrls,
        CancellationToken cancellationToken
    )
    {
        using var _ = await LedgerLocker.LockAsync(ledgerPath, cancellationToken);

        // Nothing dead anymore -- remove a stale ledger rather than leaving an empty file behind.
        if (deadUrls.Count <= 0)
        {
            try
            {
                File.Delete(ledgerPath);
            }
            catch (IOException) { }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);

        // Write to a temp file and move into place so a concurrent reader never sees a half-written
        // ledger.
        var tempPath = ledgerPath + ".tmp";
        await File.WriteAllLinesAsync(tempPath, deadUrls, cancellationToken);
        File.Move(tempPath, ledgerPath, overwrite: true);
    }
}
