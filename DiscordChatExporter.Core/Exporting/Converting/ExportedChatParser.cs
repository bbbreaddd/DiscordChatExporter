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
    private static string? RebaseLocalAssetPath(
        string? localPath,
        Func<string, string>? rebaseLocalAssetPath = null
    ) =>
        !string.IsNullOrWhiteSpace(localPath) && rebaseLocalAssetPath is not null
            ? rebaseLocalAssetPath(localPath)
            : localPath;

    private static Guild ParseGuild(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var name = json.GetProperty("name").GetNonNullString();
        var iconUrl =
            RebaseLocalAssetPath(
                json.GetPropertyOrNull("iconLocalPath")?.GetNonWhiteSpaceStringOrNull(),
                rebaseLocalAssetPath
            ) ?? json.GetProperty("iconUrl").GetNonWhiteSpaceString();

        return new Guild(id, name, iconUrl);
    }

    private static Channel ParseChannel(
        JsonElement json,
        Snowflake guildId,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var kind = json.GetProperty("type")
            .GetNonNullString()
            .Pipe(s => Enum.Parse<ChannelKind>(s));
        var name = json.GetProperty("name").GetNonNullString();
        var topic = json.GetPropertyOrNull("topic")?.GetStringOrNull();
        var iconUrl =
            RebaseLocalAssetPath(
                json.GetPropertyOrNull("iconLocalPath")?.GetNonWhiteSpaceStringOrNull(),
                rebaseLocalAssetPath
            ) ?? json.GetPropertyOrNull("iconUrl")?.GetNonWhiteSpaceStringOrNull();

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
        Dictionary<Snowflake, Role> roles,
        Func<string, string>? rebaseLocalAssetPath = null
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
        var user = ExportedMessageParser.ParseUser(userJson, rebaseLocalAssetPath);

        members[id] = new Member(user, displayName, null, roleIds);
    }

    private static void CollectMembersAndRoles(
        JsonElement messageJson,
        Dictionary<Snowflake, Member> members,
        Dictionary<Snowflake, Role> roles,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        CollectMember(messageJson.GetProperty("author"), members, roles, rebaseLocalAssetPath);

        foreach (
            var userJson in messageJson.GetPropertyOrNull("mentions")?.EnumerateArrayOrNull() ?? []
        )
            CollectMember(userJson, members, roles, rebaseLocalAssetPath);

        if (messageJson.GetPropertyOrNull("interaction") is { } interactionJson)
            CollectMember(
                interactionJson.GetProperty("user"),
                members,
                roles,
                rebaseLocalAssetPath
            );

        foreach (
            var reactionJson in messageJson.GetPropertyOrNull("reactions")?.EnumerateArrayOrNull()
                ?? []
        )
        {
            foreach (
                var userJson in reactionJson.GetPropertyOrNull("users")?.EnumerateArrayOrNull()
                    ?? []
            )
                CollectMember(userJson, members, roles, rebaseLocalAssetPath);
        }
    }

    public static (Guild Guild, Channel Channel, Snowflake? After, Snowflake? Before) ParseMetadata(
        string filePath
    )
    {
        try
        {
            using var stream = System.IO.File.OpenRead(filePath);
            var buffer = new byte[262144]; // 256KB
            var bytesRead = stream.Read(buffer, 0, buffer.Length);

            var reader = new Utf8JsonReader(buffer.AsSpan(0, bytesRead));

            Guild? guild = null;
            Channel? channel = null;
            Snowflake? after = null;
            Snowflake? before = null;

            if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        break;

                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var propertyName = reader.GetString();
                        reader.Read();

                        if (
                            string.Equals(propertyName, "guild", StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            using var doc = JsonDocument.ParseValue(ref reader);
                            guild = ParseGuild(doc.RootElement);
                        }
                        else if (
                            string.Equals(
                                propertyName,
                                "channel",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            if (guild == null)
                                break; // Trigger fallback
                            using var doc = JsonDocument.ParseValue(ref reader);
                            channel = ParseChannel(doc.RootElement, guild.Id);
                        }
                        else if (
                            string.Equals(
                                propertyName,
                                "dateRange",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            using var doc = JsonDocument.ParseValue(ref reader);
                            after = doc
                                .RootElement.GetPropertyOrNull("after")
                                ?.GetDateTimeOffsetOrNull()
                                ?.Pipe(Snowflake.FromDate);
                            before = doc
                                .RootElement.GetPropertyOrNull("before")
                                ?.GetDateTimeOffsetOrNull()
                                ?.Pipe(Snowflake.FromDate);
                        }
                        else if (
                            string.Equals(
                                propertyName,
                                "messages",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            if (guild != null && channel != null)
                                return (guild, channel, after, before);
                            break; // Trigger fallback
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore reader exceptions and fall back to full parsing
        }

        // Fallback: Parse full document
        using (var stream = System.IO.File.OpenRead(filePath))
        using (var doc = JsonDocument.Parse(stream))
        {
            var guild = ParseGuild(doc.RootElement.GetProperty("guild"));
            var channel = ParseChannel(doc.RootElement.GetProperty("channel"), guild.Id);

            var dateRange = doc.RootElement.GetProperty("dateRange");
            var after = dateRange
                .GetPropertyOrNull("after")
                ?.GetDateTimeOffsetOrNull()
                ?.Pipe(Snowflake.FromDate);
            var before = dateRange
                .GetPropertyOrNull("before")
                ?.GetDateTimeOffsetOrNull()
                ?.Pipe(Snowflake.FromDate);

            return (guild, channel, after, before);
        }
    }

    public static ExportedChat Parse(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var guild = ParseGuild(json.GetProperty("guild"), rebaseLocalAssetPath);
        var channel = ParseChannel(json.GetProperty("channel"), guild.Id, rebaseLocalAssetPath);

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
        var messages = messagesJson
            .Select(j => ExportedMessageParser.ParseMessage(j, rebaseLocalAssetPath))
            .ToArray();

        var members = new Dictionary<Snowflake, Member>();
        var roles = new Dictionary<Snowflake, Role>();

        foreach (var messageJson in messagesJson)
            CollectMembersAndRoles(messageJson, members, roles, rebaseLocalAssetPath);

        return new ExportedChat(guild, channel, after, before, messages, members, roles);
    }
}
