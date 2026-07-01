using System.Drawing;
using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/topics/permissions#role-object
public record Role(
    Snowflake Id,
    string Name,
    int Position,
    Color? Color,
    ulong Permissions,
    bool Hoist,
    bool Mentionable,
    string? IconUrl,
    string? UnicodeEmoji,
    bool Managed
) : IHasId
{
    public static Role Parse(JsonElement json)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var name = json.GetProperty("name").GetNonNullString();
        var position = json.GetProperty("position").GetInt32();

        var color = json.GetPropertyOrNull("color")
            ?.GetInt32OrNull()
            ?.Pipe(System.Drawing.Color.FromArgb)
            .WithFullAlpha()
            .NullIf(c => c.ToRgb() <= 0);

        var permissions =
            json.GetPropertyOrNull("permissions")?.GetNonWhiteSpaceStringOrNull()?.Pipe(ulong.Parse)
            ?? 0;

        var hoist = json.GetPropertyOrNull("hoist")?.GetBooleanOrNull() ?? false;
        var mentionable = json.GetPropertyOrNull("mentionable")?.GetBooleanOrNull() ?? false;
        var managed = json.GetPropertyOrNull("managed")?.GetBooleanOrNull() ?? false;
        var unicodeEmoji = json.GetPropertyOrNull("unicode_emoji")?.GetNonWhiteSpaceStringOrNull();

        var iconUrl = json.GetPropertyOrNull("icon")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(h => ImageCdn.GetRoleIconUrl(id, h));

        return new Role(
            id,
            name,
            position,
            color,
            permissions,
            hoist,
            mentionable,
            iconUrl,
            unicodeEmoji,
            managed
        );
    }
}
