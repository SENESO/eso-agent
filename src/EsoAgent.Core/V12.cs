using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EsoAgent.Core;

// ============================================================================
// v1.2 — "surpass + unify". Shared types + pure logic for: the unified activity
// timeline, fleet-relayed streams (desktop + tunnels), desktop session
// recording, Wake-on-LAN, signed update rollout, the script library, device
// schedules and the health dashboard. Pure logic lives here so it is unit-
// tested; the wiring lives in the agent, the fleet server and the clients.
// ============================================================================

// ---------- unified activity timeline ----------

/// <summary>
/// One event in the UNIFIED activity timeline — the single feed the app, the
/// fleet panel and `eso activity` all render. Sources: device audit events
/// shipped by agents, fleet-server events, operator actions, owner logins and
/// alerts. Id is a stable dedupe key: re-shipping the same device audit line
/// (at-least-once delivery) never duplicates an event.
/// </summary>
public sealed class ActivityEvent
{
    public string Id { get; set; } = "";
    public DateTimeOffset At { get; set; }
    /// <summary>device | server | operator | owner | alert</summary>
    public string Source { get; set; } = "";
    public string? DeviceCode { get; set; }
    public string? DeviceName { get; set; }
    /// <summary>Event kind: the audit endpoint for device events (e.g. "/api/exec",
    /// "desktop", "schedule"), or a server kind ("command", "login", "queue",
    /// "rollout", "wake", "alert", "enrollment", …).</summary>
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Result { get; set; } = "";

    /// <summary>Stable id for a shipped device audit line: hash of device + line
    /// offset + content, so a re-ship after reconnect dedupes exactly.</summary>
    public static string DeviceLineId(string deviceCode, long lineOffset, string line)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{deviceCode}|{lineOffset}|{line}"))).ToLowerInvariant()[..24];

    /// <summary>Parses one agent audit-log line ({ts, endpoint, summary, result})
    /// into an activity event. Returns null for lines that are not audit JSON.</summary>
    public static ActivityEvent? FromAuditLine(string deviceCode, string? deviceName, long lineOffset, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("endpoint", out var endpoint)) return null;
            var at = root.TryGetProperty("ts", out var ts) && ts.TryGetDateTimeOffset(out var dto)
                ? dto : DateTimeOffset.UtcNow;
            return new ActivityEvent
            {
                Id = DeviceLineId(deviceCode, lineOffset, line),
                At = at,
                Source = "device",
                DeviceCode = deviceCode,
                DeviceName = deviceName,
                Kind = endpoint.GetString() ?? "",
                Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                Result = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "",
            };
        }
        catch { return null; }
    }
}

/// <summary>Filter for timeline queries (server API, panel, CLI, app — one shape).</summary>
public sealed class ActivityQuery
{
    public string? Device { get; set; }
    public string? Kind { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? Since { get; set; }
    public DateTimeOffset? Until { get; set; }
    public int Limit { get; set; } = 200;

    public bool Matches(ActivityEvent e)
    {
        if (Device != null && !string.Equals(e.DeviceCode, Device, StringComparison.OrdinalIgnoreCase)) return false;
        if (Kind != null && !e.Kind.Contains(Kind, StringComparison.OrdinalIgnoreCase)) return false;
        if (Source != null && !string.Equals(e.Source, Source, StringComparison.OrdinalIgnoreCase)) return false;
        if (Since != null && e.At < Since.Value) return false;
        if (Until != null && e.At > Until.Value) return false;
        return true;
    }

    /// <summary>Applies a query to an event set: filtered, newest first, capped.
    /// Pure — the merge/order logic behind every face of the timeline.</summary>
    public static List<ActivityEvent> Apply(IEnumerable<ActivityEvent> events, ActivityQuery query)
        => events.Where(query.Matches)
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(query.Limit, 1, 5000))
            .ToList();
}

/// <summary>CSV rendering of the timeline (export). Pure — unit-tested.</summary>
public static class ActivityCsv
{
    public static string Escape(string? value)
    {
        var v = value ?? "";
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? v
            : "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    public static string ToCsv(IEnumerable<ActivityEvent> events)
    {
        var sb = new StringBuilder();
        sb.Append("at,source,deviceCode,deviceName,kind,result,summary\n");
        foreach (var e in events.OrderBy(x => x.At))
        {
            sb.Append(e.At.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(Escape(e.Source)).Append(',')
              .Append(Escape(e.DeviceCode)).Append(',')
              .Append(Escape(e.DeviceName)).Append(',')
              .Append(Escape(e.Kind)).Append(',')
              .Append(Escape(e.Result)).Append(',')
              .Append(Escape(e.Summary)).Append('\n');
        }
        return sb.ToString();
    }
}

// ---------- fleet streams (relayed desktop + tunnels) ----------

/// <summary>
/// A request to open a relayed stream on a device, SIGNED by the operator key
/// (CLI/app) or the fleet panel key (owner panel) exactly like a command —
/// the fleet server relays it and the DEVICE verifies the signature against
/// its pinned keys before opening anything. The relay gains no new power.
/// Canonical form: "fleet-stream" \n streamId \n issuedAt \n kind \n sha256(payloadJson).
/// </summary>
public sealed class FleetStreamOpenFrame
{
    public string Type { get; set; } = "stream-open";
    public string StreamId { get; set; } = "";
    /// <summary>desktop | tunnel</summary>
    public string Kind { get; set; } = "";
    public long IssuedAt { get; set; }
    /// <summary>operator | owner-panel</summary>
    public string Signer { get; set; } = "";
    public string Signature { get; set; } = "";
    /// <summary>desktop: a DesktopHello JSON. tunnel: a TunnelTarget JSON.</summary>
    public string PayloadJson { get; set; } = "{}";
}

/// <summary>The device's answer to a stream-open: accepted, or refused with a reason.</summary>
public sealed class FleetStreamOpenedFrame
{
    public string Type { get; set; } = "stream-opened";
    public string StreamId { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>One data chunk on an open stream. Desktop: Data is one desktop-protocol
/// JSON text message. Tunnel: Data is base64 raw TCP bytes.</summary>
public sealed class FleetStreamDataFrame
{
    public string Type { get; set; } = "stream-data";
    public string StreamId { get; set; } = "";
    public string Data { get; set; } = "";
}

public sealed class FleetStreamCloseFrame
{
    public string Type { get; set; } = "stream-close";
    public string StreamId { get; set; } = "";
    public string? Reason { get; set; }
}

/// <summary>First frame a stream client sends to the fleet server's stream
/// endpoints: the client's own signed stream-open (the server verifies it
/// against the operator key, then relays it verbatim to the device).</summary>
public sealed class FleetStreamOpenRequest
{
    public string Code { get; set; } = "";
    public FleetStreamOpenFrame Open { get; set; } = new();
}

// ---------- tunnels ----------

/// <summary>Where a tunnel stream lands on the device side: host:port as seen
/// FROM the device (its loopback, its LAN, its tailnet).</summary>
public sealed class TunnelTarget
{
    public string Host { get; set; } = "";
    public int Port { get; set; }

    /// <summary>Parses "host:port" (also "[v6]:port"). Pure — unit-tested.</summary>
    public static bool TryParse(string text, out TunnelTarget target)
    {
        target = new TunnelTarget();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        string host;
        string portText;
        if (t.StartsWith('['))
        {
            var close = t.IndexOf(']');
            if (close < 0 || close + 1 >= t.Length || t[close + 1] != ':') return false;
            host = t[1..close];
            portText = t[(close + 2)..];
        }
        else
        {
            var idx = t.LastIndexOf(':');
            if (idx <= 0) return false;
            host = t[..idx];
            portText = t[(idx + 1)..];
        }
        if (host.Length == 0 || !int.TryParse(portText, out var port) || port is < 1 or > 65535) return false;
        target = new TunnelTarget { Host = host, Port = port };
        return true;
    }

    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

/// <summary>One operator-side tunnel definition (UI overhaul Phase 4): a
/// local listener on the operator's machine forwarding through one device.
/// The CLI builds one from its arguments; the App's Tunnels view manages a
/// list of them. Validation is pure — unit-tested.</summary>
public sealed class TunnelSpec
{
    public int LocalPort { get; set; }
    /// <summary>Fleet device code, or host[:port] for a direct device.</summary>
    public string Device { get; set; } = "";
    /// <summary>Fixed target on the device side; null in SOCKS mode.</summary>
    public TunnelTarget? Target { get; set; }
    public bool Socks { get; set; }
    public bool Udp { get; set; }

    public string Describe() => Socks
        ? $"SOCKS5 127.0.0.1:{LocalPort} via {Device}"
        : $"{(Udp ? "UDP" : "TCP")} 127.0.0.1:{LocalPort} → {Target} via {Device}";

    public static string? Validate(TunnelSpec spec)
    {
        if (spec.LocalPort is < 1 or > 65535) return "Local port must be 1–65535.";
        if (string.IsNullOrWhiteSpace(spec.Device)) return "A device is required.";
        if (spec.Socks && spec.Udp) return "SOCKS forwarding is TCP-only.";
        if (!spec.Socks && spec.Target == null) return "A target host:port is required (or use SOCKS mode).";
        return null;
    }
}

/// <summary>TCP piping shared by both tunnel ends (agent side + CLI side):
/// socket → base64 frames out, base64 frames in → socket. Half-close is
/// propagated as an empty final chunk marker handled by the caller.</summary>
public static class TunnelPump
{
    public const int ChunkBytes = 48 * 1024;

    /// <summary>Reads the socket and forwards each chunk as base64 until EOF/error.</summary>
    public static async Task PumpSocketToFramesAsync(NetworkStream stream, Func<string, Task> sendFrame, CancellationToken ct)
    {
        var buffer = new byte[ChunkBytes];
        while (!ct.IsCancellationRequested)
        {
            int read;
            try { read = await stream.ReadAsync(buffer, ct); }
            catch { break; }
            if (read <= 0) break;
            await sendFrame(Convert.ToBase64String(buffer, 0, read));
        }
    }
}

// ---------- Wake-on-LAN ----------

/// <summary>Wake-on-LAN magic packet: 6× 0xFF + the 6-byte MAC repeated 16 times
/// (102 bytes). Pure — unit-tested byte for byte.</summary>
public static class MagicPacket
{
    public static bool TryParseMac(string text, out byte[] mac)
    {
        mac = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = new string(text.Where(c => c is not (':' or '-' or '.' or ' ')).ToArray());
        if (hex.Length != 12) return false;
        try { mac = Convert.FromHexString(hex); return true; }
        catch { return false; }
    }

    public static byte[] Build(string macText)
    {
        if (!TryParseMac(macText, out var mac))
            throw new ArgumentException($"'{macText}' is not a MAC address.", nameof(macText));
        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var repeat = 0; repeat < 16; repeat++)
            mac.CopyTo(packet, 6 + repeat * 6);
        return packet;
    }

    /// <summary>Canonical display form AA:BB:CC:DD:EE:FF ("" when unparsable).</summary>
    public static string Normalize(string? macText)
        => macText != null && TryParseMac(macText, out var mac)
            ? string.Join(":", mac.Select(b => b.ToString("X2")))
            : "";
}

public sealed class WakeRequest
{
    /// <summary>Target MAC (any common spelling) — the device that sends the packet
    /// does not need to know which device it belongs to.</summary>
    public string Mac { get; set; } = "";
}

public sealed class WakeResultDto
{
    public string Code { get; set; } = "";
    public string Mac { get; set; } = "";
    public List<string> SentBy { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

// ---------- desktop session recording ----------

/// <summary>
/// Session recording format ".esorec": JSON Lines. Line 1 is the header
/// ({kind:"esorec", version, startedAt, origin, codec, width, height}); each
/// following line is one frame ({atMs, seq, codec, full, width, height, data})
/// with atMs relative to the header's start. Any JSONL reader can replay it;
/// frames carry the exact desktop-protocol payloads.
/// </summary>
public sealed class RecordingHeader
{
    public string Kind { get; set; } = "esorec";
    public int Version { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; }
    public string Origin { get; set; } = "";
    public string Codec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class RecordingFrameLine
{
    public long AtMs { get; set; }
    public long Seq { get; set; }
    public string Codec { get; set; } = "";
    public bool Full { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Data { get; set; } = "";
}

public sealed class RecordingInfoDto
{
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string Codec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>Writes one .esorec file. Thread-safe; frames are timestamped
/// against the header's start. Used by the desktop stream runner when a
/// session opts into recording.</summary>
public sealed class SessionRecorder : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly DateTimeOffset _startedAt;
    private readonly object _gate = new();
    private bool _disposed;

    public string FileName { get; }
    public string FullPath { get; }

    public SessionRecorder(string directory, string origin, string codec, int width, int height)
    {
        Directory.CreateDirectory(directory);
        _startedAt = DateTimeOffset.UtcNow;
        FileName = $"{_startedAt:yyyyMMdd-HHmmss}-{Sanitize(origin)}.esorec";
        FullPath = Path.Combine(directory, FileName);
        _writer = new StreamWriter(new FileStream(FullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        var header = new RecordingHeader
        {
            StartedAt = _startedAt, Origin = origin, Codec = codec, Width = width, Height = height,
        };
        _writer.WriteLine(JsonSerializer.Serialize(header, EsoJson.Options));
        _writer.Flush();
    }

    public void Record(DesktopFrame frame)
    {
        var line = JsonSerializer.Serialize(new RecordingFrameLine
        {
            AtMs = (long)(DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds,
            Seq = frame.Seq, Codec = frame.Codec, Full = frame.Full,
            Width = frame.Width, Height = frame.Height, Data = frame.Data,
        }, EsoJson.Options);
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine(line);
        }
    }

    private static string Sanitize(string origin)
    {
        var chars = origin.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? "session" : new string(chars);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; }
        await _writer.FlushAsync();
        _writer.Dispose();
    }

    /// <summary>Reads a .esorec file back: header + frames. Pure file logic — unit-tested.</summary>
    public static (RecordingHeader Header, List<RecordingFrameLine> Frames) Parse(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) throw new InvalidDataException("Empty recording file.");
        var header = JsonSerializer.Deserialize<RecordingHeader>(lines[0], EsoJson.Options)
                     ?? throw new InvalidDataException("Bad recording header.");
        if (header.Kind != "esorec") throw new InvalidDataException("Not an .esorec file.");
        var frames = new List<RecordingFrameLine>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var frame = JsonSerializer.Deserialize<RecordingFrameLine>(lines[i], EsoJson.Options);
            if (frame != null) frames.Add(frame);
        }
        return (header, frames);
    }

    /// <summary>Lists recordings in a directory (newest first) with header facts.</summary>
    public static List<RecordingInfoDto> List(string directory)
    {
        var result = new List<RecordingInfoDto>();
        if (!Directory.Exists(directory)) return result;
        foreach (var file in Directory.EnumerateFiles(directory, "*.esorec"))
        {
            try
            {
                var info = new FileInfo(file);
                var dto = new RecordingInfoDto { Name = info.Name, SizeBytes = info.Length, StartedAt = info.CreationTimeUtc };
                using var reader = new StreamReader(file);
                var first = reader.ReadLine();
                if (first != null && JsonSerializer.Deserialize<RecordingHeader>(first, EsoJson.Options) is { } header)
                {
                    dto.StartedAt = header.StartedAt;
                    dto.Codec = header.Codec;
                    dto.Width = header.Width;
                    dto.Height = header.Height;
                }
                result.Add(dto);
            }
            catch { /* a torn file is skipped, never fatal */ }
        }
        return result.OrderByDescending(r => r.StartedAt).ToList();
    }
}

// ---------- device scheduled tasks (the device's own cron) ----------

/// <summary>One agent-side schedule (esoagent.json "schedules"): run a command
/// at an interval or daily at a local time. Distinct from durable jobs — this
/// is the device's own cron, persisted in config, results audit-logged (and
/// therefore shipped into the unified activity timeline).</summary>
public sealed class ScheduledTaskConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Shell name (powershell|pwsh|cmd|bash|sh) — when set, Command is a script for it.</summary>
    public string? Shell { get; set; }
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
    public string? Cwd { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    /// <summary>Run every N minutes (from the last completed run).</summary>
    public int? IntervalMinutes { get; set; }
    /// <summary>Run daily at this LOCAL time, "HH:mm".</summary>
    public string? DailyAt { get; set; }
    public int TimeoutMs { get; set; } = 600_000;
}

public sealed class ScheduleStateDto
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string Description { get; set; } = "";
    public DateTimeOffset? LastRunAt { get; set; }
    public int? LastExitCode { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Due-decision for schedules. Pure — unit-tested. Times are LOCAL
/// (a device's cron thinks in the device's timezone).</summary>
public static class SchedulePolicy
{
    public static bool TryParseDailyAt(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)
            || h is < 0 or > 23 || m is < 0 or > 59) return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }

    public static bool IsDue(ScheduledTaskConfig cfg, DateTimeOffset? lastRunLocal, DateTimeOffset nowLocal)
    {
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Command)) return false;
        if (cfg.IntervalMinutes is { } interval && interval > 0)
        {
            if (lastRunLocal == null) return true; // never ran → run at first tick
            return nowLocal - lastRunLocal.Value >= TimeSpan.FromMinutes(interval);
        }
        if (TryParseDailyAt(cfg.DailyAt, out var at))
        {
            if (nowLocal.TimeOfDay < at) return false;                 // not yet today
            if (lastRunLocal != null && lastRunLocal.Value.Date >= nowLocal.Date) return false; // already ran today
            return true;
        }
        return false;
    }

    public static string Describe(ScheduledTaskConfig cfg)
    {
        if (cfg.IntervalMinutes is { } i) return $"every {i} min";
        if (TryParseDailyAt(cfg.DailyAt, out var at)) return $"daily at {at:hh\\:mm}";
        return "no schedule (set intervalMinutes or dailyAt)";
    }
}

/// <summary>Write-path validation for schedules (PUT /api/schedules). Pure —
/// unit-tested. Returns a human error or null. The rules mirror what
/// SchedulePolicy can actually run, so the API never accepts a task the
/// service would silently ignore.</summary>
public static class ScheduleValidation
{
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
        { "powershell", "pwsh", "cmd", "bash", "sh" };

    public static string? ValidateOne(ScheduledTaskConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Name)) return "A schedule needs a name.";
        if (cfg.Name.Length > 64 || cfg.Name.Any(char.IsControl)) return $"Schedule name '{cfg.Name}' is too long or has control characters.";
        if (string.IsNullOrWhiteSpace(cfg.Command)) return $"Schedule '{cfg.Name}' needs a command.";
        if (cfg.Shell != null && !Shells.Contains(cfg.Shell)) return $"Schedule '{cfg.Name}': shell must be one of powershell, pwsh, cmd, bash, sh.";
        if (cfg.IntervalMinutes is { } i && (i < 1 || i > 10080)) return $"Schedule '{cfg.Name}': interval must be 1–10080 minutes.";
        if (cfg.DailyAt != null && !SchedulePolicy.TryParseDailyAt(cfg.DailyAt, out _)) return $"Schedule '{cfg.Name}': daily time must be HH:mm.";
        if (cfg.IntervalMinutes != null && cfg.DailyAt != null) return $"Schedule '{cfg.Name}': pick an interval OR a daily time, not both.";
        if (cfg.TimeoutMs < 1000 || cfg.TimeoutMs > 3_600_000) return $"Schedule '{cfg.Name}': timeout must be 1 s – 1 h.";
        return null;
    }

    public static string? ValidateList(IReadOnlyList<ScheduledTaskConfig> list)
    {
        if (list.Count > 100) return "Too many schedules (max 100).";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in list)
        {
            if (ValidateOne(cfg) is { } error) return error;
            if (!seen.Add(cfg.Name.Trim())) return $"Duplicate schedule name '{cfg.Name}'.";
        }
        return null;
    }
}

// ---------- fleet-wide update rollout ----------

/// <summary>Signed pointer to an update package the fleet server holds: the
/// device downloads it from the server and verifies THIS signature (operator
/// or panel key, both pinned on the device) plus the package SHA-256 before
/// staging. The relay cannot substitute a different package.</summary>
public sealed class UpdateFetchRequest
{
    /// <summary>Server-relative download path (resolved by the agent against its fleet server base URL).</summary>
    public string Path { get; set; } = "";
    /// <summary>One-rollout download token (the path alone grants nothing without it).</summary>
    public string Token { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>operator | owner-panel</summary>
    public string Signer { get; set; } = "";
    public string Signature { get; set; } = "";
}

/// <summary>Per-device rollout state machine. States: queued → sent → applying
/// → applied | failed; offline is terminal at dispatch (a device that is
/// offline when the rollout starts is NOT surprise-updated when it returns —
/// the operator re-runs the rollout). Pure transitions — unit-tested.</summary>
public static class RolloutStates
{
    public const string Queued = "queued";
    public const string Sent = "sent";
    public const string Applying = "applying";
    public const string Applied = "applied";
    public const string Failed = "failed";
    public const string Offline = "offline";

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [Queued] = new[] { Sent, Offline, Failed },
        [Sent] = new[] { Applying, Failed },
        [Applying] = new[] { Applied, Failed },
        [Applied] = Array.Empty<string>(),
        [Failed] = Array.Empty<string>(),
        [Offline] = Array.Empty<string>(),
    };

    public static bool CanTransition(string from, string to)
        => Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static bool IsTerminal(string state) => state is Applied or Failed or Offline;
}

public sealed class RolloutDeviceDto
{
    public string Code { get; set; } = "";
    public string State { get; set; } = RolloutStates.Queued;
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RolloutDto
{
    public string Id { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string CreatedBy { get; set; } = "";   // operator | owner-panel
    public DateTimeOffset CreatedAt { get; set; }
    public List<RolloutDeviceDto> Devices { get; set; } = new();
}

// ---------- script library ----------

/// <summary>A named, reusable script stored on the fleet server. Runs go
/// through the normal signed command path (the library is storage + audit,
/// not a new execution channel).</summary>
public sealed class ScriptDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Shell { get; set; } = "default";
    public string Body { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ScriptRunRecordRequest
{
    public List<string> Targets { get; set; } = new();
    /// <summary>SHA-256 (hex) of the script body the caller is about to run —
    /// recorded in the timeline so the audit shows exactly what ran.</summary>
    public string BodySha256 { get; set; } = "";
}

// ---------- health dashboard ----------

/// <summary>The fleet server's cached per-device health snapshot (from the
/// agent's doctor + status), rendered identically by the panel and the app.</summary>
public sealed class DeviceHealthDto
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Online { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>pass | warn | fail | unknown (never polled / offline)</summary>
    public string Overall { get; set; } = "unknown";
    public DateTimeOffset? PolledAt { get; set; }
    public List<DoctorCheckDto> Checks { get; set; } = new();
    /// <summary>Lowest free-disk percentage across fixed disks (null = unknown).</summary>
    public double? MinDiskFreePercent { get; set; }
    /// <summary>Days left on the agent certificate, parsed from the doctor detail (null = unknown).</summary>
    public double? CertDaysLeft { get; set; }
    public List<SupervisedStatusDto> Supervised { get; set; } = new();
}

/// <summary>Aggregates a doctor response + status into a health snapshot. Pure — unit-tested.</summary>
public static class HealthSummary
{
    public static DeviceHealthDto Build(string code, string name, bool online, DateTimeOffset lastSeen,
        DoctorResponse? doctor, StatusResponse? status, DateTimeOffset polledAt)
    {
        var dto = new DeviceHealthDto
        {
            Code = code, Name = name, Online = online, LastSeen = lastSeen, PolledAt = polledAt,
            Overall = doctor?.Overall ?? "unknown",
            Checks = doctor?.Checks ?? new List<DoctorCheckDto>(),
            Supervised = status?.Supervised ?? new List<SupervisedStatusDto>(),
        };
        if (status is { Disks.Count: > 0 })
        {
            double? min = null;
            foreach (var d in status.Disks)
            {
                if (d.TotalBytes <= 0) continue;
                var pct = 100.0 * d.FreeBytes / d.TotalBytes;
                min = min == null ? pct : Math.Min(min.Value, pct);
            }
            dto.MinDiskFreePercent = min;
        }
        var cert = dto.Checks.FirstOrDefault(c => c.Name == "certificate");
        if (cert != null) dto.CertDaysLeft = ParseCertDays(cert.Detail);
        // A supervised process that gave up is a health problem even when every
        // doctor check passes.
        if (dto.Supervised.Any(s => s.State == "gave-up") && dto.Overall == "pass")
            dto.Overall = "warn";
        return dto;
    }

    /// <summary>Extracts "(1826 days left)" / "(-3 days left)" from the doctor's
    /// certificate detail string. Null when the shape is not recognized.</summary>
    public static double? ParseCertDays(string detail)
    {
        var open = detail.LastIndexOf('(');
        var close = detail.LastIndexOf(" days left)", StringComparison.Ordinal);
        if (open < 0 || close <= open) return null;
        var text = detail[(open + 1)..close];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) ? days : null;
    }
}

// ---------- owner policy: consent + capabilities ----------

/// <summary>Attended-session consent policy (owner-set, in esoagent.json
/// "desktopPolicy"). When consent is required for a mode, the desktop host
/// must obtain the local user's on-screen accept before the session opens;
/// a timeout or a missing prompt mechanism DENIES (fail closed).</summary>
public sealed class DesktopPolicy
{
    public bool ControlConsent { get; set; }
    public bool ViewConsent { get; set; }
    public int ConsentTimeoutSeconds { get; set; } = 30;

    public bool RequiresConsent(string mode)
        => string.Equals(mode, "Control", StringComparison.OrdinalIgnoreCase) ? ControlConsent : ViewConsent;
}

/// <summary>Per-capability toggles (owner-set, in esoagent.json "capabilities").
/// Enforcement points: file endpoints + tunnels in the agent middleware,
/// desktop control + clipboard inside the desktop runner/streams. The owner
/// panel itself is never gated — it is how the owner turns things back on.</summary>
public sealed class CapabilityPolicy
{
    public bool Files { get; set; } = true;
    public bool Clipboard { get; set; } = true;
    public bool Tunnels { get; set; } = true;
    public bool DesktopControl { get; set; } = true;
}

// ---------- OS patch management ----------

public sealed class PatchInfoDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? SizeText { get; set; }
    /// <summary>security | update | other</summary>
    public string Kind { get; set; } = "update";
}

public sealed class PatchStatusResponse
{
    /// <summary>apt | windows-update | none</summary>
    public string Source { get; set; } = "none";
    public List<PatchInfoDto> Pending { get; set; } = new();
    public bool RebootRequired { get; set; }
    public string? Error { get; set; }
}

public sealed class PatchApplyRequest
{
    /// <summary>Empty = apply all pending. Otherwise only these patch ids.</summary>
    public List<string> Ids { get; set; } = new();
}

public sealed class PatchApplyResponse
{
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public int Remaining { get; set; }
    public bool RebootRequired { get; set; }
}

/// <summary>Parses `apt list --upgradable` output lines
/// ("name/suite version arch [upgradable from: x]") — pure, unit-tested.</summary>
public static class AptPatchParser
{
    public static List<PatchInfoDto> ParseUpgradable(string aptListOutput)
    {
        var result = new List<PatchInfoDto>();
        foreach (var raw in aptListOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Listing", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var name = parts[0].Split('/')[0];
            if (name.Length == 0) continue;
            result.Add(new PatchInfoDto
            {
                Id = name,
                Name = name,
                Version = parts[1],
                Kind = line.Contains("security", StringComparison.OrdinalIgnoreCase) ? "security" : "update",
            });
        }
        return result;
    }
}

// ---------- persistent shell sessions ----------

/// <summary>One persistent shell session hosted by the agent (v1.2): a named,
/// long-lived shell process whose output is buffered with sequence numbers,
/// so clients can disconnect, reconnect and catch up — the tmux model.</summary>
public sealed class ShellSessionDto
{
    public string Name { get; set; } = "";
    public string Shell { get; set; } = "";
    public int Pid { get; set; }
    public bool Running { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>Sequence number of the newest buffered output chunk.</summary>
    public long LatestSeq { get; set; }
}

public sealed class ShellSessionCreateRequest
{
    public string Name { get; set; } = "";
    /// <summary>powershell|pwsh|cmd|bash|sh|default</summary>
    public string Shell { get; set; } = "default";
}

public sealed class ShellSessionInputRequest
{
    public string Data { get; set; } = "";
}

public sealed class ShellSessionOutputResponse
{
    public string Name { get; set; } = "";
    public bool Running { get; set; }
    /// <summary>Chunks newer than the caller's `since`, oldest first.</summary>
    public List<ShellSessionChunk> Chunks { get; set; } = new();
    public long LatestSeq { get; set; }
}

public sealed class ShellSessionChunk
{
    public long Seq { get; set; }
    /// <summary>stdout | stderr</summary>
    public string Stream { get; set; } = "stdout";
    public string Text { get; set; } = "";
}

// ---------- agent file manager (API shape shared by CLI + app + panel proxy) ----------

public sealed class FileEntryDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDir { get; set; }
    public long? Size { get; set; }
}

public sealed class FileListResponse
{
    public string Path { get; set; } = "";
    public string? Parent { get; set; }
    public List<FileEntryDto> Entries { get; set; } = new();
}

public sealed class FilePathRequest
{
    public string Path { get; set; } = "";
}

public sealed class FileRenameRequest
{
    public string Path { get; set; } = "";
    public string NewPath { get; set; } = "";
}

// ---------- fleet API request DTOs (shared by server + client) ----------

public sealed class RolloutCreateRequest { public FleetTargets Targets { get; set; } = new(); }

/// <summary>Returned ONCE when a rollout is created: the rollout plus its
/// download token (the token authorizes agents to fetch the package; it is
/// never listed again — same shown-once discipline as enrollment keys).</summary>
public sealed class RolloutCreatedResponse
{
    public RolloutDto Rollout { get; set; } = new();
    public string Token { get; set; } = "";
}
public sealed class RolloutDeviceStateRequest { public string Code { get; set; } = ""; public string State { get; set; } = ""; public string? Error { get; set; } }
public sealed class WakeCodeRequest { public string Code { get; set; } = ""; }
public sealed class ExitNodeRequest { public string? Code { get; set; } }
