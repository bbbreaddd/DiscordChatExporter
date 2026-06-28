using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.Retry;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Utils;

public static class Http
{
    // Invoked whenever a request is about to be delayed, either because Discord's advisory
    // rate limit was hit (see DiscordClient) or because a request failed and is being
    // retried. Lets the CLI surface *why* an export looks stalled instead of just freezing
    // silently for up to 60 seconds at a time.
    //
    // This is scoped via AsyncLocal rather than a plain static event so that with multiple
    // channels exporting concurrently (--parallel > 1), each channel's own async call stack
    // only observes throttling caused by its *own* requests. A shared event would notify every
    // concurrently-running channel of every throttle anywhere in the process, which previously
    // caused channels that were never themselves throttled to have that dead time subtracted
    // from their fetch-rate calculation anyway, sometimes driving it to a wildly inflated number.
    private static readonly AsyncLocal<Action<TimeSpan, string>?> _throttledHandler = new();

    public static IDisposable OnThrottled(Action<TimeSpan, string> handler)
    {
        var previousHandler = _throttledHandler.Value;
        _throttledHandler.Value = handler;
        return new RestoreThrottledHandler(previousHandler);
    }

    internal static void NotifyThrottled(TimeSpan delay, string reason) =>
        _throttledHandler.Value?.Invoke(delay, reason);

    private sealed class RestoreThrottledHandler(Action<TimeSpan, string>? previousHandler)
        : IDisposable
    {
        public void Dispose() => _throttledHandler.Value = previousHandler;
    }

    public static HttpClient Client { get; } = new();

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
        ||
        // Treat all server-side errors as retryable
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/908
        (int)statusCode >= 500;

    private static bool IsRetryableException(Exception exception) =>
        exception
            .GetSelfAndDescendants()
            .Any(ex =>
                ex is TimeoutException or SocketException or AuthenticationException
                || ex is HttpRequestException hrex
                    && (hrex.StatusCode is null || IsRetryableStatusCode(hrex.StatusCode.Value))
            );

    public static ResiliencePipeline ResiliencePipeline { get; } =
        new ResiliencePipelineBuilder()
            .AddRetry(
                new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(IsRetryableException),
                    MaxRetryAttempts = 10,
                    DelayGenerator = args =>
                    {
                        var delay = Math.Min(60, Math.Pow(2, args.AttemptNumber) + 1);
                        return ValueTask.FromResult<TimeSpan?>(TimeSpan.FromSeconds(delay));
                    },
                }
            )
            .Build();

    public static ResiliencePipeline<HttpResponseMessage> ResponseResiliencePipeline { get; } =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(
                new RetryStrategyOptions<HttpResponseMessage>
                {
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<Exception>(IsRetryableException)
                        .HandleResult(m => IsRetryableStatusCode(m.StatusCode)),
                    MaxRetryAttempts = 12,
                    DelayGenerator = args =>
                    {
                        // If rate-limited, use retry-after header as the guide.
                        // The response can be null here if an exception was thrown.
                        if (args.Outcome.Result?.Headers.RetryAfter?.Delta is { } retryAfter)
                        {
                            // Add some buffer just in case
                            return ValueTask.FromResult<TimeSpan?>(
                                retryAfter + TimeSpan.FromSeconds(1)
                            );
                        }

                        var delay = Math.Min(60, Math.Pow(2, args.AttemptNumber) + 1);
                        return ValueTask.FromResult<TimeSpan?>(TimeSpan.FromSeconds(delay));
                    },
                    OnRetry = args =>
                    {
                        var reason =
                            args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests
                                ? "rate limited by Discord"
                                : "request failed, retrying";

                        NotifyThrottled(args.RetryDelay, reason);
                        return default;
                    },
                }
            )
            .Build();
}
