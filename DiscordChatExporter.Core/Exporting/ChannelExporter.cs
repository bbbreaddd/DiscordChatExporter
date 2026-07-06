using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting.Converting;
using DiscordChatExporter.Core.Exporting.Database;
using Gress;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

public class ChannelExporter(DiscordClient discord)
{
    // Live export straight into a consolidated SQLite database. Message IDs are globally unique
    // and the database enforces that via its primary key, so -- unlike the JSON path above --
    // there is no byte-level merge, partitioning, or crash-recovery machinery to replicate here:
    // every write is a plain upsert, and a transaction that never committed is simply retried
    // (from the last message id actually stored) the next time this runs.
    public async ValueTask ExportChannelAsync(
        ExportRequest request,
        SqliteExportStore databaseStore,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        // Forum channels don't have messages, they are just a list of threads
        if (request.Channel.Kind == ChannelKind.GuildForum)
        {
            throw new DiscordChatExporterException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"is a forum and cannot be exported directly. "
                    + "You need to pull its threads and export them individually."
            );
        }

        await databaseStore.UpsertGuildAsync(request.Guild, cancellationToken);
        await databaseStore.UpsertChannelAsync(request.Channel, cancellationToken);

        // Thread member lists have no dedicated export path of their own (they're not messages),
        // so piggyback on every thread export/catch-up/force-scan pass instead of a separate
        // backfill job. NOTE: for a bot without the privileged GUILD_MEMBERS intent, this list
        // endpoint 403s unconditionally (see the long comment on GetThreadMembersAsync) -- it
        // still yields an empty sequence rather than throwing in that case, so this table simply
        // stays empty rather than turning into a fatal failure for the whole channel export.
        if (request.Channel.IsThread)
        {
            var threadMembers = new List<ThreadMember>();
            await foreach (
                var threadMember in discord.GetThreadMembersAsync(
                    request.Channel.Id,
                    cancellationToken
                )
            )
            {
                threadMembers.Add(threadMember);
            }
            await databaseStore.UpsertThreadMembersAsync(
                request.Channel.Id,
                threadMembers,
                cancellationToken
            );
        }

        // Commit now, before any of the checks below can throw (e.g. an empty channel). The
        // caller rolls back the pending transaction on a non-fatal failure so that a channel's
        // partially-fetched messages don't linger half-written -- but this channel's own
        // metadata (name, position, category, ...) is already complete and correct at this
        // point, and must survive that rollback rather than reverting to a stale prior value.
        await databaseStore.FlushAsync(cancellationToken);

        var storedState = await databaseStore.GetChannelStateAsync(
            request.Channel.Id,
            cancellationToken
        );

        var stateMatchesRequest =
            storedState is not null && ChannelStateMatchesRequest(storedState, request);

        // Skip if nothing has changed since the last time this channel was exported into this
        // database -- mirrors the JSON path's manifest-based skip (see HeaderMatchesRequest).
        if (
            !request.ForceFullScan
            && storedState is not null
            && storedState.LastMessageId == request.Channel.LastMessageId
            && stateMatchesRequest
        )
        {
            progress?.Report(new ExportProgress(Percentage.FromFraction(1.0)));
            return;
        }

        // Same idea, but for --force-full-scan specifically: if this channel already went
        // through a full force-scan covering everything up to its current last message (e.g. a
        // prior --force-full-scan run reached and finished it before crashing on some other,
        // later channel), there's nothing left to backfill -- skip the redundant full re-walk
        // rather than re-fetching potentially years of history all over again.
        if (
            request.ForceFullScan
            && storedState is not null
            && storedState.ForceScannedMessageId == request.Channel.LastMessageId
            && stateMatchesRequest
        )
        {
            progress?.Report(new ExportProgress(Percentage.FromFraction(1.0)));
            return;
        }

        // Only trust the stored LastMessageId as a resume point when this run's After/Before
        // (and channel metadata) match the run that produced it. Otherwise (e.g. the user
        // widened --after to backfill older history), clamping fetchAfter to LastMessageId would
        // silently narrow the fetch range right back to "nothing new" even though the request
        // asks for a wider range -- upserts are idempotent, so re-fetching overlap is safe.
        var fetchAfter = request.After;
        if (
            !request.ForceFullScan
            && stateMatchesRequest
            && storedState?.LastMessageId is { } lastMessageId
        )
        {
            fetchAfter =
                fetchAfter is not null && fetchAfter > lastMessageId ? fetchAfter : lastMessageId;
        }
        // A channel that's already been through one full force-scan (the skip above didn't fire
        // only because new messages have since arrived) doesn't need another full re-walk --
        // everything up to the watermark was already backfilled, so just catch up on what's new,
        // the same way the non-force path resumes from LastMessageId.
        else if (
            request.ForceFullScan
            && stateMatchesRequest
            && storedState?.ForceScannedMessageId is { } forceScannedMessageId
        )
        {
            fetchAfter =
                fetchAfter is not null && fetchAfter > forceScannedMessageId
                    ? fetchAfter
                    : forceScannedMessageId;
        }

        // Forum channels don't have messages; an empty/filtered-out channel produces nothing to
        // write, which the JSON export path surfaces as a warning rather than a silent success --
        // mirror that here instead of reporting "exported" for a channel that wrote zero rows.
        if (request.Channel.IsEmpty)
        {
            throw new ChannelEmptyException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"does not contain any messages."
            );
        }

        if (
            (
                request.Before is not null
                && !request.Channel.MayHaveMessagesBefore(request.Before.Value)
            )
            || (
                request.After is not null
                && !request.Channel.MayHaveMessagesAfter(request.After.Value)
            )
        )
        {
            throw new ChannelEmptyException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"does not contain any messages within the specified period."
            );
        }

        var context = new ExportContext(discord, request);
        await context.PopulateChannelsAndRolesAsync(cancellationToken);

        foreach (var role in context.Roles.Values)
            await databaseStore.UpsertRoleAsync(role, request.Guild.Id, cancellationToken);

        var messages = !request.IsReverseMessageOrder
            ? discord.GetMessagesAsync(
                request.Channel.Id,
                fetchAfter,
                request.Before,
                progress,
                cancellationToken
            )
            : discord.GetMessagesInReverseAsync(
                request.Channel.Id,
                fetchAfter,
                request.Before,
                progress,
                cancellationToken
            );

        var maxMessageId = storedState?.LastMessageId;

        await foreach (var message in messages)
        {
            try
            {
                foreach (var user in message.GetReferencedUsers())
                {
                    await context.PopulateMemberAsync(user, cancellationToken);
                    var member = context.TryGetMember(user.Id);

                    await databaseStore.UpsertUserAsync(
                        user,
                        member,
                        context.GetUserRoles(user.Id),
                        cancellationToken
                    );
                }

                if (request.MessageFilter.IsMatch(message))
                {
                    await databaseStore.UpsertMessageAsync(
                        request.Channel.Id,
                        message,
                        cancellationToken
                    );

                    if (maxMessageId is null || message.Id > maxMessageId.Value)
                        maxMessageId = message.Id;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // always propagate cancellation
            }
            catch (Exception ex)
            {
                // A plain (non-DiscordChatExporterException) failure here is usually a transient
                // network hiccup (e.g. a cut-off HTTP response) -- one bad message must not be
                // fatal to a run spanning tens of thousands of channels/threads. Only escalate if
                // the underlying exception was already explicitly marked fatal (auth failure,
                // unrecognized HTTP error, ...); everything else just fails this one channel and
                // lets the caller move on to the next.
                throw new DiscordChatExporterException(
                    $"Failed to export message #{message.Id} "
                        + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                        + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                    ex is DiscordChatExporterException dex && dex.IsFatal,
                    ex
                );
            }
        }

        await databaseStore.UpdateChannelExportStateAsync(
            request.Channel.Id,
            maxMessageId ?? request.Channel.LastMessageId,
            request.Channel.IsArchived,
            DateTimeOffset.UtcNow,
            request.After,
            request.Before,
            cancellationToken,
            forceScannedMessageId: request.ForceFullScan
                ? maxMessageId ?? request.Channel.LastMessageId
                : null
        );

        // Commit now so a channel that completes successfully is never rolled back by a later
        // channel's failure sharing the same pending transaction.
        await databaseStore.FlushAsync(cancellationToken);
    }

    // Compares a channel's stored database state against the live request, analogous to
    // HeaderMatchesRequest for the JSON export format. Icon URLs aren't tracked in the channel
    // table, so (unlike the JSON path) an icon-only change won't by itself trigger a re-export.
    private static bool ChannelStateMatchesRequest(
        SqliteExportStore.ChannelState state,
        ExportRequest request
    )
    {
        if (state.Kind != request.Channel.Kind)
            return false;

        if (state.Name != request.Channel.Name)
            return false;

        if (state.Topic != request.Channel.Topic)
            return false;

        if (state.CategoryId != request.Channel.Parent?.Id)
            return false;

        if (state.Category != request.Channel.Parent?.Name)
            return false;

        if (state.ParentCategoryId != request.Channel.Parent?.Parent?.Id)
            return false;

        if (state.ParentCategory != request.Channel.Parent?.Parent?.Name)
            return false;

        if (state.LastExportAfter != request.After)
            return false;

        if (state.LastExportBefore != request.Before)
            return false;

        return true;
    }

    public async ValueTask ExportChannelAsync(
        ExportRequest request,
        ExportManifest? manifest = null,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        // --- Pick up a pre-existing file under a different name, if any ---
        // The freshly-computed output path embeds the current guild/category/channel name,
        // which can drift from what was used last time (channel or category renamed on
        // Discord, or the name-escaping rules changed between versions). If nothing exists
        // at the fresh path yet, look for a file already tracking this channel ID under a
        // different name/location. Rename it (along with its partitions and default-convention
        // assets folder) to the fresh path, so the file on disk picks up the channel's current
        // name instead of staying stuck under a stale one forever. If the fresh path is already
        // taken by something else, fall back to just redirecting output to the old file
        // instead, like before.
        if (request.IsIncremental)
            ExistingOutputRelocator.RelocateIfNeeded(request);

        // --- Hard-crash recovery ---
        // If a previous run was killed mid-export (process killed, power loss, OOM), the normal
        // graceful paths that finalize and merge progress never ran. Repair/promote any salvageable
        // in-progress writer temp and clean up regenerable scratch files before deciding how to
        // resume, so progress isn't silently restarted from scratch.
        if (request.IsIncremental)
            await CrashRecovery.RecoverAsync(request, manifest, cancellationToken);

        // --- Manifest-based skip: channel has not changed since last export ---
        var manifestEntry = manifest?.Channels.GetValueOrDefault(request.Channel.Id.ToString());
        if (
            request.IsIncremental
            && manifestEntry is not null
            && File.Exists(request.OutputFilePath)
        )
        {
            // Even when the channel's LastMessageId hasn't moved (no new messages), the
            // guild/category/channel metadata recorded in the existing file's header can still
            // be stale -- e.g. the channel or one of its parent categories was renamed on
            // Discord. Treat that as a reason to re-export too, since otherwise the header
            // would stay stuck under outdated names forever (there are no new messages to
            // trigger a refresh).
            bool headerMatches;
            try
            {
                headerMatches = HeaderMatchesRequest(request.OutputFilePath, request);
            }
            catch (Exception ex)
            {
                throw new DiscordChatExporterException(
                    $"Failed to parse the existing JSON export file '{request.OutputFilePath}' "
                        + "to check whether its metadata is still up to date.",
                    true,
                    ex
                );
            }

            if (
                manifestEntry.LastMessageId == request.Channel.LastMessageId?.ToString()
                && headerMatches
            )
            {
                progress?.Report(new ExportProgress(Percentage.FromFraction(1.0)));
                return;
            }
        }

        // Forum channels don't have messages, they are just a list of threads
        if (request.Channel.Kind == ChannelKind.GuildForum)
        {
            throw new DiscordChatExporterException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"is a forum and cannot be exported directly. "
                    + "You need to pull its threads and export them individually."
            );
        }

        // --- Determine fetch starting point ---
        // When we have a manifest entry with a LastMessageId, we use the streaming-append
        // path: new messages are written to a separate temp file and then merged into the
        // existing file at the byte level, without loading either file's messages into RAM.
        var fetchAfter = request.After;
        var useStreamingAppend = false;
        var existingPartitionPaths = Array.Empty<string>();
        Snowflake? resolvedLastMessageId = null;

        if (request.IsIncremental && File.Exists(request.OutputFilePath))
        {
            existingPartitionPaths = ExistingOutputRelocator.GetExistingPartitionFilePaths(
                request.OutputFilePath
            );

            if (manifestEntry?.LastMessageId is { } lastMsgIdStr)
            {
                resolvedLastMessageId = Snowflake.TryParse(lastMsgIdStr);
            }
            else if (!request.IsReverseMessageOrder)
            {
                // No manifest entry yet for this channel — either it was exported before
                // incremental/manifest support existed, or a previous run was interrupted
                // before the manifest got saved. Rather than parsing the whole channel
                // (which could be many partitions / gigabytes) to find where to resume,
                // only parse the newest partition file on disk: messages are written in
                // ascending order, so its max message ID is the channel's true resume point.
                // (This shortcut only holds for ascending order: in reverse mode the newest
                // messages land in partition #1, not the last one, so we fall through to the
                // full legacy parse below instead of risking a wrong resume point.)
                var latestPartitionPath = existingPartitionPaths[^1];

                try
                {
                    await using var inputStream = File.OpenRead(latestPartitionPath);
                    using var document = await JsonDocument.ParseAsync(
                        inputStream,
                        cancellationToken: cancellationToken
                    );
                    var latestPartitionChat = ExportedChatParser.Parse(document.RootElement);
                    if (latestPartitionChat.Messages.Any())
                        resolvedLastMessageId = latestPartitionChat.Messages.Max(m => m.Id);
                }
                catch (Exception ex)
                {
                    throw new DiscordChatExporterException(
                        $"Failed to parse the existing JSON export file '{latestPartitionPath}' "
                            + "to determine the incremental export resume point.",
                        true,
                        ex
                    );
                }
            }

            if (resolvedLastMessageId is not null)
            {
                fetchAfter =
                    fetchAfter is not null && fetchAfter > resolvedLastMessageId.Value
                        ? fetchAfter
                        : resolvedLastMessageId.Value;
                useStreamingAppend = true;
            }
        }

        // --- Streaming-append path (O(1) memory regardless of existing file size) ---
        if (useStreamingAppend)
        {
            var context = new ExportContext(discord, request);
            await context.PopulateChannelsAndRolesAsync(cancellationToken);

            var messages = !request.IsReverseMessageOrder
                ? discord.GetMessagesAsync(
                    request.Channel.Id,
                    fetchAfter,
                    request.Before,
                    progress,
                    cancellationToken
                )
                : discord.GetMessagesInReverseAsync(
                    request.Channel.Id,
                    fetchAfter,
                    request.Before,
                    progress,
                    cancellationToken
                );

            // Write new messages to a separate temp file using a fresh export context.
            // This reuses the existing MessageExporter/JsonMessageWriter pipeline as-is.
            var appendTempPath = request.OutputFilePath + ".new.tmp";
            var mergedTempPath = request.OutputFilePath + ".merged.tmp";
            long newMessageCount = 0;
            Snowflake? newMaxMessageId = null;
            var isStreamingAppendCompletedSuccessfully = false;
            // Set to true only after the merge AND the final File.Move both complete so we
            // know that appendTempPath's contents are no longer needed. If the merge throws
            // (e.g., disk full), we must NOT delete appendTempPath so the user can recover
            // by freeing space and re-running; deleting it would force a full re-fetch.
            var mergeCompleted = false;

            // Not an 'await using' local -- CheckpointsByPartition is only fully populated once
            // DisposeAsync finalizes the last partition, and the merge/index-update logic below
            // needs to read it afterward, so the variable has to outlive the disposal itself
            // (disposal is still guaranteed via the explicit try/finally right below).
            var messageExporter = new MessageExporter(context, appendTempPath);

            try
            {
                try
                {
                    try
                    {
                        await foreach (var message in messages)
                        {
                            try
                            {
                                foreach (var user in message.GetReferencedUsers())
                                    await context.PopulateMemberAsync(user, cancellationToken);

                                if (request.MessageFilter.IsMatch(message))
                                {
                                    await messageExporter.ExportMessageAsync(
                                        message,
                                        cancellationToken
                                    );
                                    newMessageCount++;
                                    if (
                                        newMaxMessageId is null
                                        || message.Id > newMaxMessageId.Value
                                    )
                                        newMaxMessageId = message.Id;
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw; // always propagate cancellation
                            }
                            catch (Exception ex)
                            {
                                // See the DB export path's identical catch for why this defaults
                                // to non-fatal: a transient per-message failure must not crash a
                                // run spanning tens of thousands of channels/threads.
                                throw new DiscordChatExporterException(
                                    $"Failed to export message #{message.Id} "
                                        + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                                        + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                                    ex is DiscordChatExporterException dex && dex.IsFatal,
                                    ex
                                );
                            }
                        }
                    }
                    catch
                    {
                        // appendTempPath is always scratch (never pre-existing precious
                        // data), so leave the default (don't force-overwrite) -- repair
                        // truncates to the last complete message and promotes it if nothing
                        // else is in the way; otherwise the bare .tmp is left for crash
                        // recovery to pick up next run, same as it already does for a real
                        // kill at this exact point.
                        messageExporter.Abandon();
                        throw;
                    }
                    finally
                    {
                        await messageExporter.DisposeAsync();
                    }
                    isStreamingAppendCompletedSuccessfully = true;
                }
                finally
                {
                    // Merge and save whatever was written up to this point, even if an exception was thrown.
                    // This enables auto-resume of progress on subsequent runs. The merge always runs, even
                    // when zero new messages were found, because it's also what refreshes the
                    // guild/channel/category metadata and exportedAt timestamp from the fresh temp export
                    // (e.g. after a rename) -- a header that's only refreshed when there happen to be new
                    // messages would stay stuck under stale metadata indefinitely on a quiet channel.
                    {
                        var mergeTargetPath = existingPartitionPaths[^1];

                        await IncrementalJsonAppender.MergeAsync(
                            mergeTargetPath,
                            appendTempPath,
                            mergedTempPath,
                            CancellationToken.None
                        );
                        File.Move(mergedTempPath, mergeTargetPath, overwrite: true);
                        mergeCompleted = true;

                        // Best-effort: keep the per-channel message index (used to patch old
                        // messages -- e.g. reaction updates -- without a full rescan) up to date
                        // with the batch just merged in. Never lets an index problem fail the
                        // export itself; a missing/stale entry just means a slower lookup (or a
                        // one-time lazy rebuild) the next time something tries to patch this
                        // channel, not lost or corrupted data.
                        try
                        {
                            var indexFilePath = MessageIndex.GetIndexFilePath(
                                request.OutputFilePath
                            );
                            var index = await MessageIndex.LoadAsync(indexFilePath);
                            var targetPartitionIndex = existingPartitionPaths.Length - 1;
                            var existingEntry = index.Partitions.Find(p =>
                                p.Index == targetPartitionIndex
                            );

                            var primaryCheckpoints =
                                messageExporter.CheckpointsByPartition.GetValueOrDefault(
                                    0,
                                    Array.Empty<(string MessageId, long ByteOffset)>()
                                );

                            var mergedEntry = IncrementalJsonAppender.BuildMergedCheckpoints(
                                mergeTargetPath,
                                appendTempPath,
                                existingEntry,
                                targetPartitionIndex,
                                primaryCheckpoints,
                                newMaxMessageId?.ToString()
                            );

                            if (mergedEntry is not null)
                            {
                                index.Partitions.RemoveAll(p => p.Index == targetPartitionIndex);
                                index.Partitions.Add(mergedEntry);
                            }

                            if (await index.SaveAsync(indexFilePath) is { } saveEx)
                                Console.Error.WriteLine(
                                    $"Failed to save message index: {saveEx.Message}"
                                );
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(
                                $"Failed to update message index (will rebuild on next patch attempt): {ex.Message}"
                            );
                        }

                        // If the newly-fetched messages alone exceeded the partition limit, the
                        // writer above already split them into further temp partitions
                        // (appendTempPath, appendTempPath " [part 2]", ...). Only the first one
                        // was merged above; promote the rest to new partitions instead of
                        // silently dropping them.
                        var nextPartitionIndex = existingPartitionPaths.Length;
                        for (var i = 1; ; i++)
                        {
                            var overflowTempPath = MessageExporter.GetPartitionFilePath(
                                appendTempPath,
                                i
                            );
                            if (!File.Exists(overflowTempPath))
                                break;

                            var newPartitionPath = MessageExporter.GetPartitionFilePath(
                                request.OutputFilePath,
                                nextPartitionIndex
                            );
                            File.Move(overflowTempPath, newPartitionPath, overwrite: false);

                            // These are moved as complete, standalone files (not byte-spliced),
                            // so their checkpoints are already valid as-is -- no offset
                            // adjustment needed, unlike the primary merge above.
                            try
                            {
                                var overflowCheckpoints =
                                    messageExporter.CheckpointsByPartition.GetValueOrDefault(
                                        i,
                                        Array.Empty<(string MessageId, long ByteOffset)>()
                                    );
                                if (overflowCheckpoints.Count > 0)
                                {
                                    var indexFilePath = MessageIndex.GetIndexFilePath(
                                        request.OutputFilePath
                                    );
                                    var index = await MessageIndex.LoadAsync(indexFilePath);
                                    index.Partitions.RemoveAll(p => p.Index == nextPartitionIndex);
                                    index.Partitions.Add(
                                        new PartitionIndexEntry
                                        {
                                            Index = nextPartitionIndex,
                                            MinMessageId = overflowCheckpoints[0].MessageId,
                                            MaxMessageId =
                                                IncrementalJsonAppender.TryGetLastMessageId(
                                                    newPartitionPath
                                                ) ?? overflowCheckpoints[^1].MessageId,
                                            Checkpoints = overflowCheckpoints
                                                .Select(c => new CheckpointEntry
                                                {
                                                    MessageId = c.MessageId,
                                                    ByteOffset = c.ByteOffset,
                                                })
                                                .ToList(),
                                        }
                                    );
                                    _ = await index.SaveAsync(indexFilePath);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine(
                                    $"Failed to update message index for overflow partition (will rebuild on next patch attempt): {ex.Message}"
                                );
                            }

                            nextPartitionIndex++;
                        }
                    }

                    // Update manifest if we either completed successfully, or managed to export new messages before failing.
                    if (
                        manifest is not null
                        && (isStreamingAppendCompletedSuccessfully || newMessageCount > 0)
                    )
                    {
                        var updatedLastMessageId =
                            newMaxMessageId?.ToString()
                            ?? resolvedLastMessageId?.ToString()
                            ?? manifestEntry?.LastMessageId;
                        manifest.UpdateEntry(
                            request.Channel.Id.ToString(),
                            updatedLastMessageId,
                            request.Channel.IsArchived
                        );
                        _ = await manifest.SaveAsync(request.BaseOutputDirPath);
                    }
                }
            }
            finally
            {
                // Only delete appendTempPath if the merge completed successfully. If the merge
                // threw (e.g., disk full), appendTempPath still contains the new messages and
                // the user may be able to recover by freeing space and re-running. Deleting it
                // here would force a full re-fetch from Discord on the next run.
                // mergedTempPath is always safe to delete — it's either already been moved into
                // place (by File.Move above) or never completed, so it's a partial temp at best.
                if (mergeCompleted && File.Exists(appendTempPath))
                    File.Delete(appendTempPath);
                if (File.Exists(mergedTempPath))
                    File.Delete(mergedTempPath);
            }

            return;
        }

        // --- Fresh export or legacy incremental path ---
        // Legacy incremental: existing file present but no manifest entry to guide us.
        // Parse the existing file to find the last message ID. This path should only
        // execute on the very first run (before the manifest exists).
        ExportedChat? existingChat = null;
        if (request.IsIncremental && File.Exists(request.OutputFilePath))
        {
            try
            {
                await using var inputStream = File.OpenRead(request.OutputFilePath);
                using var document = await JsonDocument.ParseAsync(
                    inputStream,
                    cancellationToken: cancellationToken
                );
                existingChat = ExportedChatParser.Parse(document.RootElement);
            }
            catch (Exception ex)
            {
                throw new DiscordChatExporterException(
                    $"Failed to parse the existing JSON export file '{request.OutputFilePath}' for incremental export.",
                    true,
                    ex
                );
            }
        }

        // Build context
        var freshContext = existingChat is not null
            ? new ExportContext(discord, request, existingChat.Members, existingChat.Roles)
            : new ExportContext(discord, request);
        await freshContext.PopulateChannelsAndRolesAsync(cancellationToken);

        // Validate boundaries (only for fresh exports with no existing data)
        if (existingChat is null)
        {
            if (request.Channel.IsEmpty)
            {
                throw new ChannelEmptyException(
                    $"Channel '{request.Channel.Name}' "
                        + $"of guild '{request.Guild.Name}' "
                        + $"does not contain any messages; an empty file will be created."
                );
            }

            if (
                (
                    request.Before is not null
                    && !request.Channel.MayHaveMessagesBefore(request.Before.Value)
                )
                || (
                    request.After is not null
                    && !request.Channel.MayHaveMessagesAfter(request.After.Value)
                )
            )
            {
                throw new ChannelEmptyException(
                    $"Channel '{request.Channel.Name}' "
                        + $"of guild '{request.Guild.Name}' "
                        + $"does not contain any messages within the specified period; an empty file will be created."
                );
            }
        }

        // Adjust the fetch-after for the legacy incremental path
        if (existingChat is not null && existingChat.Messages.Any())
        {
            var lastMessageId = existingChat.Messages.Max(m => m.Id);
            fetchAfter =
                fetchAfter is not null && fetchAfter > lastMessageId ? fetchAfter : lastMessageId;
        }

        var freshMessages = !request.IsReverseMessageOrder
            ? discord.GetMessagesAsync(
                request.Channel.Id,
                fetchAfter,
                request.Before,
                progress,
                cancellationToken
            )
            : discord.GetMessagesInReverseAsync(
                request.Channel.Id,
                fetchAfter,
                request.Before,
                progress,
                cancellationToken
            );

        // Fix 3: stream messages directly to the writer (no intermediate List<Message> buffer)
        // for fresh (non-incremental) exports. For the legacy incremental path, we still need
        // the list to merge with existing messages.
        IEnumerable<Message> orderedMessages;
        if (existingChat is not null)
        {
            // Legacy incremental: buffer new messages so we can merge and dedup
            var newMessages = new List<Message>();
            await foreach (var msg in freshMessages)
                newMessages.Add(msg);

            var allMessages = existingChat
                .Messages.Concat(newMessages)
                .DistinctBy(m => m.Id)
                .ToArray();

            orderedMessages = !request.IsReverseMessageOrder
                ? allMessages.OrderBy(m => m.Id)
                : allMessages.OrderByDescending(m => m.Id);
        }
        else
        {
            // Fresh export: messages arrive in order from the API, no buffering needed.
            // We use an empty placeholder here and stream below.
            orderedMessages = [];
        }

        Snowflake? maxMessageId = null;
        var isFreshExportCompletedSuccessfully = false;

        // Not an 'await using' local, for the same reason as the streaming-append path above:
        // CheckpointsByPartition needs to be read after DisposeAsync finalizes the last
        // partition, once this whole export (a from-scratch rewrite, unlike the streaming-append
        // path) has produced its complete, final set of partitions.
        var freshExporter = new MessageExporter(freshContext);

        try
        {
            try
            {
                try
                {
                    if (existingChat is not null)
                    {
                        // Legacy incremental: write merged ordered messages
                        foreach (var message in orderedMessages)
                        {
                            try
                            {
                                foreach (var user in message.GetReferencedUsers())
                                    await freshContext.PopulateMemberAsync(user, cancellationToken);

                                if (request.MessageFilter.IsMatch(message))
                                {
                                    await freshExporter.ExportMessageAsync(
                                        message,
                                        cancellationToken
                                    );
                                    if (maxMessageId is null || message.Id > maxMessageId.Value)
                                        maxMessageId = message.Id;
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw; // always propagate cancellation
                            }
                            catch (Exception ex)
                            {
                                // See the DB export path's identical catch for why this defaults
                                // to non-fatal: a transient per-message failure must not crash a
                                // run spanning tens of thousands of channels/threads.
                                throw new DiscordChatExporterException(
                                    $"Failed to export message #{message.Id} "
                                        + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                                        + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                                    ex is DiscordChatExporterException dex && dex.IsFatal,
                                    ex
                                );
                            }
                        }
                    }
                    else
                    {
                        // Fresh export: stream directly from the API to the writer (Fix 3)
                        await foreach (var message in freshMessages)
                        {
                            try
                            {
                                foreach (var user in message.GetReferencedUsers())
                                    await freshContext.PopulateMemberAsync(user, cancellationToken);

                                if (request.MessageFilter.IsMatch(message))
                                {
                                    await freshExporter.ExportMessageAsync(
                                        message,
                                        cancellationToken
                                    );
                                    if (maxMessageId is null || message.Id > maxMessageId.Value)
                                        maxMessageId = message.Id;
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw; // always propagate cancellation
                            }
                            catch (Exception ex)
                            {
                                // See the DB export path's identical catch for why this defaults
                                // to non-fatal: a transient per-message failure must not crash a
                                // run spanning tens of thousands of channels/threads.
                                throw new DiscordChatExporterException(
                                    $"Failed to export message #{message.Id} "
                                        + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                                        + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                                    ex is DiscordChatExporterException dex && dex.IsFatal,
                                    ex
                                );
                            }
                        }
                    }
                }
                catch
                {
                    // Don't allow overwrite: in the legacy-incremental sub-case (existingChat is
                    // not null), the final path already holds previously-good, non-regenerable
                    // data (the channel's prior history) that an interrupted from-scratch
                    // rewrite must never clobber with a smaller partial. Leaving it untouched and
                    // retrying next time is always safe; for the fresh-export sub-case the final
                    // path doesn't exist yet anyway, so the same default is harmless there too.
                    freshExporter.Abandon();
                    throw;
                }
            }
            finally
            {
                await freshExporter.DisposeAsync();
            }
            isFreshExportCompletedSuccessfully = true;

            // Fresh/legacy exports always (re)write every partition from scratch in one pass, so
            // -- unlike the streaming-append path -- there's no byte-level merge or offset
            // adjustment to do: the writer's own checkpoints are already correct for the final
            // file as-is. Best-effort, same as the streaming-append path's index update: never
            // lets an index problem fail the export itself.
            try
            {
                var indexFilePath = MessageIndex.GetIndexFilePath(request.OutputFilePath);
                var index = new MessageIndex();

                foreach (var (partitionIndex, checkpoints) in freshExporter.CheckpointsByPartition)
                {
                    if (checkpoints.Count <= 0)
                        continue;

                    var partitionPath = MessageExporter.GetPartitionFilePath(
                        request.OutputFilePath,
                        partitionIndex
                    );

                    index.Partitions.Add(
                        new PartitionIndexEntry
                        {
                            Index = partitionIndex,
                            MinMessageId = checkpoints[0].MessageId,
                            MaxMessageId =
                                IncrementalJsonAppender.TryGetLastMessageId(partitionPath)
                                ?? checkpoints[^1].MessageId,
                            Checkpoints = checkpoints
                                .Select(c => new CheckpointEntry
                                {
                                    MessageId = c.MessageId,
                                    ByteOffset = c.ByteOffset,
                                })
                                .ToList(),
                        }
                    );
                }

                if (await index.SaveAsync(indexFilePath) is { } saveEx)
                    Console.Error.WriteLine($"Failed to save message index: {saveEx.Message}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Failed to update message index (will rebuild on next patch attempt): {ex.Message}"
                );
            }
        }
        finally
        {
            if (manifest is not null)
            {
                // Calculate max message ID for manifest
                Snowflake? finalMaxMessageId;
                if (isFreshExportCompletedSuccessfully)
                {
                    if (existingChat is not null)
                    {
                        var orderedArr = orderedMessages.ToArray();
                        finalMaxMessageId = orderedArr.Any()
                            ? orderedArr.Max(m => m.Id)
                            : (Snowflake?)null;
                    }
                    else
                    {
                        finalMaxMessageId =
                            maxMessageId ?? (Snowflake?)request.Channel.LastMessageId;
                    }
                }
                else
                {
                    finalMaxMessageId = maxMessageId;
                }

                if (finalMaxMessageId is not null || request.Channel.LastMessageId is null)
                {
                    manifest.UpdateEntry(
                        request.Channel.Id.ToString(),
                        finalMaxMessageId?.ToString(),
                        request.Channel.IsArchived
                    );
                    _ = await manifest.SaveAsync(request.BaseOutputDirPath);
                }
            }
        }
    }

    // Compares the guild/channel/category metadata recorded in an existing incremental export's
    // header against the live metadata on the current request, to decide whether the manifest
    // fast-skip (based on LastMessageId alone) is still safe to take. Icon URLs are only
    // compared when they're guaranteed to be stored as the original remote URL in the header;
    // when assets are downloaded and embedded as local paths (--media without --cache-media),
    // the header's iconUrl can never equal the live remote URL even when nothing has changed,
    // so comparing it there would permanently defeat the skip.
    //
    // Internal (rather than private) so it can be unit-tested directly without needing a live
    // Discord connection.
    internal static bool HeaderMatchesRequest(string existingFilePath, ExportRequest request)
    {
        using var header = IncrementalJsonAppender.ParseHeader(existingFilePath);
        var root = header.RootElement;

        var compareIconUrls = !request.ShouldDownloadAssets || request.ShouldCacheAssetsOnly;

        var guildJson = root.GetProperty("guild");
        if (guildJson.GetProperty("name").GetNonNullString() != request.Guild.Name)
            return false;
        if (
            compareIconUrls
            && guildJson.GetProperty("iconUrl").GetNonWhiteSpaceStringOrNull()
                != request.Guild.IconUrl
        )
            return false;

        var channelJson = root.GetProperty("channel");

        var kind = channelJson
            .GetProperty("type")
            .GetNonNullString()
            .Pipe(s => Enum.Parse<ChannelKind>(s));
        if (kind != request.Channel.Kind)
            return false;

        if (channelJson.GetProperty("name").GetNonNullString() != request.Channel.Name)
            return false;

        if (channelJson.GetPropertyOrNull("topic")?.GetStringOrNull() != request.Channel.Topic)
            return false;

        if (
            compareIconUrls
            && channelJson.GetPropertyOrNull("iconUrl")?.GetNonWhiteSpaceStringOrNull()
                != request.Channel.IconUrl
        )
            return false;

        var categoryId = channelJson
            .GetPropertyOrNull("categoryId")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);
        if (categoryId != request.Channel.Parent?.Id)
            return false;

        if (
            channelJson.GetPropertyOrNull("category")?.GetNonWhiteSpaceStringOrNull()
            != request.Channel.Parent?.Name
        )
            return false;

        var parentCategoryId = channelJson
            .GetPropertyOrNull("parentCategoryId")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);
        if (parentCategoryId != request.Channel.Parent?.Parent?.Id)
            return false;

        if (
            channelJson.GetPropertyOrNull("parentCategory")?.GetNonWhiteSpaceStringOrNull()
            != request.Channel.Parent?.Parent?.Name
        )
            return false;

        var dateRangeJson = root.GetProperty("dateRange");

        var after = dateRangeJson
            .GetPropertyOrNull("after")
            ?.GetDateTimeOffsetOrNull()
            ?.Pipe(Snowflake.FromDate);
        if (after != request.After)
            return false;

        var before = dateRangeJson
            .GetPropertyOrNull("before")
            ?.GetDateTimeOffsetOrNull()
            ?.Pipe(Snowflake.FromDate);
        if (before != request.Before)
            return false;

        return true;
    }
}
