using System;
using System.Globalization;
using System.IO;
using System.Text;
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

        using var stream = File.OpenRead(filePath);

        // The preamble (guild, channel, dateRange, exportedAt) is small, but channel/guild
        // names may be Unicode, so we read generously and work on raw bytes.
        const int HeaderSize = 4096;
        var headerLength = (int)Math.Min(HeaderSize, stream.Length);
        var header = new byte[headerLength];
        stream.ReadExactly(header);

        // Find "messages": in the header bytes
        var msgsIdx = header.AsSpan().IndexOf(MessagesKey);
        if (msgsIdx < 0)
            throw new InvalidDataException(
                $"Could not find 'messages' array property in '{filePath}'."
            );

        // Find the '[' that opens the array (comes immediately after "messages":)
        var afterKey = header.AsSpan(msgsIdx + MessagesKey.Length);
        var openBracketOffset = afterKey.IndexOf((byte)'[');
        if (openBracketOffset < 0)
            throw new InvalidDataException(
                $"Could not find opening '[' of messages array in '{filePath}'."
            );

        var openBracketPos = msgsIdx + MessagesKey.Length + openBracketOffset;
        return (openBracketPos, closeBracketPos, count);
    }

    /// <summary>
    /// Merges <paramref name="newTempFilePath"/> (a freshly-exported JSON file containing
    /// only the new messages) into <paramref name="existingFilePath"/> by streaming both
    /// files at the byte level, writing the merged result to
    /// <paramref name="mergedTempFilePath"/>. No message objects are loaded into memory.
    /// </summary>
    public static async ValueTask MergeAsync(
        string existingFilePath,
        string newTempFilePath,
        string mergedTempFilePath,
        CancellationToken cancellationToken = default
    )
    {
        var (insertPos, existingCount) = FindInsertPoint(existingFilePath);
        var (newMsgOpenPos, newMsgClosePos, newCount) = FindMessagesContent(newTempFilePath);

        await using var output = File.Create(mergedTempFilePath);

        // Step 1: stream the existing file up to (not including) the closing ']'
        {
            await using var existing = File.OpenRead(existingFilePath);
            await CopyBytesAsync(existing, output, insertPos, cancellationToken);
        }

        // Step 2: if both sides have messages, write a comma separator
        if (existingCount > 0 && newCount > 0)
            await output.WriteAsync(","u8.ToArray(), cancellationToken);

        // Step 3: stream the new messages array content (between '[' and ']', exclusive)
        if (newCount > 0)
        {
            await using var newFile = File.OpenRead(newTempFilePath);
            newFile.Seek(newMsgOpenPos + 1, SeekOrigin.Begin); // skip the '['
            var contentLength = newMsgClosePos - newMsgOpenPos - 1;
            await CopyBytesAsync(newFile, output, contentLength, cancellationToken);
        }

        // Step 4: write the closing structure with the updated messageCount
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
