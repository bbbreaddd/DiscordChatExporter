using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/channel#reaction-object
public record Reaction(
    Emoji Emoji,
    int Count,
    IReadOnlyList<User>? Users = null,
    int BurstCount = 0,
    int NormalCount = 0,
    bool MeBurst = false,
    IReadOnlyList<string>? BurstColors = null
)
{
    public IReadOnlyList<string> BurstColors { get; } = BurstColors ?? [];

    public static Reaction Parse(JsonElement json)
    {
        var emoji = json.GetProperty("emoji").Pipe(Emoji.Parse);
        var count = json.GetProperty("count").GetInt32();

        var countDetails = json.GetPropertyOrNull("count_details");
        var burstCount = countDetails?.GetPropertyOrNull("burst")?.GetInt32OrNull() ?? 0;
        var normalCount = countDetails?.GetPropertyOrNull("normal")?.GetInt32OrNull() ?? count;
        var meBurst = json.GetPropertyOrNull("me_burst")?.GetBooleanOrNull() ?? false;
        var burstColors =
            json.GetPropertyOrNull("burst_colors")
                ?.EnumerateArrayOrNull()
                ?.Select(j => j.GetString())
                .WhereNotNull()
                .ToArray()
            ?? [];

        return new Reaction(emoji, count, null, burstCount, normalCount, meBurst, burstColors);
    }
}
