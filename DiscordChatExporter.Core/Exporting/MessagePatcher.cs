using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;

namespace DiscordChatExporter.Core.Exporting;

public record MessagePatchResult(bool Patched, string Reason);

/// <summary>
/// Refreshes a single already-exported message's stored JSON (e.g. to reflect a new reaction) in
/// place, without re-exporting the whole channel. Uses <see cref="MessageIndex"/> to jump close
/// to the message instead of scanning the partition from the start.
/// </summary>
public static class MessagePatcher
{
    public static async ValueTask<MessagePatchResult> PatchMessageAsync(
        ExportRequest request,
        DiscordClient discord,
        Snowflake messageId,
        CancellationToken cancellationToken = default
    )
    {
        var baseFilePath = request.OutputFilePath;

        var index = await MessageIndex.LoadOrBuildAsync(baseFilePath);
        var partitionEntry = index.FindPartitionForMessage(messageId);
        if (partitionEntry is null)
            return new MessagePatchResult(
                false,
                "Message id is not covered by any known partition (never exported, or the index is stale)."
            );

        var partitionPath = MessageExporter.GetPartitionFilePath(
            baseFilePath,
            partitionEntry.Index
        );
        if (!File.Exists(partitionPath))
            return new MessagePatchResult(
                false,
                $"Partition file '{partitionPath}' does not exist."
            );

        var checkpointOffset = MessageIndex.FindNearestCheckpointBefore(partitionEntry, messageId);

        var range = FindMessageByteRange(partitionPath, checkpointOffset, messageId);
        if (range is null)
            return new MessagePatchResult(
                false,
                "Message not found in the expected partition (the index may be stale)."
            );

        var (start, end) = range.Value;

        var message = await discord.TryGetMessageAsync(
            request.Channel.Id,
            messageId,
            cancellationToken
        );
        if (message is null)
            return new MessagePatchResult(false, "Message no longer exists (deleted).");

        var newMessageBytes = await RenderMessageAsync(
            request,
            discord,
            message,
            cancellationToken
        );

        await SplicePartitionAsync(partitionPath, start, end, newMessageBytes, cancellationToken);

        // Shift every checkpoint after the patch point by however much the file grew/shrank, so
        // the index stays valid for future patches without needing a rebuild.
        var lengthDelta = newMessageBytes.Length - (end - start);
        if (lengthDelta != 0)
        {
            foreach (var checkpoint in partitionEntry.Checkpoints)
            {
                if (checkpoint.ByteOffset > start)
                    checkpoint.ByteOffset += lengthDelta;
            }

            _ = await index.SaveAsync(MessageIndex.GetIndexFilePath(baseFilePath));
        }

        return new MessagePatchResult(true, "Patched successfully.");
    }

    // Renders just one message's own JSON object (no enclosing array, no header/postamble) by
    // reusing JsonMessageWriter.WriteMessageAsync directly -- this is what already knows how to
    // fetch full reaction-author lists live (JsonMessageWriter.cs, via Context.Discord), so a
    // patched message's reactions come out identical in shape to a normal full export's, with no
    // separate reaction-fetching logic needed here.
    private static async ValueTask<byte[]> RenderMessageAsync(
        ExportRequest request,
        DiscordClient discord,
        Message message,
        CancellationToken cancellationToken
    )
    {
        var context = new ExportContext(discord, request);
        await context.PopulateChannelsAndRolesAsync(cancellationToken);

        foreach (var user in message.GetReferencedUsers())
            await context.PopulateMemberAsync(user, cancellationToken);

        using var memoryStream = new MemoryStream();

        // JsonMessageWriter.DisposeAsync (needed to flush the underlying Utf8JsonWriter's
        // buffered bytes) also disposes the stream it was given -- wrap it so the MemoryStream
        // survives disposal and can still be read from afterward.
        var writer = new JsonMessageWriter(new NonDisposingStream(memoryStream), context);
        await writer.WriteMessageAsync(message, cancellationToken);
        await writer.DisposeAsync();

        return ReindentAsNestedMessage(memoryStream.ToArray());
    }

    // JsonMessageWriter renders this message as if it were a standalone top-level value, so its
    // own properties come out at 2-space indent -- but spliced into the real file, the message
    // object actually sits 2 levels deep (root -> messages array -> this object), where its
    // properties should be at 6-space indent to visually match every sibling message around it.
    // Confirmed via a real diff against production data: without this, the JSON stays perfectly
    // valid (whitespace doesn't affect parsing) but the patched message's formatting visibly
    // doesn't match its neighbors. The object's own opening '{' needs no adjustment -- it's
    // spliced in right after the file's pre-existing indentation for that position -- so every
    // line except the first gets the two missing levels' worth of extra indent prepended.
    internal static byte[] ReindentAsNestedMessage(byte[] rendered)
    {
        const string extraIndent = "    "; // 2 levels x 2-space indent

        var text = System.Text.Encoding.UTF8.GetString(rendered);
        var lines = text.Split('\n');

        for (var i = 1; i < lines.Length; i++)
            lines[i] = extraIndent + lines[i];

        return System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    // Rewrites a partition file so that the bytes in [start, end) are replaced with newBytes,
    // leaving everything else untouched. Internal (rather than private) so tests can exercise the
    // splice itself directly, without needing a live Discord connection to get here.
    internal static async ValueTask SplicePartitionAsync(
        string partitionPath,
        long start,
        long end,
        byte[] newBytes,
        CancellationToken cancellationToken = default
    )
    {
        var tempPath = partitionPath + ".patch.tmp";
        try
        {
            await using (var input = File.OpenRead(partitionPath))
            await using (var output = File.Create(tempPath))
            {
                await IncrementalJsonAppender.CopyBytesAsync(
                    input,
                    output,
                    start,
                    cancellationToken
                );
                await output.WriteAsync(newBytes, cancellationToken);
                input.Seek(end, SeekOrigin.Begin);
                await input.CopyToAsync(output, cancellationToken);
            }

            File.Move(tempPath, partitionPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException) { }

            throw;
        }
    }

    // Finds the exact [start, end) byte range of a message's own JSON object within a partition
    // file, seeking as close as the index allows first. When checkpointOffset is 0 (no usable
    // checkpoint), seeks to the true array start and scans normally. Otherwise, seeks directly to
    // a message's '{' mid-array and prepends a synthetic '[' to the read buffer -- confirmed via
    // testing that Utf8JsonReader otherwise treats that object as a standalone top-level value
    // (wrong depths, and throws on the ',' that follows it) rather than as an array element.
    internal static (long Start, long End)? FindMessageByteRange(
        string filePath,
        long checkpointOffset,
        Snowflake messageId
    )
    {
        var targetId = messageId.ToString();

        using var stream = File.OpenRead(filePath);

        long windowBase;
        var prependSyntheticArrayStart = checkpointOffset > 0;

        if (prependSyntheticArrayStart)
        {
            stream.Seek(checkpointOffset, SeekOrigin.Begin);
            windowBase = checkpointOffset - 1; // the synthetic '[' occupies this position
        }
        else
        {
            var arrayOpenOffset = IncrementalJsonAppender.FindMessagesArrayOpenOffset(filePath);
            stream.Seek(arrayOpenOffset, SeekOrigin.Begin);
            windowBase = arrayOpenOffset;
        }

        var buffer = new byte[64 * 1024];
        var dataLength = 0;

        if (prependSyntheticArrayStart)
        {
            buffer[0] = (byte)'[';
            dataLength = 1;
        }

        var state = new JsonReaderState();
        var started = !prependSyntheticArrayStart ? false : true;
        long currentObjectStart = 0;
        var matchedCurrentObject = false;

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
                        matchedCurrentObject = false;
                    }
                    else if (
                        reader.TokenType == JsonTokenType.PropertyName
                        && reader.CurrentDepth == 2
                        && reader.ValueTextEquals("id"u8)
                    )
                    {
                        reader.Read();
                        if (reader.GetString() == targetId)
                            matchedCurrentObject = true;
                    }
                    else if (
                        reader.TokenType == JsonTokenType.EndObject
                        && reader.CurrentDepth == 1
                    )
                    {
                        if (matchedCurrentObject)
                            return (currentObjectStart, windowBase + reader.BytesConsumed);
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
                // Truncated file (e.g. an unrepaired crash leftover) -- the message can't be
                // found reliably; give up rather than risk splicing into a malformed file.
                return null;
            }

            if (reachedEnd || isFinalBlock)
                return null;

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
    }

    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override System.Threading.Tasks.Task FlushAsync(
            CancellationToken cancellationToken
        ) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => inner.WriteAsync(buffer, cancellationToken);

        // The whole point: absorb disposal so the caller's stream survives it.
        protected override void Dispose(bool disposing) { }

        public override ValueTask DisposeAsync() => default;
    }
}
