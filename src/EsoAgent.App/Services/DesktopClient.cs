using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EsoAgent.Client;
using EsoAgent.Core;

namespace EsoAgent.App.Services;

/// <summary>
/// One remote-desktop session against an agent's /api/desktop WebSocket
/// (operator path: HMAC-signed upgrade, cert pinned by SHA-256 fingerprint —
/// the same scheme <see cref="ApiClient.ExecStreamAsync"/> uses for the exec
/// stream). Speaks the Core desktop protocol (DesktopHello/Welcome/Frame/
/// Input/Clipboard) and raises events; all rendering stays in the view.
/// This is the DIRECT path; the App falls back to EsoAgent.Client's
/// RelayDesktopClient (same protocol through the fleet server) when a fleet
/// device cannot be reached directly.
/// </summary>
public sealed class DesktopClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public event Action<DesktopFrame>? FrameReceived;
    public event Action<string>? ClipboardReceived;
    public event Action<string>? ChatReceived;
    public event Action<bool>? PrivacyReceived;
    /// <summary>Raised when the session ends: server close frame reason, error text, or local disconnect.</summary>
    public event Action<string>? SessionEnded;

    public bool IsConnected => _ws.State == WebSocketState.Open;

    /// <summary>Connects, sends the hello and returns the negotiated welcome (throws on a welcome error).</summary>
    public async Task<DesktopWelcome> ConnectAsync(
        ClientConfig cfg, string host, int port, DesktopHello hello, CancellationToken ct = default)
    {
        var fingerprint = cfg.Fingerprint.Trim();
        _ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert != null && string.Equals(
                Convert.ToHexString(SHA256.HashData(new X509Certificate2(cert).GetRawCertData())).ToLowerInvariant(),
                fingerprint, StringComparison.OrdinalIgnoreCase);

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = HmacAuth.Sign(cfg.TokenBytes, "GET", "/api/desktop", ts, Array.Empty<byte>());
        _ws.Options.SetRequestHeader(HmacAuth.TimestampHeader, ts.ToString());
        _ws.Options.SetRequestHeader(HmacAuth.SignatureHeader, sig);

        await _ws.ConnectAsync(new Uri($"wss://{host}:{port}/api/desktop"), ct);
        await SendJsonAsync(hello, ct);

        var welcomeJson = await ReceiveTextAsync(ct)
            ?? throw new InvalidOperationException("The agent closed the desktop session without a welcome.");
        var welcome = JsonSerializer.Deserialize<DesktopWelcome>(welcomeJson, EsoJson.Options)
                      ?? throw new InvalidOperationException("The agent sent a malformed desktop welcome.");
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
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch { }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var ended = "Session closed by the agent.";
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
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
                        if (chat != null) ChatReceived?.Invoke(chat.Text);
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
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, EsoJson.Options);
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var frame = await _ws.ReceiveAsync(buffer, ct);
            if (frame.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, frame.Count);
            if (frame.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _ws.Dispose();
        _sendLock.Dispose();
        _cts?.Dispose();
    }
}
