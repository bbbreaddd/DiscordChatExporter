using System;
using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/guild-scheduled-event#guild-scheduled-event-object
public record ScheduledEvent(
    Snowflake Id,
    Snowflake? ChannelId,
    Snowflake? CreatorId,
    string Name,
    string? Description,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    ScheduledEventStatus Status,
    ScheduledEventEntityType EntityType,
    string? Location,
    string? CoverImageUrl,
    int? UserCount
) : IHasId
{
    public static ScheduledEvent Parse(JsonElement json)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);

        var channelId = json.GetPropertyOrNull("channel_id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        var creatorId = json.GetPropertyOrNull("creator_id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        var name = json.GetProperty("name").GetNonNullString();
        var description = json.GetPropertyOrNull("description")?.GetStringOrNull();

        var startTime = json.GetProperty("scheduled_start_time").GetDateTimeOffset();
        var endTime = json.GetPropertyOrNull("scheduled_end_time")?.GetDateTimeOffsetOrNull();

        var status = json.GetProperty("status").GetInt32().Pipe(s => (ScheduledEventStatus)s);
        var entityType = json.GetProperty("entity_type")
            .GetInt32()
            .Pipe(t => (ScheduledEventEntityType)t);

        var location = json.GetPropertyOrNull("entity_metadata")
            ?.GetPropertyOrNull("location")
            ?.GetStringOrNull();

        var coverImageUrl = json.GetPropertyOrNull("image")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(h => ImageCdn.GetScheduledEventCoverUrl(id, h));

        var userCount = json.GetPropertyOrNull("user_count")?.GetInt32OrNull();

        return new ScheduledEvent(
            id,
            channelId,
            creatorId,
            name,
            description,
            startTime,
            endTime,
            status,
            entityType,
            location,
            coverImageUrl,
            userCount
        );
    }
}
