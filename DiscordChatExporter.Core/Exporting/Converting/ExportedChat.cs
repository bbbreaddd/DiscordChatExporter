using System.Collections.Generic;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;

namespace DiscordChatExporter.Core.Exporting.Converting;

// Represents the contents of a previously generated JSON export, parsed back into domain
// objects so that it can be rendered into another format without contacting the Discord API.
public record ExportedChat(
    Guild Guild,
    Channel Channel,
    Snowflake? After,
    Snowflake? Before,
    IReadOnlyList<Message> Messages,
    IReadOnlyDictionary<Snowflake, Member> Members,
    IReadOnlyDictionary<Snowflake, Role> Roles
);
