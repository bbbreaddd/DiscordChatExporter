using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Utils;
using Gress;
using JsonExtensions.Http;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord;

public class DiscordClient
{
    private readonly IReadOnlyList<string> _tokens;
    private readonly RateLimitPreference _rateLimitPreference;
    private readonly Uri _baseUri = new("https://discord.com/api/v10/", UriKind.Absolute);

    // Per-token state, indexed in parallel with `_tokens`
    private readonly TokenKind?[] _resolvedTokenKinds;
    private readonly bool[] _isTokenInvalid;

    // Index of the token that should be tried first for the next request.
    // Updated as requests succeed or fail so that subsequent requests don't
    // repeatedly retry tokens that are known not to work for the active resource.
    private int _activeTokenIndex;

    public DiscordClient(
        IReadOnlyList<string> tokens,
        RateLimitPreference rateLimitPreference = RateLimitPreference.RespectAll
    )
    {
        if (tokens.Count <= 0)
            throw new ArgumentException(
                "At least one authentication token must be provided.",
                nameof(tokens)
            );

        _tokens = tokens;
        _rateLimitPreference = rateLimitPreference;
        _resolvedTokenKinds = new TokenKind?[tokens.Count];
        _isTokenInvalid = new bool[tokens.Count];
    }

    public DiscordClient(
        string token,
        RateLimitPreference rateLimitPreference = RateLimitPreference.RespectAll
    )
        : this([token], rateLimitPreference) { }

    private async ValueTask<HttpResponseMessage> GetResponseAsync(
        string url,
        int tokenIndex,
        TokenKind tokenKind,
        CancellationToken cancellationToken = default
    ) =>
        await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, url)),
            tokenIndex,
            tokenKind,
            cancellationToken
        );

    // The request is created via a factory because the resilience pipeline may invoke this more
    // than once (retries), and an HttpRequestMessage (and its content) can only be sent a single
    // time, so each attempt needs a fresh instance.
    private async ValueTask<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest,
        int tokenIndex,
        TokenKind tokenKind,
        CancellationToken cancellationToken = default
    )
    {
        var token = _tokens[tokenIndex];

        return await Http.ResponseResiliencePipeline.ExecuteAsync(
            async innerCancellationToken =>
            {
                using var request = createRequest();

                // Don't validate because the token can have special characters
                // https://github.com/Tyrrrz/DiscordChatExporter/issues/828
                request.Headers.TryAddWithoutValidation(
                    "Authorization",
                    tokenKind == TokenKind.Bot ? $"Bot {token}" : token
                );

                var response = await Http.Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    innerCancellationToken
                );

                // Discord has advisory rate limits (communicated via response headers), but they are typically
                // way stricter than the actual rate limits enforced by the server.
                // The user may choose to ignore the advisory rate limits and only retry on hard rate limits,
                // if they want to prioritize speed over compliance (and safety of their account/bot).
                // https://github.com/Tyrrrz/DiscordChatExporter/issues/1021
                if (_rateLimitPreference.IsRespectedFor(tokenKind))
                {
                    var remainingRequestCount = response
                        .Headers.TryGetValue("X-RateLimit-Remaining")
                        ?.Pipe(s => int.ParseOrNull(s, CultureInfo.InvariantCulture));

                    var resetAfterDelay = response
                        .Headers.TryGetValue("X-RateLimit-Reset-After")
                        ?.Pipe(s => double.ParseOrNull(s, CultureInfo.InvariantCulture))
                        ?.Pipe(TimeSpan.FromSeconds);

                    // If this was the last request available before hitting the rate limit,
                    // wait out the reset time so that future requests can succeed.
                    // This may add an unnecessary delay in case the user doesn't intend to
                    // make any more requests, but implementing a smarter solution would
                    // require properly keeping track of Discord's global/per-route/per-resource
                    // rate limits and that's just way too much effort.
                    // https://discord.com/developers/docs/topics/rate-limits
                    if (remainingRequestCount <= 0 && resetAfterDelay is not null)
                    {
                        var delay =
                            // Adding a small buffer to the reset time reduces the chance of getting
                            // rate limited again, because it allows for more requests to be released.
                            (resetAfterDelay.Value + TimeSpan.FromSeconds(1))
                            // Sometimes Discord returns an absurdly high value for the reset time, which
                            // is not actually enforced by the server. So we cap it at a reasonable value.
                            .Clamp(TimeSpan.Zero, TimeSpan.FromSeconds(60));

                        Http.NotifyThrottled(delay, "rate limited by Discord");
                        await Task.Delay(delay, innerCancellationToken);
                    }
                }

                return response;
            },
            cancellationToken
        );
    }

    // Resolves the token kind for the token at the specified index, or returns null if that
    // token has been determined to be invalid altogether (i.e. not a valid user or bot token).
    private async ValueTask<TokenKind?> ResolveTokenKindAsync(
        int tokenIndex,
        CancellationToken cancellationToken = default
    )
    {
        if (_isTokenInvalid[tokenIndex])
            return null;

        if (_resolvedTokenKinds[tokenIndex] is { } resolvedTokenKind)
            return resolvedTokenKind;

        // Try authenticating as a user
        using var userResponse = await GetResponseAsync(
            "users/@me",
            tokenIndex,
            TokenKind.User,
            cancellationToken
        );

        if (userResponse.StatusCode != HttpStatusCode.Unauthorized)
            return (_resolvedTokenKinds[tokenIndex] = TokenKind.User).Value;

        // Try authenticating as a bot
        using var botResponse = await GetResponseAsync(
            "users/@me",
            tokenIndex,
            TokenKind.Bot,
            cancellationToken
        );

        if (botResponse.StatusCode != HttpStatusCode.Unauthorized)
            return (_resolvedTokenKinds[tokenIndex] = TokenKind.Bot).Value;

        _isTokenInvalid[tokenIndex] = true;
        return null;
    }

    // Resolves the token kind of the token that's currently expected to be used for requests.
    // This is only meaningful to call after at least one request has already been made,
    // because that's what determines which token is "active".
    private async ValueTask<TokenKind> ResolveActiveTokenKindAsync(
        CancellationToken cancellationToken = default
    ) =>
        await ResolveTokenKindAsync(_activeTokenIndex, cancellationToken)
        ?? throw new DiscordChatExporterException("Authentication token is invalid.", true);

    // Sends a request using one of the provided tokens, automatically falling back to the
    // other tokens if the active one turns out to be invalid, lacks access to the requested
    // resource, or is being persistently rate limited.
    private async ValueTask<HttpResponseMessage> GetResponseAsync(
        string url,
        CancellationToken cancellationToken = default
    ) =>
        await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, url)),
            cancellationToken
        );

    private async ValueTask<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken = default
    )
    {
        HttpResponseMessage? lastResponse = null;

        for (var attempt = 0; attempt < _tokens.Count; attempt++)
        {
            var tokenIndex = (_activeTokenIndex + attempt) % _tokens.Count;

            var tokenKind = await ResolveTokenKindAsync(tokenIndex, cancellationToken);

            // This token is permanently invalid, try the next one
            if (tokenKind is null)
                continue;

            var response = await SendAsync(
                createRequest,
                tokenIndex,
                tokenKind.Value,
                cancellationToken
            );

            // The token was valid before, but has since been revoked.
            // Remember that and try the next one.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _isTokenInvalid[tokenIndex] = true;
                lastResponse?.Dispose();
                lastResponse = response;
                continue;
            }

            // The active account doesn't have access to this resource, or is being rate
            // limited hard enough that even the resilience pipeline couldn't recover.
            // If there's another token left to try, see if it fares better with this
            // specific request, without giving up on the current token entirely.
            var isFallbackEligible =
                response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

            if (isFallbackEligible && attempt < _tokens.Count - 1)
            {
                lastResponse?.Dispose();
                lastResponse = response;
                continue;
            }

            // This token produced a usable response (or it's the last one we can try),
            // so prefer it for subsequent requests.
            _activeTokenIndex = tokenIndex;
            lastResponse?.Dispose();
            return response;
        }

        if (lastResponse is not null)
            return lastResponse;

        throw new DiscordChatExporterException(
            _tokens.Count == 1
                ? "Authentication token is invalid."
                : "All provided authentication tokens are invalid.",
            true
        );
    }

    private async ValueTask<JsonElement> GetJsonResponseAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await GetResponseAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => throw new DiscordChatExporterException(
                    "Authentication token is invalid.",
                    true
                ),

                HttpStatusCode.Forbidden => throw new DiscordChatExporterException(
                    $"Request to '{url}' failed: forbidden."
                ),

                HttpStatusCode.NotFound => throw new DiscordChatExporterException(
                    $"Request to '{url}' failed: not found."
                ),

                _ => throw new DiscordChatExporterException(
                    $"""
                    Request to '{url}' failed: {response
                        .StatusCode.ToString()
                        .SeparateWords(' ')
                        .ToLowerInvariant()}.
                    Response content: {await response.Content.ReadAsStringAsync(
                        cancellationToken
                    )}
                    """,
                    true
                ),
            };
        }

        return await response.Content.ReadAsJsonAsync(cancellationToken);
    }

    private async ValueTask<JsonElement?> TryGetJsonResponseAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await GetResponseAsync(url, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsJsonAsync(cancellationToken)
            : null;
    }

    private async ValueTask<JsonElement> PostJsonResponseAsync(
        string url,
        string jsonBody,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await SendAsync(
            () =>
                new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, url))
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
                },
            cancellationToken
        );

        if (!response.IsSuccessStatusCode)
        {
            throw new DiscordChatExporterException(
                $"""
                Request to '{url}' failed: {response
                    .StatusCode.ToString()
                    .SeparateWords(' ')
                    .ToLowerInvariant()}.
                Response content: {await response.Content.ReadAsStringAsync(cancellationToken)}
                """,
                true
            );
        }

        return await response.Content.ReadAsJsonAsync(cancellationToken);
    }

    private static bool IsRefreshableUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (
            string.Equals(uri.Host, "cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "media.discordapp.net", StringComparison.OrdinalIgnoreCase)
        );

    // Refreshes expired Discord CDN links (cdn.discordapp.com / media.discordapp.net) via the
    // official 'refresh-urls' endpoint, returning a map of original -> freshly-signed URL. Only
    // Discord-hosted URLs are refreshable; anything else is ignored. Used to recover assets whose
    // stored signed URLs have since expired so that they can be downloaded again.
    public async ValueTask<IReadOnlyDictionary<string, string>> RefreshAttachmentUrlsAsync(
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken = default
    )
    {
        var refreshable = urls.Where(IsRefreshableUrl).Distinct(StringComparer.Ordinal).ToArray();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // The endpoint accepts only a limited number of URLs per request.
        foreach (var batch in refreshable.Chunk(50))
        {
            // Built via the JSON DOM rather than the serializer because reflection-based
            // serialization is disabled in this (trimming-friendly) build.
            var attachmentUrls = new JsonArray();
            foreach (var url in batch)
                attachmentUrls.Add(JsonValue.Create(url));

            var body = new JsonObject { ["attachment_urls"] = attachmentUrls }.ToJsonString();

            var json = await PostJsonResponseAsync(
                "attachments/refresh-urls",
                body,
                cancellationToken
            );

            var refreshedUrls = json.GetPropertyOrNull("refreshed_urls");
            if (refreshedUrls is null)
                continue;

            foreach (var item in refreshedUrls.Value.EnumerateArray())
            {
                var original = item.GetPropertyOrNull("original")?.GetNonWhiteSpaceStringOrNull();
                var refreshed = item.GetPropertyOrNull("refreshed")?.GetNonWhiteSpaceStringOrNull();

                if (original is not null && refreshed is not null)
                    result[original] = refreshed;
            }
        }

        return result;
    }

    public async ValueTask<Application> GetApplicationAsync(
        CancellationToken cancellationToken = default
    )
    {
        var response = await GetJsonResponseAsync("applications/@me", cancellationToken);
        return Application.Parse(response);
    }

    private async ValueTask EnsureMessageContentIntentAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (await ResolveActiveTokenKindAsync(cancellationToken) != TokenKind.Bot)
            return;

        var application = await GetApplicationAsync(cancellationToken);
        if (application.IsMessageContentIntentEnabled)
            return;

        throw new DiscordChatExporterException(
            "Provided bot account is missing the MESSAGE_CONTENT privileged intent.",
            true
        );
    }

    public async ValueTask<User?> TryGetUserAsync(
        Snowflake userId,
        CancellationToken cancellationToken = default
    )
    {
        var response = await TryGetJsonResponseAsync($"users/{userId}", cancellationToken);
        return response?.Pipe(User.Parse);
    }

    public async IAsyncEnumerable<Guild> GetUserGuildsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        yield return Guild.DirectMessages;

        var currentAfter = Snowflake.Zero;
        while (true)
        {
            var url = new UrlBuilder()
                .SetPath("users/@me/guilds")
                .SetQueryParameter("limit", "100")
                .SetQueryParameter("after", currentAfter.ToString())
                .Build();

            var response = await GetJsonResponseAsync(url, cancellationToken);

            var count = 0;
            foreach (var guildJson in response.EnumerateArray())
            {
                var guild = Guild.Parse(guildJson);
                yield return guild;

                currentAfter = guild.Id;
                count++;
            }

            if (count <= 0)
                yield break;
        }
    }

    public async ValueTask<Guild> GetGuildAsync(
        Snowflake guildId,
        CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
            return Guild.DirectMessages;

        var response = await GetJsonResponseAsync($"guilds/{guildId}", cancellationToken);
        return Guild.Parse(response);
    }

    public async IAsyncEnumerable<Channel> GetGuildChannelsAsync(
        Snowflake guildId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
        {
            var response = await GetJsonResponseAsync("users/@me/channels", cancellationToken);
            foreach (var channelJson in response.EnumerateArray())
                yield return Channel.Parse(channelJson);
        }
        else
        {
            var response = await GetJsonResponseAsync(
                $"guilds/{guildId}/channels",
                cancellationToken
            );

            var channelsJson = response
                .EnumerateArray()
                .OrderBy(j => j.GetProperty("position").GetInt32())
                .ThenBy(j => j.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse))
                .ToArray();

            var parentsById = channelsJson
                .Where(j => j.GetProperty("type").GetInt32() == (int)ChannelKind.GuildCategory)
                .Select((j, i) => Channel.Parse(j, null, i + 1))
                .ToDictionary(j => j.Id);

            // Discord channel positions are relative, so we need to normalize them
            // so that the user may refer to them more easily in file name templates.
            var position = 0;

            foreach (var channelJson in channelsJson)
            {
                var parent = channelJson
                    .GetPropertyOrNull("parent_id")
                    ?.GetNonWhiteSpaceStringOrNull()
                    ?.Pipe(Snowflake.Parse)
                    .Pipe(parentsById.GetValueOrDefault);

                yield return Channel.Parse(channelJson, parent, position);
                position++;
            }
        }
    }

    public async IAsyncEnumerable<Channel> GetGuildThreadsAsync(
        Snowflake guildId,
        bool includeArchived = false,
        Snowflake? before = null,
        Snowflake? after = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
            yield break;

        var channels = await GetGuildChannelsAsync(guildId, cancellationToken);

        foreach (
            var channel in await GetChannelThreadsAsync(
                channels,
                includeArchived,
                before,
                after,
                cancellationToken: cancellationToken
            )
        )
        {
            yield return channel;
        }
    }

    public async IAsyncEnumerable<Role> GetGuildRolesAsync(
        Snowflake guildId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
            yield break;

        var response = await GetJsonResponseAsync($"guilds/{guildId}/roles", cancellationToken);
        foreach (var roleJson in response.EnumerateArray())
            yield return Role.Parse(roleJson);
    }

    public async IAsyncEnumerable<GuildEmoji> GetGuildEmojisAsync(
        Snowflake guildId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
            yield break;

        var response = await GetJsonResponseAsync($"guilds/{guildId}/emojis", cancellationToken);
        foreach (var emojiJson in response.EnumerateArray())
            yield return GuildEmoji.Parse(emojiJson);
    }

    public async IAsyncEnumerable<GuildSticker> GetGuildStickersAsync(
        Snowflake guildId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
            yield break;

        var response = await GetJsonResponseAsync($"guilds/{guildId}/stickers", cancellationToken);
        foreach (var stickerJson in response.EnumerateArray())
            yield return GuildSticker.Parse(stickerJson);
    }

    public async IAsyncEnumerable<ScheduledEvent> GetGuildScheduledEventsAsync(
        Snowflake guildId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
            yield break;

        var response = await GetJsonResponseAsync(
            $"guilds/{guildId}/scheduled-events",
            cancellationToken
        );
        foreach (var eventJson in response.EnumerateArray())
            yield return ScheduledEvent.Parse(eventJson);
    }

    public async ValueTask<Member?> TryGetGuildMemberAsync(
        Snowflake guildId,
        Snowflake memberId,
        CancellationToken cancellationToken = default
    )
    {
        if (guildId == Guild.DirectMessages.Id)
            return null;

        var response = await TryGetJsonResponseAsync(
            $"guilds/{guildId}/members/{memberId}",
            cancellationToken
        );
        return response?.Pipe(j => Member.Parse(j, guildId));
    }

    public async ValueTask<Invite?> TryGetInviteAsync(
        string code,
        CancellationToken cancellationToken = default
    )
    {
        var response = await TryGetJsonResponseAsync($"invites/{code}", cancellationToken);
        return response?.Pipe(Invite.Parse);
    }

    public async ValueTask<Channel> GetChannelAsync(
        Snowflake channelId,
        CancellationToken cancellationToken = default
    )
    {
        var response = await GetJsonResponseAsync($"channels/{channelId}", cancellationToken);

        var parentId = response
            .GetPropertyOrNull("parent_id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        // It's possible for the parent channel to be inaccessible, despite the
        // child channel being accessible.
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/1108
        var parent = parentId is not null
            ? await TryGetChannelAsync(parentId.Value, cancellationToken)
            : null;

        return Channel.Parse(response, parent);
    }

    public async ValueTask<Channel?> TryGetChannelAsync(
        Snowflake channelId,
        CancellationToken cancellationToken = default
    )
    {
        var response = await TryGetJsonResponseAsync($"channels/{channelId}", cancellationToken);
        if (response is null)
            return null;

        var parentId = response
            .Value.GetPropertyOrNull("parent_id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        Channel? parent = null;
        if (parentId is not null)
        {
            // It's possible for the parent channel to be inaccessible, despite the
            // child channel being accessible.
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/1108
            parent = await TryGetChannelAsync(parentId.Value, cancellationToken);
        }

        return Channel.Parse(response.Value, parent);
    }

    // Fetches a single message directly by id, without paginating through the channel's history.
    // Used to refresh one already-exported message's current state (e.g. after a reaction
    // changes) instead of a full re-export. Returns null if the message (or channel) no longer
    // exists or isn't accessible -- callers treat that as "nothing to update", not an error.
    public async ValueTask<Message?> TryGetMessageAsync(
        Snowflake channelId,
        Snowflake messageId,
        CancellationToken cancellationToken = default
    )
    {
        var response = await TryGetJsonResponseAsync(
            $"channels/{channelId}/messages/{messageId}",
            cancellationToken
        );

        return response?.Pipe(Message.Parse);
    }

    public async IAsyncEnumerable<Channel> GetChannelThreadsAsync(
        IReadOnlyList<Channel> channels,
        bool includeArchived = false,
        Snowflake? before = null,
        Snowflake? after = null,
        ExportManifest? manifest = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var filteredChannels = channels
            // Categories cannot have threads
            .Where(c => !c.IsCategory)
            // Voice channels cannot have threads
            .Where(c => !c.IsVoice)
            // Empty channels cannot have threads
            .Where(c => !c.IsEmpty)
            // If the 'before' boundary is specified, skip channels that don't have messages
            // for that range, because thread-start event should always be accompanied by a message.
            // Note that we don't perform a similar check for the 'after' boundary, because
            // threads may have messages in range, even if the parent channel doesn't.
            .Where(c => before is null || c.MayHaveMessagesBefore(before.Value))
            .ToArray();

        // Track yielded thread IDs to avoid duplicates that can occur when a thread transitions
        // from active to archived between the two separate API calls used to fetch threads.
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/1433
        var seenThreadIds = new HashSet<Snowflake>();
        var threadsList = new List<Channel>();

        // User accounts can only fetch threads using the search endpoint
        if (await ResolveActiveTokenKindAsync(cancellationToken) == TokenKind.User)
        {
            await Parallel.ForEachAsync(
                filteredChannels,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 8,
                    CancellationToken = cancellationToken,
                },
                async (channel, ct) =>
                {
                    // Either include both active and archived threads, or only active threads
                    foreach (
                        var isArchived in includeArchived ? new[] { false, true } : new[] { false }
                    )
                    {
                        // Offset is just the index of the last thread in the previous batch
                        var currentOffset = 0;
                        while (true)
                        {
                            var url = new UrlBuilder()
                                .SetPath($"channels/{channel.Id}/threads/search")
                                .SetQueryParameter("sort_by", "last_message_time")
                                .SetQueryParameter("sort_order", "desc")
                                .SetQueryParameter(
                                    "archived",
                                    isArchived.ToString().ToLowerInvariant()
                                )
                                .SetQueryParameter("offset", currentOffset.ToString())
                                .Build();

                            // Can be null on channels that the user cannot access or channels without threads
                            var response = await TryGetJsonResponseAsync(url, ct);
                            if (response is null)
                                break;

                            var breakOuter = false;

                            var threadsJson = response
                                .Value.GetProperty("threads")
                                .EnumerateArray()
                                .ToArray();
                            if (threadsJson.Length == 0)
                                break;

                            foreach (var threadJson in threadsJson)
                            {
                                var thread = Channel.Parse(threadJson, channel);
                                currentOffset++;

                                // If the 'after' boundary is specified, we can break early,
                                // because threads are sorted by last message timestamp.
                                if (after is not null && !thread.MayHaveMessagesAfter(after.Value))
                                {
                                    breakOuter = true;
                                    break;
                                }

                                lock (seenThreadIds)
                                {
                                    var isAlreadyExported =
                                        manifest?.Channels.TryGetValue(
                                            thread.Id.ToString(),
                                            out var entry
                                        ) == true
                                        && entry.IsArchived;
                                    if (isAlreadyExported)
                                    {
                                        if (isArchived)
                                            breakOuter = true;
                                        continue;
                                    }

                                    if (seenThreadIds.Add(thread.Id))
                                    {
                                        lock (threadsList)
                                        {
                                            threadsList.Add(thread);
                                        }
                                    }
                                }
                            }

                            if (breakOuter)
                                break;

                            if (!response.Value.GetProperty("has_more").GetBoolean())
                                break;
                        }
                    }
                }
            );
        }
        // Bot accounts can only fetch threads using the threads endpoint
        else
        {
            var guilds = new HashSet<Snowflake>();
            foreach (var channel in filteredChannels)
                guilds.Add(channel.GuildId);

            // Active threads
            foreach (var guildId in guilds)
            {
                var parentsById = filteredChannels.ToDictionary(c => c.Id);

                var response = await GetJsonResponseAsync(
                    $"guilds/{guildId}/threads/active",
                    cancellationToken
                );

                foreach (var threadJson in response.GetProperty("threads").EnumerateArray())
                {
                    var parent = threadJson
                        .GetPropertyOrNull("parent_id")
                        ?.GetNonWhiteSpaceStringOrNull()
                        ?.Pipe(Snowflake.Parse)
                        .Pipe(parentsById.GetValueOrDefault);

                    if (filteredChannels.Contains(parent))
                    {
                        var thread = Channel.Parse(threadJson, parent);

                        lock (seenThreadIds)
                        {
                            if (seenThreadIds.Add(thread.Id))
                            {
                                lock (threadsList)
                                {
                                    threadsList.Add(thread);
                                }
                            }
                        }
                    }
                }
            }

            // Archived threads
            if (includeArchived)
            {
                await Parallel.ForEachAsync(
                    filteredChannels,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 8,
                        CancellationToken = cancellationToken,
                    },
                    async (channel, ct) =>
                    {
                        foreach (var archiveType in new[] { "public", "private" })
                        {
                            // This endpoint parameter expects an ISO8601 timestamp, not a snowflake
                            var currentBefore = before
                                ?.ToDate()
                                .ToString("O", CultureInfo.InvariantCulture);

                            while (true)
                            {
                                // Threads are sorted by archive timestamp, not by last message timestamp
                                var url = new UrlBuilder()
                                    .SetPath(
                                        $"channels/{channel.Id}/threads/archived/{archiveType}"
                                    )
                                    .SetQueryParameter("before", currentBefore)
                                    .Build();

                                // Can be null on certain channels
                                var response = await TryGetJsonResponseAsync(url, ct);
                                if (response is null)
                                    break;

                                var breakOuter = false;

                                foreach (
                                    var threadJson in response
                                        .Value.GetProperty("threads")
                                        .EnumerateArray()
                                )
                                {
                                    var thread = Channel.Parse(threadJson, channel);

                                    currentBefore = threadJson
                                        .GetProperty("thread_metadata")
                                        .GetProperty("archive_timestamp")
                                        .GetString();

                                    lock (seenThreadIds)
                                    {
                                        var isAlreadyExported =
                                            manifest?.Channels.TryGetValue(
                                                thread.Id.ToString(),
                                                out var entry
                                            ) == true
                                            && entry.IsArchived;
                                        if (isAlreadyExported)
                                        {
                                            breakOuter = true;
                                            break;
                                        }

                                        if (seenThreadIds.Add(thread.Id))
                                        {
                                            lock (threadsList)
                                            {
                                                threadsList.Add(thread);
                                            }
                                        }
                                    }
                                }

                                if (breakOuter)
                                    break;

                                if (!response.Value.GetProperty("has_more").GetBoolean())
                                    break;
                            }
                        }
                    }
                );
            }
        }

        foreach (var thread in threadsList)
        {
            yield return thread;
        }
    }

    private async ValueTask<Message?> TryGetFirstMessageAsync(
        Snowflake channelId,
        Snowflake? after = null,
        CancellationToken cancellationToken = default
    )
    {
        var url = new UrlBuilder()
            .SetPath($"channels/{channelId}/messages")
            .SetQueryParameter("limit", "1")
            .SetQueryParameter("after", (after ?? Snowflake.Zero).ToString())
            .Build();

        var response = await GetJsonResponseAsync(url, cancellationToken);
        var message = response.EnumerateArray().Select(Message.Parse).FirstOrDefault();

        return message;
    }

    private async ValueTask<Message?> TryGetLastMessageAsync(
        Snowflake channelId,
        Snowflake? before = null,
        CancellationToken cancellationToken = default
    )
    {
        var url = new UrlBuilder()
            .SetPath($"channels/{channelId}/messages")
            .SetQueryParameter("limit", "1")
            .SetQueryParameter("before", before?.ToString())
            .Build();

        var response = await GetJsonResponseAsync(url, cancellationToken);
        return response.EnumerateArray().Select(Message.Parse).LastOrDefault();
    }

    public async IAsyncEnumerable<Message> GetMessagesAsync(
        Snowflake channelId,
        Snowflake? after = null,
        Snowflake? before = null,
        IProgress<ExportProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        // Get the last message in the specified range, so we can later calculate the
        // progress based on the difference between message timestamps.
        // This also snapshots the boundaries, which means that messages posted after
        // the export started will not appear in the output.
        var lastMessage = await TryGetLastMessageAsync(channelId, before, cancellationToken);
        if (lastMessage is null || lastMessage.Timestamp < after?.ToDate())
            yield break;

        // Keep track of the first message in range in order to calculate the progress
        var firstMessage = default(Message);

        var currentAfter = after ?? Snowflake.Zero;
        while (true)
        {
            var url = new UrlBuilder()
                .SetPath($"channels/{channelId}/messages")
                .SetQueryParameter("limit", "100")
                .SetQueryParameter("after", currentAfter.ToString())
                .Build();

            var response = await GetJsonResponseAsync(url, cancellationToken);

            var messages = response
                .EnumerateArray()
                .Select(Message.Parse)
                // Messages are returned from newest to oldest, so we need to reverse them
                .Reverse()
                .ToArray();

            // Break if there are no messages (can happen if messages are deleted during execution)
            if (!messages.Any())
                yield break;

            // If all messages are empty, make sure that it's not because the bot account doesn't
            // have the MESSAGE_CONTENT intent enabled.
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/1106#issuecomment-1741548959
            if (messages.All(m => m.IsEmpty))
                await EnsureMessageContentIntentAsync(cancellationToken);

            foreach (var message in messages)
            {
                firstMessage ??= message;

                // Ensure that the messages are in range
                if (message.Timestamp > lastMessage.Timestamp)
                    yield break;

                // Report progress based on timestamps
                if (progress is not null)
                {
                    var exportedDuration = (message.Timestamp - firstMessage.Timestamp).Duration();
                    var totalDuration = (lastMessage.Timestamp - firstMessage.Timestamp).Duration();

                    progress.Report(
                        new ExportProgress(
                            Percentage.FromFraction(
                                // Avoid division by zero if all messages have the exact same timestamp
                                // (which happens when there's only one message in the channel)
                                totalDuration > TimeSpan.Zero
                                    ? exportedDuration / totalDuration
                                    : 1
                            ),
                            message.Timestamp
                        )
                    );
                }

                yield return message;
                currentAfter = message.Id;
            }
        }
    }

    public async IAsyncEnumerable<Message> GetMessagesInReverseAsync(
        Snowflake channelId,
        Snowflake? after = null,
        Snowflake? before = null,
        IProgress<ExportProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        // Get the first message in the specified range, so we can later calculate the
        // progress based on the difference between message timestamps.
        // Snapshotting is not necessary here because new messages can't appear in the past.
        var firstMessage = await TryGetFirstMessageAsync(channelId, after, cancellationToken);
        if (firstMessage is null || firstMessage.Timestamp > before?.ToDate())
            yield break;

        // Keep track of the last message in range in order to calculate the progress
        var lastMessage = default(Message);

        var currentBefore = before;
        while (true)
        {
            var url = new UrlBuilder()
                .SetPath($"channels/{channelId}/messages")
                .SetQueryParameter("limit", "100")
                .SetQueryParameter("before", currentBefore?.ToString())
                .Build();

            var response = await GetJsonResponseAsync(url, cancellationToken);

            var messages = response.EnumerateArray().Select(Message.Parse).ToArray();

            // Break if there are no messages (can happen if messages are deleted during execution)
            if (!messages.Any())
                yield break;

            // If all messages are empty, make sure that it's not because the bot account doesn't
            // have the MESSAGE_CONTENT intent enabled.
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/1106#issuecomment-1741548959
            if (messages.All(m => m.IsEmpty))
                await EnsureMessageContentIntentAsync(cancellationToken);

            foreach (var message in messages)
            {
                lastMessage ??= message;

                // Report progress based on timestamps
                if (progress is not null)
                {
                    var exportedDuration = (lastMessage.Timestamp - message.Timestamp).Duration();
                    var totalDuration = (lastMessage.Timestamp - firstMessage.Timestamp).Duration();

                    progress.Report(
                        new ExportProgress(
                            Percentage.FromFraction(
                                // Avoid division by zero if all messages have the exact same timestamp
                                // (which happens when there's only one message in the channel)
                                totalDuration > TimeSpan.Zero
                                    ? exportedDuration / totalDuration
                                    : 1
                            ),
                            message.Timestamp
                        )
                    );
                }

                yield return message;
            }

            currentBefore = messages.Last().Id;
        }
    }

    public async IAsyncEnumerable<User> GetMessageReactionsAsync(
        Snowflake channelId,
        Snowflake messageId,
        Emoji emoji,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var reactionName = emoji.Id is not null
            // Custom emoji
            ? emoji.Name + ':' + emoji.Id
            // Standard emoji
            : emoji.Name;

        var currentAfter = Snowflake.Zero;
        while (true)
        {
            var url = new UrlBuilder()
                .SetPath(
                    $"channels/{channelId}/messages/{messageId}/reactions/{Uri.EscapeDataString(reactionName)}"
                )
                .SetQueryParameter("limit", "100")
                .SetQueryParameter("after", currentAfter.ToString())
                .Build();

            // Can be null on reactions with an emoji that has been deleted (?)
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/1226
            var response = await TryGetJsonResponseAsync(url, cancellationToken);
            if (response is null)
                yield break;

            var count = 0;
            foreach (var userJson in response.Value.EnumerateArray())
            {
                var user = User.Parse(userJson);
                yield return user;

                currentAfter = user.Id;
                count++;
            }

            if (count <= 0)
                yield break;
        }
    }
}
