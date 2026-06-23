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
using Gress;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

public class ChannelExporter(DiscordClient discord)
{
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

            try
            {
                try
                {
                    await using (var messageExporter = new MessageExporter(context, appendTempPath))
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
                            catch (Exception ex)
                            {
                                throw new DiscordChatExporterException(
                                    $"Failed to export message #{message.Id} "
                                        + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                                        + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                                    ex is not DiscordChatExporterException dex || dex.IsFatal,
                                    ex
                                );
                            }
                        }
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
                        await manifest.SaveAsync(request.BaseOutputDirPath);
                    }
                }
            }
            finally
            {
                // Always clean up temp files, even on failure
                if (File.Exists(appendTempPath))
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

        try
        {
            await using (var freshExporter = new MessageExporter(freshContext))
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
                                await freshExporter.ExportMessageAsync(message, cancellationToken);
                                if (maxMessageId is null || message.Id > maxMessageId.Value)
                                    maxMessageId = message.Id;
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new DiscordChatExporterException(
                                $"Failed to export message #{message.Id} "
                                    + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                                    + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                                ex is not DiscordChatExporterException dex || dex.IsFatal,
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
                                await freshExporter.ExportMessageAsync(message, cancellationToken);
                                if (maxMessageId is null || message.Id > maxMessageId.Value)
                                    maxMessageId = message.Id;
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new DiscordChatExporterException(
                                $"Failed to export message #{message.Id} "
                                    + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                                    + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                                ex is not DiscordChatExporterException dex || dex.IsFatal,
                                ex
                            );
                        }
                    }
                }
            }
            isFreshExportCompletedSuccessfully = true;
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
                    await manifest.SaveAsync(request.BaseOutputDirPath);
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
