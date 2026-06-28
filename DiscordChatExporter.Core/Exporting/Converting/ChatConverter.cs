using System;
using System.Threading;
using System.Threading.Tasks;
using Gress;

namespace DiscordChatExporter.Core.Exporting.Converting;

public class ChatConverter
{
    public async ValueTask ConvertAsync(
        ExportedChat chat,
        ExportRequest request,
        IProgress<Percentage>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var context = new ExportContext(null, request, chat.Members, chat.Roles);

        // Initialize the exporter before further checks to ensure the file is created even if
        // the chat does not contain any messages.
        await using var messageExporter = new MessageExporter(context);

        for (var i = 0; i < chat.Messages.Count; i++)
        {
            var message = chat.Messages[i];

            if (request.MessageFilter.IsMatch(message))
                await messageExporter.ExportMessageAsync(message, cancellationToken);

            progress?.Report(Percentage.FromFraction((i + 1.0) / chat.Messages.Count));
        }
    }

    public async ValueTask ConvertAsync(
        System.Collections.Generic.IReadOnlyList<
            Func<CancellationToken, ValueTask<ExportedChat>>
        > chatProviders,
        ExportRequest request,
        IProgress<Percentage>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (chatProviders.Count <= 0)
            return;

        // Load the first chat to initialize context
        var firstChat = await chatProviders[0](cancellationToken);
        var context = new ExportContext(null, request, firstChat.Members, firstChat.Roles);

        await using var messageExporter = new MessageExporter(context);

        for (var fileIndex = 0; fileIndex < chatProviders.Count; fileIndex++)
        {
            var chat =
                fileIndex == 0 ? firstChat : await chatProviders[fileIndex](cancellationToken);

            for (var i = 0; i < chat.Messages.Count; i++)
            {
                var message = chat.Messages[i];

                if (request.MessageFilter.IsMatch(message))
                    await messageExporter.ExportMessageAsync(message, cancellationToken);
            }

            progress?.Report(Percentage.FromFraction((fileIndex + 1.0) / chatProviders.Count));
        }
    }
}
