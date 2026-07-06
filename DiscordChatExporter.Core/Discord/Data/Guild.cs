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
    int? ApproximateMemberCount = null,
    int? VerificationLevel = null,
    int? ExplicitContentFilter = null,
    int? MfaLevel = null,
    Snowflake? SystemChannelId = null,
    Snowflake? RulesChannelId = null,
    Snowflake? PublicUpdatesChannelId = null,
    Snowflake? AfkChannelId = null,
    int? AfkTimeout = null,
    string? PreferredLocale = null,
    string? VanityUrlCode = null,
    // Raw passthrough JSON (not modeled field-by-field) for guild-config blobs that are
    // read-mostly reference data rather than something the exporter needs to transform for
    // JSON/HTML output -- SQLite's json_extract can query into these later if needed.
    string? FeaturesJson = null,
    string? WelcomeScreenJson = null,
    // Fetched separately (GET guilds/{id}/onboarding is its own endpoint, not part of the
    // guild object) -- populated via a `with` expression by whoever calls
    // DiscordClient.TryGetGuildOnboardingJsonAsync, not by Guild.Parse itself.
    string? OnboardingJson = null
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

        Snowflake? ParseSnowflakeOrNull(string propertyName) =>
            json.GetPropertyOrNull(propertyName)
                ?.GetNonWhiteSpaceStringOrNull()
                ?.Pipe(Snowflake.Parse);

        return new Guild(
            id,
            name,
            iconUrl,
            bannerUrl,
            json.GetPropertyOrNull("premium_tier")?.GetInt32OrNull(),
            json.GetPropertyOrNull("premium_subscription_count")?.GetInt32OrNull(),
            approximateMemberCount,
            json.GetPropertyOrNull("verification_level")?.GetInt32OrNull(),
            json.GetPropertyOrNull("explicit_content_filter")?.GetInt32OrNull(),
            json.GetPropertyOrNull("mfa_level")?.GetInt32OrNull(),
            ParseSnowflakeOrNull("system_channel_id"),
            ParseSnowflakeOrNull("rules_channel_id"),
            ParseSnowflakeOrNull("public_updates_channel_id"),
            ParseSnowflakeOrNull("afk_channel_id"),
            json.GetPropertyOrNull("afk_timeout")?.GetInt32OrNull(),
            json.GetPropertyOrNull("preferred_locale")?.GetNonWhiteSpaceStringOrNull(),
            json.GetPropertyOrNull("vanity_url_code")?.GetNonWhiteSpaceStringOrNull(),
            json.GetPropertyOrNull("features")?.GetRawText(),
            json.GetPropertyOrNull("welcome_screen")?.GetRawText()
        );
    }
}
