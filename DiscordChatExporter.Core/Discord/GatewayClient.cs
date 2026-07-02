using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Discord;

public class GatewayClient
{
    private readonly string _token;
    private string? _sessionId;
    private string? _resumeGatewayUrl;
    private int? _sequenceNumber;
    private bool _lastHeartbeatAcked = true;
    private TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);

    // Consecutive (re)connect attempts that failed to reach READY/RESUMED. Reset to zero the
    // moment a connection becomes healthy again. Once it crosses the threshold we raise
    // ConnectionDown exactly once (guarded by _hasNotifiedConnectionDown) so an operator can be
    // told the gateway is stuck, without spamming on every retry -- and ConnectionRestored once
    // it recovers.
    private const int ConnectionDownThreshold = 5;
    private int _consecutiveFailures;
    private bool _hasNotifiedConnectionDown;

    public event Func<string, JsonElement, ValueTask>? DispatchReceived;
    public event Action<string>? LogMessage;
    public event Action<Exception>? ErrorOccurred;
    public event Action? FatalCloseOccurred;

    // Raised once when consecutive failed (re)connects cross ConnectionDownThreshold, and again
    // (ConnectionRestored) when the connection next reaches READY/RESUMED. The int is the number
    // of consecutive failures observed.
    public event Action<int>? ConnectionDown;
    public event Action<int>? ConnectionRestored;

    public GatewayClient(string token)
    {
        _token = token;
    }

    private void Log(string msg) => LogMessage?.Invoke(msg);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ClientWebSocket? webSocket = null;
            CancellationTokenSource? heartbeatCts = null;
            try
            {
                webSocket = new ClientWebSocket();

                var urlString = (_resumeGatewayUrl ?? "https://gateway.discord.gg")
                    .Replace("http:", "ws:")
                    .Replace("https:", "wss:");
                if (urlString.EndsWith('/'))
                {
                    urlString = urlString[..^1];
                }
                var uri = new Uri($"{urlString}/?v=10&encoding=json");

                Log($"Connecting to gateway at {uri}...");
                await webSocket.ConnectAsync(uri, cancellationToken);
                Log("Gateway connected.");

                _lastHeartbeatAcked = true;

                heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await ReceiveLoopAsync(webSocket, heartbeatCts);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(ex);
            }
            finally
            {
                if (heartbeatCts is not null)
                {
                    heartbeatCts.Cancel();
                    heartbeatCts.Dispose();
                }
                if (webSocket is not null)
                {
                    try
                    {
                        if (
                            webSocket.State == WebSocketState.Open
                            || webSocket.State == WebSocketState.CloseReceived
                        )
                        {
                            await webSocket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Reconnecting",
                                CancellationToken.None
                            );
                        }
                    }
                    catch
                    {
                        // Ignore close failures
                    }
                    webSocket.Dispose();
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= ConnectionDownThreshold && !_hasNotifiedConnectionDown)
                {
                    _hasNotifiedConnectionDown = true;
                    ConnectionDown?.Invoke(_consecutiveFailures);
                }

                Log($"Gateway disconnected. Reconnecting in {_reconnectDelay.TotalSeconds}s...");
                try
                {
                    await Task.Delay(_reconnectDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                _reconnectDelay = TimeSpan.FromTicks(
                    Math.Min(TimeSpan.FromSeconds(60).Ticks, _reconnectDelay.Ticks * 2)
                );
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket webSocket, CancellationTokenSource loopCts)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        while (webSocket.State == WebSocketState.Open && !loopCts.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    loopCts.Token
                );
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage && !loopCts.IsCancellationRequested);

            if (result.MessageType == WebSocketMessageType.Close || loopCts.IsCancellationRequested)
            {
                if (result.CloseStatus.HasValue)
                {
                    var code = (int)result.CloseStatus.Value;
                    Log(
                        $"Gateway closed connection with code: {code} ({result.CloseStatusDescription})"
                    );
                    HandleCloseCode(code);
                }
                break;
            }

            ms.Position = 0;
            using var document = await JsonDocument.ParseAsync(
                ms,
                cancellationToken: loopCts.Token
            );
            await HandleMessageAsync(webSocket, document.RootElement, loopCts);
        }
    }

    private void HandleCloseCode(int code)
    {
        if (code is 4004 or 4010 or 4011 or 4012 or 4013 or 4014)
        {
            Log($"Fatal gateway close status: {code}. Exiting.");
            FatalCloseOccurred?.Invoke();
        }
        else if (code is 4007 or 4009)
        {
            Log($"Gateway close status {code} prevents session resume. Resetting session.");
            _sessionId = null;
            _resumeGatewayUrl = null;
            _sequenceNumber = null;
        }
    }

    private async Task HandleMessageAsync(
        ClientWebSocket webSocket,
        JsonElement root,
        CancellationTokenSource loopCts
    )
    {
        if (!root.TryGetProperty("op", out var opProp) || opProp.ValueKind != JsonValueKind.Number)
            return;

        var op = opProp.GetInt32();

        if (root.TryGetProperty("s", out var sProp) && sProp.ValueKind == JsonValueKind.Number)
        {
            _sequenceNumber = sProp.GetInt32();
        }

        switch (op)
        {
            case 10: // Hello
                if (
                    root.TryGetProperty("d", out var dProp)
                    && dProp.TryGetProperty("heartbeat_interval", out var hbProp)
                )
                {
                    var interval = hbProp.GetInt32();
                    _ = Task.Run(
                        () => HeartbeatLoopAsync(webSocket, interval, loopCts.Token),
                        loopCts.Token
                    );
                }

                if (!string.IsNullOrEmpty(_sessionId))
                {
                    Log("Sending Resume payload...");
                    await SendPayloadAsync(
                        webSocket,
                        writer =>
                        {
                            writer.WriteStartObject();
                            writer.WriteNumber("op", 6);
                            writer.WriteStartObject("d");
                            writer.WriteString("token", _token);
                            writer.WriteString("session_id", _sessionId);
                            if (_sequenceNumber is { } seq)
                                writer.WriteNumber("seq", seq);
                            else
                                writer.WriteNull("seq");
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        },
                        loopCts.Token
                    );
                }
                else
                {
                    Log("Sending Identify payload...");
                    await SendPayloadAsync(
                        webSocket,
                        writer =>
                        {
                            writer.WriteStartObject();
                            writer.WriteNumber("op", 2);
                            writer.WriteStartObject("d");
                            writer.WriteString("token", _token);
                            // 1 (GUILDS) | 8 (GUILD_EMOJIS_AND_STICKERS) | 512 (GUILD_MESSAGES)
                            // | 1024 (GUILD_MESSAGE_REACTIONS) | 32768 (MESSAGE_CONTENT, privileged)
                            // | 65536 (GUILD_SCHEDULED_EVENTS). Without MESSAGE_CONTENT, Discord
                            // strips content/embeds/attachments/components/poll from every
                            // message that isn't a DM, doesn't mention the bot, or wasn't sent by
                            // the bot itself.
                            writer.WriteNumber("intents", 99849);
                            writer.WriteStartObject("properties");
                            writer.WriteString("os", "linux");
                            writer.WriteString("browser", "discord-live-watcher-db");
                            writer.WriteString("device", "discord-live-watcher-db");
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        },
                        loopCts.Token
                    );
                }
                break;

            case 11: // Heartbeat ACK
                _lastHeartbeatAcked = true;
                break;

            case 1: // Heartbeat request from server
                await SendHeartbeatAsync(webSocket, loopCts.Token);
                break;

            case 7: // Reconnect
                Log("Server requested reconnect. Closing connection to resume...");
                loopCts.Cancel();
                break;

            case 9: // Invalid Session
                var resumable =
                    root.TryGetProperty("d", out var resProp)
                    && resProp.ValueKind == JsonValueKind.True;
                Log($"Invalid session payload received. Resumable={resumable}");
                if (!resumable)
                {
                    _sessionId = null;
                    _resumeGatewayUrl = null;
                    _sequenceNumber = null;
                }
                loopCts.Cancel();
                break;

            case 0: // Dispatch
                if (
                    root.TryGetProperty("t", out var tProp)
                    && tProp.ValueKind == JsonValueKind.String
                )
                {
                    var eventType = tProp.GetString() ?? "";
                    var eventData = root.GetProperty("d");

                    if (eventType is "READY" or "RESUMED")
                    {
                        _reconnectDelay = TimeSpan.FromSeconds(5);

                        if (_hasNotifiedConnectionDown)
                        {
                            ConnectionRestored?.Invoke(_consecutiveFailures);
                            _hasNotifiedConnectionDown = false;
                        }
                        _consecutiveFailures = 0;

                        if (eventType == "READY")
                        {
                            if (eventData.TryGetProperty("session_id", out var sessProp))
                            {
                                _sessionId = sessProp.GetString();
                            }
                            if (eventData.TryGetProperty("resume_gateway_url", out var resUrlProp))
                            {
                                _resumeGatewayUrl = resUrlProp.GetString();
                            }
                        }
                    }

                    if (DispatchReceived is not null)
                    {
                        try
                        {
                            await DispatchReceived(eventType, eventData);
                        }
                        catch (Exception ex)
                        {
                            Log($"Error handling dispatch event {eventType}: {ex.Message}");
                        }
                    }
                }
                break;
        }
    }

    private async Task HeartbeatLoopAsync(
        ClientWebSocket webSocket,
        int intervalMs,
        CancellationToken cancellationToken
    )
    {
        try
        {
            while (
                webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested
            )
            {
                await Task.Delay(intervalMs, cancellationToken);

                if (!_lastHeartbeatAcked)
                {
                    Log(
                        "Heartbeat was not ACKed before next heartbeat came due. Forcing reconnect..."
                    );
                    webSocket.Abort();
                    break;
                }

                _lastHeartbeatAcked = false;
                await SendHeartbeatAsync(webSocket, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"Error in heartbeat loop: {ex.Message}");
        }
    }

    private async ValueTask SendHeartbeatAsync(
        ClientWebSocket webSocket,
        CancellationToken cancellationToken
    )
    {
        await SendPayloadAsync(
            webSocket,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("op", 1);
                if (_sequenceNumber is { } seq)
                    writer.WriteNumber("d", seq);
                else
                    writer.WriteNull("d");
                writer.WriteEndObject();
            },
            cancellationToken
        );
    }

    // Gateway payloads are written with Utf8JsonWriter rather than JsonSerializer: this app is
    // published with PublishTrimmed, which disables reflection-based serialization
    // (IsReflectionEnabledByDefault=false), so serializing anonymous objects throws at runtime.
    private async ValueTask SendPayloadAsync(
        ClientWebSocket webSocket,
        Action<Utf8JsonWriter> writePayload,
        CancellationToken cancellationToken
    )
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writePayload(writer);
        }

        await webSocket.SendAsync(
            new ArraySegment<byte>(buffer.WrittenSpan.ToArray()),
            WebSocketMessageType.Text,
            true,
            cancellationToken
        );
    }
}
