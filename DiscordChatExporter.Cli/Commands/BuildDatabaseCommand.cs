using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting.Converting;
using DiscordChatExporter.Core.Exporting.Database;
using Gress;
using Spectre.Console;

namespace DiscordChatExporter.Cli.Commands;

[Command(
    "todatabase",
    Description = "Builds or updates a consolidated SQLite database from previously exported "
        + "JSON chat log(s), without contacting Discord. Every channel found under the input "
        + "path(s) is imported into the same database file, keyed by guild/channel/message ID, "
        + "so the command can be re-run at any time (e.g. after a fresh export) to pick up new "
        + "or changed messages without duplicating existing rows."
)]
public partial class BuildDatabaseCommand : ICommand
{
    [CommandOption(
        "input",
        'i',
        Description = "Path to a JSON export file, or a directory containing JSON export files (searched recursively)."
    )]
    public required IReadOnlyList<string> InputPaths { get; set; }

    [CommandOption(
        "output",
        'o',
        Description = "Path to the SQLite database file. Created if it doesn't already exist, otherwise updated in place."
    )]
    public string OutputPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = Path.GetFullPath(value);
    } = "";

    [CommandOption(
        "skip-unchanged",
        Description = "Skip channels whose stored data is already at least as new as the input JSON export."
    )]
    public bool ShouldSkipUnchanged { get; set; }

    [CommandOption(
        "strict",
        Description = "Treat any per-channel import error as a fatal failure. "
            + "By default the command succeeds as long as at least one channel imported successfully."
    )]
    public bool IsStrict { get; set; }

    private static int GetPartitionIndex(string filePath)
    {
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        var match = Regex.Match(fileNameWithoutExt, @"\[part\s*(\d+)\]$", RegexOptions.IgnoreCase);

        return match.Success ? int.Parse(match.Groups[1].Value) - 1 : 0;
    }

    private static string GetBaseFileNameKey(string filePath)
    {
        var dirPath = Path.GetDirectoryName(filePath) ?? "";
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

        var baseName = Regex.Replace(
            fileNameWithoutExt,
            @"\s*\[part\s*\d+\]$",
            "",
            RegexOptions.IgnoreCase
        );

        return Path.Combine(dirPath, baseName);
    }

    // Imports one channel's worth of (possibly partitioned) JSON files into the database.
    // Returns false if the channel was skipped because it's already up to date.
    private async ValueTask<bool> ImportChannelGroupAsync(
        SqliteExportStore store,
        IReadOnlyList<string> groupFilePaths,
        IProgress<Percentage>? progress,
        CancellationToken cancellationToken
    )
    {
        var firstFilePath = groupFilePaths[0];
        var (guild, channel, _, _) = ExportedChatParser.ParseMetadata(firstFilePath);

        if (ShouldSkipUnchanged)
        {
            var newestInputWriteTime = groupFilePaths.Max(File.GetLastWriteTimeUtc);
            var storedLastExportedAt = await store.GetChannelLastExportedAtAsync(
                channel.Id,
                cancellationToken
            );

            if (storedLastExportedAt is not null && storedLastExportedAt >= newestInputWriteTime)
                return false;
        }

        await store.UpsertGuildAsync(guild, cancellationToken);
        await store.UpsertChannelAsync(channel, cancellationToken);

        // Pass 1: collect every member/role mentioned anywhere in the file group, so users can
        // be upserted with their nickname/color/roles before any message referencing them lands.
        var members = new Dictionary<Snowflake, Member>();
        var roles = new Dictionary<Snowflake, Role>();
        var totalMessageCount = 0;

        foreach (var filePath in groupFilePaths)
        {
            var (fileMembers, fileRoles, fileMessageCount) =
                await ExportedChatParser.CollectMetadataAndCountStreamingAsync(
                    filePath,
                    cancellationToken: cancellationToken
                );

            foreach (var (id, member) in fileMembers)
                members[id] = member;
            foreach (var (id, role) in fileRoles)
                roles[id] = role;

            totalMessageCount += fileMessageCount;
        }

        foreach (var role in roles.Values)
            await store.UpsertRoleAsync(role, guild.Id, cancellationToken);

        foreach (var member in members.Values)
            await store.UpsertUserAsync(member.User, member, roles, cancellationToken);

        // Pass 2: stream messages from every partition file and upsert them one at a time, so
        // memory use stays flat regardless of how large the channel's export is.
        Snowflake? maxMessageId = null;
        var processedMessageCount = 0;

        foreach (var filePath in groupFilePaths)
        {
            await foreach (
                var message in ExportedChatParser.StreamParsedMessagesAsync(
                    filePath,
                    cancellationToken
                )
            )
            {
                await store.UpsertMessageAsync(channel.Id, message, cancellationToken);

                if (maxMessageId is null || message.Id > maxMessageId.Value)
                    maxMessageId = message.Id;

                processedMessageCount++;
                progress?.Report(
                    Percentage.FromFraction(
                        (double)processedMessageCount / Math.Max(1, totalMessageCount)
                    )
                );
            }
        }

        await store.UpdateChannelExportStateAsync(
            channel.Id,
            maxMessageId ?? channel.LastMessageId,
            channel.IsArchived,
            DateTimeOffset.UtcNow,
            null,
            null,
            cancellationToken
        );

        // Commit this channel's writes now, so a crash partway through the overall run only
        // loses progress on the channel currently in flight, not everything imported so far.
        await store.FlushAsync(cancellationToken);

        return true;
    }

    public async ValueTask ExecuteAsync(IConsole console)
    {
        var cancellationToken = console.RegisterCancellationHandlerWithSignals();

        if (string.IsNullOrWhiteSpace(OutputPath))
            throw new CommandException("Option --output is required.");

        var inputFilePaths = new List<string>();
        foreach (var inputPath in InputPaths)
        {
            if (Directory.Exists(inputPath))
            {
                inputFilePaths.AddRange(
                    Directory.EnumerateFiles(inputPath, "*.json", SearchOption.AllDirectories)
                );
            }
            else if (File.Exists(inputPath))
            {
                inputFilePaths.Add(inputPath);
            }
            else
            {
                throw new CommandException($"Input path '{inputPath}' does not exist.");
            }
        }

        inputFilePaths.RemoveAll(path =>
            string.Equals(
                Path.GetFileName(path),
                ".discord_backup_manifest.json",
                StringComparison.OrdinalIgnoreCase
            )
        );

        if (inputFilePaths.Count <= 0)
        {
            throw new CommandException(
                "No JSON export files found at the specified input path(s)."
            );
        }

        var groupedInputFiles = inputFilePaths
            .GroupBy(GetBaseFileNameKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(GetPartitionIndex).ToList())
            .ToList();

        await using var store = await SqliteExportStore.OpenAsync(OutputPath, cancellationToken);

        var errorsByFile = new ConcurrentDictionary<string, string>();
        var skippedCount = 0;
        var importedCount = 0;

        await console.Output.WriteLineAsync(
            $"Importing {groupedInputFiles.Count} channel(s) into '{OutputPath}'..."
        );

        await console
            .CreateProgressTicker()
            .StartAsync(async ctx =>
            {
                foreach (var group in groupedInputFiles)
                {
                    var firstFilePath = group[0];

                    try
                    {
                        var imported = true;
                        await ctx.StartTaskAsync(
                            Markup.Escape(Path.GetFileName(GetBaseFileNameKey(firstFilePath))),
                            async progress =>
                                imported = await ImportChannelGroupAsync(
                                    store,
                                    group,
                                    progress.ToPercentageBased(),
                                    cancellationToken
                                )
                        );

                        if (imported)
                            importedCount++;
                        else
                            skippedCount++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await store.RollbackAsync(CancellationToken.None);

                        errorsByFile[firstFilePath] = ex.Message;

                        if (IsStrict)
                            throw new CommandException(
                                $"Failed to import '{firstFilePath}': {ex.Message}",
                                innerException: ex
                            );
                    }
                }
            });

        await store.FlushAsync(cancellationToken);

        using (console.WithForegroundColor(ConsoleColor.White))
        {
            await console.Output.WriteLineAsync(
                $"Successfully imported {importedCount} channel(s)."
            );

            if (skippedCount > 0)
                await console.Output.WriteLineAsync(
                    $"Skipped {skippedCount} unchanged channel(s)."
                );
        }

        if (!errorsByFile.IsEmpty)
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Red))
            {
                await console.Error.WriteLineAsync("Failed to import the following channel(s):");
            }

            foreach (var (filePath, message) in errorsByFile)
            {
                await console.Error.WriteAsync($"{filePath}: ");
                using (console.WithForegroundColor(ConsoleColor.Red))
                    await console.Error.WriteLineAsync(message);
            }

            await console.Error.WriteLineAsync();
        }

        if (errorsByFile.Count >= groupedInputFiles.Count)
            throw new CommandException("Import failed.");
    }
}
