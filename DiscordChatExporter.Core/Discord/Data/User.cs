using System.Drawing;
using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/user#user-object
public partial record User(
    Snowflake Id,
    bool IsBot,
    // Remove after Discord migrates all accounts to the new system.
    // With that, also remove the DiscriminatorFormatted and FullName properties.
    // Replace existing calls to FullName with Name (not DisplayName).
    int? Discriminator,
    string Name,
    string DisplayName,
    string AvatarUrl,
    // Global (not per-guild -- Discord's guild member object has no per-guild banner) profile
    // banner/accent color. Only present on a full user fetch (message author, direct
    // GET user/{id}), not on the cut-down user object embedded elsewhere.
    string? BannerUrl = null,
    Color? AccentColor = null,
    bool IsSystem = false,
    int? Flags = null,
    int? PublicFlags = null,
    int? PremiumType = null,
    string? AvatarDecorationUrl = null
) : IHasId
{
    public string DiscriminatorFormatted { get; } =
        Discriminator is not null ? $"{Discriminator:0000}" : "0000";

    // This effectively represents the user's true identity.
    // In the old system, this is formed from the username and discriminator.
    // In the new system, the username is already the user's unique identifier.
    public string FullName => Discriminator is not null ? $"{Name}#{DiscriminatorFormatted}" : Name;
}

public partial record User
{
    public static User Parse(JsonElement json)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var isBot = json.GetPropertyOrNull("bot")?.GetBooleanOrNull() ?? false;

        var discriminator = json.GetPropertyOrNull("discriminator")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(int.Parse)
            .NullIfDefault();

        var name = json.GetProperty("username").GetNonNullString();
        var displayName =
            json.GetPropertyOrNull("global_name")?.GetNonWhiteSpaceStringOrNull() ?? name;

        var avatarIndex = discriminator % 5 ?? (int)((id.Value >> 22) % 6);

        var avatarUrl =
            json.GetPropertyOrNull("avatar")
                ?.GetNonWhiteSpaceStringOrNull()
                ?.Pipe(h => ImageCdn.GetUserAvatarUrl(id, h))
            ?? ImageCdn.GetFallbackUserAvatarUrl(avatarIndex);

        var bannerUrl = json.GetPropertyOrNull("banner")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(h => ImageCdn.GetUserBannerUrl(id, h));

        var accentColor = json.GetPropertyOrNull("accent_color")
            ?.GetInt32OrNull()
            ?.Pipe(System.Drawing.Color.FromArgb)
            .WithFullAlpha()
            .NullIf(c => c.ToRgb() <= 0);

        var isSystem = json.GetPropertyOrNull("system")?.GetBooleanOrNull() ?? false;
        var flags = json.GetPropertyOrNull("flags")?.GetInt32OrNull();
        var publicFlags = json.GetPropertyOrNull("public_flags")?.GetInt32OrNull();
        var premiumType = json.GetPropertyOrNull("premium_type")?.GetInt32OrNull();
        var avatarDecorationUrl = json.GetPropertyOrNull("avatar_decoration_data")
            ?.GetPropertyOrNull("asset")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(h => $"https://cdn.discordapp.com/avatar-decorations/{id}/{h}.png");

        return new User(
            id,
            isBot,
            discriminator,
            name,
            displayName,
            avatarUrl,
            bannerUrl,
            accentColor,
            isSystem,
            flags,
            publicFlags,
            premiumType,
            avatarDecorationUrl
        );
    }
}
