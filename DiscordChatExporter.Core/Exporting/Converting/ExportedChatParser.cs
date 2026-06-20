using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting.Converting;

public static class ExportedChatParser
{
    private static Guild ParseGuild(JsonElement json)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var name = json.GetProperty("name").GetNonNullString();
        var iconUrl = json.GetProperty("iconUrl").GetNonWhiteSpaceString();

        return new Guild(id, name, iconUrl);
    }

    private static Channel ParseChannel(JsonElement json, Snowflake guildId)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var kind = json.GetProperty("type")
            .GetNonNullString()
            .Pipe(s => Enum.Parse<ChannelKind>(s));
        var name = json.GetProperty("name").GetNonNullString();
        var topic = json.GetPropertyOrNull("topic")?.GetStringOrNull();
        var iconUrl = json.GetPropertyOrNull("iconUrl")?.GetNonWhiteSpaceStringOrNull();

        var categoryId = json.GetPropertyOrNull("categoryId")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        var categoryName = json.GetPropertyOrNull("category")?.GetNonWhiteSpaceStringOrNull();

        var parentCategoryId = json.GetPropertyOrNull("parentCategoryId")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        var parentCategoryName = json.GetPropertyOrNull("parentCategory")
            ?.GetNonWhiteSpaceStringOrNull();

        // For threads, 'category'/'categoryId' refer to the parent channel, which may itself
        // belong to a category, captured separately as 'parentCategory'/'parentCategoryId'.
        // Exports created before this field existed won't have it, so the hierarchy will be
        // one level shorter for threads in those files.
        var grandparent = parentCategoryId is not null
            ? new Channel(
                parentCategoryId.Value,
                ChannelKind.GuildCategory,
                guildId,
                null,
                parentCategoryName ?? parentCategoryId.Value.ToString(),
                null,
                null,
                null,
                false,
                null
            )
            : null;

        var parent = categoryId is not null
            ? new Channel(
                categoryId.Value,
                ChannelKind.GuildCategory,
                guildId,
                grandparent,
                categoryName ?? categoryId.Value.ToString(),
                null,
                null,
                null,
                false,
                null
            )
            : null;

        // LastMessageId isn't part of the export schema
        return new Channel(id, kind, guildId, parent, name, null, iconUrl, topic, false, null);
    }

    // Collects member/role info embedded in a user object from the export schema. Entries
    // that include a "roles" array (authors, mentions, interaction users) take precedence
    // over reaction-only entries, which omit roles and nicknames.
    private static void CollectMember(
        JsonElement userJson,
        Dictionary<Snowflake, Member> members,
        Dictionary<Snowflake, Role> roles
    )
    {
        var id = userJson.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var rolesJson = userJson.GetPropertyOrNull("roles")?.EnumerateArrayOrNull();

        if (rolesJson is null && members.ContainsKey(id))
            return;

        var roleIds = new List<Snowflake>();

        if (rolesJson is not null)
        {
            foreach (var roleJson in rolesJson)
            {
                var role = ExportedMessageParser.ParseRole(roleJson);
                roles.TryAdd(role.Id, role);
                roleIds.Add(role.Id);
            }
        }

        var displayName = userJson.GetPropertyOrNull("nickname")?.GetNonWhiteSpaceStringOrNull();
        var user = ExportedMessageParser.ParseUser(userJson);

        members[id] = new Member(user, displayName, null, roleIds);
    }

    private static void CollectMembersAndRoles(
        JsonElement messageJson,
        Dictionary<Snowflake, Member> members,
        Dictionary<Snowflake, Role> roles
    )
    {
        CollectMember(messageJson.GetProperty("author"), members, roles);

        foreach (
            var userJson in messageJson.GetPropertyOrNull("mentions")?.EnumerateArrayOrNull() ?? []
        )
            CollectMember(userJson, members, roles);

        if (messageJson.GetPropertyOrNull("interaction") is { } interactionJson)
            CollectMember(interactionJson.GetProperty("user"), members, roles);

        foreach (
            var reactionJson in messageJson.GetPropertyOrNull("reactions")?.EnumerateArrayOrNull()
                ?? []
        )
        {
            foreach (
                var userJson in reactionJson.GetPropertyOrNull("users")?.EnumerateArrayOrNull()
                    ?? []
            )
                CollectMember(userJson, members, roles);
        }
    }

    public static ExportedChat Parse(JsonElement json)
    {
        var guild = json.GetProperty("guild").Pipe(ParseGuild);
        var channel = ParseChannel(json.GetProperty("channel"), guild.Id);

        var dateRange = json.GetProperty("dateRange");
        var after = dateRange
            .GetPropertyOrNull("after")
            ?.GetDateTimeOffsetOrNull()
            ?.Pipe(Snowflake.FromDate);
        var before = dateRange
            .GetPropertyOrNull("before")
            ?.GetDateTimeOffsetOrNull()
            ?.Pipe(Snowflake.FromDate);

        var messagesJson = json.GetProperty("messages").EnumerateArray().ToArray();
        var messages = messagesJson.Select(ExportedMessageParser.ParseMessage).ToArray();

        var members = new Dictionary<Snowflake, Member>();
        var roles = new Dictionary<Snowflake, Role>();

        foreach (var messageJson in messagesJson)
            CollectMembersAndRoles(messageJson, members, roles);

        return new ExportedChat(guild, channel, after, before, messages, members, roles);
    }
}
