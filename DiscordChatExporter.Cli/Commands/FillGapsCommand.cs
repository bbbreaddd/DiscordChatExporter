using System;
using System.IO;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Database;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;

namespace DiscordChatExporter.Cli.Commands;

[Command(
    "fillgaps",
    Description = "Backfills the downtime gaps the watcher recorded while catch-up was disabled. "
        + "Each gap is the range of messages between a channel's stored cursor and its actual "
        + "latest message at a reconnect -- messages that arrived while the watcher was offline "
        + "and nothing backfilled. This fetches just those ranges (not a full rescan) and marks "
        + "them filled."
)]
public partial class FillGapsCommand : DiscordCommandBase
{
    [CommandOption("output", 'o', Description = "Path to the SQLite database file.")]
    public required string OutputPath
    {
        get;
        set => field = Path.GetFullPath(value);
    }

    [CommandOption(
        "guild",
        'g',
        Description = "Only fill gaps for this server. By default, gaps for every server in the "
            + "database are filled."
    )]
    public Snowflake? GuildId { get; set; }

    [CommandOption("media", Description = "Download media referenced by the backfilled messages.")]
    public bool ShouldDownloadAssets { get; set; }

    [CommandOption(
        "media-dir",
        Description = "Download media to this directory. If not specified, the media directory will be derived from the output path."
    )]
    public string? AssetsDirPath
    {
        get;
        set => field = value is not null ? Path.GetFullPath(value) : null;
    }

    [CommandOption(
        "retry-failed",
        Description = "Retry media URLs previously recorded as permanently gone (404/410), "
            + "instead of skipping them. By default such URLs are only attempted once."
    )]
    public bool RetryFailedMedia { get; set; }

    [CommandOption(
        "enrich-reactors",
        Description = "Fetch the full reactor list for backfilled messages (one paginated request "
            + "per unique emoji per message). Off by default: gap messages store emoji + counts only."
    )]
    public bool EnrichReactors { get; set; }

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        await base.ExecuteAsync(console);

        if (!string.IsNullOrWhiteSpace(AssetsDirPath) && !ShouldDownloadAssets)
            throw new CommandException("Option --media-dir cannot be used without --media.");

        if (!File.Exists(OutputPath))
        {
            throw new CommandException(
                $"Database file '{OutputPath}' does not exist. "
                    + "fillgaps expects a database already produced by the watcher -- check the "
                    + "--output path."
            );
        }

        var cancellationToken = console.RegisterCancellationHandlerWithSignals();

        await using var store = await SqliteExportStore.OpenAsync(
            OutputPath,
            ShouldDownloadAssets ? AssetsDirPath ?? $"{OutputPath}_Files" : null,
            RetryFailedMedia,
            cancellationToken
        );

        var gaps = await store.GetUnfilledGapsAsync(GuildId, cancellationToken);
        if (gaps.Count == 0)
        {
            await console.Output.WriteLineAsync("No unfilled gaps to backfill.");
            return;
        }

        await console.Output.WriteLineAsync($"Filling {gaps.Count} gap(s)...");

        var exporter = new ChannelExporter(Discord);
        var filled = 0;
        var failed = 0;

        foreach (var gap in gaps)
        {
            try
            {
                // The channel may have been deleted since the gap was recorded -- skip it if so.
                var channel = await Discord.TryGetChannelAsync(gap.ChannelId, cancellationToken);
                if (channel is null)
                {
                    await console.Output.WriteLineAsync(
                        $"Skipping gap in channel {gap.ChannelId}: channel no longer exists."
                    );
                    // Mark filled so a deleted channel's gap doesn't linger forever.
                    await store.MarkGapFilledAsync(gap.Id, cancellationToken);
                    await store.FlushAsync(cancellationToken);
                    continue;
                }

                var guild = await Discord.GetGuildAsync(channel.GuildId, cancellationToken);

                // Snapshot the channel's export window so we can restore it after the bounded
                // backfill: ChannelExporter records this run's After/Before onto the channel row,
                // which would otherwise make a later catch-up treat the channel as needing a full
                // rescan. The message cursor (last_message_id) is never regressed by the export.
                var state = await store.GetChannelStateAsync(gap.ChannelId, cancellationToken);

                var request = new ExportRequest(
                    guild,
                    channel,
                    OutputPath,
                    null,
                    ExportFormat.Db,
                    gap.AfterMessageId,
                    gap.BeforeMessageId,
                    PartitionLimit.Null,
                    MessageFilter.Null,
                    false,
                    true,
                    false,
                    false,
                    null,
                    false,
                    enrichReactors: EnrichReactors
                );

                try
                {
                    await exporter.ExportChannelAsync(request, store, null, cancellationToken);
                }
                catch (ChannelEmptyException)
                {
                    // The gap window turned out to contain no messages (e.g. everything in it was
                    // already deleted on Discord). Nothing to backfill -- still count it resolved.
                }

                await store.SetChannelExportWindowAsync(
                    gap.ChannelId,
                    state?.LastExportAfter,
                    state?.LastExportBefore,
                    cancellationToken
                );
                await store.MarkGapFilledAsync(gap.Id, cancellationToken);
                await store.FlushAsync(cancellationToken);
                filled++;

                await console.Output.WriteLineAsync(
                    $"Filled gap in '{channel.Name}' ({gap.AfterMessageId} .. {gap.BeforeMessageId})."
                );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                using (console.WithForegroundColor(ConsoleColor.Yellow))
                    await console.Error.WriteLineAsync(
                        $"Skipped gap {gap.Id} in channel {gap.ChannelId}: {ex.Message}"
                    );
            }
        }

        await console.Output.WriteLineAsync(
            $"Done. Filled {filled} gap(s)"
                + (failed > 0 ? $", {failed} failed (left unfilled for a later retry)." : ".")
        );
    }
}
