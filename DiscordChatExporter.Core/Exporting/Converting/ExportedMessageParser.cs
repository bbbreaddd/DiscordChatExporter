using System;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Discord.Data.Embeds;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting.Converting;

// Parses domain objects from the JSON export schema (as written by JsonMessageWriter),
// which is distinct from the live Discord API schema parsed by Discord/Data/*.Parse(JsonElement).
internal static class ExportedMessageParser
{
    private static string? RebaseLocalAssetPath(
        string? localPath,
        Func<string, string>? rebaseLocalAssetPath = null
    ) =>
        !string.IsNullOrWhiteSpace(localPath) && rebaseLocalAssetPath is not null
            ? rebaseLocalAssetPath(localPath)
            : localPath;

    public static User ParseUser(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var isBot = json.GetPropertyOrNull("isBot")?.GetBooleanOrNull() ?? false;

        // JsonMessageWriter always writes a 4-digit discriminator string, defaulting to
        // "0000" for users without a legacy discriminator. NullIfDefault() reverses this,
        // matching the behavior of the live API parser.
        var discriminator = json.GetProperty("discriminator")
            .GetNonWhiteSpaceString()
            .Pipe(int.Parse)
            .NullIfDefault();

        var name = json.GetProperty("name").GetNonNullString();
        var displayName =
            json.GetPropertyOrNull("nickname")?.GetNonWhiteSpaceStringOrNull() ?? name;
        var avatarUrl =
            RebaseLocalAssetPath(
                json.GetPropertyOrNull("avatarLocalPath")?.GetNonWhiteSpaceStringOrNull(),
                rebaseLocalAssetPath
            ) ?? json.GetProperty("avatarUrl").GetNonWhiteSpaceString();

        return new User(id, isBot, discriminator, name, displayName, avatarUrl);
    }

    public static Role ParseRole(JsonElement json)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var name = json.GetProperty("name").GetNonNullString();
        var position = json.GetProperty("position").GetInt32();

        var color = json.GetPropertyOrNull("color")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(ColorTranslator.FromHtml);

        return new Role(id, name, position, color);
    }

    public static Emoji ParseEmoji(JsonElement json)
    {
        var id = json.GetPropertyOrNull("id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        var name =
            json.GetPropertyOrNull("name")?.GetNonWhiteSpaceStringOrNull() ?? "Unknown Emoji";

        var isAnimated = json.GetPropertyOrNull("isAnimated")?.GetBooleanOrNull() ?? false;

        return new Emoji(id, name, isAnimated);
    }

    public static Attachment ParseAttachment(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var url =
            RebaseLocalAssetPath(
                json.GetPropertyOrNull("localPath")?.GetNonWhiteSpaceStringOrNull(),
                rebaseLocalAssetPath
            ) ?? json.GetProperty("url").GetNonWhiteSpaceString();
        var fileName = json.GetProperty("fileName").GetNonNullString();
        var fileSize = json.GetProperty("fileSizeBytes").GetInt64().Pipe(FileSize.FromBytes);

        // Not present in the export schema
        return new Attachment(id, url, fileName, null, null, null, fileSize);
    }

    public static EmbedAuthor ParseEmbedAuthor(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var name = json.GetPropertyOrNull("name")?.GetStringOrNull();
        var url = json.GetPropertyOrNull("url")?.GetNonWhiteSpaceStringOrNull();
        var iconUrl =
            RebaseLocalAssetPath(
                json.GetPropertyOrNull("iconLocalPath")?.GetNonWhiteSpaceStringOrNull(),
                rebaseLocalAssetPath
            ) ?? json.GetPropertyOrNull("iconUrl")?.GetNonWhiteSpaceStringOrNull();

        return new EmbedAuthor(name, url, iconUrl, null);
    }

    public static EmbedImage ParseEmbedImage(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var url =
            RebaseLocalAssetPath(
                json.GetPropertyOrNull("localPath")?.GetNonWhiteSpaceStringOrNull(),
                rebaseLocalAssetPath
            ) ?? json.GetPropertyOrNull("url")?.GetNonWhiteSpaceStringOrNull();
        var width = json.GetPropertyOrNull("width")?.GetInt32OrNull();
        var height = json.GetPropertyOrNull("height")?.GetInt32OrNull();

        return new EmbedImage(url, null, width, height);
    }

    public static EmbedVideo ParseEmbedVideo(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var url =
            RebaseLocalAssetPath(
                json.GetPropertyOrNull("localPath")?.GetNonWhiteSpaceStringOrNull(),
                rebaseLocalAssetPath
            ) ?? json.GetPropertyOrNull("url")?.GetNonWhiteSpaceStringOrNull();
        var width = json.GetPropertyOrNull("width")?.GetInt32OrNull();
        var height = json.GetPropertyOrNull("height")?.GetInt32OrNull();

        return new EmbedVideo(url, null, width, height);
    }

    public static EmbedFooter ParseEmbedFooter(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var text = json.GetProperty("text").GetNonNullString();
        var iconUrl =
            RebaseLocalAssetPath(
                json.GetPropertyOrNull("iconLocalPath")?.GetNonWhiteSpaceStringOrNull(),
                rebaseLocalAssetPath
            ) ?? json.GetPropertyOrNull("iconUrl")?.GetNonWhiteSpaceStringOrNull();

        return new EmbedFooter(text, iconUrl, null);
    }

    public static EmbedField ParseEmbedField(JsonElement json)
    {
        var name = json.GetProperty("name").GetNonNullString();
        var value = json.GetProperty("value").GetNonNullString();
        var isInline = json.GetPropertyOrNull("isInline")?.GetBooleanOrNull() ?? false;

        return new EmbedField(name, value, isInline);
    }

    public static Embed ParseEmbed(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var title = json.GetPropertyOrNull("title")?.GetNonWhiteSpaceStringOrNull();
        var url = json.GetPropertyOrNull("url")?.GetNonWhiteSpaceStringOrNull();
        var timestamp = json.GetPropertyOrNull("timestamp")?.GetDateTimeOffsetOrNull();
        var description = json.GetPropertyOrNull("description")?.GetNonWhiteSpaceStringOrNull();

        var color = json.GetPropertyOrNull("color")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(ColorTranslator.FromHtml);

        var author = json.GetPropertyOrNull("author")
            ?.Pipe(j => ParseEmbedAuthor(j, rebaseLocalAssetPath));
        var thumbnail = json.GetPropertyOrNull("thumbnail")
            ?.Pipe(j => ParseEmbedImage(j, rebaseLocalAssetPath));
        var video = json.GetPropertyOrNull("video")
            ?.Pipe(j => ParseEmbedVideo(j, rebaseLocalAssetPath));
        var footer = json.GetPropertyOrNull("footer")
            ?.Pipe(j => ParseEmbedFooter(j, rebaseLocalAssetPath));

        var images =
            json.GetPropertyOrNull("images")
                ?.EnumerateArrayOrNull()
                ?.Select(j => ParseEmbedImage(j, rebaseLocalAssetPath))
                .ToArray()
            ?? [];

        var fields =
            json.GetPropertyOrNull("fields")
                ?.EnumerateArrayOrNull()
                ?.Select(ParseEmbedField)
                .ToArray()
            ?? [];

        // The export schema does not capture the embed's original type (rich/image/video/etc.),
        // so special embed projections (Spotify, YouTube, Twitch) won't be recognized.
        return new Embed(
            title,
            EmbedKind.Rich,
            url,
            timestamp,
            color,
            author,
            description,
            fields,
            thumbnail,
            images,
            video,
            footer
        );
    }

    public static Sticker ParseSticker(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var name = json.GetProperty("name").GetNonNullString();
        var format = json.GetProperty("format")
            .GetNonNullString()
            .Pipe(s => Enum.Parse<StickerFormat>(s));
        var sourceUrl =
            RebaseLocalAssetPath(
                json.GetPropertyOrNull("sourceLocalPath")?.GetNonWhiteSpaceStringOrNull(),
                rebaseLocalAssetPath
            ) ?? json.GetProperty("sourceUrl").GetNonWhiteSpaceString();

        return new Sticker(id, name, format, sourceUrl);
    }

    public static Reaction ParseReaction(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var emoji = json.GetProperty("emoji").Pipe(ParseEmoji);
        var count = json.GetProperty("count").GetInt32();

        var users = json.GetPropertyOrNull("users")
            ?.EnumerateArrayOrNull()
            ?.Select(j => ParseUser(j, rebaseLocalAssetPath))
            .ToArray();

        return new Reaction(emoji, count, users);
    }

    public static MessageReference ParseMessageReference(JsonElement json)
    {
        // Older exports don't include the reference type, default to a normal reply reference
        var kind =
            json.GetPropertyOrNull("type")
                ?.GetNonWhiteSpaceStringOrNull()
                ?.Pipe(s => Enum.Parse<MessageReferenceKind>(s))
            ?? MessageReferenceKind.Default;

        var messageId = json.GetPropertyOrNull("messageId")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        var channelId = json.GetPropertyOrNull("channelId")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        var guildId = json.GetPropertyOrNull("guildId")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        return new MessageReference(kind, messageId, channelId, guildId);
    }

    public static MessageSnapshot ParseMessageSnapshot(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var timestamp = json.GetProperty("timestamp").GetDateTimeOffset();
        var editedTimestamp = json.GetPropertyOrNull("timestampEdited")?.GetDateTimeOffsetOrNull();
        var content = json.GetProperty("content").GetNonNullString();

        var attachments =
            json.GetPropertyOrNull("attachments")
                ?.EnumerateArrayOrNull()
                ?.Select(j => ParseAttachment(j, rebaseLocalAssetPath))
                .ToArray()
            ?? [];

        var embeds =
            json.GetPropertyOrNull("embeds")
                ?.EnumerateArrayOrNull()
                ?.Select(j => ParseEmbed(j, rebaseLocalAssetPath))
                .ToArray()
            ?? [];

        var stickers =
            json.GetPropertyOrNull("stickers")
                ?.EnumerateArrayOrNull()
                ?.Select(j => ParseSticker(j, rebaseLocalAssetPath))
                .ToArray()
            ?? [];

        return new MessageSnapshot(
            timestamp,
            editedTimestamp,
            content,
            attachments,
            embeds,
            stickers
        );
    }

    public static Interaction ParseInteraction(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var name = json.GetProperty("name").GetNonNullString();
        var user = ParseUser(json.GetProperty("user"), rebaseLocalAssetPath);

        return new Interaction(id, name, user);
    }

    public static Message ParseMessage(
        JsonElement json,
        Func<string, string>? rebaseLocalAssetPath = null
    )
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var kind = json.GetProperty("type")
            .GetNonNullString()
            .Pipe(s => Enum.Parse<MessageKind>(s));

        var timestamp = json.GetProperty("timestamp").GetDateTimeOffset();
        var editedTimestamp = json.GetPropertyOrNull("timestampEdited")?.GetDateTimeOffsetOrNull();
        var callEndedTimestamp = json.GetPropertyOrNull("callEndedTimestamp")
            ?.GetDateTimeOffsetOrNull();

        var isPinned = json.GetPropertyOrNull("isPinned")?.GetBooleanOrNull() ?? false;
        var content = json.GetProperty("content").GetNonNullString();

        // JsonMessageWriter stores the rendered fallback text (e.g. "Changed the channel name:
        // <name>") as the content of system notifications. GetFallbackContent() embeds the raw
        // name into that same template, so strip the prefix back off to avoid doubling it up
        // when the message is re-rendered.
        if (kind == MessageKind.ChannelNameChange)
        {
            const string prefix = "Changed the channel name: ";
            content = content.StartsWith(prefix, StringComparison.Ordinal)
                ? content[prefix.Length..]
                : "";
        }

        var author = ParseUser(json.GetProperty("author"), rebaseLocalAssetPath);

        var attachments =
            json.GetPropertyOrNull("attachments")
                ?.EnumerateArrayOrNull()
                ?.Select(j => ParseAttachment(j, rebaseLocalAssetPath))
                .ToArray()
            ?? [];

        var embeds =
            json.GetPropertyOrNull("embeds")
                ?.EnumerateArrayOrNull()
                ?.Select(j => ParseEmbed(j, rebaseLocalAssetPath))
                .ToArray()
            ?? [];

        var stickers =
            json.GetPropertyOrNull("stickers")
                ?.EnumerateArrayOrNull()
                ?.Select(j => ParseSticker(j, rebaseLocalAssetPath))
                .ToArray()
            ?? [];

        var reactions =
            json.GetPropertyOrNull("reactions")
                ?.EnumerateArrayOrNull()
                ?.Select(j => ParseReaction(j, rebaseLocalAssetPath))
                .ToArray()
            ?? [];

        var mentionedUsers =
            json.GetPropertyOrNull("mentions")
                ?.EnumerateArrayOrNull()
                ?.Select(j => ParseUser(j, rebaseLocalAssetPath))
                .ToArray()
            ?? [];

        var reference = json.GetPropertyOrNull("reference")?.Pipe(ParseMessageReference);

        // The export schema doesn't include the full referenced message, only its reference
        var forwardedMessage = json.GetPropertyOrNull("forwardedMessage")
            ?.Pipe(j => ParseMessageSnapshot(j, rebaseLocalAssetPath));

        var interaction = json.GetPropertyOrNull("interaction")
            ?.Pipe(j => ParseInteraction(j, rebaseLocalAssetPath));

        return new Message(
            id,
            kind,
            MessageFlags.None,
            author,
            timestamp,
            editedTimestamp,
            callEndedTimestamp,
            isPinned,
            content,
            attachments,
            embeds,
            stickers,
            reactions,
            mentionedUsers,
            reference,
            null,
            forwardedMessage,
            interaction
        );
    }
}
