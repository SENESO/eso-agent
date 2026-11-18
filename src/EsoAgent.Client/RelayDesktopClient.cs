using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;

namespace EsoAgent.Client;

/// <summary>
/// Relayed desktop client (v1.2): the same surface as the App's direct
/// DesktopClient, but the session travels through the fleet server. The
/// stream-open is signed with the OPERATOR key; the server verifies it,
/// relays it, and the DEVICE verifies it again before streaming — the
/// relay cannot open sessions on its own. Desktop frames then flow as
/// stream data, byte-identical to the direct protocol.
/// </summary>
public sealed class RelayDesktopClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private ClientWebSocket? _socket;

    public event Action<DesktopFrame>? FrameReceived;
    public event Action<string>? ClipboardReceived;
    public event Action<string>? ChatReceived;
    public event Action<bool>? PrivacyReceived;
    /// <summary>Raised when the session ends: close reason, error text, or local disconnect.</summary>
    public event Action<string>? SessionEnded;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>Connects through the fleet server and returns the negotiated welcome (throws on refusal).</summary>
    public async Task<DesktopWelcome> ConnectAsync(FleetApiClient fleet, string code, DesktopHello hello, CancellationToken ct = default)
    {
        _socket = await fleet.ConnectStreamSocketAsync("/fleet/desktop", ct);
        var streamId = Guid.NewGuid().ToString("N");
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payloadJson = JsonSerializer.Serialize(hello, EsoJson.Options);
        var open = new FleetStreamOpenFrame
        {
            StreamId = streamId,
            Kind = "desktop",
            IssuedAt = issuedAt,
            Signer = "operator",
            PayloadJson = payloadJson,
            Signature = OperatorSigning.SignStream(fleet.OperatorKey, streamId, issuedAt, "desktop", payloadJson),
        };
        await SendRawAsync(JsonSerializer.Serialize(new FleetStreamOpenRequest { Code = code, Open = open }, EsoJson.Options), ct);

        var openedJson = await ReceiveTextAsync(ct)
            ?? throw new InvalidOperationException("The fleet server closed the stream without an answer.");
        var opened = JsonSerializer.Deserialize<FleetStreamOpenedFrame>(openedJson, EsoJson.Options);
        if (opened is not { Ok: true })
            throw new InvalidOperationException(opened?.Error ?? "The device refused the relayed desktop session.");

        var welcomeJson = await ReceiveTextAsync(ct)
            ?? throw new InvalidOperationException("The device closed the relayed session without a welcome.");
        var welcome = JsonSerializer.Deserialize<DesktopWelcome>(welcomeJson, EsoJson.Options)
            ?? throw new InvalidOperationException("The device sent a malformed desktop welcome.");
        if (!string.IsNullOrEmpty(welcome.Error))
            throw new InvalidOperationException(welcome.Error);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        return welcome;
    }

    public Task SendInputAsync(DesktopInput input) => SendJsonAsync(input, CancellationToken.None);

    public Task SendClipboardAsync(string text) => SendJsonAsync(new DesktopClipboard { Text = text }, CancellationToken.None);

    public Task SendPrivacyAsync(bool enabled) => SendJsonAsync(new DesktopPrivacy { Enabled = enabled }, CancellationToken.None);

    public Task SendChatAsync(string text) => SendJsonAsync(new DesktopChat { Text = text, From = "operator" }, CancellationToken.None);

    public async Task DisconnectAsync()
    {
        try { await SendJsonAsync(new DesktopClose { Reason = "Client disconnected." }, CancellationToken.None); } catch { }
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_socket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch { }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var ended = "Session closed by the device.";
        try
        {
            while (!ct.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                var json = await ReceiveTextAsync(ct);
                if (json == null) break;
                using var doc = JsonDocument.Parse(json);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "frame":
                        var frame = JsonSerializer.Deserialize<DesktopFrame>(json, EsoJson.Options);
                        if (frame != null) FrameReceived?.Invoke(frame);
                        break;
                    case "clipboard":
                        var clip = JsonSerializer.Deserialize<DesktopClipboard>(json, EsoJson.Options);
                        if (clip != null) ClipboardReceived?.Invoke(clip.Text);
                        break;
                    case "chat":
                        var chat = JsonSerializer.Deserialize<DesktopChat>(json, EsoJson.Options);
                        if (chat != null && chat.From == "device") ChatReceived?.Invoke(chat.Text);
                        break;
                    case "privacy":
                        var privacy = JsonSerializer.Deserialize<DesktopPrivacy>(json, EsoJson.Options);
                        if (privacy != null) PrivacyReceived?.Invoke(privacy.Enabled);
                        break;
                    case "close":
                        var close = JsonSerializer.Deserialize<DesktopClose>(json, EsoJson.Options);
                        ended = close?.Reason ?? ended;
                        return;
                }
            }
        }
        catch (OperationCanceledException) { ended = "Disconnected."; }
        catch (Exception ex) { ended = $"Session error: {ex.Message}"; }
        finally { SessionEnded?.Invoke(ended); }
    }

    private async Task SendJsonAsync<T>(T message, CancellationToken ct)
        => await SendRawAsync(JsonSerializer.Serialize(message, EsoJson.Options), ct);

    private async Task SendRawAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try { await _socket!.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var frame = await _socket!.ReceiveAsync(buffer, ct);
            if (frame.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, frame.Count);
            if (frame.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _socket?.Dispose();
        _sendLock.Dispose();
        _cts?.Dispose();
    }
}
