using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EsoAgent.Core;

namespace EsoAgent.Windows;

/// <summary>
/// Service-side remote-desktop host: a desktop session is a DEDICATED
/// EsoAgentTrayPipe connection that turns into a bidirectional JSON-lines
/// stream after the open handshake — frames/clipboard flow tray→service,
/// input/clipboard/close flow service→tray. The tray (user session) does the
/// actual capture/injection; session 0 cannot. A missing tray is a clean error.
/// </summary>
public sealed class TrayDesktopHost : IDesktopHost
{
    public IReadOnlyCollection<string> SupportedCodecs => new[] { "jpeg" };

    public async Task<(IDesktopSession? Session, string? Error)> OpenAsync(DesktopHello hello, string codec, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", TrayPipeForwarder.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(timeout.Token);
        }
        catch
        {
            pipe.Dispose();
            return (null, "Tray companion is not running (could not connect to the EsoAgentTrayPipe pipe). Start EsoAgent.Tray in the user session.");
        }

        var session = new TrayDesktopSession(pipe);
        var (ok, error) = await session.OpenAsync(hello, codec, ct);
        if (!ok)
        {
            await session.DisposeAsync();
            return (null, error ?? "The tray refused the desktop session.");
        }
        return (session, null);
    }

    /// <summary>Attended-session consent (v1.2): asks the tray to prompt the
    /// local user. Any failure — no tray, timeout, dismissal — is a NO.</summary>
    public Task<bool> RequestConsentAsync(string mode, TimeSpan timeout, CancellationToken ct = default)
        => RequestConsentAsync(new ConsentRequest { Mode = mode, Requester = "a remote operator" }, timeout, ct);

    /// <summary>Consent v2 (Phase 5): the tray prompt also names the
    /// requester and lists the session's permissions (requester/clipboard
    /// ride along in the pipe payload; an older tray ignores them).</summary>
    public async Task<bool> RequestConsentAsync(ConsentRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout + TimeSpan.FromSeconds(15));
            using var pipe = new NamedPipeClientStream(".", TrayPipeForwarder.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutCts.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };
            var payload = new JsonObject
            {
                ["action"] = "consent",
                ["mode"] = request.Mode,
                ["timeoutSeconds"] = Math.Clamp((int)timeout.TotalSeconds, 5, 300),
                ["requester"] = request.Requester,
                ["clipboard"] = request.Clipboard,
            };
            await writer.WriteLineAsync(payload.ToJsonString(EsoJson.Options).AsMemory(), timeoutCts.Token);
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            if (line == null) return false;
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch { return false; }
    }
}

public sealed class TrayDesktopSession : IDesktopSession
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentQueue<DesktopFrame> _frames = new();
    private readonly SemaphoreSlim _frameSignal = new(0);
    private readonly ConcurrentQueue<string> _deviceChat = new();
    private string? _clipboard;
    private string? _clipboardSeen;
    private bool _privacyOn;
    private CancellationTokenSource? _readCts;

    public string Codec { get; private set; } = "jpeg";
    public MonitorInfo Monitor { get; private set; } = new();
    public IReadOnlyList<MonitorInfo> AllMonitors { get; private set; } = Array.Empty<MonitorInfo>();

    public TrayDesktopSession(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 8192, leaveOpen: true) { AutoFlush = true };
    }

    public async Task<(bool Ok, string? Error)> OpenAsync(DesktopHello hello, string codec, CancellationToken ct)
    {
        Codec = codec;
        var payload = new JsonObject
        {
            ["action"] = "desktop-open",
            ["codec"] = codec,
            ["hello"] = JsonSerializer.SerializeToNode(hello, EsoJson.Options),
        };
        await WriteLineAsync(payload.ToJsonString(EsoJson.Options), ct);
        var line = await _reader.ReadLineAsync(ct);
        if (line == null) return (false, "Tray closed the connection during the desktop handshake.");
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            return (false, root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown tray error.");
        AllMonitors = root.GetProperty("monitors").Deserialize<List<MonitorInfo>>(EsoJson.Options) ?? new();
        Monitor = AllMonitors.FirstOrDefault(m => m.Index == hello.Monitor) ?? AllMonitors.FirstOrDefault() ?? new MonitorInfo();
        _readCts = new CancellationTokenSource();
        _ = ReadLoopAsync(_readCts.Token);
        return (true, null);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line == null) break;
                using var doc = JsonDocument.Parse(line);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "frame")
                {
                    var frame = JsonSerializer.Deserialize<DesktopFrame>(line, EsoJson.Options);
                    if (frame != null) { _frames.Enqueue(frame); _frameSignal.Release(); }
                }
                else if (type == "clipboard")
                {
                    var clip = JsonSerializer.Deserialize<DesktopClipboard>(line, EsoJson.Options);
                    if (clip != null) _clipboard = clip.Text;
                }
                else if (type == "chat")
                {
                    var chat = JsonSerializer.Deserialize<DesktopChat>(line, EsoJson.Options);
                    if (chat != null && chat.From == "device") _deviceChat.Enqueue(chat.Text);
                }
            }
        }
        catch { /* stream ended */ }
    }

    public async Task<DesktopFrame?> NextFrameAsync(CancellationToken ct = default)
    {
        // Wait briefly for a frame; null = "nothing new" so the runner can poll the kill switch.
        try { await _frameSignal.WaitAsync(TimeSpan.FromMilliseconds(250), ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        return _frames.TryDequeue(out var frame) ? frame : null;
    }

    public async Task SendInputAsync(DesktopInput input, CancellationToken ct = default)
        => await WriteLineAsync(JsonSerializer.Serialize(input, EsoJson.Options), ct);

    public Task<string?> GetClipboardAsync(CancellationToken ct = default)
    {
        var current = _clipboard;
        if (current == _clipboardSeen) return Task.FromResult<string?>(null);
        _clipboardSeen = current;
        return Task.FromResult(current);
    }

    public async Task SetClipboardAsync(string text, CancellationToken ct = default)
    {
        _clipboardSeen = text; // don't echo it back
        await WriteLineAsync(JsonSerializer.Serialize(new DesktopClipboard { Text = text }, EsoJson.Options), ct);
    }

    /// <summary>Privacy mode (v1.2): the tray covers every screen with a black
    /// overlay and drops remote input while it is on; the stream itself keeps
    /// flowing (the operator still sees the real desktop).</summary>
    public async Task<bool> SetPrivacyAsync(bool enabled, CancellationToken ct = default)
    {
        _privacyOn = enabled;
        await WriteLineAsync(JsonSerializer.Serialize(new DesktopPrivacy { Enabled = enabled }, EsoJson.Options), ct);
        return true; // the tray applies the overlay itself; a dead pipe throws above and the runner reports the failure
    }

    public async Task SendChatAsync(string text, CancellationToken ct = default)
        => await WriteLineAsync(JsonSerializer.Serialize(new DesktopChat { Text = text, From = "operator" }, EsoJson.Options), ct);

    public Task<string?> GetChatAsync(CancellationToken ct = default)
        => Task.FromResult(_deviceChat.TryDequeue(out var text) ? text : null);

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try { await _writer.WriteLineAsync(line.AsMemory(), ct); }
        finally { _writeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_privacyOn)
        {
            try { await WriteLineAsync(JsonSerializer.Serialize(new DesktopPrivacy { Enabled = false }, EsoJson.Options), CancellationToken.None); } catch { }
        }
        try { await WriteLineAsync(JsonSerializer.Serialize(new DesktopClose { Reason = "client disconnected" }, EsoJson.Options), CancellationToken.None); } catch { }
        try { _readCts?.Cancel(); } catch { }
        _reader.Dispose();
        _writer.Dispose();
        _pipe.Dispose();
    }
}
