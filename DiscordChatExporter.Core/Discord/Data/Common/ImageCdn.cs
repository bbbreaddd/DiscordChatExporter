using System;
using System.Globalization;
using System.Linq;

namespace DiscordChatExporter.Core.Discord.Data.Common;

// https://discord.com/developers/docs/reference#image-formatting
public static class ImageCdn
{
    // Standard emoji are rendered through Twemoji
    public static string GetStandardEmojiUrl(string emojiName)
    {
        var runes = emojiName.EnumerateRunes().ToArray();

        // Variant selector rune is skipped in Twemoji IDs,
        // except when the emoji also contains a zero-width joiner.
        // VS = 0xfe0f; ZWJ = 0x200d.
        var filteredRunes = runes.Any(r => r.Value == 0x200d)
            ? runes
            : runes.Where(r => r.Value != 0xfe0f);

        var twemojiId = string.Join(
            "-",
            filteredRunes.Select(r => r.Value.ToString("x", CultureInfo.InvariantCulture))
        );

        return $"https://cdn.jsdelivr.net/gh/twitter/twemoji@latest/assets/svg/{twemojiId}.svg";
    }

    public static string GetCustomEmojiUrl(Snowflake emojiId, bool isAnimated = false) =>
        isAnimated
            // Animated emoji must be requested as animated WebP, not GIF. Discord no longer
            // generates a .gif rendition for newer animated emoji -- that endpoint returns 415
            // Unsupported Media Type -- while .webp?animated=true serves the animated image for
            // both old and new emoji. Without animated=true the .webp endpoint returns only a
            // static first frame.
            ? $"https://cdn.discordapp.com/emojis/{emojiId}.webp?animated=true"
            : $"https://cdn.discordapp.com/emojis/{emojiId}.png";

    public static string GetGuildIconUrl(Snowflake guildId, string iconHash, int size = 512) =>
        iconHash.StartsWith("a_", StringComparison.Ordinal)
            ? $"https://cdn.discordapp.com/icons/{guildId}/{iconHash}.gif?size={size}"
            : $"https://cdn.discordapp.com/icons/{guildId}/{iconHash}.png?size={size}";

    public static string GetGuildBannerUrl(Snowflake guildId, string bannerHash, int size = 512) =>
        bannerHash.StartsWith("a_", StringComparison.Ordinal)
            ? $"https://cdn.discordapp.com/banners/{guildId}/{bannerHash}.gif?size={size}"
            : $"https://cdn.discordapp.com/banners/{guildId}/{bannerHash}.png?size={size}";

    // Splashes are never animated, unlike icons/banners -- no "a_" gif check needed.
    public static string GetGuildSplashUrl(Snowflake guildId, string splashHash, int size = 512) =>
        $"https://cdn.discordapp.com/splashes/{guildId}/{splashHash}.png?size={size}";

    public static string GetGuildDiscoverySplashUrl(
        Snowflake guildId,
        string splashHash,
        int size = 512
    ) => $"https://cdn.discordapp.com/discovery-splashes/{guildId}/{splashHash}.png?size={size}";

    public static string GetChannelIconUrl(Snowflake channelId, string iconHash, int size = 512) =>
        iconHash.StartsWith("a_", StringComparison.Ordinal)
            ? $"https://cdn.discordapp.com/channel-icons/{channelId}/{iconHash}.gif?size={size}"
            : $"https://cdn.discordapp.com/channel-icons/{channelId}/{iconHash}.png?size={size}";

    public static string GetUserAvatarUrl(Snowflake userId, string avatarHash, int size = 512) =>
        avatarHash.StartsWith("a_", StringComparison.Ordinal)
            ? $"https://cdn.discordapp.com/avatars/{userId}/{avatarHash}.gif?size={size}"
            : $"https://cdn.discordapp.com/avatars/{userId}/{avatarHash}.png?size={size}";

    public static string GetFallbackUserAvatarUrl(int index = 0) =>
        $"https://cdn.discordapp.com/embed/avatars/{index}.png";

    public static string GetUserBannerUrl(Snowflake userId, string bannerHash, int size = 512) =>
        bannerHash.StartsWith("a_", StringComparison.Ordinal)
            ? $"https://cdn.discordapp.com/banners/{userId}/{bannerHash}.gif?size={size}"
            : $"https://cdn.discordapp.com/banners/{userId}/{bannerHash}.png?size={size}";

    public static string GetMemberAvatarUrl(
        Snowflake guildId,
        Snowflake userId,
        string avatarHash,
        int size = 512
    ) =>
        avatarHash.StartsWith("a_", StringComparison.Ordinal)
            ? $"https://cdn.discordapp.com/guilds/{guildId}/users/{userId}/avatars/{avatarHash}.gif?size={size}"
            : $"https://cdn.discordapp.com/guilds/{guildId}/users/{userId}/avatars/{avatarHash}.png?size={size}";

    public static string GetStickerUrl(Snowflake stickerId, string format = "png") =>
        // GIF-format (animated) stickers are served only from the media proxy host; the same path
        // on cdn.discordapp.com returns 404. PNG/APNG (.png) and Lottie (.json) stickers live on
        // the regular CDN as usual.
        format == "gif"
            ? $"https://media.discordapp.net/stickers/{stickerId}.gif"
            : $"https://cdn.discordapp.com/stickers/{stickerId}.{format}";

    public static string GetRoleIconUrl(Snowflake roleId, string iconHash, int size = 512) =>
        $"https://cdn.discordapp.com/role-icons/{roleId}/{iconHash}.png?size={size}";

    public static string GetScheduledEventCoverUrl(
        Snowflake eventId,
        string imageHash,
        int size = 512
    ) => $"https://cdn.discordapp.com/guild-events/{eventId}/{imageHash}.png?size={size}";
}
