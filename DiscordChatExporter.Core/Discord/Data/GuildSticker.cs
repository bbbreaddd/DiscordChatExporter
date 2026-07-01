using System;
using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/sticker#sticker-object
public record GuildSticker(
    Snowflake Id,
    string Name,
    string? Description,
    string? Tags,
    StickerFormat Format,
    string SourceUrl,
    Snowflake? CreatorId,
    bool IsAvailable
) : IHasId
{
    public static GuildSticker Parse(JsonElement json)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var name = json.GetProperty("name").GetNonNullString();
        var description = json.GetPropertyOrNull("description")?.GetStringOrNull();
        var tags = json.GetPropertyOrNull("tags")?.GetStringOrNull();
        var format = json.GetProperty("format_type").GetInt32().Pipe(t => (StickerFormat)t);

        var sourceUrl = ImageCdn.GetStickerUrl(
            id,
            format switch
            {
                StickerFormat.Png => "png",
                StickerFormat.Apng => "png",
                StickerFormat.Lottie => "json",
                StickerFormat.Gif => "gif",
                _ => throw new InvalidOperationException($"Unknown sticker format '{format}'."),
            }
        );

        var creatorId = json.GetPropertyOrNull("user")
            ?.GetPropertyOrNull("id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        var isAvailable = json.GetPropertyOrNull("available")?.GetBooleanOrNull() ?? true;

        return new GuildSticker(
            id,
            name,
            description,
            tags,
            format,
            sourceUrl,
            creatorId,
            isAvailable
        );
    }
}
