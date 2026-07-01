namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/guild-scheduled-event#guild-scheduled-event-object-guild-scheduled-event-status
public enum ScheduledEventStatus
{
    Scheduled = 1,
    Active = 2,
    Completed = 3,
    Canceled = 4,
}
