namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/guild-scheduled-event#guild-scheduled-event-object-guild-scheduled-event-entity-types
public enum ScheduledEventEntityType
{
    StageInstance = 1,
    Voice = 2,
    External = 3,
}
