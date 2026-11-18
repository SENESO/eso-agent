using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace EsoAgent.Core;

/// <summary>
/// The transport a desktop session runs over: the direct/panel WebSocket, or
/// the fleet relay's stream channel (v1.2). The runner speaks desktop-protocol
/// JSON text messages either way — the relay just carries them.
/// </summary>
public interface IDesktopChannel
{
    Task SendTextAsync(string json, CancellationToken ct);
    /// <summary>Next text message, or null when the peer closed.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken ct);
}

/// <summary>Adapts a WebSocket to <see cref="IDesktopChannel"/>.</summary>
public sealed class WebSocketDesktopChannel : IDesktopChannel
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public WebSocketDesktopChannel(WebSocket ws) => _ws = ws;

    public async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(ct);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendGate.Release(); }
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
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
}

/// <summary>
/// Runs one desktop session over a channel: handshake → welcome → frame pump +
/// input/clipboard receive loop. Shared by the HMAC API endpoint (/api/desktop,
/// operator), the owner panel endpoint (/panel/desktop, owner session) and the
/// fleet relay stream (v1.2, origin "fleet" — the stream-open was verified
/// against the device's pinned keys before this runs).
/// Safety: the owner's kill switch ends any live session immediately; session
/// start/stop/mode, recording and privacy toggles are audit-logged; input is
/// honored only in Control mode.
/// </summary>
public static class DesktopStreamRunner
{
    public static async Task RunAsync(WebSocket ws, AgentState state, string origin, CancellationToken ct)
    {
        var channel = new WebSocketDesktopChannel(ws);
        await RunAsync(channel, state, origin, ct);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
    }

    public static async Task RunAsync(IDesktopChannel channel, AgentState state, string origin, CancellationToken ct)
    {
        var helloJson = await channel.ReceiveTextAsync(ct);
        if (helloJson == null) return;
        DesktopHello hello;
        try { hello = JsonSerializer.Deserialize<DesktopHello>(helloJson, EsoJson.Options) ?? new DesktopHello(); }
        catch { await SendAsync(channel, new DesktopWelcome { Error = "Bad hello frame." }, ct); return; }

        var mode = DesktopNegotiation.NormalizeMode(hello.Mode);
        var host = state.Platform.DesktopHost;
        if (host == null)
        {
            await SendAsync(channel, new DesktopWelcome { Error = "Remote desktop is not available on this agent (no user-session host)." }, ct);
            return;
        }
        var codec = DesktopNegotiation.PickCodec(host.SupportedCodecs, hello.Codecs);
        if (codec == null)
        {
            await SendAsync(channel, new DesktopWelcome { Error = $"No common codec. Host offers: {string.Join(", ", host.SupportedCodecs)}." }, ct);
            return;
        }

        // Capability policy (v1.2): the owner can disable desktop CONTROL —
        // view-only sessions still work, control is refused before anything opens.
        if (mode == "Control" && !state.Config.Capabilities.DesktopControl)
        {
            state.Audit.Write("desktop", $"session refused ({origin}): desktop control disabled by owner capability policy", "refused");
            await SendAsync(channel, new DesktopWelcome { Error = "Desktop control is disabled on this device by the owner's capability policy." }, ct);
            return;
        }

        // Consent policy (v1.2): attended sessions — when the owner requires
        // it, the person AT the device must accept on screen first. No prompt
        // mechanism or a timeout = denied (fail closed).
        if (state.Config.DesktopPolicy.RequiresConsent(mode))
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(state.Config.DesktopPolicy.ConsentTimeoutSeconds, 5, 120));
            var accepted = false;
            try { accepted = await host.RequestConsentAsync(mode, timeout, ct); }
            catch { accepted = false; }
            if (!accepted)
            {
                state.Audit.Write("desktop", $"session refused ({origin}): local consent not given for mode={mode}", "refused");
                await SendAsync(channel, new DesktopWelcome { Error = "The person at this device did not accept the session (owner consent policy)." }, ct);
                return;
            }
            state.Audit.Write("desktop", $"local consent granted ({origin}) mode={mode}", "ok");
        }

        var (session, error) = await host.OpenAsync(hello, codec, ct);
        if (session == null)
        {
            await SendAsync(channel, new DesktopWelcome { Error = error ?? "Could not open a desktop session." }, ct);
            return;
        }

        // Session recording (v1.2): opt-in per session via the hello. The file
        // lives under the data dir's recordings/ folder; the welcome carries
        // its name so every face (app/panel/CLI) can show the ●REC state.
        SessionRecorder? recorder = null;
        if (hello.Record)
        {
            try { recorder = new SessionRecorder(state.RecordingsDir, origin, codec, session.Monitor.Width, session.Monitor.Height); }
            catch (Exception ex) { state.Audit.Write("desktop", $"recording could not start: {ex.Message}", "error"); }
        }

        state.Audit.Write("desktop",
            $"session started ({origin}) mode={mode} codec={codec} monitor={session.Monitor.Index}" +
            (recorder != null ? $" recording={recorder.FileName}" : ""), "ok");
        try
        {
            await SendAsync(channel, new DesktopWelcome
            {
                Mode = mode,
                Codec = codec,
                Monitors = session.AllMonitors.ToList(),
                Width = session.Monitor.Width,
                Height = session.Monitor.Height,
                Recording = recorder?.FileName,
            }, ct);

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var receive = ReceiveLoopAsync(channel, session, mode, state, sessionCts,
                (frame, token) => SendAsync(channel, frame, token));
            var lastClipboard = (string?)null;
            var lastClipboardPoll = DateTimeOffset.MinValue;

            // Adaptive streaming (item 37): measure egress per ~2 s window and
            // retune quality/fps within the legibility floor. The Windows tray
            // session also adapts internally; SetStreamParams is a no-op there.
            var adaptQuality = Math.Clamp(hello.Quality <= 0 ? 60 : hello.Quality, 10, 95);
            var adaptFps = Math.Clamp(hello.MaxFps <= 0 ? 10 : hello.MaxFps, 1, 30);
            var requestedQuality = adaptQuality;
            var requestedFps = adaptFps;
            var windowStart = DateTimeOffset.UtcNow;
            long windowBytes = 0;
            var windowFrames = 0;
            var floorNotified = false;

            while (!sessionCts.Token.IsCancellationRequested)
            {
                if (state.KillSwitch.IsLocked)
                {
                    await SendAsync(channel, new DesktopClose { Reason = "Agent locked by the owner's kill switch." }, ct);
                    break;
                }
                DesktopFrame? frame;
                try { frame = await session.NextFrameAsync(sessionCts.Token); }
                catch (OperationCanceledException) { break; }
                if (frame != null)
                {
                    await SendAsync(channel, frame, ct);
                    recorder?.Record(frame);
                    windowBytes += frame.Data.Length;
                    windowFrames++;
                }

                // Adaptive evaluation at window boundaries (not per frame).
                if (windowFrames > 0 && DateTimeOffset.UtcNow - windowStart >= TimeSpan.FromSeconds(2))
                {
                    var elapsed = (DateTimeOffset.UtcNow - windowStart).TotalSeconds;
                    var adaptation = AdaptiveQuality.Evaluate(adaptQuality, adaptFps, requestedQuality, requestedFps,
                        windowBytes / elapsed, (double)windowBytes / windowFrames);
                    if (adaptation.Changed)
                    {
                        adaptQuality = adaptation.Quality;
                        adaptFps = adaptation.Fps;
                        try { session.SetStreamParams(adaptQuality, adaptFps); } catch { }
                    }
                    if (adaptation.BelowFloor && !floorNotified)
                    {
                        floorNotified = true;
                        state.Audit.Write("desktop", $"bandwidth below legibility floor ({origin}): {adaptation.Reason}", "warn");
                        await SendAsync(channel, new DesktopNotice
                        {
                            Kind = "bandwidth-floor",
                            Message = "This connection cannot sustain the legibility floor (quality "
                                + $"{AdaptiveQuality.FloorQuality}, {AdaptiveQuality.FloorFps} fps). "
                                + "Streaming continues AT the floor — expect a slow picture rather than an unreadable one.",
                        }, ct);
                    }
                    else if (!adaptation.BelowFloor) floorNotified = false;
                    windowStart = DateTimeOffset.UtcNow;
                    windowBytes = 0;
                    windowFrames = 0;
                }

                if (DateTimeOffset.UtcNow - lastClipboardPoll > TimeSpan.FromSeconds(2))
                {
                    lastClipboardPoll = DateTimeOffset.UtcNow;
                    if (state.Config.Capabilities.Clipboard)
                    {
                        try
                        {
                            var clip = await session.GetClipboardAsync(sessionCts.Token);
                            if (clip != null && clip != lastClipboard)
                            {
                                lastClipboard = clip;
                                await SendAsync(channel, new DesktopClipboard { Text = clip }, ct);
                            }
                        }
                        catch { /* clipboard is best-effort */ }
                    }
                    try
                    {
                        var chat = await session.GetChatAsync(sessionCts.Token);
                        if (!string.IsNullOrEmpty(chat))
                        {
                            state.Audit.Write("desktop", $"chat device->operator ({chat.Length} chars)", "ok");
                            await SendAsync(channel, new DesktopChat { Text = chat, From = "device" }, ct);
                        }
                    }
                    catch { /* chat is best-effort */ }
                }
            }
            sessionCts.Cancel();
            try { await receive; } catch { }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            state.Audit.Write("desktop", $"session error: {ex.Message}", "error");
        }
        finally
        {
            if (recorder != null)
            {
                await recorder.DisposeAsync();
                state.Audit.Write("desktop", $"session recording saved: {recorder.FileName}", "ok");
            }
            await session.DisposeAsync();
            state.Audit.Write("desktop", $"session ended ({origin}) mode={mode}", "ok");
        }
    }

    private static async Task ReceiveLoopAsync(IDesktopChannel channel, IDesktopSession session, string mode,
        AgentState state, CancellationTokenSource sessionCts, Func<object, CancellationToken, Task> send)
    {
        try
        {
            while (!sessionCts.Token.IsCancellationRequested)
            {
                var json = await channel.ReceiveTextAsync(sessionCts.Token);
                if (json == null) break;
                using var doc = JsonDocument.Parse(json);
                var type = doc.RootElement.GetProperty("type").GetString();
                switch (type)
                {
                    case "input":
                        if (mode != "Control") break; // view-only: input is dropped by design
                        var input = JsonSerializer.Deserialize<DesktopInput>(json, EsoJson.Options);
                        if (input != null) await session.SendInputAsync(input, sessionCts.Token);
                        break;
                    case "clipboard":
                        if (!state.Config.Capabilities.Clipboard) break; // owner disabled clipboard sync
                        var clip = JsonSerializer.Deserialize<DesktopClipboard>(json, EsoJson.Options);
                        if (clip != null) await session.SetClipboardAsync(clip.Text, sessionCts.Token);
                        break;
                    case "chat":
                    {
                        // In-session chat (v1.2): delivered to the person at the
                        // device via the session host; content is NOT logged —
                        // only direction + length, like a phone call record.
                        var chat = JsonSerializer.Deserialize<DesktopChat>(json, EsoJson.Options);
                        if (chat != null && !string.IsNullOrWhiteSpace(chat.Text))
                        {
                            state.Audit.Write("desktop", $"chat operator->device ({chat.Text.Length} chars)", "ok");
                            try { await session.SendChatAsync(chat.Text, sessionCts.Token); }
                            catch (Exception ex) { state.Audit.Write("desktop", $"chat delivery failed: {ex.Message}", "error"); }
                        }
                        break;
                    }
                    case "privacy":
                    {
                        // Privacy mode (v1.2): blank the device's screen while the
                        // session streams on. Honored in any mode (it protects the
                        // person at the machine), audit-logged, and echoed back so
                        // the client shows the authoritative state.
                        var privacy = JsonSerializer.Deserialize<DesktopPrivacy>(json, EsoJson.Options);
                        if (privacy != null)
                        {
                            // The session reports whether the requested state is
                            // ACTUALLY in effect; the echo is that truth, never
                            // the request. A head that cannot blank (e.g. an X
                            // server without DPMS, §13) must not let the operator
                            // believe the screen went dark when it did not.
                            var applied = !privacy.Enabled;
                            try { applied = await session.SetPrivacyAsync(privacy.Enabled, sessionCts.Token); }
                            catch (Exception ex) { state.Audit.Write("desktop", $"privacy toggle failed: {ex.Message}", "error"); }
                            if (applied)
                            {
                                state.Audit.Write("desktop", $"privacy mode {(privacy.Enabled ? "ON (screen blanked)" : "OFF (screen restored)")}", "ok");
                                await send(privacy, sessionCts.Token);
                            }
                            else
                            {
                                state.Audit.Write("desktop", "privacy mode NOT applied: this device cannot blank its screen (privacy unavailable here)", "error");
                                await SendAsync(channel, new DesktopNotice
                                {
                                    Kind = "privacy-unavailable",
                                    Message = "Privacy mode is unavailable on this device — the screen was NOT blanked.",
                                }, sessionCts.Token);
                                await send(new DesktopPrivacy { Enabled = false }, sessionCts.Token);
                            }
                        }
                        break;
                    }
                    case "close":
                        sessionCts.Cancel();
                        break;
                }
            }
        }
        catch { /* receive loop ends with the session */ }
        finally { sessionCts.Cancel(); }
    }

    private static async Task SendAsync<T>(IDesktopChannel channel, T frame, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(frame, EsoJson.Options);
        await channel.SendTextAsync(json, ct);
    }
}
