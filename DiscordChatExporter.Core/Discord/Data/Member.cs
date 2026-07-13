using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/guild#guild-member-object
public partial record Member(
    User User,
    string? DisplayName,
    string? AvatarUrl,
    IReadOnlyList<Snowflake> RoleIds,
    DateTimeOffset? JoinedAt = null,
    DateTimeOffset? PremiumSince = null,
    DateTimeOffset? CommunicationDisabledUntil = null,
    bool Pending = false,
    int? Flags = null,
    string? Permissions = null,
    string? AvatarDecorationUrl = null
) : IHasId
{
    public Snowflake Id { get; } = User.Id;
}

public partial record Member
{
    public static Member CreateFallback(User user) => new(user, null, null, []);

    // A MESSAGE_CREATE/MESSAGE_UPDATE gateway payload embeds the author's guild member object under
    // "member", but WITHOUT the nested "user" (the user lives in the top-level "author" instead).
    // This builds a Member from that block plus the already-parsed author, so the live watch path
    // can capture nick/roles/joined_at/etc. straight from the event instead of a REST member fetch.
    public static Member ParseFromMessage(JsonElement memberJson, User author, Snowflake guildId)
    {
        var displayName = memberJson.GetPropertyOrNull("nick")?.GetNonWhiteSpaceStringOrNull();

        var roleIds =
            memberJson
                .GetPropertyOrNull("roles")
                ?.EnumerateArray()
                .Select(j => j.GetNonWhiteSpaceString())
                .Select(Snowflake.Parse)
                .ToArray()
            ?? [];

        var avatarUrl = memberJson
            .GetPropertyOrNull("avatar")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(h => ImageCdn.GetMemberAvatarUrl(guildId, author.Id, h));

        var joinedAt = memberJson.GetPropertyOrNull("joined_at")?.GetDateTimeOffsetOrNull();
        var premiumSince = memberJson.GetPropertyOrNull("premium_since")?.GetDateTimeOffsetOrNull();
        var communicationDisabledUntil = memberJson
            .GetPropertyOrNull("communication_disabled_until")
            ?.GetDateTimeOffsetOrNull();
        var pending = memberJson.GetPropertyOrNull("pending")?.GetBooleanOrNull() ?? false;

        var flags = memberJson.GetPropertyOrNull("flags")?.GetInt32OrNull();
        var permissions = memberJson.GetPropertyOrNull("permissions")?.GetStringOrNull();
        var avatarDecorationUrl = memberJson
            .GetPropertyOrNull("avatar_decoration_data")
            ?.GetPropertyOrNull("asset")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(h => $"https://cdn.discordapp.com/avatar-decorations/{author.Id}/{h}.png");

        return new Member(
            author,
            displayName,
            avatarUrl,
            roleIds,
            joinedAt,
            premiumSince,
            communicationDisabledUntil,
            pending,
            flags,
            permissions,
            avatarDecorationUrl
        );
    }

    public static Member Parse(JsonElement json, Snowflake? guildId = null)
    {
        var user = json.GetProperty("user").Pipe(User.Parse);
        var displayName = json.GetPropertyOrNull("nick")?.GetNonWhiteSpaceStringOrNull();

        var roleIds =
            json.GetPropertyOrNull("roles")
                ?.EnumerateArray()
                .Select(j => j.GetNonWhiteSpaceString())
                .Select(Snowflake.Parse)
                .ToArray()
            ?? [];

        var avatarUrl = guildId is not null
            ? json.GetPropertyOrNull("avatar")
                ?.GetNonWhiteSpaceStringOrNull()
                ?.Pipe(h => ImageCdn.GetMemberAvatarUrl(guildId.Value, user.Id, h))
            : null;

        var joinedAt = json.GetPropertyOrNull("joined_at")?.GetDateTimeOffsetOrNull();
        var premiumSince = json.GetPropertyOrNull("premium_since")?.GetDateTimeOffsetOrNull();
        var communicationDisabledUntil = json.GetPropertyOrNull("communication_disabled_until")
            ?.GetDateTimeOffsetOrNull();
        var pending = json.GetPropertyOrNull("pending")?.GetBooleanOrNull() ?? false;

        var flags = json.GetPropertyOrNull("flags")?.GetInt32OrNull();
        var permissions = json.GetPropertyOrNull("permissions")?.GetStringOrNull();
        var avatarDecorationUrl = json.GetPropertyOrNull("avatar_decoration_data")
            ?.GetPropertyOrNull("asset")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(h => $"https://cdn.discordapp.com/avatar-decorations/{user.Id}/{h}.png");

        return new Member(
            user,
            displayName,
            avatarUrl,
            roleIds,
            joinedAt,
            premiumSince,
            communicationDisabledUntil,
            pending,
            flags,
            permissions,
            avatarDecorationUrl
        );
    }
}
