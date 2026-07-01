using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using JsonExtensions.Reading;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/interactions/message-components
// Captures the structural tree (type/style/label/custom_id/url/disabled/placeholder/emoji +
// recursive children for action rows) well enough to see "this message had these buttons," not a
// full interactive replay. Select-menu options are kept as a compact JSON passthrough
// (OptionsJson) rather than another nested type.
public record MessageComponent(
    int Type,
    int? Style,
    string? Label,
    string? CustomId,
    string? Url,
    bool? Disabled,
    string? Placeholder,
    string? OptionsJson,
    IReadOnlyList<MessageComponent> Children
)
{
    public static MessageComponent Parse(JsonElement json)
    {
        var type = json.GetProperty("type").GetInt32();
        var style = json.GetPropertyOrNull("style")?.GetInt32OrNull();
        var label = json.GetPropertyOrNull("label")?.GetStringOrNull();
        var customId = json.GetPropertyOrNull("custom_id")?.GetStringOrNull();
        var url = json.GetPropertyOrNull("url")?.GetStringOrNull();
        var disabled = json.GetPropertyOrNull("disabled")?.GetBooleanOrNull();
        var placeholder = json.GetPropertyOrNull("placeholder")?.GetStringOrNull();

        var optionsJson = json.GetPropertyOrNull("options")?.GetRawText();

        var children =
            json.GetPropertyOrNull("components")?.EnumerateArrayOrNull()?.Select(Parse).ToArray()
            ?? [];

        return new MessageComponent(
            type,
            style,
            label,
            customId,
            url,
            disabled,
            placeholder,
            optionsJson,
            children
        );
    }
}
