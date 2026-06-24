using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting;

public class ExportManifest
{
    private static readonly SemaphoreSlim SaveSemaphore = new(1, 1);

    [JsonPropertyName("channels")]
    public ConcurrentDictionary<string, ChannelManifestEntry> Channels { get; set; } = new();

    public static async ValueTask<ExportManifest> LoadAsync(string directoryPath)
    {
        var filePath = Path.Combine(directoryPath, ".discord_backup_manifest.json");
        if (!File.Exists(filePath))
            return new ExportManifest();

        try
        {
            await using var stream = File.OpenRead(filePath);
            var manifest = await JsonSerializer.DeserializeAsync(
                stream,
                ExportManifestJsonContext.Default.ExportManifest
            );
            return manifest ?? new ExportManifest();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load manifest: {ex}");
            return new ExportManifest();
        }
    }

    public async ValueTask SaveAsync(string directoryPath)
    {
        var filePath = Path.Combine(directoryPath, ".discord_backup_manifest.json");
        var tempFilePath = filePath + ".tmp";

        await SaveSemaphore.WaitAsync();
        try
        {
            Directory.CreateDirectory(directoryPath);
            await using (var stream = File.Create(tempFilePath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    this,
                    ExportManifestJsonContext.Default.ExportManifest
                );
            }
            File.Move(tempFilePath, filePath, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save manifest: {ex}");
            // Ignore save failures to not crash the backup process
        }
        finally
        {
            SaveSemaphore.Release();
        }
    }

    public void RemoveEntry(string channelId) => Channels.TryRemove(channelId, out _);

    public void UpdateEntry(string channelId, string? lastMessageId, bool isArchived)
    {
        Channels[channelId] = new ChannelManifestEntry
        {
            LastMessageId = lastMessageId,
            IsArchived = isArchived,
            LastExportedAt = DateTimeOffset.UtcNow,
        };
    }
}

public class ChannelManifestEntry
{
    [JsonPropertyName("lastMessageId")]
    public string? LastMessageId { get; set; }

    [JsonPropertyName("isArchived")]
    public bool IsArchived { get; set; }

    [JsonPropertyName("lastExportedAt")]
    public DateTimeOffset LastExportedAt { get; set; }
}

[JsonSerializable(typeof(ExportManifest))]
[JsonSerializable(typeof(ChannelManifestEntry))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ExportManifestJsonContext : JsonSerializerContext;
