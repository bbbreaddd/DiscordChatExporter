using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
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

        try
        {
            for (var i = 0; i < chat.Messages.Count; i++)
            {
                var message = chat.Messages[i];

                if (request.MessageFilter.IsMatch(message))
                    await messageExporter.ExportMessageAsync(message, cancellationToken);

                progress?.Report(Percentage.FromFraction((i + 1.0) / chat.Messages.Count));
            }
        }
        catch
        {
            // The output is always fully regenerable from the source chat, so the repaired
            // partial is allowed to overwrite whatever (possibly complete, now-stale) output
            // exists from a previous run -- a future run just reconverts the rest.
            messageExporter.Abandon(allowOverwrite: true);
            throw;
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

        try
        {
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
        catch
        {
            messageExporter.Abandon(allowOverwrite: true);
            throw;
        }
    }

    public async ValueTask ConvertStreamingAsync(
        IReadOnlyList<string> groupFilePaths,
        ExportRequest request,
        Func<string, Func<string, string>?> getRebaseLocalAssetPath,
        IProgress<Percentage>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (groupFilePaths.Count <= 0)
            return;

        // Pass 1: Collect members and roles from all files in the group sequentially, and count total messages
        var members = new Dictionary<Snowflake, Member>();
        var roles = new Dictionary<Snowflake, Role>();
        var totalMessages = 0;
        var filesMetadata = new List<(string FilePath, int MessageCount)>();

        foreach (var filePath in groupFilePaths)
        {
            var rebaseLocalAssetPath = getRebaseLocalAssetPath(filePath);

            var (fileMembers, fileRoles, fileCount) =
                await ExportedChatParser.CollectMetadataAndCountStreamingAsync(
                    filePath,
                    rebaseLocalAssetPath,
                    cancellationToken
                );

            foreach (var (k, v) in fileMembers)
                members[k] = v;
            foreach (var (k, v) in fileRoles)
                roles[k] = v;

            totalMessages += fileCount;
            filesMetadata.Add((filePath, fileCount));
        }

        // Pass 2: Stream messages one-by-one and write them directly to the exporter
        var context = new ExportContext(null, request, members, roles);
        await using var messageExporter = new MessageExporter(context);

        try
        {
            var messagesProcessed = 0;
            foreach (var (filePath, fileCount) in filesMetadata)
            {
                var rebaseLocalAssetPath = getRebaseLocalAssetPath(filePath);

                await foreach (
                    var messageJson in ExportedChatParser.StreamMessagesAsync(
                        filePath,
                        cancellationToken
                    )
                )
                {
                    var message = ExportedMessageParser.ParseMessage(
                        messageJson,
                        rebaseLocalAssetPath
                    );

                    if (request.MessageFilter.IsMatch(message))
                        await messageExporter.ExportMessageAsync(message, cancellationToken);

                    messagesProcessed++;
                    progress?.Report(
                        Percentage.FromFraction(
                            (double)messagesProcessed / Math.Max(1, totalMessages)
                        )
                    );
                }
            }
        }
        catch
        {
            messageExporter.Abandon(allowOverwrite: true);
            throw;
        }
    }
}
