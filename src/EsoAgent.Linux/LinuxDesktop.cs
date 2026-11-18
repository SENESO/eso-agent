using System.Security.Cryptography;
using EsoAgent.Core;

namespace EsoAgent.Linux;

/// <summary>
/// Linux remote-desktop host (X11). Capture: scrot/import → PNG → ImageMagick
/// `convert` to JPEG (the negotiated "jpeg" codec), with unchanged-frame skip
/// by content hash. Input: xdotool. Clipboard: xclip/xsel. All of it runs with
/// DISPLAY pointed at the owner's session (:0 by default).
/// Wayland is refused cleanly — portal capture is a later phase; never faked.
/// Live verification is PENDING FIRST REAL INSTALL (this build sandbox has no display).
/// </summary>
public sealed class LinuxDesktopHost : IDesktopHost
{
    private readonly IProcessRunner _runner;

    public LinuxDesktopHost(IProcessRunner runner) => _runner = runner;

    public IReadOnlyCollection<string> SupportedCodecs
        => FindTool("scrot") != null || FindTool("import") != null
            ? (FindTool("convert") != null ? new[] { "jpeg" } : Array.Empty<string>())
            : Array.Empty<string>();

    public async Task<(IDesktopSession? Session, string? Error)> OpenAsync(DesktopHello hello, string codec, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return (null, "Remote desktop is not available on Wayland yet (portal capture is a later phase).");

        var capture = FindTool("scrot") ?? FindTool("import");
        if (capture == null)
            return (null, "No X11 capture tool available (looked for scrot, import).");
        if (FindTool("convert") == null)
            return (null, "ImageMagick 'convert' is required for JPEG encoding and was not found.");

        var session = new LinuxDesktopSession(_runner, capture, hello);
        var probe = await session.ProbeAsync(ct);
        if (probe != null) return (null, probe);
        return (session, null);
    }

    /// <summary>Attended-session consent (v1.2): a zenity/kdialog prompt in the
    /// owner's session with a hard timeout — no answer (or no prompt tool)
    /// means NO. The prompt runs as the daemon with the session env, which
    /// works on a standard X11 login; where it cannot reach the display it
    /// fails closed, as the policy intends.</summary>
    /// <summary>The consent prompt's text for a session mode. The runner
    /// passes the normalized mode ("View" / "Control") — the comparison
    /// MUST stay case-insensitive: an exact "control" match here once made
    /// every Control request prompt as VIEW (§13).</summary>
    internal static string ConsentPromptText(string mode)
        => string.Equals(mode, "Control", StringComparison.OrdinalIgnoreCase)
            ? "A remote operator is asking to CONTROL this computer. Allow?"
            : "A remote operator is asking to VIEW this computer's screen. Allow?";

    public async Task<bool> RequestConsentAsync(string mode, TimeSpan timeout, CancellationToken ct = default)
    {
        var seconds = Math.Clamp((int)timeout.TotalSeconds, 5, 300);
        var text = ConsentPromptText(mode);
        var zenity = FindTool("zenity");
        if (zenity != null)
        {
            var r = await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
            {
                File = FindTool("timeout") ?? "timeout",
                Args = new[] { seconds.ToString(), zenity, "--question", "--title=EsoAgent", "--text", text, "--ok-label=Allow", "--cancel-label=Deny" },
                Env = SessionEnv(), TimeoutMs = (seconds + 10) * 1000,
            }, ct);
            return r.ExitCode == 0;
        }
        var kdialog = FindTool("kdialog");
        if (kdialog != null)
        {
            var r = await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
            {
                File = FindTool("timeout") ?? "timeout",
                Args = new[] { seconds.ToString(), kdialog, "--title", "EsoAgent", "--yesno", text },
                Env = SessionEnv(), TimeoutMs = (seconds + 10) * 1000,
            }, ct);
            return r.ExitCode == 0;
        }
        return false; // no way to ask → deny (fail closed)
    }

    internal static string? FindTool(string name)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            try
            {
                if (File.Exists(candidate)
                    && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    return candidate;
            }
            catch { }
        }
        return null;
    }

    internal static Dictionary<string, string> SessionEnv()
    {
        var env = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) env["DISPLAY"] = ":0";
        return env;
    }
}

public sealed class LinuxDesktopSession : IDesktopSession
{
    private readonly IProcessRunner _runner;
    private readonly string _captureTool;
    private readonly DesktopHello _hello;
    private readonly string _workDir;
    private byte[]? _lastHash;
    private long _seq;
    private DateTimeOffset _lastFrameAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public string Codec => "jpeg";
    public MonitorInfo Monitor { get; private set; } = new() { Index = 0, Name = "X11 display", Primary = true };
    public IReadOnlyList<MonitorInfo> AllMonitors => new[] { Monitor };

    public LinuxDesktopSession(IProcessRunner runner, string captureTool, DesktopHello hello)
    {
        _runner = runner;
        _captureTool = captureTool;
        _hello = hello;
        _workDir = Path.Combine(Path.GetTempPath(), $"eso-desktop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    /// <summary>Captures once to learn the display size; returns an error string or null.</summary>
    public async Task<string?> ProbeAsync(CancellationToken ct)
    {
        var frame = await CaptureJpegAsync(ct);
        if (frame == null) return "X11 capture failed (no reachable display at DISPLAY=:0, or the capture tool errored).";
        return null;
    }

    private int _qualityOverride;
    private int _fpsOverride;

    /// <summary>Adaptive streaming retune (item 37): volatile overrides read
    /// per frame; 0 = use the hello values.</summary>
    public void SetStreamParams(int quality, int maxFps)
    {
        _qualityOverride = quality;
        _fpsOverride = maxFps;
    }

    public async Task<DesktopFrame?> NextFrameAsync(CancellationToken ct = default)
    {
        var fps = Math.Clamp(_fpsOverride > 0 ? _fpsOverride : _hello.MaxFps <= 0 ? 8 : _hello.MaxFps, 1, 30);
        var interval = TimeSpan.FromSeconds(1.0 / fps);
        var wait = interval - (DateTimeOffset.UtcNow - _lastFrameAt);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        _lastFrameAt = DateTimeOffset.UtcNow;

        var jpeg = await CaptureJpegAsync(ct);
        if (jpeg == null) return null;
        var hash = SHA256.HashData(jpeg);
        if (_lastHash != null && hash.AsSpan().SequenceEqual(_lastHash)) return null; // unchanged → skip
        _lastHash = hash;
        return new DesktopFrame
        {
            Seq = ++_seq, Codec = "jpeg", Full = true,
            Width = Monitor.Width, Height = Monitor.Height,
            Data = Convert.ToBase64String(jpeg),
        };
    }

    private async Task<byte[]?> CaptureJpegAsync(CancellationToken ct)
    {
        var png = Path.Combine(_workDir, "frame.png");
        var jpg = Path.Combine(_workDir, "frame.jpg");
        var toolName = Path.GetFileName(_captureTool);
        var args = toolName == "scrot" ? new[] { png } : new[] { "-window", "root", png };
        var cap = await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
        {
            File = _captureTool, Args = args, Env = LinuxDesktopHost.SessionEnv(), TimeoutMs = 15_000,
        }, ct);
        if (cap.ExitCode != 0 || !File.Exists(png)) return null;

        var quality = Math.Clamp(_qualityOverride > 0 ? _qualityOverride : _hello.Quality <= 0 ? 60 : _hello.Quality, 10, 95);
        var conv = await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
        {
            File = LinuxDesktopHost.FindTool("convert")!, Args = new[] { png, "-quality", quality.ToString(), jpg }, TimeoutMs = 15_000,
        }, ct);
        if (conv.ExitCode != 0 || !File.Exists(jpg)) return null;

        // Learn the real size from ImageMagick identify (cheap, once).
        if (Monitor.Width == 0)
        {
            var id = await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
            {
                File = LinuxDesktopHost.FindTool("identify") ?? LinuxDesktopHost.FindTool("convert")!,
                Args = new[] { "-format", "%wx%h", jpg }, TimeoutMs = 10_000,
            }, ct);
            var parts = id.Stdout.Trim().Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                Monitor = new MonitorInfo { Index = 0, Name = "X11 display", Width = w, Height = h, Primary = true };
        }
        return await File.ReadAllBytesAsync(jpg, ct);
    }

    public async Task SendInputAsync(DesktopInput input, CancellationToken ct = default)
    {
        var xdotool = LinuxDesktopHost.FindTool("xdotool");
        if (xdotool == null) return;
        var env = LinuxDesktopHost.SessionEnv();
        async Task Run(params string[] args)
            => await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest { File = xdotool, Args = args, Env = env, TimeoutMs = 5_000 }, ct);

        var w = Monitor.Width > 0 ? Monitor.Width : 1920;
        var h = Monitor.Height > 0 ? Monitor.Height : 1080;
        switch (input.Kind)
        {
            case "mouseMove":
                await Run("mousemove", ((int)(input.X * w)).ToString(), ((int)(input.Y * h)).ToString());
                break;
            case "mouseButton":
            {
                await Run("mousemove", ((int)(input.X * w)).ToString(), ((int)(input.Y * h)).ToString());
                var button = input.Button switch { "middle" => "2", "right" => "3", _ => "1" };
                await Run(input.Down ? "mousedown" : "mouseup", button);
                break;
            }
            case "wheel":
                await Run("click", input.Delta > 0 ? "4" : "5");
                break;
            case "key":
                var key = MapKey(input);
                if (key != null) await Run(input.Down ? "keydown" : "keyup", key);
                break;
        }
    }

    private static string? MapKey(DesktopInput input)
    {
        var key = input.Key;
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (key.Contains('+')) // combo like "ctrl+alt+delete" → xdotool syntax
            return string.Join("+", key.Split('+').Select(p => MapSingle(p.Trim())));
        var parts = new List<string>();
        if (input.Ctrl) parts.Add("ctrl");
        if (input.Alt) parts.Add("alt");
        if (input.Shift) parts.Add("shift");
        if (input.Meta) parts.Add("super");
        var single = MapSingle(key);
        // Modifier flags + the modifier key itself (e.g. keydown of Control) → just the modifier.
        if (parts.Count > 0 && single is "ctrl" or "alt" or "shift" or "super") return single;
        parts.Add(single);
        return string.Join("+", parts);
    }

    private static string MapSingle(string key) => key switch
    {
        "Enter" => "Return",
        "Escape" => "Escape",
        "Backspace" => "BackSpace",
        "Delete" => "Delete",
        " " => "space",
        "Control" => "ctrl",
        "Alt" => "alt",
        "Shift" => "shift",
        "Meta" => "super",
        _ => key.Length == 1 ? key : key, // letters/digits pass through; named keys mostly match X11 keysyms
    };

    public async Task<string?> GetClipboardAsync(CancellationToken ct = default)
    {
        var xclip = LinuxDesktopHost.FindTool("xclip");
        if (xclip != null)
        {
            var r = await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
            {
                File = xclip, Args = new[] { "-o", "-selection", "clipboard" }, Env = LinuxDesktopHost.SessionEnv(), TimeoutMs = 5_000,
            }, ct);
            return r.ExitCode == 0 ? r.Stdout : null;
        }
        var xsel = LinuxDesktopHost.FindTool("xsel");
        if (xsel != null)
        {
            var r = await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
            {
                File = xsel, Args = new[] { "--clipboard", "--output" }, Env = LinuxDesktopHost.SessionEnv(), TimeoutMs = 5_000,
            }, ct);
            return r.ExitCode == 0 ? r.Stdout : null;
        }
        return null;
    }

    public async Task SetClipboardAsync(string text, CancellationToken ct = default)
    {
        var xclip = LinuxDesktopHost.FindTool("xclip");
        if (xclip != null)
        {
            await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
            {
                File = xclip, Args = new[] { "-i", "-selection", "clipboard" }, Env = LinuxDesktopHost.SessionEnv(), Stdin = text, TimeoutMs = 5_000,
            }, ct);
            return;
        }
        var xsel = LinuxDesktopHost.FindTool("xsel");
        if (xsel != null)
            await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
            {
                File = xsel, Args = new[] { "--clipboard", "--input" }, Env = LinuxDesktopHost.SessionEnv(), Stdin = text, TimeoutMs = 5_000,
            }, ct);
    }

    private bool _privacyOn;

    /// <summary>Does `xset q` output prove the DPMS extension exists? xset
    /// exits 0 for `dpms force …` even when the server has NO DPMS extension
    /// (it only prints "server does not have extension for dpms option"),
    /// so the exit code cannot be trusted — the query output can: a server
    /// with DPMS prints a "DPMS is Enabled/Disabled" line (§13).</summary>
    internal static bool XsetQueryShowsDpms(string xsetQueryOutput)
        => xsetQueryOutput.Contains("DPMS is", StringComparison.Ordinal);

    /// <summary>Privacy mode (v1.2): DPMS-blank the physical display. The
    /// remote stream keeps flowing (capture reads the framebuffer, not the
    /// panel). Local input lock is NOT enforced on Linux in this version —
    /// there is no standard tool for it; the blanking is the enforced half.
    /// Returns whether the requested state is ACTUALLY in effect: enabling
    /// first probes `xset q` for the DPMS extension, because xset reports
    /// success (exit 0) even when it did nothing (§13 — the Simular cocoon
    /// X server has no DPMS and the blank silently never happened). Dispose
    /// restores the panel.</summary>
    public async Task<bool> SetPrivacyAsync(bool enabled, CancellationToken ct = default)
    {
        var xset = LinuxDesktopHost.FindTool("xset");
        if (!enabled)
        {
            // "Off" is in effect unless we actually blanked the panel.
            if (!_privacyOn) return true;
            _privacyOn = false;
            if (xset == null) return true;
            await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
            {
                File = xset, Args = new[] { "dpms", "force", "on" },
                Env = LinuxDesktopHost.SessionEnv(), TimeoutMs = 5_000,
            }, ct);
            return true;
        }
        if (xset == null) return false;
        var query = await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
        {
            File = xset, Args = new[] { "q" },
            Env = LinuxDesktopHost.SessionEnv(), TimeoutMs = 5_000,
        }, ct);
        if (query.ExitCode != 0 || !XsetQueryShowsDpms(query.Stdout ?? "")) return false;
        _privacyOn = true;
        await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
        {
            File = xset, Args = new[] { "dpms", "force", "off" },
            Env = LinuxDesktopHost.SessionEnv(), TimeoutMs = 5_000,
        }, ct);
        return true;
    }

    /// <summary>Operator → device chat (v1.2): a desktop notification. The
    /// reverse direction has no Linux surface in this version (notify-send is
    /// one-way); GetChatAsync stays null here — stated plainly, not faked.</summary>
    public async Task SendChatAsync(string text, CancellationToken ct = default)
    {
        var notify = LinuxDesktopHost.FindTool("notify-send");
        if (notify == null) return;
        await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
        {
            File = notify,
            Args = new[] { "--app-name=EsoAgent", "Message from your operator", text.Length > 400 ? text[..400] : text },
            Env = LinuxDesktopHost.SessionEnv(), TimeoutMs = 5_000,
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_privacyOn)
        {
            try { await SetPrivacyAsync(false); } catch { }
        }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }
}
