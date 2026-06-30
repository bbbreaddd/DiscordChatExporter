using System;
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
    private static (byte[] Header, int MessagesKeyIndex) ReadHeaderBytes(string filePath)
    {
        const int InitialHeaderSize = 4096;
        const int MaxHeaderSize = 1024 * 1024; // 1 MB — handles extreme guild/channel/topic lengths

        using var stream = File.OpenRead(filePath);

        var headerSize = InitialHeaderSize;
        while (true)
        {
            var headerLength = (int)Math.Min(headerSize, stream.Length);
            var header = new byte[headerLength];
            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(header);

            var msgsIdx = header.AsSpan().IndexOf(MessagesKey);
            if (msgsIdx >= 0)
                return (header, msgsIdx);

            if (headerLength >= stream.Length || headerSize >= MaxHeaderSize)
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
        var (header, msgsIdx) = ReadHeaderBytes(filePath);

        // Find the '[' that opens the array (comes immediately after "messages":)
        var afterKey = header.AsSpan(msgsIdx + MessagesKey.Length);
        var openBracketOffset = afterKey.IndexOf((byte)'[');
        if (openBracketOffset < 0)
            throw new InvalidDataException(
                $"Could not find opening '[' of messages array in '{filePath}'."
            );

        return msgsIdx + MessagesKey.Length + openBracketOffset;
    }

    /// <summary>
    /// Parses just the header portion of an export file (guild, channel, dateRange,
    /// exportedAt) into a standalone <see cref="JsonDocument" />, without touching the
    /// (potentially huge) messages array. Used to check whether the metadata recorded in an
    /// existing incremental export is still up to date, without loading the whole file.
    /// </summary>
    public static JsonDocument ParseHeader(string filePath)
    {
        var (header, msgsIdx) = ReadHeaderBytes(filePath);

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

    private static async ValueTask CopyBytesAsync(
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
}
