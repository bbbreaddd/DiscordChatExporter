using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using AsyncKeyedLock;
using DiscordChatExporter.Core.Utils;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal record ExportAssetDownloadResult(string FilePath, long SizeBytes, string? Sha256Hash);

// 'offline' makes the downloader purely local: it resolves to an already-cached file when one
// exists, but never contacts the network. A cache miss returns null instead of downloading, so
// the caller can keep the original remote URL. This is what the 'convert' command uses, since
// converting is meant to be an offline operation -- downloading is the job of the export step.
internal partial class ExportAssetDownloader(
    string workingDirPath,
    bool reuse,
    bool offline = false,
    Func<string, string>? getRelativeFilePath = null
)
{
    private static readonly AsyncKeyedLocker<string> Locker = new();

    // File paths of the previously downloaded assets. Concurrent because a single downloader
    // instance may be shared across parallel downloads of distinct URLs (e.g. 'checkmedia'
    // warming a channel's cache); the per-file Locker only guards same-file races.
    private readonly ConcurrentDictionary<string, string> _previousPathsByUrl = new(
        StringComparer.Ordinal
    );

    // In offline mode we always reuse whatever is already on disk; there is no download to avoid
    // a redundant request for, so reuse is implied regardless of the caller's preference.
    private bool ShouldReuse => reuse || offline;

    public async ValueTask<string?> DownloadAsync(
        string url,
        CancellationToken cancellationToken = default
    ) => await DownloadAsync(url, url, cancellationToken);

    // Downloads from 'downloadUrl' but caches the result under the name derived from 'url'. This
    // lets a caller fetch from a freshly-refreshed Discord CDN link while keeping the cache keyed
    // by the original (stored) URL, so that an offline 'convert' -- which only knows the original
    // URL -- still resolves to the same file.
    public async ValueTask<string?> DownloadAsync(
        string url,
        string downloadUrl,
        CancellationToken cancellationToken = default
    )
    {
        var relativeFilePath = getRelativeFilePath?.Invoke(url) ?? GetFileNameFromUrl(url);
        return await DownloadAsync(url, downloadUrl, relativeFilePath, cancellationToken);
    }

    public async ValueTask<string?> DownloadAsync(
        string url,
        string downloadUrl,
        string relativeFilePath,
        CancellationToken cancellationToken = default
    ) =>
        (
            await DownloadWithInfoAsync(
                url,
                downloadUrl,
                relativeFilePath,
                hashMaxBytes: null,
                cancellationToken
            )
        )?.FilePath;

    public async ValueTask<ExportAssetDownloadResult?> DownloadWithInfoAsync(
        string url,
        string downloadUrl,
        string relativeFilePath,
        long? hashMaxBytes = null,
        CancellationToken cancellationToken = default
    )
    {
        var filePath = Path.Combine(workingDirPath, relativeFilePath);

        using var _ = await Locker.LockAsync(filePath, cancellationToken);

        if (_previousPathsByUrl.TryGetValue(url, out var cachedFilePath))
            return GetResult(cachedFilePath, null);

        // Reuse existing files if we're allowed to
        if (ShouldReuse && IsFileValid(filePath))
            return GetResult(_previousPathsByUrl[url] = filePath, null);

        // Check for a file cached by the legacy naming scheme (5-char hash) and rename it
        // to the new naming scheme to preserve backwards compatibility with existing exports
        if (ShouldReuse)
        {
            var legacyFilePath = Path.Combine(workingDirPath, GetLegacyFileNameFromUrl(url));
            if (IsFileValid(legacyFilePath))
            {
                // Overwrite in case the destination file was created concurrently between our
                // earlier existence check and this move operation
                try
                {
                    File.Move(legacyFilePath, filePath, overwrite: true);
                    return GetResult(_previousPathsByUrl[url] = filePath, null);
                }
                catch (IOException)
                {
                    // The legacy file was moved or deleted concurrently or something else happened.
                    // Upgrading old files is not crucial, so we can just move on.
                }
            }
        }

        // Offline mode never reaches the network: an uncached asset stays remote.
        if (offline)
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? workingDirPath);

        // Download to a process-unique temp file and atomically rename into place, rather than
        // writing straight to 'filePath'. The in-process Locker above only guards against races
        // within this process; a second OS process downloading the same URL into the same
        // shared media dir (e.g. a 'checkmedia --download' run overlapping a scheduled export)
        // would otherwise both open 'filePath' with FileShare.None, the loser would get an
        // IOException, and its catch-all cleanup would unlink the file out from under the
        // winner's still-open handle (the winner keeps writing successfully to the now-deleted
        // inode and never notices). A unique temp name means both processes can download
        // independently with no collision, and the final rename is atomic -- whichever one
        // lands last simply wins with a complete file either way.
        var tempFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";

        string? sha256Hash = null;

        try
        {
            await Http.ResiliencePipeline.ExecuteAsync(
                async innerCancellationToken =>
                {
                    // Download the file
                    using var response = await Http.Client.GetAsync(
                        downloadUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        innerCancellationToken
                    );

                    response.EnsureSuccessStatusCode();

                    await using (
                        var input = await response.Content.ReadAsStreamAsync(innerCancellationToken)
                    )
                    await using (var output = File.Create(tempFilePath))
                    {
                        using var hasher = hashMaxBytes is not null
                            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                            : null;

                        var buffer = new byte[81920];
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer, innerCancellationToken);
                            if (read <= 0)
                                break;

                            await output.WriteAsync(
                                buffer.AsMemory(0, read),
                                innerCancellationToken
                            );

                            if (hasher is not null)
                            {
                                if (output.Length <= hashMaxBytes)
                                {
                                    hasher.AppendData(buffer, 0, read);
                                }
                                else
                                {
                                    hasher.Dispose();
                                }
                            }
                        }

                        if (output.Length <= 0)
                            throw new HttpRequestException("Downloaded asset is empty.");

                        if (hasher is not null && output.Length <= hashMaxBytes)
                            sha256Hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
                    }

                    File.Move(tempFilePath, filePath, true);
                },
                cancellationToken
            );
        }
        catch
        {
            // A download interrupted mid-stream (cancellation, timeout, network error) leaves a
            // truncated temp file behind. Clean it up; 'filePath' itself was never touched.
            try
            {
                File.Delete(tempFilePath);
            }
            catch (IOException) { }

            throw;
        }

        return GetResult(_previousPathsByUrl[url] = filePath, sha256Hash);
    }

    private static ExportAssetDownloadResult? GetResult(string filePath, string? sha256Hash)
    {
        try
        {
            return new ExportAssetDownloadResult(
                filePath,
                new FileInfo(filePath).Length,
                sha256Hash
            );
        }
        catch (IOException)
        {
            return null;
        }
    }
}

internal partial class ExportAssetDownloader
{
    // Internal so that the SQLite export store can key its dead-link ledger on the same
    // normalized form used for the cache filename hash -- otherwise a re-signed CDN URL would
    // never match a previously-ledgered entry, defeating the ledger's purpose.
    internal static string NormalizeUrl(string url)
    {
        // Remove signature parameters from Discord CDN URLs to normalize them. Both hosts below
        // sign URLs with the same ex/is/hm query params (media.discordapp.net is what embed
        // thumbnail/image ProxyUrls typically use -- see DiscordClient's own signed-host check).
        // Without stripping it here too, the same logical asset re-resolved with a freshly
        // signed media.discordapp.net URL would hash to a different cache file name, defeating
        // the dedup that the shared media folder exists for.
        var uri = new Uri(url);
        if (
            !string.Equals(uri.Host, "cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, "media.discordapp.net", StringComparison.OrdinalIgnoreCase)
        )
            return url;

        var query = HttpUtility.ParseQueryString(uri.Query);
        query.Remove("ex");
        query.Remove("is");
        query.Remove("hm");

        return uri.GetLeftPart(UriPartial.Path) + query;
    }

    private static string GetFileNameFromUrl(string url, string urlHash)
    {
        // Try to extract the file name from URL
        var fileName = new Uri(url, UriKind.RelativeOrAbsolute).TryGetFileName();

        // If it's not there, just use the URL hash as the file name
        if (string.IsNullOrWhiteSpace(fileName))
            return urlHash;

        // Otherwise, use the original file name but inject the hash in the middle
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var fileExtension = Path.GetExtension(fileName);

        // Probably not a file extension, just a dot in a long file name
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/812
        if (fileExtension.Length > 41)
        {
            fileNameWithoutExtension = fileName;
            fileExtension = "";
        }

        return Path.EscapeFileName(
            fileNameWithoutExtension.Truncate(42) + '-' + urlHash + fileExtension
        );
    }

    // Internal so that media-verification tooling (e.g. the 'checkmedia' command) can compute the
    // exact same on-disk file name a real download/reuse would, and check the cache without
    // contacting the network.
    internal static string GetFileNameFromUrl(string url) =>
        GetFileNameFromUrl(
            url,
            // 16 chars = 64 bits, reaches 1% collision probability at ~609 million files
            SHA256
                .HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)))
                .Pipe(Convert.ToHexStringLower)
                .Truncate(16)
        );

    // Legacy naming used a 5-char hash, kept for backwards compatibility with existing exports
    internal static string GetLegacyFileNameFromUrl(string url) =>
        GetFileNameFromUrl(
            url,
            SHA256
                .HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)))
                .Pipe(Convert.ToHexStringLower)
                // 5 chars = 20 bits, reaches 1% collision probability at ~145 files
                .Truncate(5)
        );

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
}
