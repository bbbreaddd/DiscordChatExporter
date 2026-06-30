using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/channel#overwrite-object
public record PermissionOverwrite(
    Snowflake Id,
    PermissionOverwriteKind Kind,
    ulong Allow,
    ulong Deny
) : IHasId
{
    public static PermissionOverwrite Parse(JsonElement json)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var kind = json.GetProperty("type").GetInt32().Pipe(t => (PermissionOverwriteKind)t);

        var allow = json.GetProperty("allow").GetNonNullString().Pipe(ulong.Parse);
        var deny = json.GetProperty("deny").GetNonNullString().Pipe(ulong.Parse);

        return new PermissionOverwrite(id, kind, allow, deny);
    }
}
