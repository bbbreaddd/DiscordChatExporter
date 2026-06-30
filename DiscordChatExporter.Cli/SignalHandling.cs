using System.Runtime.InteropServices;
using System.Threading;
using CliFx.Infrastructure;

namespace DiscordChatExporter.Cli;

internal static class SignalHandling
{
    // CliFx's IConsole.RegisterCancellationHandler() only reacts to Ctrl+C (it hooks
    // Console.CancelKeyPress, which on Unix corresponds to SIGINT). There is no equivalent
    // handling anywhere for SIGTERM or SIGHUP -- the .NET runtime's default disposition for
    // those is to terminate the process immediately, with no chance for the export/convert loop
    // to stop cleanly, identical to a SIGKILL or power loss. That matters here because this CLI
    // is commonly run under a process supervisor (systemd, a watcher script forwarding signals
    // to its process group) that stops it with SIGTERM, not Ctrl+C.
    //
    // This links CliFx's SIGINT-driven token with our own SIGTERM/SIGHUP registration, so all
    // three signals cancel the same token and the export loop gets one consistent chance to wind
    // down (finish or abandon the in-flight message, run crash-repair, exit) instead of being cut
    // off mid-write only when killed via SIGTERM/SIGHUP specifically.
    public static CancellationToken RegisterCancellationHandlerWithSignals(this IConsole console)
    {
        var ctrlCToken = console.RegisterCancellationHandler();

        var cts = new CancellationTokenSource();

        // 'context.Cancel = true' suppresses the runtime's default abrupt-termination behavior
        // for the signal, giving the cancellation token a chance to propagate before the process
        // exits. Registrations are intentionally never disposed -- they need to stay alive for
        // the lifetime of the process, exactly like CliFx's own Console.CancelKeyPress hook.
        PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;
                cts.Cancel();
            }
        );

        PosixSignalRegistration.Create(
            PosixSignal.SIGHUP,
            context =>
            {
                context.Cancel = true;
                cts.Cancel();
            }
        );

        return CancellationTokenSource.CreateLinkedTokenSource(ctrlCToken, cts.Token).Token;
    }
}
