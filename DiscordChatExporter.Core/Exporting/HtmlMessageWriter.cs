using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord.Data;
using WebMarkupMin.Core;

namespace DiscordChatExporter.Core.Exporting;

internal class HtmlMessageWriter(Stream stream, ExportContext context, string themeName)
    : MessageWriter(stream, context)
{
    private readonly TextWriter _writer = new StreamWriter(stream);

    private readonly HtmlMinifier _minifier = new();
    private readonly List<Message> _messageGroup = [];

    public HtmlPageFeatures PageFeatures { get; private set; }

    public string ThemeName { get; } = themeName;

    // Note: in reverse order, last message appears earlier than the first message
    private bool CanJoinGroup(Message message)
    {
        // If the group is empty, any message can join it
        if (_messageGroup.LastOrDefault() is not { } lastMessage)
            return true;

        // Reply-like messages cannot join existing groups because they need to appear first
        if (message.IsReplyLike)
            return false;

        // Grouping for system notifications
        if (message.IsSystemNotification)
        {
            // Can only be grouped with other system notifications
            if (!lastMessage.IsSystemNotification)
                return false;
        }
        // Grouping for normal messages
        else
        {
            // Can only be grouped with other normal messages
            if (lastMessage.IsSystemNotification)
                return false;

            // Messages must be within 7 minutes of each other
            if ((message.Timestamp - lastMessage.Timestamp).Duration().TotalMinutes > 7)
                return false;

            // Messages must be sent by the same author
            if (message.Author.Id != lastMessage.Author.Id)
                return false;

            // If the author changed their name after the last message, their new messages
            // cannot join the existing group.
            if (
                !string.Equals(
                    message.Author.FullName,
                    lastMessage.Author.FullName,
                    StringComparison.Ordinal
                )
            )
                return false;
        }

        return true;
    }

    // Use <!--wmm:ignore--> to preserve blocks of code inside the templates
    private string Minify(string html) => _minifier.Minify(html, false).MinifiedContent;

    public override async ValueTask WritePreambleAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _writer.WriteLineAsync(
            Minify(
                await new PreambleTemplate { Context = Context, ThemeName = ThemeName }.RenderAsync(
                    cancellationToken
                )
            )
        );
    }

    private async ValueTask WriteMessageGroupAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default
    )
    {
        await _writer.WriteLineAsync(
            Minify(
                await new MessageGroupTemplate
                {
                    Context = Context,
                    Messages = messages,
                }.RenderAsync(cancellationToken)
            )
        );
    }

    public override async ValueTask WriteMessageAsync(
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        PageFeatures |= GetPageFeatures(message);
        await base.WriteMessageAsync(message, cancellationToken);

        // If the message can be grouped, buffer it for now
        if (CanJoinGroup(message))
        {
            _messageGroup.Add(message);
        }
        // Otherwise, flush the group and render messages
        else
        {
            await WriteMessageGroupAsync(_messageGroup, cancellationToken);

            _messageGroup.Clear();
            _messageGroup.Add(message);
        }
    }

    private HtmlPageFeatures GetPageFeatures(Message message)
    {
        var features = HtmlPageFeatures.None;

        if (Context.Request.ShouldFormatMarkdown)
        {
            features |= GetMarkdownFeatures(message.Content);
            features |= GetMarkdownFeatures(message.ForwardedMessage?.Content);
            features |= GetMarkdownFeatures(message.ReferencedMessage?.Content);

            foreach (var embed in message.Embeds)
            {
                features |= GetMarkdownFeatures(embed.Title);
                features |= GetMarkdownFeatures(embed.Description);

                foreach (var field in embed.Fields)
                {
                    features |= GetMarkdownFeatures(field.Name);
                    features |= GetMarkdownFeatures(field.Value);
                }
            }
        }

        if (
            message.Stickers.Any(s => s.Format == StickerFormat.Lottie)
            || message.ForwardedMessage?.Stickers.Any(s => s.Format == StickerFormat.Lottie) == true
        )
        {
            features |= HtmlPageFeatures.LottieStickers;
        }

        return features;
    }

    private static HtmlPageFeatures GetMarkdownFeatures(string? markdown) =>
        !string.IsNullOrWhiteSpace(markdown) && markdown.Contains("```", StringComparison.Ordinal)
            ? HtmlPageFeatures.HighlightCode
            : HtmlPageFeatures.None;

    public override async ValueTask WritePostambleAsync(
        CancellationToken cancellationToken = default
    )
    {
        // Flush current message group
        if (_messageGroup.Any())
            await WriteMessageGroupAsync(_messageGroup, cancellationToken);

        await _writer.WriteLineAsync(
            Minify(
                await new PostambleTemplate
                {
                    Context = Context,
                    MessagesWritten = MessagesWritten,
                }.RenderAsync(cancellationToken)
            )
        );
    }

    public override async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await base.DisposeAsync();
    }
}
