using System;
using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/channel#thread-member-object
public record ThreadMember(Snowflake UserId, DateTimeOffset JoinTimestamp, int Flags)
{
    public static ThreadMember Parse(JsonElement json)
    {
        var userId = json.GetProperty("user_id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var joinTimestamp = json.GetProperty("join_timestamp").GetDateTimeOffset();
        var flags = json.GetPropertyOrNull("flags")?.GetInt32OrNull() ?? 0;

        return new ThreadMember(userId, joinTimestamp, flags);
    }
}
