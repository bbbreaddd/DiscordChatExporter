using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Embeds;
using DiscordChatExporter.Core.Markdown.Parsing;
using JsonExtensions.Writing;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal class JsonMessageWriter(Stream stream, ExportContext context)
    : MessageWriter(stream, context)
{
    private readonly Utf8JsonWriter _writer = new(
        stream,
        new JsonWriterOptions
        {
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/450
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = true,
            // Validation errors may mask actual failures
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/413
            SkipValidation = true,
        }
    );

    private async ValueTask<string> FormatMarkdownAsync(
        string markdown,
        CancellationToken cancellationToken = default
    ) =>
        Context.Request.ShouldFormatMarkdown
            ? await PlainTextMarkdownVisitor.FormatAsync(Context, markdown, cancellationToken)
            : markdown;

    private async ValueTask WriteAssetUrlAsync(
        string propertyName,
        string url,
        string? localPathPropertyName = null,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteString(
            propertyName,
            await Context.ResolveAssetUrlAsync(url, cancellationToken)
        );

        if (!string.IsNullOrWhiteSpace(localPathPropertyName))
        {
            var localPath = await Context.TryGetCachedAssetLocalPathAsync(url, cancellationToken);
            if (!string.IsNullOrWhiteSpace(localPath))
                _writer.WriteString(localPathPropertyName, localPath);
        }
    }

    private async ValueTask WriteUserAsync(
        User user,
        bool includeRoles = true,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("id", user.Id.ToString());
        _writer.WriteString("name", user.Name);
        _writer.WriteString("discriminator", user.DiscriminatorFormatted);

        _writer.WriteString(
            "nickname",
            Context.TryGetMember(user.Id)?.DisplayName ?? user.DisplayName
        );

        _writer.WriteString("color", Context.TryGetUserColor(user.Id)?.ToHexString());
        _writer.WriteBoolean("isBot", user.IsBot);

        if (includeRoles)
        {
            _writer.WritePropertyName("roles");
            await WriteRolesAsync(Context.GetUserRoles(user.Id), cancellationToken);
        }

        await WriteAssetUrlAsync(
            "avatarUrl",
            Context.TryGetMember(user.Id)?.AvatarUrl ?? user.AvatarUrl,
            "avatarLocalPath",
            cancellationToken
        );

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmojiAsync(
        Emoji emoji,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("id", emoji.Id.ToString());
        _writer.WriteString("name", emoji.Name);
        _writer.WriteString("code", emoji.Code);
        _writer.WriteBoolean("isAnimated", emoji.IsAnimated);
        _writer.WriteString(
            "imageUrl",
            await Context.ResolveAssetUrlAsync(emoji.ImageUrl, cancellationToken)
        );

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteRolesAsync(
        IReadOnlyList<Role> roles,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartArray();

        foreach (var role in roles)
        {
            _writer.WriteStartObject();

            _writer.WriteString("id", role.Id.ToString());
            _writer.WriteString("name", role.Name);
            _writer.WriteString("color", role.Color?.ToHexString());
            _writer.WriteNumber("position", role.Position);

            _writer.WriteEndObject();
        }

        _writer.WriteEndArray();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteAttachmentAsync(
        Attachment attachment,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("id", attachment.Id.ToString());
        await WriteAssetUrlAsync("url", attachment.Url, "localPath", cancellationToken);

        _writer.WriteString("fileName", attachment.FileName);
        _writer.WriteNumber("fileSizeBytes", attachment.FileSize.TotalBytes);

        _writer.WriteEndObject();
    }

    private async ValueTask WriteEmbedAuthorAsync(
        EmbedAuthor embedAuthor,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("name", embedAuthor.Name);
        _writer.WriteString("url", embedAuthor.Url);

        if (!string.IsNullOrWhiteSpace(embedAuthor.IconUrl))
        {
            await WriteAssetUrlAsync(
                "iconUrl",
                embedAuthor.IconProxyUrl ?? embedAuthor.IconUrl,
                "iconLocalPath",
                cancellationToken
            );

            _writer.WriteString("iconCanonicalUrl", embedAuthor.IconUrl);
        }

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmbedImageAsync(
        EmbedImage embedImage,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        if (!string.IsNullOrWhiteSpace(embedImage.Url))
        {
            await WriteAssetUrlAsync(
                "url",
                embedImage.ProxyUrl ?? embedImage.Url,
                "localPath",
                cancellationToken
            );

            _writer.WriteString("canonicalUrl", embedImage.Url);
        }

        _writer.WriteNumber("width", embedImage.Width);
        _writer.WriteNumber("height", embedImage.Height);

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmbedVideoAsync(
        EmbedVideo embedVideo,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        if (!string.IsNullOrWhiteSpace(embedVideo.Url))
        {
            await WriteAssetUrlAsync(
                "url",
                embedVideo.ProxyUrl ?? embedVideo.Url,
                "localPath",
                cancellationToken
            );

            _writer.WriteString("canonicalUrl", embedVideo.Url);
        }

        _writer.WriteNumber("width", embedVideo.Width);
        _writer.WriteNumber("height", embedVideo.Height);

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmbedFooterAsync(
        EmbedFooter embedFooter,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("text", embedFooter.Text);

        if (!string.IsNullOrWhiteSpace(embedFooter.IconUrl))
        {
            await WriteAssetUrlAsync(
                "iconUrl",
                embedFooter.IconProxyUrl ?? embedFooter.IconUrl,
                "iconLocalPath",
                cancellationToken
            );

            _writer.WriteString("iconCanonicalUrl", embedFooter.IconUrl);
        }

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmbedFieldAsync(
        EmbedField embedField,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("name", await FormatMarkdownAsync(embedField.Name, cancellationToken));
        _writer.WriteString(
            "value",
            await FormatMarkdownAsync(embedField.Value, cancellationToken)
        );
        _writer.WriteBoolean("isInline", embedField.IsInline);

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmbedAsync(
        Embed embed,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString(
            "title",
            await FormatMarkdownAsync(embed.Title ?? "", cancellationToken)
        );
        _writer.WriteString("url", embed.Url);
        _writer.WriteString("timestamp", embed.Timestamp?.Pipe(Context.NormalizeDate));
        _writer.WriteString(
            "description",
            await FormatMarkdownAsync(embed.Description ?? "", cancellationToken)
        );

        if (embed.Color is not null)
            _writer.WriteString("color", embed.Color.Value.ToHexString());

        if (embed.Author is not null)
        {
            _writer.WritePropertyName("author");
            await WriteEmbedAuthorAsync(embed.Author, cancellationToken);
        }

        if (embed.Thumbnail is not null)
        {
            _writer.WritePropertyName("thumbnail");
            await WriteEmbedImageAsync(embed.Thumbnail, cancellationToken);
        }

        if (embed.Image is not null)
        {
            _writer.WritePropertyName("image");
            await WriteEmbedImageAsync(embed.Image, cancellationToken);
        }

        if (embed.Video is not null)
        {
            _writer.WritePropertyName("video");
            await WriteEmbedVideoAsync(embed.Video, cancellationToken);
        }

        if (embed.Footer is not null)
        {
            _writer.WritePropertyName("footer");
            await WriteEmbedFooterAsync(embed.Footer, cancellationToken);
        }

        // Images
        _writer.WriteStartArray("images");

        foreach (var image in embed.Images)
            await WriteEmbedImageAsync(image, cancellationToken);

        _writer.WriteEndArray();

        // Fields
        _writer.WriteStartArray("fields");

        foreach (var field in embed.Fields)
            await WriteEmbedFieldAsync(field, cancellationToken);

        _writer.WriteEndArray();

        // Inline emoji
        _writer.WriteStartArray("inlineEmojis");

        if (!string.IsNullOrWhiteSpace(embed.Description))
        {
            foreach (
                var emoji in MarkdownParser
                    .ExtractEmojis(embed.Description)
                    .DistinctBy(e => e.Name, StringComparer.Ordinal)
            )
            {
                await WriteEmojiAsync(
                    new Emoji(emoji.Id, emoji.Name, emoji.IsAnimated),
                    cancellationToken
                );
            }
        }

        _writer.WriteEndArray();

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteStickerAsync(
        Sticker sticker,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("id", sticker.Id.ToString());
        _writer.WriteString("name", sticker.Name);
        _writer.WriteString("format", sticker.Format.ToString());
        await WriteAssetUrlAsync(
            "sourceUrl",
            sticker.SourceUrl,
            "sourceLocalPath",
            cancellationToken
        );

        _writer.WriteEndObject();
    }

    public override async ValueTask WritePreambleAsync(
        CancellationToken cancellationToken = default
    )
    {
        // Root object (start)
        _writer.WriteStartObject();

        // Guild
        _writer.WriteStartObject("guild");
        _writer.WriteString("id", Context.Request.Guild.Id.ToString());
        _writer.WriteString("name", Context.Request.Guild.Name);

        await WriteAssetUrlAsync(
            "iconUrl",
            Context.Request.Guild.IconUrl,
            "iconLocalPath",
            cancellationToken
        );

        _writer.WriteEndObject();

        // Channel
        _writer.WriteStartObject("channel");
        _writer.WriteString("id", Context.Request.Channel.Id.ToString());
        _writer.WriteString("type", Context.Request.Channel.Kind.ToString());

        // Original schema did not account for threads, so 'category' actually refers to the parent channel
        _writer.WriteString("categoryId", Context.Request.Channel.Parent?.Id.ToString());
        _writer.WriteString("category", Context.Request.Channel.Parent?.Name);

        // For threads, the parent channel above may itself belong to a category. Store it
        // separately so that the full hierarchy can be reconstructed later (e.g. by the
        // 'convert' command).
        _writer.WriteString(
            "parentCategoryId",
            Context.Request.Channel.Parent?.Parent?.Id.ToString()
        );
        _writer.WriteString("parentCategory", Context.Request.Channel.Parent?.Parent?.Name);

        _writer.WriteString("name", Context.Request.Channel.Name);
        _writer.WriteString("topic", Context.Request.Channel.Topic);

        if (!string.IsNullOrWhiteSpace(Context.Request.Channel.IconUrl))
        {
            await WriteAssetUrlAsync(
                "iconUrl",
                Context.Request.Channel.IconUrl,
                "iconLocalPath",
                cancellationToken
            );
        }

        _writer.WriteStartArray("permissionOverwrites");
        foreach (var overwrite in Context.Request.Channel.PermissionOverwrites)
        {
            _writer.WriteStartObject();
            _writer.WriteString("id", overwrite.Id.ToString());
            _writer.WriteString("type", overwrite.Kind.ToString());
            _writer.WriteString("allow", overwrite.Allow.ToString());
            _writer.WriteString("deny", overwrite.Deny.ToString());
            _writer.WriteEndObject();
        }
        _writer.WriteEndArray();

        _writer.WriteEndObject();

        // Date range
        _writer.WriteStartObject("dateRange");
        _writer.WriteString("after", Context.Request.After?.ToDate().Pipe(Context.NormalizeDate));
        _writer.WriteString("before", Context.Request.Before?.ToDate().Pipe(Context.NormalizeDate));
        _writer.WriteEndObject();

        // Timestamp
        _writer.WriteString("exportedAt", Context.NormalizeDate(DateTimeOffset.UtcNow));

        // Message array (start)
        _writer.WriteStartArray("messages");
        await _writer.FlushAsync(cancellationToken);
    }

    public override async ValueTask WriteMessageAsync(
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        await base.WriteMessageAsync(message, cancellationToken);

        _writer.WriteStartObject();

        // Metadata
        _writer.WriteString("id", message.Id.ToString());
        _writer.WriteString("type", message.Kind.ToString());
        _writer.WriteString("timestamp", Context.NormalizeDate(message.Timestamp));
        _writer.WriteString(
            "timestampEdited",
            message.EditedTimestamp?.Pipe(Context.NormalizeDate)
        );
        _writer.WriteString(
            "callEndedTimestamp",
            message.CallEndedTimestamp?.Pipe(Context.NormalizeDate)
        );
        _writer.WriteBoolean("isPinned", message.IsPinned);

        // Content
        if (message.IsSystemNotification)
        {
            _writer.WriteString("content", message.GetFallbackContent());
        }
        else
        {
            _writer.WriteString(
                "content",
                await FormatMarkdownAsync(message.Content, cancellationToken)
            );
        }

        // Author
        _writer.WritePropertyName("author");
        await WriteUserAsync(message.Author, true, cancellationToken);

        // Attachments
        _writer.WriteStartArray("attachments");

        foreach (var attachment in message.Attachments)
            await WriteAttachmentAsync(attachment, cancellationToken);

        _writer.WriteEndArray();

        // Embeds
        _writer.WriteStartArray("embeds");

        foreach (var embed in message.Embeds)
            await WriteEmbedAsync(embed, cancellationToken);

        _writer.WriteEndArray();

        // Stickers
        _writer.WriteStartArray("stickers");

        foreach (var sticker in message.Stickers)
            await WriteStickerAsync(sticker, cancellationToken);

        _writer.WriteEndArray();

        // Reactions
        _writer.WriteStartArray("reactions");

        foreach (var reaction in message.Reactions)
        {
            _writer.WriteStartObject();

            // Emoji
            _writer.WritePropertyName("emoji");
            await WriteEmojiAsync(reaction.Emoji, cancellationToken);

            _writer.WriteNumber("count", reaction.Count);

            // Reaction authors
            _writer.WriteStartArray("users");

            if (reaction.Users is not null)
            {
                foreach (var user in reaction.Users)
                    await WriteUserAsync(user, false, cancellationToken);
            }
            else if (Context.Discord is not null)
            {
                await foreach (
                    var user in Context.Discord.GetMessageReactionsAsync(
                        Context.Request.Channel.Id,
                        message.Id,
                        reaction.Emoji,
                        cancellationToken
                    )
                )
                {
                    await WriteUserAsync(user, false, cancellationToken);
                }
            }

            _writer.WriteEndArray();

            _writer.WriteEndObject();
        }

        _writer.WriteEndArray();

        // Mentions
        _writer.WriteStartArray("mentions");
        foreach (var user in message.MentionedUsers)
            await WriteUserAsync(user, true, cancellationToken);

        _writer.WriteEndArray();

        // Message reference
        if (message.Reference is not null)
        {
            _writer.WriteStartObject("reference");
            _writer.WriteString("type", message.Reference.Kind.ToString());
            _writer.WriteString("messageId", message.Reference.MessageId?.ToString());
            _writer.WriteString("channelId", message.Reference.ChannelId?.ToString());
            _writer.WriteString("guildId", message.Reference.GuildId?.ToString());
            _writer.WriteEndObject();
        }

        // Forwarded message
        if (message.ForwardedMessage is not null)
        {
            _writer.WriteStartObject("forwardedMessage");

            _writer.WriteString(
                "timestamp",
                Context.NormalizeDate(message.ForwardedMessage.Timestamp)
            );

            _writer.WriteString(
                "timestampEdited",
                message.ForwardedMessage.EditedTimestamp?.Pipe(Context.NormalizeDate)
            );

            _writer.WriteString(
                "content",
                await FormatMarkdownAsync(message.ForwardedMessage.Content, cancellationToken)
            );

            // Forwarded attachments
            _writer.WriteStartArray("attachments");

            foreach (var attachment in message.ForwardedMessage.Attachments)
                await WriteAttachmentAsync(attachment, cancellationToken);

            _writer.WriteEndArray();

            // Forwarded embeds
            _writer.WriteStartArray("embeds");
            foreach (var embed in message.ForwardedMessage.Embeds)
                await WriteEmbedAsync(embed, cancellationToken);
            _writer.WriteEndArray();

            // Forwarded stickers
            _writer.WriteStartArray("stickers");

            foreach (var sticker in message.ForwardedMessage.Stickers)
                await WriteStickerAsync(sticker, cancellationToken);

            _writer.WriteEndArray();

            _writer.WriteEndObject();
        }

        // Interaction
        if (message.Interaction is not null)
        {
            _writer.WriteStartObject("interaction");

            _writer.WriteString("id", message.Interaction.Id.ToString());
            _writer.WriteString("name", message.Interaction.Name);

            _writer.WritePropertyName("user");
            await WriteUserAsync(message.Interaction.User, true, cancellationToken);

            _writer.WriteEndObject();
        }

        // Inline emoji
        _writer.WriteStartArray("inlineEmojis");

        foreach (
            var emoji in MarkdownParser
                .ExtractEmojis(message.Content)
                .DistinctBy(e => e.Name, StringComparer.Ordinal)
        )
        {
            await WriteEmojiAsync(
                new Emoji(emoji.Id, emoji.Name, emoji.IsAnimated),
                cancellationToken
            );
        }

        _writer.WriteEndArray();

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    public override async ValueTask WritePostambleAsync(
        CancellationToken cancellationToken = default
    )
    {
        // Message array (end)
        _writer.WriteEndArray();

        _writer.WriteNumber("messageCount", MessagesWritten);

        // Root object (end)
        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await base.DisposeAsync();
    }
}
