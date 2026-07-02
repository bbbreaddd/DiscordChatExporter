using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ExportAssetDownloaderSpecs
{
    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void Media_discordapp_net_URLs_with_different_signatures_resolve_to_the_same_cache_file_name()
    {
        // Arrange: same asset, two different (e.g. freshly re-signed) signature query strings --
        // this is the common shape for embed thumbnail/image ProxyUrls.
        var url1 =
            "https://media.discordapp.net/attachments/1/2/image.png?ex=111&is=222&hm=aaa&width=100";
        var url2 =
            "https://media.discordapp.net/attachments/1/2/image.png?ex=999&is=888&hm=zzz&width=100";

        // Act
        var fileName1 = ExportAssetDownloader.GetFileNameFromUrl(url1);
        var fileName2 = ExportAssetDownloader.GetFileNameFromUrl(url2);

        // Assert: the dedup goal of the shared media folder only holds if these collapse to the
        // same cache file, despite the differing signatures.
        fileName1.Should().Be(fileName2);
    }

    [Fact]
    public void Cdn_discordapp_com_URLs_with_different_signatures_still_resolve_to_the_same_cache_file_name()
    {
        // Regression guard: media.discordapp.net normalization must not have broken the
        // pre-existing cdn.discordapp.com case.
        var url1 = "https://cdn.discordapp.com/attachments/1/2/image.png?ex=111&is=222&hm=aaa";
        var url2 = "https://cdn.discordapp.com/attachments/1/2/image.png?ex=999&is=888&hm=zzz";

        ExportAssetDownloader
            .GetFileNameFromUrl(url1)
            .Should()
            .Be(ExportAssetDownloader.GetFileNameFromUrl(url2));
    }

    [Fact]
    public async Task I_can_download_an_asset_without_leaving_a_temp_file_behind()
    {
        // Arrange: a real local HTTP server, so this exercises ExportAssetDownloader's actual
        // download-to-temp-then-atomic-rename path (no mocking).
        var port = GetFreeTcpPort();
        var prefix = $"http://127.0.0.1:{port}/";
        const string content = "fake asset bytes";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes(content);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.OutputStream.Close();
        });

        using var workingDir = TempDirectory.Create();

        try
        {
            var downloader = new ExportAssetDownloader(workingDir.Path, reuse: true);

            // Act
            var filePath = await downloader.DownloadAsync($"{prefix}image.png");
            await serverTask;

            // Assert
            filePath.Should().NotBeNull();
            File.Exists(filePath!).Should().BeTrue();
            (await File.ReadAllTextAsync(filePath!)).Should().Be(content);

            // The download writes to a unique "<file>.<guid>.tmp" path and renames it into
            // place; if that ever regressed back to writing the final path directly (or left
            // the temp behind), this would catch it.
            Directory
                .GetFiles(workingDir.Path, "*.tmp")
                .Should()
                .BeEmpty("the temp file used during download should always be renamed away");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task I_can_hash_a_downloaded_asset_while_streaming_it()
    {
        // Arrange: a real local HTTP server, so this exercises the same streaming path as
        // database media downloads.
        var port = GetFreeTcpPort();
        var prefix = $"http://127.0.0.1:{port}/";
        var content = Encoding.UTF8.GetBytes("fake asset bytes");

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.ContentLength64 = content.Length;
            await context.Response.OutputStream.WriteAsync(content);
            context.Response.OutputStream.Close();
        });

        using var workingDir = TempDirectory.Create();

        try
        {
            var downloader = new ExportAssetDownloader(workingDir.Path, reuse: true);

            // Act
            var result = await downloader.DownloadWithInfoAsync(
                $"{prefix}image.png",
                $"{prefix}image.png",
                "image.png",
                hashMaxBytes: 50 * 1024 * 1024
            );
            await serverTask;

            // Assert
            result.Should().NotBeNull();
            result!.SizeBytes.Should().Be(content.Length);
            result.Sha256Hash.Should().Be(Convert.ToHexStringLower(SHA256.HashData(content)));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_failed_download_cleans_up_its_temp_file_and_leaves_no_partial_at_the_final_path()
    {
        // Arrange: a server that always responds 404, so the download fails after starting to
        // stream a (short) error body.
        var port = GetFreeTcpPort();
        var prefix = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 404;
            context.Response.OutputStream.Close();
        });

        using var workingDir = TempDirectory.Create();

        try
        {
            var downloader = new ExportAssetDownloader(workingDir.Path, reuse: true);

            // Act
            var act = async () => await downloader.DownloadAsync($"{prefix}missing.png");
            await act.Should().ThrowAsync<Exception>();
            await serverTask;

            // Assert: nothing left behind at all -- no temp file, no partial final file.
            Directory.GetFiles(workingDir.Path).Should().BeEmpty();
        }
        finally
        {
            listener.Stop();
        }
    }
}
