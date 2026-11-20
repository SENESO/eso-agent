namespace EsoAgent.Core;

// Remote desktop (update 7): TeamViewer-class live view + control, hosted by the
// user-session companion (a service in session 0 cannot capture or inject input).
//
// Session flow over WebSocket:
//   client → server : DesktopHello  { mode, codecs[], monitor }
//   server → client : DesktopWelcome{ mode, codec, monitors[], width, height } | { error }
//   server → client : DesktopFrame  { seq, codec, full, width, height, data(base64) }
//                     — codec "jpeg": data is a whole JPEG (host-encoded).
//                     — codec "rawdiff": data is a tile-diff packet (see FramePipeline).
//   client → server : DesktopInput  (mouse/keyboard; ignored unless mode=Control)
//   both ways       : DesktopClipboard { text }
//   either          : { "type": "close" }
//
// The frame channel is codec-negotiated so a hardware H.264 codec (Media
// Foundation / VAAPI) can slot in later without protocol changes. v1 codecs are
// "jpeg" (adaptive quality) and "rawdiff" (tile diff + deflate, pure managed).

public sealed class MonitorInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Primary { get; set; }
}

public sealed class DesktopHello
{
    public string Type { get; set; } = "hello";
    /// <summary>View | Control — explicit in the handshake; input is honored only in Control.</summary>
    public string Mode { get; set; } = "View";
    public List<string> Codecs { get; set; } = new();
    public int Monitor { get; set; }
    public int Quality { get; set; } = 60;   // jpeg quality hint 10..95
    public int MaxFps { get; set; } = 10;
    /// <summary>Opt-in session recording (v1.2): when true, the agent records the
    /// frames it sends to an .esorec file under its data dir (see SessionRecorder).</summary>
    public bool Record { get; set; }
}

public sealed class DesktopWelcome
{
    public string Type { get; set; } = "welcome";
    public string? Error { get; set; }
    public string Mode { get; set; } = "";
    public string Codec { get; set; } = "";
    public List<MonitorInfo> Monitors { get; set; } = new();
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Recording file name on the agent when the session is being recorded (v1.2), else null.</summary>
    public string? Recording { get; set; }
}

/// <summary>
/// Privacy mode (v1.2): either side may toggle it mid-session. When enabled,
/// the device blanks its physical screen (and, where the platform supports
/// it, locks local input) while the remote view keeps streaming — the
/// TeamViewer "black screen". The runner echoes the frame back as the
/// authoritative state, and every toggle is audit-logged.
/// </summary>
/// <summary>An out-of-band session notice (v1.2): the runner uses it to say
/// something the operator must know — currently "bandwidth-floor", raised
/// when the link cannot sustain even the legibility floor (the stream keeps
/// running AT the floor; it never silently degrades below it).</summary>
public sealed class DesktopNotice
{
    public string Type { get; set; } = "notice";
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class DesktopPrivacy
{
    public string Type { get; set; } = "privacy";
    public bool Enabled { get; set; }
}

public sealed class DesktopFrame
{
    public string Type { get; set; } = "frame";
    public long Seq { get; set; }
    public string Codec { get; set; } = "";
    /// <summary>True when the packet is a full frame (rawdiff keyframe); false = tile diff.</summary>
    public bool Full { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Data { get; set; } = "";   // base64
}

public sealed class DesktopInput
{
    public string Type { get; set; } = "input";
    /// <summary>mouseMove | mouseButton | wheel | key</summary>
    public string Kind { get; set; } = "";
    public double X { get; set; }            // normalized 0..1 across the captured monitor
    public double Y { get; set; }
    public string? Button { get; set; }      // left | middle | right
    public bool Down { get; set; }
    public int Delta { get; set; }           // wheel
    /// <summary>Key name: single char, or a special name (Enter, Escape, Tab, Backspace,
    /// Delete, Home, End, PageUp, PageDown, ArrowUp/…, F1..F12) or a combo "ctrl+alt+delete".</summary>
    public string? Key { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Meta { get; set; }
}

public sealed class DesktopClipboard
{
    public string Type { get; set; } = "clipboard";
    public string Text { get; set; } = "";
}

public sealed class DesktopClose
{
    public string Type { get; set; } = "close";
    public string? Reason { get; set; }
}

/// <summary>
/// In-session text chat (v1.2): operator ↔ the person at the device, both
/// directions over the same session channel. From is "operator" (client →
/// device) or "device" (device → client, set by the runner). Every message
/// is audit-logged on the device (text itself is not — only its length and
/// direction, so private chat content does not land in logs).
/// </summary>
public sealed class DesktopChat
{
    public string Type { get; set; } = "chat";
    public string Text { get; set; } = "";
    public string From { get; set; } = "operator";
}

/// <summary>
/// Named desktop quality presets (UI overhaul Phase 3) — the session bar
/// offers names, not a raw 10–95 spinner. Every value stays inside the
/// ranges the agents already enforce: quality 10..95, fps clamped to
/// 1..30 by every capture path (DesktopStreamRunner, the Windows tray
/// handler, the Linux helper), and the stream adapts DOWN from the
/// requested ceiling to the measured link (AdaptiveQuality), never below
/// the legibility floor. Auto is the adaptive default: the highest
/// ceiling the protocol carries, with adaptation doing the tuning — it
/// replaces the app's old hard-coded 12 fps cap.
/// </summary>
public enum DesktopQualityPreset { Auto, Balanced, Speed, Quality }

public static class DesktopPresets
{
    /// <summary>The highest MaxFps any agent capture path will honour.</summary>
    public const int MaxFpsCeiling = 30;

    public static DesktopQualityPreset Default => DesktopQualityPreset.Auto;

    public static (int Quality, int MaxFps) For(DesktopQualityPreset preset) => preset switch
    {
        // Motion first: the legibility-floor quality, full frame-rate ceiling.
        DesktopQualityPreset.Speed => (40, 30),
        // A steadier middle: session quality, a capped rate for quiet links.
        DesktopQualityPreset.Balanced => (60, 20),
        // Sharpness first: near-lossless stills, motion traded away.
        DesktopQualityPreset.Quality => (88, 15),
        // Adaptive default: standard quality, full ceiling, agent-side
        // adaptation tunes the real rate/quality to the measured link.
        _ => (60, 30),
    };

    public static bool TryParse(string? name, out DesktopQualityPreset preset) =>
        Enum.TryParse(name, ignoreCase: true, out preset);
}

/// <summary>Codec/mode negotiation — pure logic, unit-tested.</summary>
public static class DesktopNegotiation
{
    /// <summary>Server preference order. jpeg first (smallest), rawdiff as the always-available baseline.</summary>
    public static readonly string[] ServerCodecPreference = { "jpeg", "rawdiff" };

    public static string? PickCodec(IReadOnlyCollection<string> hostCodecs, IReadOnlyCollection<string> clientCodecs)
    {
        foreach (var codec in ServerCodecPreference)
            if (hostCodecs.Contains(codec) && clientCodecs.Contains(codec)) return codec;
        return null;
    }

    public static string NormalizeMode(string? mode)
        => string.Equals(mode, "Control", StringComparison.OrdinalIgnoreCase) ? "Control" : "View";
}
