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
        // The converted output's path is derived from the guild/category/channel's current
        // name, which can drift from a previous run if any of those got renamed on Discord in
        // the meantime. If nothing exists yet at the freshly-computed path, relocate whatever
        // was previously converted for this channel ID (even under a template directory like
        // "%G/%T/%C/" that moved entirely) instead of leaving a stale, disconnected copy behind.
        ExistingOutputRelocator.RelocateIfNeeded(request);

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
}
