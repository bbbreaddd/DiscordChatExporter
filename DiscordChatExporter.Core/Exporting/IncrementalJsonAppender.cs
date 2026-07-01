using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting;

/// <summary>
/// Merges a new-messages JSON export file into an existing one without loading either
/// file's messages into memory. Operates at the byte level using known structural
/// invariants of the <see cref="JsonMessageWriter"/> output format.
/// </summary>
internal static class IncrementalJsonAppender
{
    private static ReadOnlySpan<byte> MessageCountKey => "\"messageCount\""u8;
    private static ReadOnlySpan<byte> MessagesKey => "\"messages\":"u8;

    /// <summary>
    /// Finds the byte offset of the closing <c>]</c> that ends the <c>messages</c> array
    /// in an existing export file, and returns the current <c>messageCount</c> value.
    /// Only reads the last ~512 bytes of the file.
    /// </summary>
    public static (long InsertPosition, long ExistingMessageCount) FindInsertPoint(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var fileLength = stream.Length;

        // Read only the tail — enough to contain the closing ']', messageCount, and '}'
        const int TailSize = 512;
        var tailStart = Math.Max(0, fileLength - TailSize);
        var tailLength = (int)(fileLength - tailStart);

        stream.Seek(tailStart, SeekOrigin.Begin);
        var tail = new byte[tailLength];
        stream.ReadExactly(tail);

        // Locate the "messageCount" property key in the tail
        var mcIdx = tail.AsSpan().LastIndexOf(MessageCountKey);
        if (mcIdx < 0)
            throw new InvalidDataException(
                $"Could not find 'messageCount' property in '{filePath}'. "
                    + "The file may be corrupt or in an unexpected format."
            );

        // Parse the numeric value that follows the ':'
        var afterKey = tail.AsSpan(mcIdx + MessageCountKey.Length);
        var colonOffset = afterKey.IndexOf((byte)':');
        if (colonOffset < 0)
            throw new InvalidDataException($"Malformed 'messageCount' property in '{filePath}'.");

        var numSpan = afterKey[(colonOffset + 1)..];
        // Trim leading ASCII whitespace
        while (
            numSpan.Length > 0 && numSpan[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'
        )
            numSpan = numSpan[1..];

        var numLen = 0;
        while (
            numLen < numSpan.Length && numSpan[numLen] >= (byte)'0' && numSpan[numLen] <= (byte)'9'
        )
            numLen++;

        if (numLen == 0)
            throw new InvalidDataException(
                $"Could not parse 'messageCount' value in '{filePath}'."
            );

        var existingCount = long.Parse(
            Encoding.ASCII.GetString(numSpan[..numLen]),
            CultureInfo.InvariantCulture
        );

        // The closing ']' of the messages array is the last ']' before "messageCount"
        var bracketIdx = tail.AsSpan(0, mcIdx).LastIndexOf((byte)']');
        if (bracketIdx < 0)
            throw new InvalidDataException(
                $"Could not find the messages array closing bracket in '{filePath}'. "
                    + "The file may be corrupt."
            );

        return (tailStart + bracketIdx, existingCount);
    }

    /// <summary>
    /// Reads enough of the start of an export file to contain the entire header (guild,
    /// channel, dateRange, exportedAt) and locates the <c>"messages":</c> key within it.
    /// Grows the read buffer until the key is found, since channel/guild names and topics may
    /// be long and/or multi-byte Unicode. Never reads past the header into the (potentially
    /// huge) messages array itself.
    /// </summary>
    private static (byte[] Header, int MessagesKeyIndex, int BomLength) ReadHeaderBytes(
        string filePath
    )
    {
        const int InitialHeaderSize = 4096;
        const int MaxHeaderSize = 1024 * 1024; // 1 MB — handles extreme guild/channel/topic lengths

        using var stream = File.OpenRead(filePath);

        // Skip a leading UTF-8 BOM if present. DiscordChatExporter never writes one, but a file
        // re-saved once by a BOM-emitting external tool would otherwise make every offset below
        // wrong and JsonDocument.Parse throw on every subsequent incremental run for that file.
        var bomLength = 0;
        if (stream.Length >= 3)
        {
            Span<byte> bomBuffer = stackalloc byte[3];
            stream.ReadExactly(bomBuffer);
            if (bomBuffer[0] == 0xEF && bomBuffer[1] == 0xBB && bomBuffer[2] == 0xBF)
                bomLength = 3;
            else
                stream.Seek(0, SeekOrigin.Begin);
        }

        var headerSize = InitialHeaderSize;
        while (true)
        {
            var headerLength = (int)Math.Min(headerSize, stream.Length - bomLength);
            var header = new byte[headerLength];
            stream.Seek(bomLength, SeekOrigin.Begin);
            stream.ReadExactly(header);

            var msgsIdx = header.AsSpan().IndexOf(MessagesKey);
            if (msgsIdx >= 0)
                return (header, msgsIdx, bomLength);

            if (headerLength >= stream.Length - bomLength || headerSize >= MaxHeaderSize)
                throw new InvalidDataException(
                    $"Could not find 'messages' array property in '{filePath}'. "
                        + "The file may be corrupt or in an unexpected format."
                );

            headerSize *= 2;
        }
    }

    /// <summary>
    /// Finds the byte range of the content inside the <c>messages</c> array of a
    /// newly-written export file (the bytes between <c>[</c> and <c>]</c>, exclusive),
    /// and returns the <c>messageCount</c>.
    /// </summary>
    public static (
        long OpenBracketPos,
        long CloseBracketPos,
        long MessageCount
    ) FindMessagesContent(string filePath)
    {
        var (closeBracketPos, count) = FindInsertPoint(filePath);
        var openBracketPos = FindMessagesArrayOpenOffset(filePath);
        return (openBracketPos, closeBracketPos, count);
    }

    /// <summary>
    /// Returns the byte offset of the <c>[</c> that opens the <c>messages</c> array. Unlike
    /// <see cref="FindMessagesContent"/>, this only reads the header and does not require the
    /// file to have a valid trailing structure (closing <c>]</c> / <c>messageCount</c>), so it
    /// can be used on an export that was truncated by a crash mid-way through the messages array.
    /// </summary>
    public static long FindMessagesArrayOpenOffset(string filePath)
    {
        var (header, msgsIdx, bomLength) = ReadHeaderBytes(filePath);

        // Find the '[' that opens the array (comes immediately after "messages":)
        var afterKey = header.AsSpan(msgsIdx + MessagesKey.Length);
        var openBracketOffset = afterKey.IndexOf((byte)'[');
        if (openBracketOffset < 0)
            throw new InvalidDataException(
                $"Could not find opening '[' of messages array in '{filePath}'."
            );

        return bomLength + msgsIdx + MessagesKey.Length + openBracketOffset;
    }

    /// <summary>
    /// Parses just the header portion of an export file (guild, channel, dateRange,
    /// exportedAt) into a standalone <see cref="JsonDocument" />, without touching the
    /// (potentially huge) messages array. Used to check whether the metadata recorded in an
    /// existing incremental export is still up to date, without loading the whole file.
    /// </summary>
    public static JsonDocument ParseHeader(string filePath)
    {
        var (header, msgsIdx, _) = ReadHeaderBytes(filePath);

        // Trim trailing whitespace and the comma that separated the header from the
        // "messages" property, then close the root object, turning the header prefix into a
        // small standalone JSON document.
        var prefixLength = msgsIdx;
        while (prefixLength > 0 && IsJsonWhiteSpace(header[prefixLength - 1]))
            prefixLength--;
        if (prefixLength > 0 && header[prefixLength - 1] == (byte)',')
            prefixLength--;

        var json = new byte[prefixLength + 1];
        Array.Copy(header, json, prefixLength);
        json[prefixLength] = (byte)'}';

        return JsonDocument.Parse(json);
    }

    private static bool IsJsonWhiteSpace(byte b) =>
        b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    /// <summary>
    /// Merges <paramref name="newTempFilePath"/> (a freshly-exported JSON file containing the
    /// new messages, written with up-to-date guild/channel/category metadata) into
    /// <paramref name="existingFilePath"/> by streaming both files at the byte level, writing
    /// the merged result to <paramref name="mergedTempFilePath"/>. No message objects are
    /// loaded into memory.
    /// </summary>
    /// <remarks>
    /// The merged file's header (guild, channel, dateRange, exportedAt) always comes from
    /// <paramref name="newTempFilePath"/> rather than <paramref name="existingFilePath"/>, so a
    /// rename (or any other metadata change) is picked up on every incremental run, even one
    /// that finds zero new messages.
    /// </remarks>
    public static async ValueTask MergeAsync(
        string existingFilePath,
        string newTempFilePath,
        string mergedTempFilePath,
        CancellationToken cancellationToken = default
    )
    {
        var (existingMsgOpenPos, existingMsgClosePos, existingCount) = FindMessagesContent(
            existingFilePath
        );
        var (newMsgOpenPos, newMsgClosePos, newCount) = FindMessagesContent(newTempFilePath);

        await using var output = File.Create(mergedTempFilePath);

        // Step 1: stream the fresh temp file's header up to and including the opening '[' of
        // the messages array. This is what refreshes guild/channel/category metadata and
        // exportedAt on every run, regardless of whether any new messages were found.
        {
            await using var newFile = File.OpenRead(newTempFilePath);
            await CopyBytesAsync(newFile, output, newMsgOpenPos + 1, cancellationToken);
        }

        // Step 2: stream the existing file's messages array content (between '[' and ']',
        // exclusive), carrying over all previously-exported messages
        if (existingCount > 0)
        {
            await using var existing = File.OpenRead(existingFilePath);
            existing.Seek(existingMsgOpenPos + 1, SeekOrigin.Begin); // skip the '['
            var contentLength = existingMsgClosePos - existingMsgOpenPos - 1;
            await CopyBytesAsync(existing, output, contentLength, cancellationToken);
        }

        // Step 3: if both sides have messages, write a comma separator
        if (existingCount > 0 && newCount > 0)
            await output.WriteAsync(","u8.ToArray(), cancellationToken);

        // Step 4: stream the new messages array content (between '[' and ']', exclusive)
        if (newCount > 0)
        {
            await using var newFile = File.OpenRead(newTempFilePath);
            newFile.Seek(newMsgOpenPos + 1, SeekOrigin.Begin); // skip the '['
            var contentLength = newMsgClosePos - newMsgOpenPos - 1;
            await CopyBytesAsync(newFile, output, contentLength, cancellationToken);
        }

        // Step 5: write the closing structure with the updated messageCount
        var totalCount = existingCount + newCount;
        var closing = Encoding.UTF8.GetBytes($"\n  ],\n  \"messageCount\": {totalCount}\n}}");
        await output.WriteAsync(closing, cancellationToken);
    }

    // Internal (rather than private) so MessagePatcher can reuse it for its own byte-range
    // splicing, instead of duplicating the same chunked-copy loop.
    internal static async ValueTask CopyBytesAsync(
        Stream source,
        Stream destination,
        long length,
        CancellationToken cancellationToken = default
    )
    {
        var buffer = new byte[81920]; // 80 KiB chunks
        var remaining = length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    /// <summary>
    /// Returns the "id" of the last message in the <c>messages</c> array (the one nearest the
    /// closing <c>]</c>), or <see langword="null"/> if the array is empty or the file can't be
    /// read. Used to check whether a saved-merge replay would duplicate messages that are
    /// already present in the target, without loading the (potentially huge) file into memory.
    /// </summary>
    public static string? TryGetLastMessageId(string filePath)
    {
        try
        {
            return ScanLastMessageId(filePath, FindMessagesArrayOpenOffset(filePath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a <see cref="PartitionIndexEntry"/> for a partition file that doesn't have a
    /// persisted index yet (e.g. one written before this feature existed, or one whose index
    /// couldn't be incrementally maintained -- see <see cref="BuildMergedCheckpoints"/>), by
    /// scanning it once from the start. O(file size), but only ever needs to run once per
    /// partition: the result is meant to be persisted so every future lookup is fast.
    /// </summary>
    /// <remarks>
    /// Must sample checkpoints at the same interval <see cref="JsonMessageWriter"/> does (message
    /// #1, #501, #1001, ...) so that an index built this way for old content and one maintained
    /// incrementally for new content line up the same way if a channel ever mixes the two.
    /// </remarks>
    public static PartitionIndexEntry? TryBuildIndexForPartition(
        string partitionFilePath,
        int partitionIndex
    )
    {
        try
        {
            return ScanIndexForPartition(
                partitionFilePath,
                FindMessagesArrayOpenOffset(partitionFilePath),
                partitionIndex
            );
        }
        catch
        {
            return null;
        }
    }

    private const int IndexCheckpointInterval = 500;

    private static PartitionIndexEntry? ScanIndexForPartition(
        string filePath,
        long arrayOpenOffset,
        int partitionIndex
    )
    {
        using var stream = File.OpenRead(filePath);
        stream.Seek(arrayOpenOffset, SeekOrigin.Begin); // positioned at '['

        var buffer = new byte[64 * 1024];
        var dataLength = 0;
        var windowBase = arrayOpenOffset;
        var state = new JsonReaderState();

        var started = false;
        var capturingId = false;
        var currentObjectStart = 0L;
        var messageCount = 0L;
        string? firstMessageId = null;
        string? lastMessageId = null;
        var checkpoints = new List<CheckpointEntry>();

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
                        if (reader.TokenType == JsonTokenType.StartArray)
                            started = true;
                        continue;
                    }

                    if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 1)
                    {
                        currentObjectStart = windowBase + reader.TokenStartIndex;
                    }
                    else if (
                        reader.TokenType == JsonTokenType.PropertyName
                        && reader.CurrentDepth == 2
                        && reader.ValueTextEquals("id"u8)
                    )
                    {
                        capturingId = true;
                    }
                    else if (capturingId && reader.TokenType == JsonTokenType.String)
                    {
                        capturingId = false;
                        var messageId = reader.GetString()!;
                        messageCount++;
                        firstMessageId ??= messageId;
                        lastMessageId = messageId;

                        if (messageCount % IndexCheckpointInterval == 1)
                            checkpoints.Add(
                                new CheckpointEntry
                                {
                                    MessageId = messageId,
                                    ByteOffset = currentObjectStart,
                                }
                            );
                    }
                    else if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 0)
                    {
                        reachedEnd = true;
                        break;
                    }
                }
            }
            catch (JsonException)
            {
                // Truncated file (e.g. a leftover from a crash that hasn't been repaired yet) --
                // stop with whatever was fully read so far rather than fail the whole build.
                break;
            }

            if (reachedEnd || isFinalBlock)
                break;

            var consumed = (int)reader.BytesConsumed;
            state = reader.CurrentState;
            windowBase += consumed;
            var leftover = dataLength - consumed;
            if (leftover > 0)
                Buffer.BlockCopy(buffer, consumed, buffer, 0, leftover);
            dataLength = leftover;

            if (dataLength == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);
        }

        if (firstMessageId is null || lastMessageId is null)
            return null;

        return new PartitionIndexEntry
        {
            Index = partitionIndex,
            MinMessageId = firstMessageId,
            MaxMessageId = lastMessageId,
            Checkpoints = checkpoints,
        };
    }

    private static string? ScanLastMessageId(string filePath, long arrayOpenOffset)
    {
        using var stream = File.OpenRead(filePath);
        stream.Seek(arrayOpenOffset, SeekOrigin.Begin); // positioned at '['

        var buffer = new byte[64 * 1024];
        var dataLength = 0;
        var state = new JsonReaderState();

        var started = false;
        string? lastId = null;
        // Set right after seeing a depth-2 "id" property name (the message object's own id,
        // written first -- see JsonMessageWriter.WriteMessageAsync), cleared once its string
        // value is captured. Nested ids (author.id, mentions[].id, etc.) sit one level deeper
        // and are never seen at this depth.
        var capturingId = false;

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
                        if (reader.TokenType == JsonTokenType.StartArray)
                            started = true;
                        continue;
                    }

                    if (
                        reader.TokenType == JsonTokenType.PropertyName
                        && reader.CurrentDepth == 2
                        && reader.ValueTextEquals("id"u8)
                    )
                    {
                        capturingId = true;
                    }
                    else if (capturingId)
                    {
                        if (reader.TokenType == JsonTokenType.String)
                            lastId = reader.GetString();
                        capturingId = false;
                    }

                    if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 0)
                    {
                        reachedEnd = true;
                        break;
                    }
                }
            }
            catch (JsonException)
            {
                // A truncated trailing message never finished being read, so it can't have
                // overwritten 'lastId' with a half-written value -- whatever was captured from
                // the last fully-read message is still correct.
                return lastId;
            }

            if (reachedEnd || isFinalBlock)
                return lastId;

            var consumed = (int)reader.BytesConsumed;
            state = reader.CurrentState;
            var leftover = dataLength - consumed;
            if (leftover > 0)
                Buffer.BlockCopy(buffer, consumed, buffer, 0, leftover);
            dataLength = leftover;

            if (dataLength == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);
        }
    }

    /// <summary>
    /// Computes the merged, offset-adjusted set of index checkpoints for a partition after a
    /// <see cref="MergeAsync"/> call, given the persisted checkpoints for the existing content
    /// (if any) and the raw checkpoints captured while writing <paramref name="newTempFilePath"/>
    /// (relative to that file's own start, from <c>JsonMessageWriter.Checkpoints</c>). Mirrors
    /// the exact same byte-position arithmetic <see cref="MergeAsync"/> uses to combine the two
    /// files, so a checkpoint recorded during either half lands at the correct offset in the
    /// resulting merged file. Does not read or write message content -- call this alongside (not
    /// instead of) <see cref="MergeAsync"/>.
    /// </summary>
    /// <returns>
    /// The merged index entry, or <see langword="null"/> if the existing content has no
    /// persisted index to build on (the caller should leave the index alone in that case; a
    /// lazy full rebuild -- triggered on the next patch attempt -- will cover the whole
    /// partition, old and new content alike, rather than risk an index with an inaccurate
    /// minimum message id for content it never saw).
    /// </returns>
    public static PartitionIndexEntry? BuildMergedCheckpoints(
        string existingFilePath,
        string newTempFilePath,
        PartitionIndexEntry? existingIndexEntry,
        int partitionIndex,
        IReadOnlyList<(string MessageId, long ByteOffset)> newCheckpoints,
        string? newMaxMessageId
    )
    {
        var (existingMsgOpenPos, existingMsgClosePos, existingCount) = FindMessagesContent(
            existingFilePath
        );

        if (existingCount > 0 && existingIndexEntry is null)
            return null;

        var (newMsgOpenPos, _, newCount) = FindMessagesContent(newTempFilePath);
        if (newCount <= 0)
            return existingIndexEntry;

        var existingContentStartInMerged = newMsgOpenPos + 1;
        var existingContentLength =
            existingCount > 0 ? existingMsgClosePos - existingMsgOpenPos - 1 : 0;
        var newContentStartInMerged =
            existingContentStartInMerged + existingContentLength + (existingCount > 0 ? 1 : 0);
        var deltaNew = newContentStartInMerged - (newMsgOpenPos + 1);

        var mergedCheckpoints = new List<CheckpointEntry>();

        if (existingIndexEntry is not null)
        {
            var deltaExisting = existingContentStartInMerged - (existingMsgOpenPos + 1);
            foreach (var checkpoint in existingIndexEntry.Checkpoints)
            {
                mergedCheckpoints.Add(
                    new CheckpointEntry
                    {
                        MessageId = checkpoint.MessageId,
                        ByteOffset = checkpoint.ByteOffset + deltaExisting,
                    }
                );
            }
        }

        foreach (var (messageId, byteOffset) in newCheckpoints)
        {
            mergedCheckpoints.Add(
                new CheckpointEntry { MessageId = messageId, ByteOffset = byteOffset + deltaNew }
            );
        }

        var minMessageId = existingIndexEntry?.MinMessageId;
        if (string.IsNullOrEmpty(minMessageId))
            minMessageId = newCheckpoints.Count > 0 ? newCheckpoints[0].MessageId : "";

        var maxMessageId = newMaxMessageId ?? existingIndexEntry?.MaxMessageId ?? "";

        return new PartitionIndexEntry
        {
            Index = partitionIndex,
            MinMessageId = minMessageId,
            MaxMessageId = maxMessageId,
            Checkpoints = mergedCheckpoints,
        };
    }
}
