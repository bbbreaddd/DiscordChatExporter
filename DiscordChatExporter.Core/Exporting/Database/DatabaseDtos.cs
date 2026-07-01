using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Embeds;
using DiscordChatExporter.Core.Markdown;
using DiscordChatExporter.Core.Markdown.Parsing;

namespace DiscordChatExporter.Core.Exporting.Database;

// Plain serialization shapes for the JSON columns stored alongside the relational core of the
// database. Field names intentionally mirror the schema produced by JsonMessageWriter /
// consumed by ExportedMessageParser, so the JSON columns stay familiar and inspectable via
// json_extract() to anyone who has already seen a JSON export.

internal record RoleDto(string Id, string Name, string? Color, int Position);

internal record UserDto(
    string Id,
    string Name,
    string Discriminator,
    string? Nickname,
    string? Color,
    bool IsBot,
    string AvatarUrl,
    IReadOnlyList<RoleDto> Roles
);

internal record AttachmentDto(string Id, string Url, string FileName, long FileSizeBytes);

internal record EmbedAuthorDto(string? Name, string? Url, string? IconUrl);

internal record EmbedImageDto(string? Url, int? Width, int? Height);

internal record EmbedFooterDto(string Text, string? IconUrl);

internal record EmbedFieldDto(string Name, string Value, bool IsInline);

internal record EmbedDto(
    string? Title,
    string? Url,
    DateTimeOffset? Timestamp,
    string? Description,
    string? Color,
    EmbedAuthorDto? Author,
    EmbedImageDto? Thumbnail,
    EmbedImageDto? Video,
    EmbedFooterDto? Footer,
    IReadOnlyList<EmbedImageDto> Images,
    IReadOnlyList<EmbedFieldDto> Fields
);

internal record StickerDto(string Id, string Name, string Format, string SourceUrl);

internal record ReactionUserDto(
    string Id,
    string Name,
    string Discriminator,
    bool IsBot,
    string AvatarUrl
);

internal record MessageReferenceDto(
    string Type,
    string? MessageId,
    string? ChannelId,
    string? GuildId
);

internal record MessageSnapshotDto(
    DateTimeOffset Timestamp,
    DateTimeOffset? TimestampEdited,
    string Content,
    IReadOnlyList<AttachmentDto> Attachments,
    IReadOnlyList<EmbedDto> Embeds,
    IReadOnlyList<StickerDto> Stickers
);

internal record InteractionDto(string Id, string Name, UserDto User);

internal record EmojiDto(string? Id, string Name, string Code, bool IsAnimated, string ImageUrl);

// Reflection-based JSON serialization is disabled for the Cli app (it's published trimmed), so
// every type serialized into a database JSON column needs a source-generated type info entry.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RoleDto[]))]
[JsonSerializable(typeof(EmbedDto[]))]
[JsonSerializable(typeof(StickerDto[]))]
[JsonSerializable(typeof(ReactionUserDto[]))]
[JsonSerializable(typeof(MessageReferenceDto))]
[JsonSerializable(typeof(MessageSnapshotDto))]
[JsonSerializable(typeof(InteractionDto))]
[JsonSerializable(typeof(EmojiDto[]))]
internal partial class DatabaseJsonContext : JsonSerializerContext;

internal static class DatabaseJson
{
    private static string? ToHex(Color? color) =>
        color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null;

    // Used by the offline JSON importer, which only has a Member (role IDs) plus a
    // separately-collected role lookup table, not resolved Role objects.
    public static UserDto MapUser(
        User user,
        Member? member,
        IReadOnlyDictionary<Snowflake, Role> roles
    ) =>
        BuildUserDto(
            user,
            (member?.RoleIds ?? []).Select(id => roles.GetValueOrDefault(id)).WhereNotNull(),
            member?.DisplayName,
            member?.AvatarUrl
        );

    // Used by the live-export path, which resolves roles/nickname/avatar directly off an
    // ExportContext instead of collecting them from a JSON file.
    public static UserDto MapUser(
        User user,
        IReadOnlyList<Role> roles,
        string? nickname,
        string? avatarUrl
    ) => BuildUserDto(user, roles, nickname, avatarUrl);

    private static IEnumerable<Role> WhereNotNull(this IEnumerable<Role?> roles) =>
        roles.Where(r => r is not null)!;

    private static UserDto BuildUserDto(
        User user,
        IEnumerable<Role> roles,
        string? nickname,
        string? avatarUrl
    )
    {
        var roleDtos = roles
            .Select(r => new RoleDto(r.Id.ToString(), r.Name, ToHex(r.Color), r.Position))
            .ToArray();

        // Matches the client's "highest positioned role with a color wins" display rule.
        var effectiveColor = roleDtos
            .Where(r => r.Color is not null)
            .OrderByDescending(r => r.Position)
            .Select(r => r.Color)
            .FirstOrDefault();

        return new UserDto(
            user.Id.ToString(),
            user.Name,
            user.DiscriminatorFormatted,
            nickname ?? user.DisplayName,
            effectiveColor,
            user.IsBot,
            avatarUrl ?? user.AvatarUrl,
            roleDtos
        );
    }

    public static EmojiDto MapEmoji(EmojiNode node) =>
        new(node.Id?.ToString(), node.Name, node.Code, node.IsAnimated, node.ImageUrl);

    public static IReadOnlyList<EmojiDto> ExtractInlineEmojis(string content) =>
        MarkdownParser
            .ExtractEmojis(content)
            .DistinctBy(e => e.Name, StringComparer.Ordinal)
            .Select(MapEmoji)
            .ToArray();

    public static ReactionUserDto MapReactionUser(User user) =>
        new(user.Id.ToString(), user.Name, user.DiscriminatorFormatted, user.IsBot, user.AvatarUrl);

    public static AttachmentDto MapAttachment(Attachment attachment) =>
        new(
            attachment.Id.ToString(),
            attachment.Url,
            attachment.FileName,
            attachment.FileSize.TotalBytes
        );

    public static EmbedDto MapEmbed(Embed embed) =>
        new(
            embed.Title,
            embed.Url,
            embed.Timestamp,
            embed.Description,
            ToHex(embed.Color),
            embed.Author is { } author
                ? new EmbedAuthorDto(author.Name, author.Url, author.IconUrl)
                : null,
            embed.Thumbnail is { } thumbnail
                ? new EmbedImageDto(thumbnail.Url, thumbnail.Width, thumbnail.Height)
                : null,
            embed.Video is { } video
                ? new EmbedImageDto(video.Url, video.Width, video.Height)
                : null,
            embed.Footer is { } footer ? new EmbedFooterDto(footer.Text, footer.IconUrl) : null,
            embed.Images.Select(i => new EmbedImageDto(i.Url, i.Width, i.Height)).ToArray(),
            embed.Fields.Select(f => new EmbedFieldDto(f.Name, f.Value, f.IsInline)).ToArray()
        );

    public static StickerDto MapSticker(Sticker sticker) =>
        new(sticker.Id.ToString(), sticker.Name, sticker.Format.ToString(), sticker.SourceUrl);

    public static MessageReferenceDto MapReference(MessageReference reference) =>
        new(
            reference.Kind.ToString(),
            reference.MessageId?.ToString(),
            reference.ChannelId?.ToString(),
            reference.GuildId?.ToString()
        );

    public static MessageSnapshotDto MapForwardedMessage(MessageSnapshot snapshot) =>
        new(
            snapshot.Timestamp,
            snapshot.EditedTimestamp,
            snapshot.Content,
            snapshot.Attachments.Select(MapAttachment).ToArray(),
            snapshot.Embeds.Select(MapEmbed).ToArray(),
            snapshot.Stickers.Select(MapSticker).ToArray()
        );

    public static InteractionDto MapInteraction(Interaction interaction) =>
        new(interaction.Id.ToString(), interaction.Name, MapUser(interaction.User, [], null, null));

    // Returns a value suitable for direct use as a SqliteParameter value: DBNull.Value for a
    // null input, otherwise the serialized JSON string (boxed as object to match either case).
    public static object ToDbParam(MessageReferenceDto? value) =>
        value is null
            ? DBNull.Value
            : JsonSerializer.Serialize(value, DatabaseJsonContext.Default.MessageReferenceDto);

    public static object ToDbParam(MessageSnapshotDto? value) =>
        value is null
            ? DBNull.Value
            : JsonSerializer.Serialize(value, DatabaseJsonContext.Default.MessageSnapshotDto);

    public static object ToDbParam(InteractionDto? value) =>
        value is null
            ? DBNull.Value
            : JsonSerializer.Serialize(value, DatabaseJsonContext.Default.InteractionDto);

    public static string SerializeRoles(IReadOnlyList<RoleDto> values) =>
        values.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(values.ToArray(), DatabaseJsonContext.Default.RoleDtoArray);

    public static string SerializeEmbeds(IReadOnlyList<EmbedDto> values) =>
        values.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(values.ToArray(), DatabaseJsonContext.Default.EmbedDtoArray);

    public static string SerializeStickers(IReadOnlyList<StickerDto> values) =>
        values.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(
                values.ToArray(),
                DatabaseJsonContext.Default.StickerDtoArray
            );

    public static string SerializeReactionUsers(IReadOnlyList<ReactionUserDto> values) =>
        values.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(
                values.ToArray(),
                DatabaseJsonContext.Default.ReactionUserDtoArray
            );

    public static string SerializeInlineEmojis(IReadOnlyList<EmojiDto> values) =>
        values.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(values.ToArray(), DatabaseJsonContext.Default.EmojiDtoArray);
}
