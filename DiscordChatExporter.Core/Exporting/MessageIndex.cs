using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting;

/// <summary>
/// Sparse per-channel index that maps message IDs to roughly where they live on disk, so a
/// single already-exported message can be found and patched (e.g. to reflect a new reaction)
/// without scanning the whole channel from the start.
/// </summary>
/// <remarks>
/// Deliberately not encoded into partition filenames: <see cref="ExistingOutputRelocator"/>,
/// <see cref="CrashRecovery"/>, and the convert command's partition-grouping logic all
/// derive/discover partition paths purely from index number, and changing that would be an
/// invasive, backward-compatibility-breaking change across all of them. This sidecar file can be
/// silently absent (older archives, or ones from before this feature) or rebuilt from scratch at
/// any time with no correctness impact -- it is purely a lookup optimization, never a source of
/// truth for what messages exist.
/// </remarks>
public class MessageIndex
{
    [JsonPropertyName("partitions")]
    public List<PartitionIndexEntry> Partitions { get; set; } = [];

    public static string GetIndexFilePath(string baseFilePath) => baseFilePath + ".index.json";

    /// <summary>
    /// Loads the channel's persisted index and fills in any partition it doesn't yet cover (an
    /// archive from before this feature existed, or one whose index couldn't be incrementally
    /// maintained -- see <see cref="IncrementalJsonAppender.BuildMergedCheckpoints"/>) by
    /// scanning those specific partitions once, then persists the completed index so this is a
    /// one-time cost per partition rather than a recurring one.
    /// </summary>
    public static async ValueTask<MessageIndex> LoadOrBuildAsync(string baseFilePath)
    {
        var indexFilePath = GetIndexFilePath(baseFilePath);
        var index = await LoadAsync(indexFilePath);

        var partitionPaths = ExistingOutputRelocator.GetExistingPartitionFilePaths(baseFilePath);
        var changed = false;

        for (var i = 0; i < partitionPaths.Length; i++)
        {
            if (index.Partitions.Exists(p => p.Index == i))
                continue;

            var built = IncrementalJsonAppender.TryBuildIndexForPartition(partitionPaths[i], i);
            if (built is null)
                continue;

            index.Partitions.Add(built);
            changed = true;
        }

        if (changed)
            _ = await index.SaveAsync(indexFilePath);

        return index;
    }

    public static async ValueTask<MessageIndex> LoadAsync(string indexFilePath)
    {
        if (!File.Exists(indexFilePath))
            return new MessageIndex();

        try
        {
            await using var stream = File.OpenRead(indexFilePath);
            var index = await JsonSerializer.DeserializeAsync(
                stream,
                MessageIndexJsonContext.Default.MessageIndex
            );
            return index ?? new MessageIndex();
        }
        catch (Exception ex)
        {
            // The index is purely an optimization -- a corrupt or unreadable one should never
            // block a patch attempt; just proceed as if there were no index (callers fall back
            // to a full rebuild).
            Console.Error.WriteLine($"Failed to load message index (will rebuild): {ex.Message}");
            return new MessageIndex();
        }
    }

    // Returns null on success, or the exception on failure. Never throws.
    public async ValueTask<Exception?> SaveAsync(string indexFilePath)
    {
        var tempFilePath = indexFilePath + ".tmp";

        try
        {
            await using (var stream = File.Create(tempFilePath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    this,
                    MessageIndexJsonContext.Default.MessageIndex
                );
            }
            File.Move(tempFilePath, indexFilePath, true);
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save message index: {ex.Message}");
            return ex;
        }
    }

    // Returns the partition that could contain the given message id (by numeric id range), or
    // null if no known partition covers it (never captured by this index, or the index is stale
    // and needs rebuilding).
    public PartitionIndexEntry? FindPartitionForMessage(Snowflake messageId)
    {
        foreach (var partition in Partitions)
        {
            var min = Snowflake.TryParse(partition.MinMessageId);
            var max = Snowflake.TryParse(partition.MaxMessageId);
            if (min is null || max is null)
                continue;

            if (messageId.CompareTo(min.Value) >= 0 && messageId.CompareTo(max.Value) <= 0)
                return partition;
        }

        return null;
    }

    // Returns the best known byte offset to seek to before scanning forward for messageId --
    // the closest checkpoint at or before it, or 0 (scan from the start of the partition) if the
    // index has no checkpoints for it yet. Checkpoints are always stored in ascending order.
    public static long FindNearestCheckpointBefore(
        PartitionIndexEntry partition,
        Snowflake messageId
    )
    {
        long best = 0;

        foreach (var checkpoint in partition.Checkpoints)
        {
            var checkpointId = Snowflake.TryParse(checkpoint.MessageId);
            if (checkpointId is null)
                continue;

            if (checkpointId.Value.CompareTo(messageId) <= 0)
                best = checkpoint.ByteOffset;
            else
                break;
        }

        return best;
    }
}

public class PartitionIndexEntry
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("minMessageId")]
    public string MinMessageId { get; set; } = "";

    [JsonPropertyName("maxMessageId")]
    public string MaxMessageId { get; set; } = "";

    [JsonPropertyName("checkpoints")]
    public List<CheckpointEntry> Checkpoints { get; set; } = [];
}

public class CheckpointEntry
{
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = "";

    [JsonPropertyName("byteOffset")]
    public long ByteOffset { get; set; }
}

[JsonSerializable(typeof(MessageIndex))]
[JsonSerializable(typeof(PartitionIndexEntry))]
[JsonSerializable(typeof(CheckpointEntry))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class MessageIndexJsonContext : JsonSerializerContext;
