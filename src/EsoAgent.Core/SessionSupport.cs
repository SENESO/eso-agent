using System.Text;

namespace EsoAgent.Core;

// Pure session-support logic for UI overhaul Phase 3, kept in Core so the
// numbers the session bar shows and the terminal's catch-up behaviour are
// unit-tested instead of eyeballed.

/// <summary>
/// Live statistics for one desktop session, computed from frame arrivals.
/// The desktop protocol carries no timestamps, so there is no honest RTT
/// to show; what the operator sees is what the client can measure itself:
/// arrival frame rate over a sliding window, stream throughput, and the
/// average frame size. Feed it every received frame; read snapshots on a
/// UI timer.
/// </summary>
public sealed class DesktopStatsTracker
{
    private const long WindowMs = 2000;
    private readonly Queue<(long Ms, int Bytes)> _samples = new();

    public long Frames { get; private set; }
    public long TotalBytes { get; private set; }
    public int LastFrameBytes { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Codec { get; private set; } = "";

    public void AddFrame(int bytes, int width, int height, string codec, long nowMs)
    {
        Frames++;
        TotalBytes += bytes;
        LastFrameBytes = bytes;
        Width = width;
        Height = height;
        Codec = codec;
        _samples.Enqueue((nowMs, bytes));
        Prune(nowMs);
    }

    private void Prune(long nowMs)
    {
        while (_samples.Count > 0 && _samples.Peek().Ms < nowMs - WindowMs)
            _samples.Dequeue();
    }

    /// <summary>Windowed rates. Fps counts frame intervals (n−1 gaps), so a
    /// single frame reports 0 rather than inventing a rate.</summary>
    public (double Fps, double BytesPerSecond, double AvgFrameBytes) Snapshot(long nowMs)
    {
        Prune(nowMs);
        if (_samples.Count < 2) return (0, 0, LastFrameBytes);
        var first = _samples.First();
        var last = _samples.Last();
        var spanMs = last.Ms - first.Ms;
        if (spanMs <= 0) return (0, 0, LastFrameBytes);
        long bytes = 0;
        foreach (var s in _samples) bytes += s.Bytes;
        var fps = (_samples.Count - 1) * 1000.0 / spanMs;
        var bps = bytes * 1000.0 / spanMs;
        var avg = (double)bytes / _samples.Count;
        return (fps, bps, avg);
    }

    public void Reset()
    {
        _samples.Clear();
        Frames = 0;
        TotalBytes = 0;
        LastFrameBytes = 0;
        Width = 0;
        Height = 0;
        Codec = "";
    }
}

/// <summary>
/// The client side of a persistent shell session's output: an append-only
/// text buffer fed by sequence-numbered chunks. Reattaching after the view
/// was away replays "everything since LastSeq"; chunks that overlap what
/// is already held are dropped by sequence, chunks that arrive out of
/// order within one reply are sorted first, and a gap (the agent evicted
/// old chunks from its bounded ring) is accepted — the text simply
/// continues, which is the honest tmux-style behaviour.
/// </summary>
public sealed class ShellSessionBuffer
{
    private readonly StringBuilder _text = new();
    private readonly int _maxChars;

    public ShellSessionBuffer(int maxChars = 512 * 1024) => _maxChars = maxChars;

    public long LastSeq { get; private set; }
    public string Text => _text.ToString();
    public bool IsEmpty => _text.Length == 0;

    /// <summary>Append a batch of chunks; returns how many were new.</summary>
    public int Append(IEnumerable<ShellSessionChunk> chunks)
    {
        var added = 0;
        foreach (var chunk in chunks.OrderBy(c => c.Seq))
        {
            if (chunk.Seq <= LastSeq) continue;
            _text.Append(chunk.Text);
            LastSeq = chunk.Seq;
            added++;
        }
        if (added > 0 && _text.Length > _maxChars)
        {
            // Trim from the front on a line boundary so the visible text
            // never starts mid-line; sequence tracking is unaffected.
            var s = _text.ToString();
            var drop = s.Length - _maxChars;
            var nl = s.IndexOf('\n', drop);
            _text.Clear();
            _text.Append(nl >= 0 ? s.AsSpan(nl + 1) : s.AsSpan(drop));
        }
        return added;
    }

    public void Clear()
    {
        _text.Clear();
        LastSeq = 0;
    }
}

/// <summary>The four phases a desktop session moves through. UI affordances
/// (the device-row Connect button, the session bar, the recents) render
/// from this one state — never from timers or optimism (owner verdict
/// 2026-11-22: "the Connect button is decorative; its state never changes
/// while the connection works").</summary>
public enum DesktopSessionPhase
{
    Idle,
    Connecting,
    Connected,
    Disconnecting,
}

/// <summary>
/// The desktop session lifecycle as a small guarded state machine, kept in
/// Core so the transitions are unit-tested. The view drives it from the
/// REAL lifecycle points only: connect start (the attempt begins), the
/// welcome frame (connected), the operator's disconnect or the client's
/// session-ended event (back to idle). Invalid transitions are no-ops, so
/// a stale session's late event can never resurrect or kill the state of
/// the session that replaced it.
/// </summary>
public sealed class DesktopSessionTracker
{
    public DesktopSessionPhase Phase { get; private set; } = DesktopSessionPhase.Idle;

    /// <summary>Fleet code of the device the active (or last attempted)
    /// session targets; null in Idle and for the direct device.</summary>
    public string? DeviceCode { get; private set; }

    /// <summary>Raised after every accepted transition.</summary>
    public event Action? Changed;

    public bool IsActive => Phase != DesktopSessionPhase.Idle;

    /// <summary>True while a session in <paramref name="phase"/> targets
    /// this device code (case-insensitive; codes are identity here).</summary>
    public bool IsActiveFor(string? deviceCode, DesktopSessionPhase? phase = null)
        => IsActive
           && deviceCode != null
           && string.Equals(DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase)
           && (phase == null || Phase == phase.Value);

    /// <summary>Idle → Connecting. Refused (false) while another session is
    /// in flight — the caller must end it first.</summary>
    public bool BeginConnect(string? deviceCode)
    {
        if (Phase != DesktopSessionPhase.Idle) return false;
        DeviceCode = deviceCode;
        Transition(DesktopSessionPhase.Connecting);
        return true;
    }

    /// <summary>Connecting → Connected (the welcome frame arrived).</summary>
    public bool MarkConnected()
    {
        if (Phase != DesktopSessionPhase.Connecting) return false;
        Transition(DesktopSessionPhase.Connected);
        return true;
    }

    /// <summary>Connecting → Idle (every connection path failed).</summary>
    public bool MarkConnectFailed()
    {
        if (Phase != DesktopSessionPhase.Connecting) return false;
        DeviceCode = null;
        Transition(DesktopSessionPhase.Idle);
        return true;
    }

    /// <summary>Connecting/Connected → Disconnecting (the operator asked
    /// to end it, or the view is detaching from the device).</summary>
    public bool BeginDisconnect()
    {
        if (Phase is not (DesktopSessionPhase.Connecting or DesktopSessionPhase.Connected)) return false;
        Transition(DesktopSessionPhase.Disconnecting);
        return true;
    }

    /// <summary>Any phase → Idle (teardown finished). Safe to call when
    /// already idle; the device code is cleared either way.</summary>
    public void MarkDisconnected()
    {
        DeviceCode = null;
        if (Phase != DesktopSessionPhase.Idle) Transition(DesktopSessionPhase.Idle);
    }

    /// <summary>Connected → Idle (the session ended on its own: the agent
    /// closed it or the transport dropped). A no-op from any other phase,
    /// so a replaced session's late end event cannot idle the new one.</summary>
    public bool MarkSessionEnded()
    {
        if (Phase != DesktopSessionPhase.Connected) return false;
        DeviceCode = null;
        Transition(DesktopSessionPhase.Idle);
        return true;
    }

    private void Transition(DesktopSessionPhase next)
    {
        Phase = next;
        Changed?.Invoke();
    }
}

/// <summary>Text hygiene for session-end reasons. The desktop clients end
/// a session with a plain reason ("Session closed by the agent.", the
/// agent's own close reason, "Disconnected.") — or, on a transport error,
/// with "Session error: " + a raw exception message. The raw form must
/// never reach the UI (owner order: plain-language status lines only), so
/// it sanitizes to null and the caller shows its generic line instead.</summary>
public static class DesktopSessionText
{
    public static string? SanitizeEndReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var trimmed = reason.Trim();
        if (trimmed.StartsWith("Session error:", StringComparison.OrdinalIgnoreCase)) return null;
        return trimmed;
    }
}
