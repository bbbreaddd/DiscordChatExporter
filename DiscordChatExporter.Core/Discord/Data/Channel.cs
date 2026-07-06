using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/channel#channel-object
public partial record Channel(
    Snowflake Id,
    ChannelKind Kind,
    Snowflake GuildId,
    Channel? Parent,
    string Name,
    int? Position,
    string? IconUrl,
    string? Topic,
    bool IsArchived,
    Snowflake? LastMessageId,
    IReadOnlyList<PermissionOverwrite> PermissionOverwrites,
    bool IsNsfw = false,
    int? SlowmodeSeconds = null,
    int? Bitrate = null,
    int? UserLimit = null,
    // Forum tag definitions (on the forum channel itself) and applied tags (on a thread that
    // belongs to a forum) -- raw passthrough JSON, same rationale as Guild's FeaturesJson/
    // WelcomeScreenJson: reference data, not something the exporter needs to transform.
    string? AvailableTagsJson = null,
    string? AppliedTagsJson = null,
    // Thread creator (top-level owner_id) and Discord's own (capped/approximate) activity
    // counters -- separate from thread_metadata below, and separate from the exact counts
    // derivable by querying our own message table.
    Snowflake? OwnerId = null,
    int? MessageCount = null,
    int? MemberCount = null,
    int? TotalMessageSent = null,
    // Remaining thread_metadata fields (archived is already captured above as IsArchived).
    int? AutoArchiveDuration = null,
    DateTimeOffset? ArchiveTimestamp = null,
    bool IsLocked = false,
    bool IsInvitable = true,
    DateTimeOffset? CreateTimestamp = null
) : IHasId
{
    public bool IsDirect { get; } =
        Kind is ChannelKind.DirectTextChat or ChannelKind.DirectGroupTextChat;

    public bool IsGuild => !IsDirect;

    public bool IsCategory { get; } = Kind == ChannelKind.GuildCategory;

    public bool IsVoice { get; } =
        Kind is ChannelKind.GuildVoiceChat or ChannelKind.GuildStageVoice;

    public bool IsThread { get; } =
        Kind
        is ChannelKind.GuildNewsThread
            or ChannelKind.GuildPublicThread
            or ChannelKind.GuildPrivateThread;

    public bool IsEmpty { get; } = LastMessageId is null;

    public IEnumerable<Channel> GetParents()
    {
        var current = Parent;
        while (current is not null)
        {
            yield return current;
            current = current.Parent;
        }
    }

    public Channel? TryGetRootParent() => GetParents().LastOrDefault();

    public string GetHierarchicalName() =>
        string.Join(" / ", GetParents().Reverse().Select(c => c.Name).Append(Name));

    public bool MayHaveMessagesAfter(Snowflake messageId) => !IsEmpty && messageId < LastMessageId;

    public bool MayHaveMessagesBefore(Snowflake messageId) => !IsEmpty && messageId > Id;
}

public partial record Channel
{
    public static Channel Parse(JsonElement json, Channel? parent = null, int? positionHint = null)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var kind = json.GetProperty("type").GetInt32().Pipe(t => (ChannelKind)t);

        var guildId =
            json.GetPropertyOrNull("guild_id")
                ?.GetNonWhiteSpaceStringOrNull()
                ?.Pipe(Snowflake.Parse)
            ?? Guild.DirectMessages.Id;

        var name =
            // Guild channel
            json.GetPropertyOrNull("name")?.GetNonWhiteSpaceStringOrNull()
            // DM channel
            ?? json.GetPropertyOrNull("recipients")
                ?.EnumerateArrayOrNull()
                ?.Select(User.Parse)
                .OrderBy(u => u.Id)
                .Select(u => u.DisplayName)
                .Pipe(s => string.Join(", ", s))
            // Fallback
            ?? id.ToString();

        var position = positionHint ?? json.GetPropertyOrNull("position")?.GetInt32OrNull();

        // Icons can only be set for group DM channels
        var iconUrl = json.GetPropertyOrNull("icon")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(h => ImageCdn.GetChannelIconUrl(id, h));

        var topic = json.GetPropertyOrNull("topic")?.GetStringOrNull();

        var threadMetadata = json.GetPropertyOrNull("thread_metadata");

        var isArchived = threadMetadata?.GetPropertyOrNull("archived")?.GetBooleanOrNull() ?? false;
        var autoArchiveDuration = threadMetadata
            ?.GetPropertyOrNull("auto_archive_duration")
            ?.GetInt32OrNull();
        var archiveTimestamp = threadMetadata
            ?.GetPropertyOrNull("archive_timestamp")
            ?.GetDateTimeOffsetOrNull();
        var isLocked = threadMetadata?.GetPropertyOrNull("locked")?.GetBooleanOrNull() ?? false;
        // Absent for threads created before this field existed; true is Discord's own default.
        var isInvitable =
            threadMetadata?.GetPropertyOrNull("invitable")?.GetBooleanOrNull() ?? true;
        var createTimestamp = threadMetadata
            ?.GetPropertyOrNull("create_timestamp")
            ?.GetDateTimeOffsetOrNull();

        var lastMessageId = json.GetPropertyOrNull("last_message_id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        var permissionOverwrites =
            json.GetPropertyOrNull("permission_overwrites")
                ?.EnumerateArrayOrNull()
                ?.Select(PermissionOverwrite.Parse)
                .ToArray()
            ?? [];

        var isNsfw = json.GetPropertyOrNull("nsfw")?.GetBooleanOrNull() ?? false;
        var slowmodeSeconds = json.GetPropertyOrNull("rate_limit_per_user")?.GetInt32OrNull();
        var bitrate = json.GetPropertyOrNull("bitrate")?.GetInt32OrNull();
        var userLimit = json.GetPropertyOrNull("user_limit")?.GetInt32OrNull();
        var availableTagsJson = json.GetPropertyOrNull("available_tags")?.GetRawText();
        var appliedTagsJson = json.GetPropertyOrNull("applied_tags")?.GetRawText();

        var ownerId = json.GetPropertyOrNull("owner_id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);
        var messageCount = json.GetPropertyOrNull("message_count")?.GetInt32OrNull();
        var memberCount = json.GetPropertyOrNull("member_count")?.GetInt32OrNull();
        var totalMessageSent = json.GetPropertyOrNull("total_message_sent")?.GetInt32OrNull();

        return new Channel(
            id,
            kind,
            guildId,
            parent,
            name,
            position,
            iconUrl,
            topic,
            isArchived,
            lastMessageId,
            permissionOverwrites,
            isNsfw,
            slowmodeSeconds,
            bitrate,
            userLimit,
            availableTagsJson,
            appliedTagsJson,
            ownerId,
            messageCount,
            memberCount,
            totalMessageSent,
            autoArchiveDuration,
            archiveTimestamp,
            isLocked,
            isInvitable,
            createTimestamp
        );
    }
}
