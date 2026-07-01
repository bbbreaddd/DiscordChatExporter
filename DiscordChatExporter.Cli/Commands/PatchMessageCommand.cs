using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;

namespace DiscordChatExporter.Cli.Commands;

[Command(
    "patch-message",
    Description = "Refreshes a single already-exported message's stored JSON (e.g. after a "
        + "reaction changes) in place, without re-exporting the whole channel. Uses a per-channel "
        + "index (built lazily on first use, then reused) to locate the message quickly "
        + "regardless of how old it is, instead of scanning the channel from the start. "
        + "This is a prototype -- one message per invocation, meant to be called once per "
        + "reaction/edit event rather than batched."
)]
public partial class PatchMessageCommand : DiscordCommandBase
{
    [CommandOption("channel", 'c', Description = "Channel ID.")]
    public required Snowflake ChannelId { get; set; }

    [CommandOption("message", 'm', Description = "Message ID to refresh.")]
    public required Snowflake MessageId { get; set; }

    [CommandOption(
        "output",
        'o',
        Description = "Directory the channel was originally exported to (must match, so the "
            + "same output file path is computed)."
    )]
    public string OutputPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = Path.GetFullPath(value);
    } = Directory.GetCurrentDirectory();

    [CommandOption(
        "after",
        Description = "Only include messages sent after this date/message. Must match whatever was used for the original export."
    )]
    public Snowflake? After { get; set; }

    [CommandOption(
        "before",
        Description = "Only include messages sent before this date/message. Must match whatever was used for the original export."
    )]
    public Snowflake? Before { get; set; }

    [CommandOption(
        "markdown",
        Description = "Process markdown, mentions, and other special tokens. Should match what the original export used."
    )]
    public bool ShouldFormatMarkdown { get; set; } = true;

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        await base.ExecuteAsync(console);

        var cancellationToken = console.RegisterCancellationHandlerWithSignals();

        var stopwatch = Stopwatch.StartNew();

        var channel = await Discord.GetChannelAsync(ChannelId, cancellationToken);
        var guild = await Discord.GetGuildAsync(channel.GuildId, cancellationToken);

        var request = new ExportRequest(
            guild,
            channel,
            OutputPath,
            null,
            ExportFormat.Json,
            After,
            Before,
            PartitionLimit.Null,
            MessageFilter.Null,
            false,
            ShouldFormatMarkdown,
            false,
            false,
            null,
            false
        );

        await console.Output.WriteLineAsync(
            $"Patching message {MessageId} in channel '{channel.Name}' (#{channel.Id})..."
        );

        var result = await MessagePatcher.PatchMessageAsync(
            request,
            Discord,
            MessageId,
            cancellationToken
        );

        stopwatch.Stop();

        if (result.Patched)
        {
            await console.Output.WriteLineAsync(
                $"Done in {stopwatch.ElapsedMilliseconds}ms: {result.Reason}"
            );
        }
        else
        {
            using (console.WithForegroundColor(ConsoleColor.DarkYellow))
            {
                await console.Error.WriteLineAsync(
                    $"Not patched ({stopwatch.ElapsedMilliseconds}ms): {result.Reason}"
                );
            }
        }
    }
}
