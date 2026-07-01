using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Database;

namespace DiscordChatExporter.Cli.Commands;

[Command(
    "mark-deleted",
    Description = "Marks one or more already-exported messages as deleted (soft-delete: the row "
        + "and its content are preserved, only a deleted-at timestamp is set) in a consolidated "
        + "SQLite database. Accepts multiple -m options so a single MESSAGE_DELETE_BULK gateway "
        + "event becomes one invocation instead of one per deleted message. Makes no Discord "
        + "request -- by the time this is called the message is already gone."
)]
public partial class MarkMessageDeletedCommand : ICommand
{
    [CommandOption("channel", 'c', Description = "Channel ID the deleted message(s) belonged to.")]
    public required Snowflake ChannelId { get; set; }

    [CommandOption("message", 'm', Description = "Message ID to mark as deleted. Repeatable.")]
    public required IReadOnlyList<Snowflake> MessageIds { get; set; }

    [CommandOption("output", 'o', Description = "Path to the SQLite database file.")]
    public required string OutputPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = Path.GetFullPath(value);
    }

    [CommandOption(
        "format",
        'f',
        Description = "Kept for parity with other database-targeting commands. Only 'Db' is supported."
    )]
    public ExportFormat ExportFormat { get; set; } = ExportFormat.Db;

    public async ValueTask ExecuteAsync(IConsole console)
    {
        if (ExportFormat != ExportFormat.Db)
            throw new CommandException("Option --format only supports 'Db' for mark-deleted.");

        if (!File.Exists(OutputPath))
        {
            throw new CommandException(
                $"Database file '{OutputPath}' does not exist. "
                    + "mark-deleted expects a database already created by a prior "
                    + "'export --format Db' or 'todatabase' run -- check the --output path."
            );
        }

        var cancellationToken = console.RegisterCancellationHandlerWithSignals();

        await using var store = await SqliteExportStore.OpenAsync(OutputPath, cancellationToken);

        var deletedAt = DateTimeOffset.UtcNow;
        var notUpdatedMessageIds = new List<Snowflake>();
        foreach (var messageId in MessageIds)
        {
            var wasUpdated = await store.MarkMessageDeletedAsync(
                ChannelId,
                messageId,
                deletedAt,
                cancellationToken
            );
            if (!wasUpdated)
                notUpdatedMessageIds.Add(messageId);
        }

        await store.FlushAsync(cancellationToken);

        var updatedCount = MessageIds.Count - notUpdatedMessageIds.Count;
        await console.Output.WriteLineAsync(
            $"Marked {updatedCount} message(s) in channel #{ChannelId} as deleted."
        );

        if (notUpdatedMessageIds.Count > 0)
        {
            using (console.WithForegroundColor(ConsoleColor.DarkYellow))
            {
                await console.Error.WriteLineAsync(
                    $"Warning: {notUpdatedMessageIds.Count} message(s) were not updated "
                        + $"(not found in channel #{ChannelId}, or already marked deleted): "
                        + string.Join(", ", notUpdatedMessageIds)
                );
            }
        }
    }
}
