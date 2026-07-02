using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/guild#guild-object
public partial record Guild(
    Snowflake Id,
    string Name,
    string IconUrl,
    string? BannerUrl = null,
    int? PremiumTier = null,
    int? PremiumSubscriptionCount = null,
    int? ApproximateMemberCount = null
) : IHasId
{
    public bool IsDirect { get; } = Id == Snowflake.Zero;
}

public partial record Guild
{
    // Direct messages are encapsulated within a special pseudo-guild for consistency
    public static Guild DirectMessages { get; } =
        new(Snowflake.Zero, "Direct Messages", ImageCdn.GetFallbackUserAvatarUrl());

    public static Guild Parse(JsonElement json)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var name = json.GetProperty("name").GetNonNullString();

        var iconUrl =
            json.GetPropertyOrNull("icon")
                ?.GetNonWhiteSpaceStringOrNull()
                ?.Pipe(h => ImageCdn.GetGuildIconUrl(id, h))
            ?? ImageCdn.GetFallbackUserAvatarUrl();

        var bannerUrl = json.GetPropertyOrNull("banner")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(h => ImageCdn.GetGuildBannerUrl(id, h));

        var approximateMemberCount =
            json.GetPropertyOrNull("approximate_member_count")?.GetInt32OrNull()
            ?? json.GetPropertyOrNull("member_count")?.GetInt32OrNull();

        return new Guild(
            id,
            name,
            iconUrl,
            bannerUrl,
            json.GetPropertyOrNull("premium_tier")?.GetInt32OrNull(),
            json.GetPropertyOrNull("premium_subscription_count")?.GetInt32OrNull(),
            approximateMemberCount
        );
    }
}
