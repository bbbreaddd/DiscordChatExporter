using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting;

/// <summary>
/// Recovers progress left behind by a hard crash (process killed, power loss, OOM) during a
/// previous incremental export run, where the normal graceful-shutdown paths (writer disposal,
/// the merge step, the manifest save) never got a chance to run.
///
/// Two kinds of leftovers can exist for a channel after such a crash:
///
/// 1. A fresh-export writer temp (<c>&lt;partition&gt;.tmp</c>) whose final file was never
///    created because the crash happened mid-stream, before the postamble was written and the
///    temp was atomically renamed into place. This holds real, otherwise-lost progress (possibly
///    gigabytes), so it is repaired (truncated to the last complete message + a synthesized
///    postamble) and promoted to the real partition path. The next run then resumes from it.
///
/// 2. Streaming-append scratch files (<c>.new.tmp</c>, <c>.new.tmp.tmp</c>, <c>.merged.tmp</c>,
///    and append overflow partitions). These are always regenerable: the real export file plus
///    the manifest survive a crash here intact, so the next run simply re-fetches from the
///    manifest's resume point. They are deleted so they don't accumulate on disk forever.
///
/// The whole pass is strictly best-effort: any failure is swallowed so it can never break or
/// abort the export that follows it. In the worst case a leftover is left untouched and the
/// channel is re-exported from the last finalized state, exactly as before this feature existed.
/// </summary>
internal static class CrashRecovery
{
    public static async ValueTask RecoverAsync(
        ExportRequest request,
        ExportManifest? manifest = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var outputFilePath = request.OutputFilePath;
            var dirPath = Path.GetDirectoryName(outputFilePath);
            if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath))
                return;

            // The .new.tmp path is what the streaming-append path writes to during a fetch.
            // After a successful fetch, .new.tmp is a complete, valid JSON export file.
            // After a failed merge (e.g., disk full), .new.tmp is intentionally preserved so
            // the next run can complete the merge rather than re-fetching from Discord.
            var appendTempPath = outputFilePath + ".new.tmp";

            // 1. Delete only the non-recoverable scratch files:
            //    - ".new.tmp.tmp": crash mid-write of the first fetch partition (incomplete write
            //      of appendTempPath itself — distinct from appendTempPath which is finalized)
            //    - ".new [part N].tmp.tmp": crash mid-write of an overflow fetch partition
            //    - ".merged.tmp": partial merge output (always regenerable from the real partition
            //      + appendTempPath, so safe to delete)
            //    - ".post.tmp": HTML pagination post-processing temp (regenerable by re-converting)
            //
            // DO NOT delete ".new.tmp" or ".new [part N].tmp" here — those are complete, valid
            // fetch partitions (the message exporter finalized them before the crash or disk-full
            // happened). Step 2 below tries to complete the merge with them.
            var appendTempWritePath = appendTempPath + ".tmp"; // the mid-write temp = ".new.tmp.tmp"
            var mergedPrefix = outputFilePath + ".merged";
            var postPrefix = outputFilePath + ".post";
            var overflowPartPrefix = outputFilePath + ".new [part ";

            foreach (var path in Directory.EnumerateFiles(dirPath))
            {
                if (
                    string.Equals(path, appendTempWritePath, StringComparison.Ordinal)
                    || path.StartsWith(mergedPrefix, StringComparison.Ordinal)
                    || path.StartsWith(postPrefix, StringComparison.Ordinal)
                    // Overflow partition mid-write temps: ".new [part N].tmp.tmp"
                    || (
                        path.StartsWith(overflowPartPrefix, StringComparison.Ordinal)
                        && path.EndsWith(".tmp.tmp", StringComparison.Ordinal)
                    )
                )
                    TryDelete(path);
            }

            // 2. If a complete fetch partition (.new.tmp) was left behind — either by a crash
            //    after the fetch completed but before the merge ran, or by a previous run that
            //    couldn't complete the merge (e.g., disk full) — try to complete the merge now.
            //    If it still fails, leave the file in place so the next run can try again.
            if (File.Exists(appendTempPath))
            {
                var mergeCompleted = await TryCompleteSavedMergeAsync(
                    appendTempPath,
                    outputFilePath,
                    manifest,
                    request.Channel.Id.ToString(),
                    request.BaseOutputDirPath,
                    cancellationToken
                );

                if (mergeCompleted)
                {
                    TryDelete(appendTempPath);
                    // Overflow partitions were already promoted inside TryCompleteSavedMergeAsync.
                    // Delete any that remain (orphaned because the main file was gone, or promotion
                    // succeeded and they've already been moved).
                    for (var i = 1; ; i++)
                    {
                        var overflowPath = MessageExporter.GetPartitionFilePath(appendTempPath, i);
                        if (!File.Exists(overflowPath))
                            break;
                        TryDelete(overflowPath);
                    }
                }
                // On failure, leave .new.tmp and overflow partitions for the next run to retry.
            }

            // 3. Salvage a fresh-export writer temp. Walk partitions in order: finalized ones are
            //    skipped (any stale ".tmp" sitting next to them is deleted), and the first gap is
            //    where a crash would have left the in-progress partition as a bare ".tmp".
            var salvaged = false;
            for (var index = 0; ; index++)
            {
                var partitionPath = MessageExporter.GetPartitionFilePath(outputFilePath, index);
                var partitionTempPath = partitionPath + ".tmp";

                if (File.Exists(partitionPath))
                {
                    if (File.Exists(partitionTempPath))
                        TryDelete(partitionTempPath);
                    continue;
                }

                if (File.Exists(partitionTempPath))
                {
                    if (
                        await TryRepairAndPromoteAsync(
                            partitionTempPath,
                            partitionPath,
                            cancellationToken
                        )
                    )
                        salvaged = true;
                    else
                        TryDelete(partitionTempPath);
                }

                // First missing partition reached — nothing finalized beyond this point.
                break;
            }

            // A salvaged file becomes the new baseline on disk. Drop any manifest entry for the
            // channel so the resume point is recomputed from the file's actual contents rather
            // than trusted from a (possibly stale) manifest value, which would otherwise risk
            // re-fetching already-present messages and duplicating them on append.
            if (salvaged && manifest is not null)
            {
                manifest.RemoveEntry(request.Channel.Id.ToString());
                if (await manifest.SaveAsync(request.BaseOutputDirPath) is { } saveEx)
                    Console.Error.WriteLine(
                        $"Crash recovery: manifest save failed (resume point may be stale): {saveEx.Message}"
                    );
            }
        }
        catch (Exception ex)
        {
            // Never let recovery abort the export it precedes.
            Console.Error.WriteLine($"Crash recovery failed (continuing anyway): {ex}");
        }
    }

    // Closing structure appended to a repaired file, matching the byte format that both the JSON
    // writer's postamble and IncrementalJsonAppender produce, so the result reads back identically
    // (FindInsertPoint / FindMessagesContent rely on the trailing "]" and "messageCount").
    private static byte[] BuildPostamble(long messageCount) =>
        Encoding.UTF8.GetBytes($"\n  ],\n  \"messageCount\": {messageCount}\n}}");

    internal static async ValueTask<bool> TryRepairAndPromoteAsync(
        string tempPath,
        string finalPath,
        CancellationToken cancellationToken = default,
        bool overwrite = false
    )
    {
        try
        {
            long arrayOpenOffset;
            try
            {
                arrayOpenOffset = IncrementalJsonAppender.FindMessagesArrayOpenOffset(tempPath);
            }
            catch
            {
                // Crash happened before the header / messages array was even written — nothing
                // structurally salvageable.
                return false;
            }

            var (truncateOffset, messageCount) = ScanCompleteMessages(tempPath, arrayOpenOffset);

            // No complete message survived (e.g. crash right after the preamble). Not worth
            // promoting an empty file over re-exporting from scratch.
            if (messageCount <= 0)
                return false;

            // Truncate off any half-written trailing message and write a fresh, valid postamble.
            await using (
                var fs = new FileStream(
                    tempPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None
                )
            )
            {
                fs.SetLength(truncateOffset);
                fs.Seek(0, SeekOrigin.End);
                await fs.WriteAsync(BuildPostamble(messageCount), cancellationToken);
                await fs.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, finalPath, overwrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tries to complete a merge that was left stranded (either by a crash after the fetch
    /// finished but before the merge ran, or by a previous merge attempt that failed mid-way
    /// through, e.g. due to disk full). On success the merged output is in place, all overflow
    /// partitions are promoted, and the manifest entry is cleared so the next export re-reads
    /// the resume point from the merged file's actual contents. Returns <see langword="false"/>
    /// on any failure (leaving all files untouched for the next run to retry).
    /// </summary>
    internal static async ValueTask<bool> TryCompleteSavedMergeAsync(
        string appendTempPath,
        string outputFilePath,
        ExportManifest? manifest,
        string channelId,
        string baseOutputDirPath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var existingPartitionPaths = ExistingOutputRelocator.GetExistingPartitionFilePaths(
                outputFilePath
            );

            if (existingPartitionPaths.Length == 0)
            {
                // The output file was deleted between runs (rare). appendTempPath contains the
                // newest fetched messages with no existing baseline to merge into; just promote
                // it directly to outputFilePath and handle any overflow partitions below.
                File.Move(appendTempPath, outputFilePath, overwrite: false);

                for (var i = 1; ; i++)
                {
                    var overflowPath = MessageExporter.GetPartitionFilePath(appendTempPath, i);
                    if (!File.Exists(overflowPath))
                        break;
                    var targetPath = MessageExporter.GetPartitionFilePath(outputFilePath, i);
                    File.Move(overflowPath, targetPath, overwrite: false);
                }
            }
            else
            {
                var mergeTargetPath = existingPartitionPaths[^1];
                var mergedTempPath = outputFilePath + ".merged.tmp";

                // If the process died after the merge below already committed but before this
                // whole completion routine finished (e.g. mid manifest save, or before
                // appendTempPath got deleted), appendTempPath survives untouched and this method
                // gets called again on the next run. The streaming-append merge has no id-based
                // dedup (unlike the legacy whole-file rewrite path's DistinctBy), so blindly
                // re-merging would duplicate every message in appendTempPath. Detect that by
                // checking whether the merge target's last message already matches
                // appendTempPath's last message -- if so, this exact batch is already merged in,
                // and only the overflow-partition promotion / cleanup below still needs to run
                // (those are already idempotent: a promoted file is gone from its old path, so
                // re-running that loop is a no-op).
                var appendLastMessageId = IncrementalJsonAppender.TryGetLastMessageId(
                    appendTempPath
                );
                var alreadyMerged =
                    appendLastMessageId is not null
                    && appendLastMessageId
                        == IncrementalJsonAppender.TryGetLastMessageId(mergeTargetPath);

                if (!alreadyMerged)
                {
                    try
                    {
                        await IncrementalJsonAppender.MergeAsync(
                            mergeTargetPath,
                            appendTempPath,
                            mergedTempPath,
                            cancellationToken
                        );
                        File.Move(mergedTempPath, mergeTargetPath, overwrite: true);
                    }
                    catch
                    {
                        TryDelete(mergedTempPath);
                        throw;
                    }
                }

                // Promote any overflow fetch partitions to real partition files.
                var nextPartitionIndex = existingPartitionPaths.Length;
                for (var i = 1; ; i++)
                {
                    var overflowPath = MessageExporter.GetPartitionFilePath(appendTempPath, i);
                    if (!File.Exists(overflowPath))
                        break;
                    var targetPath = MessageExporter.GetPartitionFilePath(
                        outputFilePath,
                        nextPartitionIndex
                    );
                    File.Move(overflowPath, targetPath, overwrite: false);
                    nextPartitionIndex++;
                }
            }

            // Clear the manifest so the next run recomputes the resume point from the merged
            // file rather than trusting a now-stale cached last-message ID.
            if (manifest is not null)
            {
                manifest.RemoveEntry(channelId);
                if (await manifest.SaveAsync(baseOutputDirPath) is { } saveEx)
                    Console.Error.WriteLine(
                        $"Crash recovery: manifest save failed after merge completion (resume point may be stale): {saveEx.Message}"
                    );
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Crash recovery: saved-merge completion failed (will retry next run): {ex.Message}"
            );
            return false;
        }
    }

    /// <summary>
    /// Streams through the <c>messages</c> array of a possibly-truncated export, returning the
    /// absolute byte offset just past the last complete message object together with the number
    /// of complete messages found. Uses a chunked <see cref="Utf8JsonReader"/> so it never loads
    /// the (potentially huge) file into memory.
    /// </summary>
    internal static (long TruncateOffset, long MessageCount) ScanCompleteMessages(
        string filePath,
        long arrayOpenOffset
    )
    {
        using var stream = File.OpenRead(filePath);
        stream.Seek(arrayOpenOffset, SeekOrigin.Begin); // positioned at '['

        var buffer = new byte[64 * 1024];
        var dataLength = 0;
        var windowBase = arrayOpenOffset; // file offset of buffer[0] for the current reader
        var state = new JsonReaderState();

        var started = false;
        long messageCount = 0;
        // Fallback when zero complete messages exist: truncate right after the '[' (empty array).
        var lastGoodOffset = arrayOpenOffset + 1;

        while (true)
        {
            var read = stream.Read(buffer, dataLength, buffer.Length - dataLength);
            var isFinalBlock = read == 0;
            dataLength += read;

            var reader = new Utf8JsonReader(buffer.AsSpan(0, dataLength), isFinalBlock, state);
            var reachedEnd = false;

            try
            {
                while (reader.Read())
                {
                    if (!started)
                    {
                        // The first token must be the array's opening '['.
                        if (reader.TokenType == JsonTokenType.StartArray)
                            started = true;
                        continue;
                    }

                    // A message object closes at depth 1 (directly inside the messages array);
                    // its own nested objects/arrays close at depth >= 2 and are ignored here.
                    if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 1)
                    {
                        messageCount++;
                        lastGoodOffset = windowBase + reader.BytesConsumed;
                    }
                    else if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 0)
                    {
                        // The array itself closed cleanly — the file was valid up to here. Stop
                        // before reading the trailing "messageCount"/'}' (which, treated as a
                        // root-level array, would look like invalid trailing data).
                        reachedEnd = true;
                        break;
                    }
                }
            }
            catch (JsonException)
            {
                // The trailing message was cut off mid-write. Everything up to lastGoodOffset is
                // still intact, so stop there.
                return (lastGoodOffset, messageCount);
            }

            if (reachedEnd || isFinalBlock)
                return (lastGoodOffset, messageCount);

            // Carry over the bytes the reader couldn't consume yet and refill.
            var consumed = (int)reader.BytesConsumed;
            state = reader.CurrentState;
            windowBase += consumed;
            var leftover = dataLength - consumed;
            if (leftover > 0)
                Buffer.BlockCopy(buffer, consumed, buffer, 0, leftover);
            dataLength = leftover;

            // A single token (e.g. a message with a very large embed) bigger than the whole
            // buffer would otherwise stall with nothing consumed — grow the buffer to fit.
            if (dataLength == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; a leftover temp is harmless beyond the disk space it occupies.
        }
    }
}
