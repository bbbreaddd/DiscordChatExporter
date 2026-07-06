using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/emoji#emoji-object
public record GuildEmoji(
    Snowflake Id,
    string Name,
    bool IsAnimated,
    string ImageUrl,
    Snowflake? CreatorId,
    bool IsAvailable,
    bool IsManaged,
    // Raw passthrough JSON array of role ids allowed to use this emoji (empty array "[]" means
    // unrestricted -- same convention as the other reference-data JSON blobs added in V13/V14).
    string? RoleIdsJson = null
) : IHasId
{
    public static GuildEmoji Parse(JsonElement json)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var name = json.GetProperty("name").GetNonNullString();
        var isAnimated = json.GetPropertyOrNull("animated")?.GetBooleanOrNull() ?? false;
        var imageUrl = ImageCdn.GetCustomEmojiUrl(id, isAnimated);

        var creatorId = json.GetPropertyOrNull("user")
            ?.GetPropertyOrNull("id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        var isAvailable = json.GetPropertyOrNull("available")?.GetBooleanOrNull() ?? true;
        var isManaged = json.GetPropertyOrNull("managed")?.GetBooleanOrNull() ?? false;
        var roleIdsJson = json.GetPropertyOrNull("roles")?.GetRawText();

        return new GuildEmoji(
            id,
            name,
            isAnimated,
            imageUrl,
            creatorId,
            isAvailable,
            isManaged,
            roleIdsJson
        );
    }
}
