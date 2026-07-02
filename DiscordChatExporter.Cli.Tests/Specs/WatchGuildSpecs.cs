using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Commands.Shared;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Database;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using DiscordChatExporter.Core.Utils;
using FluentAssertions;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class WatchGuildSpecs
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler { get; set; } =
            req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Handler(request);
        }
    }

    [Fact]
    public async Task Repeated_message_events_debounce_to_one_channel_export()
    {
        // Arrange
        var queue = new WatchGuildQueue(
            channelDebounce: TimeSpan.FromMilliseconds(50),
            patchDebounce: TimeSpan.FromMilliseconds(50),
            deleteDebounce: TimeSpan.FromMilliseconds(50)
        );
        var channelId = new Snowflake(123);

        // Act
        queue.EnqueueChannelExport(channelId, DateTimeOffset.UtcNow);
        queue.EnqueueChannelExport(channelId, DateTimeOffset.UtcNow);

        // Assert before delay
        queue.TryDequeue().Should().BeNull();

        // Wait for debounce
        await Task.Delay(100);

        // Assert after delay
        var item = queue.TryDequeue();
        item.Should().NotBeNull();
        item.Should().BeOfType<ExportChannelItem>();
        ((ExportChannelItem)item!).ChannelId.Should().Be(channelId);

        queue.TryDequeue().Should().BeNull();
    }

    [Fact]
    public async Task Repeated_patch_events_for_the_same_message_debounce_to_one_patch()
    {
        // Arrange
        var queue = new WatchGuildQueue(
            channelDebounce: TimeSpan.FromMilliseconds(50),
            patchDebounce: TimeSpan.FromMilliseconds(50),
            deleteDebounce: TimeSpan.FromMilliseconds(50)
        );
        var channelId = new Snowflake(123);
        var messageId = new Snowflake(456);

        // Act
        queue.EnqueuePatch(channelId, messageId, "REACTION_ADD");
        queue.EnqueuePatch(channelId, messageId, "REACTION_REMOVE");

        // Assert before delay
        queue.TryDequeue().Should().BeNull();

        // Wait for debounce
        await Task.Delay(100);

        // Assert after delay
        var item = queue.TryDequeue();
        item.Should().NotBeNull();
        item.Should().BeOfType<PatchMessageItem>();
        var patch = (PatchMessageItem)item!;
        patch.ChannelId.Should().Be(channelId);
        patch.MessageId.Should().Be(messageId);

        queue.TryDequeue().Should().BeNull();
    }

    [Fact]
    public async Task Deletes_batch_per_channel()
    {
        // Arrange
        var queue = new WatchGuildQueue(
            channelDebounce: TimeSpan.FromMilliseconds(50),
            patchDebounce: TimeSpan.FromMilliseconds(50),
            deleteDebounce: TimeSpan.FromMilliseconds(50)
        );
        var channelId = new Snowflake(123);
        var msg1 = new Snowflake(1);
        var msg2 = new Snowflake(2);

        // Act
        queue.EnqueueDelete(channelId, msg1);
        queue.EnqueueDelete(channelId, msg2);

        // Wait for debounce
        await Task.Delay(100);

        // Assert
        var item = queue.TryDequeue();
        item.Should().NotBeNull();
        item.Should().BeOfType<MarkDeletedItem>();
        var delete = (MarkDeletedItem)item!;
        delete.ChannelId.Should().Be(channelId);
        delete.MessageIds.Should().BeEquivalentTo([msg1, msg2]);

        queue.TryDequeue().Should().BeNull();
    }

    [Fact]
    public async Task Patches_and_deletes_are_selected_before_channel_exports()
    {
        // Arrange
        var queue = new WatchGuildQueue(
            channelDebounce: TimeSpan.FromMilliseconds(50),
            patchDebounce: TimeSpan.FromMilliseconds(50),
            deleteDebounce: TimeSpan.FromMilliseconds(50)
        );
        var channelId = new Snowflake(123);
        var messageId = new Snowflake(456);

        // Act
        queue.EnqueueChannelExport(channelId, DateTimeOffset.UtcNow);
        queue.EnqueuePatch(channelId, messageId, "REACTION_ADD");
        queue.EnqueueDelete(channelId, messageId);

        // Wait for debounce
        await Task.Delay(100);

        // Assert priority (Patches first, Deletes second, Exports third)
        var item1 = queue.TryDequeue();
        item1.Should().BeOfType<PatchMessageItem>();

        var item2 = queue.TryDequeue();
        item2.Should().BeOfType<MarkDeletedItem>();

        var item3 = queue.TryDequeue();
        item3.Should().BeOfType<ExportChannelItem>();
    }

    private static Message ParseTestMessage(long id) =>
        Message.Parse(
            JsonDocument
                .Parse(
                    $$"""
                    {
                      "id": "{{id}}",
                      "type": 0,
                      "content": "hi",
                      "channel_id": "123",
                      "author": { "id": "1", "username": "a", "discriminator": "0000", "avatar": null },
                      "attachments": [],
                      "embeds": [],
                      "pinned": false,
                      "timestamp": "2023-06-15T12:00:00+00:00"
                    }
                    """
                )
                .RootElement
        );

    [Fact]
    public void Live_message_upserts_are_selected_before_patches_and_exports()
    {
        // Arrange
        var queue = new WatchGuildQueue(
            channelDebounce: TimeSpan.FromMilliseconds(1),
            patchDebounce: TimeSpan.FromMilliseconds(1),
            deleteDebounce: TimeSpan.FromMilliseconds(1)
        );
        var channelId = new Snowflake(123);

        // Act: enqueue an export and a patch first, then a live message.
        queue.EnqueueChannelExport(channelId, DateTimeOffset.UtcNow);
        queue.EnqueuePatch(channelId, new Snowflake(2), "REACTION_ADD");
        queue.EnqueueMessageUpsert(channelId, ParseTestMessage(1));

        // Assert: the live message is dequeued first, with no debounce wait.
        queue.TryDequeue().Should().BeOfType<UpsertMessageItem>();
    }

    [Fact]
    public void Poll_vote_events_are_selected_before_debounced_patches()
    {
        var queue = new WatchGuildQueue(
            channelDebounce: TimeSpan.FromMilliseconds(50),
            patchDebounce: TimeSpan.FromMilliseconds(50),
            deleteDebounce: TimeSpan.FromMilliseconds(50)
        );

        queue.EnqueuePatch(new Snowflake(123), new Snowflake(456), "MESSAGE_UPDATE");
        queue.EnqueuePollVote(
            new Snowflake(123),
            new Snowflake(456),
            3,
            new Snowflake(789),
            true,
            "MESSAGE_POLL_VOTE_ADD"
        );

        queue.TryDequeue().Should().BeOfType<PollVoteItem>();
    }

    [Fact]
    public void Live_message_upsert_retries_then_gives_up()
    {
        var queue = new WatchGuildQueue();
        var item = new UpsertMessageItem(new Snowflake(123), ParseTestMessage(1));

        queue.ReportFailure(item).Should().BeTrue(); // attempt 0 -> re-queued as attempt 1
        var retry1 = queue.TryDequeue() as UpsertMessageItem;
        retry1!.Attempt.Should().Be(1);

        queue.ReportFailure(retry1).Should().BeTrue(); // attempt 1 -> re-queued as attempt 2
        var retry2 = queue.TryDequeue() as UpsertMessageItem;
        retry2!.Attempt.Should().Be(2);

        queue.ReportFailure(retry2).Should().BeFalse(); // attempt 2 -> gives up
    }

    [Fact]
    public void Queue_retries_failed_items_with_backoff_and_eventually_gives_up()
    {
        // Arrange
        var queue = new WatchGuildQueue();
        var channelId = new Snowflake(123);

        // Act
        queue.EnqueueChannelExport(channelId, isCatchup: true);
        var item = queue.TryDequeue();
        item.Should().NotBeNull();

        // 1st failure
        queue.ReportFailure(item!).Should().BeTrue();
        // Since due time is now + 60s, it's not due immediately.
        queue.TryDequeue().Should().BeNull();

        // Let's mock failure loop manually
        var patchItem = new PatchMessageItem(channelId, new Snowflake(456), "TEST");
        queue.ReportFailure(patchItem).Should().BeTrue(); // retry count 1
        queue.ReportFailure(patchItem).Should().BeTrue(); // retry count 2
        queue.ReportFailure(patchItem).Should().BeFalse(); // retry count 3 -> gives up!
    }

    [Fact]
    public async Task GetPinnedMessagesAsync_handles_paginated_objects_format()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        using var httpClient = new HttpClient(mockHandler);

        var paginatedPinsJson = """
            {
              "items": [
                {
                  "message": {
                    "id": "999",
                    "type": 0,
                    "content": "pinned content",
                    "channel_id": "300",
                    "author": {
                      "id": "1",
                      "username": "alice",
                      "discriminator": "0000",
                      "avatar": null
                    },
                    "attachments": [],
                    "embeds": [],
                    "pinned": true,
                    "timestamp": "2023-06-15T12:00:00+00:00"
                  },
                  "pinned_at": "2023-06-15T12:00:00+00:00"
                }
              ],
              "has_more": false
            }
            """;

        mockHandler.Handler = req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("channels/300/messages/pins"))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            paginatedPinsJson,
                            Encoding.UTF8,
                            "application/json"
                        ),
                    }
                );
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var discord = new DiscordClient("fake-token", httpClient: httpClient);

        // Act
        var pins = await discord.GetPinnedMessagesAsync(new Snowflake(300));

        // Assert
        pins.Should().HaveCount(1);
        pins[0].Id.Value.Should().Be(999);
        pins[0].Content.Should().Be("pinned content");
    }

    [Fact]
    public async Task Database_export_skips_when_last_message_id_matches_but_does_not_skip_on_forced_scan()
    {
        var mockHandler = new MockHttpMessageHandler();
        using var httpClient = new HttpClient(mockHandler);

        var guildJson = """
            {
              "id": "100",
              "name": "Test Guild",
              "icon": null
            }
            """;

        var channelJson = """
            {
              "id": "300",
              "type": 0,
              "guild_id": "100",
              "name": "general",
              "position": 1,
              "topic": null,
              "nsfw": false,
              "last_message_id": "999"
            }
            """;

        var messagesJson = """
            [
              {
                "id": "999",
                "type": 0,
                "content": "hello",
                "channel_id": "300",
                "author": {
                  "id": "1",
                  "username": "alice",
                  "discriminator": "0000",
                  "avatar": null
                },
                "attachments": [],
                "embeds": [],
                "pinned": false,
                "timestamp": "2023-06-15T12:00:00+00:00"
              }
            ]
            """;

        var emptyMessagesJson = "[]";

        var getMessagesCount = 0;

        mockHandler.Handler = req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("guilds/100"))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(guildJson, Encoding.UTF8, "application/json"),
                    }
                );
            }
            if (path.EndsWith("guilds/100/channels"))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            $"[{channelJson}]",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    }
                );
            }
            if (path.EndsWith("guilds/100/roles"))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                    }
                );
            }
            if (path.EndsWith("channels/300"))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(channelJson, Encoding.UTF8, "application/json"),
                    }
                );
            }
            if (path.EndsWith("channels/300/messages"))
            {
                getMessagesCount++;
                var query = req.RequestUri?.Query ?? "";
                if (query.Contains("after=999"))
                {
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                emptyMessagesJson,
                                Encoding.UTF8,
                                "application/json"
                            ),
                        }
                    );
                }
                else
                {
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                messagesJson,
                                Encoding.UTF8,
                                "application/json"
                            ),
                        }
                    );
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var discord = new DiscordClient("fake-token", httpClient: httpClient);
        using var dbTemp = TempFile.Create();
        var dbPath = dbTemp.Path;

        // Fetch guild & channel
        var guild = await discord.GetGuildAsync(new Snowflake(100));
        var channel = await discord.GetChannelAsync(new Snowflake(300));

        var exporter = new ChannelExporter(discord);

        // 1. First export: Should fetch and insert messages
        {
            await using var store = await SqliteExportStore.OpenAsync(dbPath);
            var request = new ExportRequest(
                guild,
                channel,
                dbPath,
                null,
                ExportFormat.Db,
                null,
                null,
                PartitionLimit.Null,
                MessageFilter.Null,
                false,
                true,
                false,
                false,
                null,
                false
            );
            await exporter.ExportChannelAsync(request, store);
        }

        getMessagesCount.Should().Be(3); // 1 for last message query + 1 for message page + 1 for empty page check

        // 2. Second export (normal): should skip because last_message_id (999) matches and ForceFullScan is false
        getMessagesCount = 0;
        {
            await using var store = await SqliteExportStore.OpenAsync(dbPath);
            var request = new ExportRequest(
                guild,
                channel,
                dbPath,
                null,
                ExportFormat.Db,
                null,
                null,
                PartitionLimit.Null,
                MessageFilter.Null,
                false,
                true,
                false,
                false,
                null,
                false
            );
            await exporter.ExportChannelAsync(request, store);
        }

        getMessagesCount.Should().Be(0); // SKIPPED!

        // 3. Third export (forced scan): should NOT skip even though last_message_id matches
        getMessagesCount = 0;
        {
            await using var store = await SqliteExportStore.OpenAsync(dbPath);
            var request = new ExportRequest(
                guild,
                channel,
                dbPath,
                null,
                ExportFormat.Db,
                null,
                null,
                PartitionLimit.Null,
                MessageFilter.Null,
                false,
                true,
                false,
                false,
                null,
                false,
                forceFullScan: true
            );
            await exporter.ExportChannelAsync(request, store);
        }

        getMessagesCount.Should().BeGreaterThan(0); // NOT skipped!
    }

    [Fact]
    public async Task Live_export_resumes_from_stored_cursor_and_does_not_skip_a_gap()
    {
        // Regression guard for the "after = live message timestamp" bug: a message that arrived
        // during a gap (e.g. a non-resumable reconnect) must still be fetched. The watcher must
        // resume from the channel's stored cursor, not from the newest message's timestamp.
        var mockHandler = new MockHttpMessageHandler();
        using var httpClient = new HttpClient(mockHandler);

        var guildJson = """
            { "id": "100", "name": "Test Guild", "icon": null }
            """;

        // Channel currently advertises a much newer last_message_id (3000) than what the DB has
        // on file (1000) -- i.e. messages 1001..3000 arrived while we weren't listening.
        var channelJson = """
            {
              "id": "300",
              "type": 0,
              "guild_id": "100",
              "name": "general",
              "position": 1,
              "topic": null,
              "nsfw": false,
              "last_message_id": "3000"
            }
            """;

        static string MessageJson(long id) =>
            $$"""
                {
                  "id": "{{id}}",
                  "type": 0,
                  "content": "msg {{id}}",
                  "channel_id": "300",
                  "author": {
                    "id": "1",
                    "username": "alice",
                    "discriminator": "0000",
                    "avatar": null
                  },
                  "attachments": [],
                  "embeds": [],
                  "pinned": false,
                  "timestamp": "2023-06-15T12:00:00+00:00"
                }
                """;

        // Records the `after=` cursor the exporter actually asked Discord for.
        var afterValues = new List<string>();

        mockHandler.Handler = req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            var query = req.RequestUri?.Query ?? "";

            if (path.EndsWith("guilds/100"))
                return Json(guildJson);
            if (path.EndsWith("guilds/100/channels"))
                return Json($"[{channelJson}]");
            if (path.EndsWith("guilds/100/roles"))
                return Json("[]");
            if (path.EndsWith("channels/300"))
                return Json(channelJson);
            if (path.EndsWith("channels/300/messages"))
            {
                var afterMatch = System.Text.RegularExpressions.Regex.Match(query, "after=(\\d+)");

                // The last-message probe (limit=1&before=, no after=) snapshots the newest
                // message on the channel so progress can be computed.
                if (!afterMatch.Success)
                    return Json($"[{MessageJson(3000)}]");

                afterValues.Add(afterMatch.Groups[1].Value);

                // The stored cursor is 1000; resuming from it returns the gap message (2000)
                // then stops. If the exporter wrongly resumed from the channel's newest id
                // (3000), it would ask after=3000 and never see message 2000.
                if (afterMatch.Groups[1].Value == "1000")
                    return Json($"[{MessageJson(2000)}]");
                return Json("[]");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var discord = new DiscordClient("fake-token", httpClient: httpClient);
        using var dbTemp = TempFile.Create();
        var dbPath = dbTemp.Path;

        var guild = await discord.GetGuildAsync(new Snowflake(100));
        var channel = await discord.GetChannelAsync(new Snowflake(300));
        var exporter = new ChannelExporter(discord);

        // Seed the DB so the channel's stored cursor is 1000 (simulate a prior backfill that
        // stopped there), by first exporting with the channel reporting last_message_id=1000.
        await using (var store = await SqliteExportStore.OpenAsync(dbPath))
        {
            await store.UpsertGuildAsync(guild);
            await store.UpsertChannelAsync(channel);
            await store.UpdateChannelExportStateAsync(
                channel.Id,
                new Snowflake(1000),
                false,
                DateTimeOffset.UtcNow,
                null,
                null
            );
            await store.FlushAsync();
        }

        // Act: run the same export path the watcher uses (After = null).
        await using (var store = await SqliteExportStore.OpenAsync(dbPath))
        {
            var request = new ExportRequest(
                guild,
                channel,
                dbPath,
                null,
                ExportFormat.Db,
                null,
                null,
                PartitionLimit.Null,
                MessageFilter.Null,
                false,
                true,
                false,
                false,
                null,
                false
            );
            await exporter.ExportChannelAsync(request, store);
        }

        // Assert: it resumed from the stored cursor (1000), not from the channel's newest id.
        afterValues.Should().Contain("1000");
        afterValues.Should().NotContain("3000");

        static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
    }
}
