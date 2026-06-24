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

            // 1. Delete regenerable streaming-append scratch files. Everything produced by the
            //    append path lives under the "<output>.new" or "<output>.merged" prefixes, so a
            //    simple prefix match catches .new.tmp, .new.tmp.tmp, ".new [part N].tmp" and
            //    .merged.tmp without having to enumerate partition indices.
            var newPrefix = outputFilePath + ".new";
            var mergedPrefix = outputFilePath + ".merged";
            foreach (var path in Directory.EnumerateFiles(dirPath))
            {
                if (
                    path.StartsWith(newPrefix, StringComparison.Ordinal)
                    || path.StartsWith(mergedPrefix, StringComparison.Ordinal)
                )
                    TryDelete(path);
            }

            // 2. Salvage a fresh-export writer temp. Walk partitions in order: finalized ones are
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
                await manifest.SaveAsync(request.BaseOutputDirPath);
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
        CancellationToken cancellationToken = default
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

            File.Move(tempPath, finalPath, overwrite: false);
            return true;
        }
        catch
        {
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
