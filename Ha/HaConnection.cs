using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace BabyMonitarr.Backend.Ha;

/// <summary>
/// One live Home Assistant WebSocket client. Owns the send pump so that broadcasts from the
/// timer thread never race a reply written from the receive loop — a WebSocket allows only one
/// send at a time.
/// </summary>
public sealed class HaConnection
{
    private const int ReceiveBufferSize = 8 * 1024;
    private const int MaxMessageBytes = 256 * 1024;

    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly Channel<string> _outbound;

    public HaConnection(
        string id, string userName, string? baseUrl, WebSocket socket, int sendQueueCapacity, ILogger logger)
    {
        Id = id;
        UserName = userName;
        BaseUrl = baseUrl;
        _socket = socket;
        _logger = logger;
        _outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(sendQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });
    }

    public string Id { get; }

    public string UserName { get; }

    /// <summary>
    /// Scheme and host of the handshake, e.g. "http://192.168.1.10:8080". Cast receivers pull HLS
    /// from this, so it must be an address they can reach — the same hint the SignalR hub takes
    /// from its own request.
    /// </summary>
    public string? BaseUrl { get; }

    /// <summary>
    /// Queues a pre-serialized frame. Returns false when the queue is full, which means the
    /// client is not draining and the connection should be dropped rather than buffered further.
    /// </summary>
    public bool TryEnqueue(string json) => _outbound.Writer.TryWrite(json);

    /// <summary>
    /// Pumps both directions until the socket closes or <paramref name="ct"/> fires.
    /// <paramref name="onMessage"/> is invoked for every well-formed client frame.
    /// </summary>
    public async Task RunAsync(
        Func<HaConnection, JsonElement, CancellationToken, Task> onMessage,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sendPump = SendLoopAsync(linked.Token);

        try
        {
            await ReceiveLoopAsync(onMessage, linked.Token);
        }
        finally
        {
            linked.Cancel();
            _outbound.Writer.TryComplete();
            try
            {
                await sendPump;
            }
            catch (OperationCanceledException)
            {
                // Expected once the receive side has torn the connection down.
            }
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var json in _outbound.Reader.ReadAllAsync(ct))
            {
                if (_socket.State != WebSocketState.Open) break;

                var bytes = Encoding.UTF8.GetBytes(json);
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "HA connection {ConnectionId} send failed", Id);
        }
    }

    private async Task ReceiveLoopAsync(
        Func<HaConnection, JsonElement, CancellationToken, Task> onMessage,
        CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        var message = new MemoryStream();

        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogInformation("HA connection {ConnectionId} dropped: {Message}", Id, ex.Message);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", ct);
                break;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                await CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", ct);
                break;
            }

            if (!result.EndOfMessage) continue;

            var payload = message.ToArray();
            message.SetLength(0);

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                TryEnqueue(HaFrames.Error(HaProtocol.ErrBadRequest, "Message is not valid JSON", null));
                continue;
            }

            using (document)
            {
                try
                {
                    await onMessage(this, document.RootElement, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling HA message on connection {ConnectionId}", Id);
                    TryEnqueue(HaFrames.Error(HaProtocol.ErrInternal, "Command failed", null));
                }
            }
        }
    }

    public async Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open && _socket.State != WebSocketState.CloseReceived) return;

        try
        {
            await _socket.CloseAsync(status, description, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "HA connection {ConnectionId} close failed", Id);
        }
    }
}

/// <summary>Serializes protocol frames once so a broadcast can reuse the same string.</summary>
public static class HaFrames
{
    public static string Build(string type, object? data, string? reference = null) =>
        JsonSerializer.Serialize(
            new HaEnvelope { Type = type, Data = data, Ref = reference },
            HaProtocol.JsonOptions);

    public static string Error(string code, string message, string? reference) =>
        Build(HaProtocol.Error, new HaErrorData(code, message), reference);

    /// <summary>
    /// Compares two frames of the same type by payload, ignoring the envelope timestamp, which
    /// always differs. Used to push only what actually changed.
    /// </summary>
    public static bool PayloadEquals(string? left, string? right)
    {
        if (left == null || right == null) return false;

        static string Payload(string frame)
        {
            int index = frame.IndexOf("\"data\":", StringComparison.Ordinal);
            return index < 0 ? frame : frame[index..];
        }

        return string.Equals(Payload(left), Payload(right), StringComparison.Ordinal);
    }
}
