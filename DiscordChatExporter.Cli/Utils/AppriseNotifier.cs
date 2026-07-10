using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Cli.Utils;

// Sends operator notifications through the Apprise CLI (bundled in the watcher image). Service URLs are
// written to a temporary Apprise config file rather than passed as CLI arguments, so webhook tokens
// don't leak into the process list. Every failure is swallowed and reported via the return value -- a
// notification problem must never take down the watcher.
public sealed class AppriseNotifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<string> _urls;
    private readonly string _appriseBinary;

    public AppriseNotifier(IReadOnlyList<string> urls, string appriseBinary = "apprise")
    {
        _urls = urls;
        _appriseBinary = appriseBinary;
    }

    public bool HasTargets => _urls.Count > 0;

    // Returns true only if apprise ran and exited zero. Never throws.
    public async Task<bool> TryNotifyAsync(
        string title,
        string body,
        CancellationToken cancellationToken = default
    )
    {
        if (_urls.Count == 0)
            return false;

        string? configPath = null;
        try
        {
            configPath = Path.Combine(Path.GetTempPath(), $"apprise-{Guid.NewGuid():N}.conf");
            await File.WriteAllTextAsync(
                configPath,
                string.Join('\n', _urls) + '\n',
                cancellationToken
            );

            var psi = new ProcessStartInfo
            {
                FileName = _appriseBinary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add(body);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(configPath);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return false;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            timeoutCts.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort -- the process may have already exited.
                }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            // Missing binary, IO error, etc. -- notifications are best-effort.
            return false;
        }
        finally
        {
            if (configPath is not null)
            {
                try
                {
                    File.Delete(configPath);
                }
                catch
                {
                    // Ignore cleanup failures.
                }
            }
        }
    }
}
