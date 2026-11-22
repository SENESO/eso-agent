using System.Text.Json;

namespace EsoAgent.Core;

/// <summary>
/// Server configuration, read from esoagent.json in the data directory.
/// The data directory comes from --data-dir, ESOAGENT_HOME, or the platform default
/// (Windows: C:\Program Files\EsoAgent\data — set by the installer; Linux: /etc/esoagent).
/// No IPs or tokens are hardcoded anywhere; the token lives in its own file.
/// </summary>
public sealed class AgentConfig
{
    public int Port { get; set; } = 8737;
    /// <summary>Owner-editable device name (panel/esoagent.json). Null = hostname.</summary>
    public string? DeviceName { get; set; }
    /// <summary>Fleet enrollment. Disabled/absent = Standalone mode (direct by IP, as before).</summary>
    public FleetOptions Fleet { get; set; } = new();
    /// <summary>WireGuard mesh options (fleet networking layer).</summary>
    public MeshOptions Mesh { get; set; } = new();
    /// <summary>Explicit bind addresses. Empty = auto: Tailscale interface (100.64.0.0/10) + loopback.</summary>
    public List<string> BindAddresses { get; set; } = new();
    /// <summary>Supervised processes (v1.1): long-lived processes the agent keeps
    /// alive per their restart policy. Empty = nothing supervised.</summary>
    public List<SupervisedProcessConfig> Supervised { get; set; } = new();
    /// <summary>Device scheduled tasks (v1.2): the device's own cron — commands
    /// run at an interval or daily at a local time. Empty = none.</summary>
    public List<ScheduledTaskConfig> Schedules { get; set; } = new();
    /// <summary>Attended-session consent policy (v1.2, owner-set): whether a
    /// desktop session needs the local user's on-screen accept before it starts.</summary>
    public DesktopPolicy DesktopPolicy { get; set; } = new();
    /// <summary>Per-capability toggles (v1.2, owner-set): independently disable
    /// file transfer / clipboard sync / tunnels / desktop control. The agent
    /// enforces; surfaces only display. All default ON (the operator's power
    /// is unchanged until the owner says otherwise).</summary>
    public CapabilityPolicy Capabilities { get; set; } = new();
    /// <summary>App-command registry (v1.2): named programs the operator can
    /// run like commands (Blender/Unreal presets in USER-GUIDE). Empty = none.</summary>
    public List<AppCommandConfig> AppCommands { get; set; } = new();
    /// <summary>Telemetry threshold watch (v1.2). Disabled by default — the
    /// owner/operator opts in per device.</summary>
    public TelemetryWatchConfig TelemetryWatch { get; set; } = new();
    /// <summary>Window watch (v1.2 item 39, perception stack tier 3):
    /// window open/close/focus changes as anomaly events. Off by default —
    /// needs a user session to enumerate windows.</summary>
    public WindowWatchConfig WindowWatch { get; set; } = new();
    /// <summary>Crash watch (v1.2): report NEW application crashes into the
    /// activity timeline. On by default; the owner can turn it off here.</summary>
    public bool CrashWatchEnabled { get; set; } = true;
    /// <summary>File-change watches (v1.2): directories whose changes stream
    /// to followers and into the activity timeline.</summary>
    public List<WatchConfig> Watches { get; set; } = new();
    public ReleaseUpdateConfig ReleaseUpdate { get; set; } = new();
    public string DataDir { get; set; } = "";
    public string TokenFile { get; set; } = "token";
    public string CertFile { get; set; } = "cert.pfx";
    public string AuditLogFile { get; set; } = "logs/audit.log";
    public string LockFile { get; set; } = "disabled.lock";

    [System.Text.Json.Serialization.JsonIgnore]
    public string ResolvedDataDir { get; private set; } = "";

    public string ResolvePath(string relativeOrAbsolute)
        => Path.IsPathFullyQualified(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.Combine(ResolvedDataDir, relativeOrAbsolute);

    public static string DefaultDataDir()
    {
        var env = Environment.GetEnvironmentVariable("ESOAGENT_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return OperatingSystem.IsWindows() ? @"C:\Program Files\EsoAgent\data" : "/etc/esoagent";
    }

    public string ResolvedDeviceName => string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName!;

    public static AgentConfig Load(string? dataDirArg)
    {
        var dir = dataDirArg ?? DefaultDataDir();
        var cfg = new AgentConfig();
        var jsonPath = Path.Combine(dir, "esoagent.json");
        if (File.Exists(jsonPath))
        {
            var loaded = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(jsonPath), EsoJson.Options);
            if (loaded != null) cfg = loaded;
        }
        cfg.ResolvedDataDir = dir;
        if (string.IsNullOrWhiteSpace(cfg.DataDir)) cfg.DataDir = dir;
        Directory.CreateDirectory(dir);
        return cfg;
    }
}

/// <summary>
/// The owner's fleet hub, shipped as the default so a fresh install shows its
/// device code with zero setup: the agent enrolls against this hub on first
/// run and lands as a Pending device; an operator still has to approve it
/// before any command is relayed. To run a different hub (or none), set
/// fleet.serverUrl / fleet.enabled in esoagent.json — an explicit value in
/// the file always wins over these defaults. If the hub's certificate is
/// ever rotated, update ServerFingerprint here and in the install scripts.
/// </summary>
public static class FleetDefaults
{
    public const string ServerUrl = "wss://100.98.165.106:8740/fleet/connect";
    public const string ServerFingerprint = "9b5c0bcc778ebd660d3997f1d9b6dfcc0eae678b1340b3f6d8dfcbe1d0e849c9";
}

/// <summary>Fleet enrollment options (in esoagent.json). The agent dials OUT to the
/// server over WSS; no inbound ports are needed on the device.</summary>
public sealed class FleetOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>wss:// URL of the fleet server's /fleet/connect endpoint.</summary>
    public string? ServerUrl { get; set; } = FleetDefaults.ServerUrl;
    /// <summary>SHA-256 fingerprint of the fleet server's cert (pin). Empty = system validation (fine for a CA cert; set it for self-signed).</summary>
    public string? ServerFingerprint { get; set; } = FleetDefaults.ServerFingerprint;
    /// <summary>Optional pre-approval enrollment key for unattended installs (presented at hello).</summary>
    public string? EnrollmentKey { get; set; }
}

/// <summary>
/// The owner's kill switch. When locked, the API refuses every command endpoint
/// (only /api/status answers, reporting locked=true). On Windows the tray toggles it
/// over the control pipe; on Linux a local control command / lock file does.
/// Locking is a local act only — it can never be lifted remotely.
/// </summary>
public sealed class KillSwitch
{
    private readonly string _lockFile;
    private volatile bool _memoryLock;

    public KillSwitch(string lockFile) => _lockFile = lockFile;

    public bool IsLocked => _memoryLock || File.Exists(_lockFile);

    public void Lock()
    {
        _memoryLock = true;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_lockFile))!);
        File.WriteAllText(_lockFile, DateTimeOffset.UtcNow.ToString("O"));
    }

    public void Unlock()
    {
        _memoryLock = false;
        if (File.Exists(_lockFile)) File.Delete(_lockFile);
    }
}
