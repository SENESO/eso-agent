using System.Text.Json;
using System.Text.Json.Serialization;

namespace EsoAgent.Core;

/// <summary>Shared JSON settings for the wire protocol (camelCase, case-insensitive reads).</summary>
public static class EsoJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

// ---------- exec / shell ----------

/// <summary>
/// An unrestricted process execution request. There is deliberately NO whitelist,
/// allowlist or blocked-command list anywhere in EsoAgent: exec runs anything the
/// service account could run (LocalSystem / root), exactly like an SSH shell.
/// The only controls are HMAC auth, the audit log and the owner's kill switch.
/// </summary>
public sealed class ExecRequest
{
    public string File { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
    public string? Cwd { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? Stdin { get; set; }
    public int TimeoutMs { get; set; } = 60_000;
}

public sealed class ExecResponse
{
    public int? ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public bool TimedOut { get; set; }
    public bool Truncated { get; set; }
    public long DurationMs { get; set; }
}

public sealed class ShellRequest
{
    /// <summary>powershell | pwsh | cmd | bash | sh — resolved against what the host actually has.</summary>
    public string Shell { get; set; } = "";
    public string Script { get; set; } = "";
    /// <summary>Extra environment variables for the child process (v1.1). Values are
    /// applied to the process only — they are NEVER written to the audit log
    /// (audit summaries carry key names only; see EnvAudit).</summary>
    public Dictionary<string, string>? Env { get; set; }
    public int TimeoutMs { get; set; } = 60_000;
}

public sealed class LaunchResponse
{
    public int Pid { get; set; }
}

public sealed class LaunchedProcessDto
{
    public int Pid { get; set; }
    public string File { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public bool StillRunning { get; set; }
}

// ---------- processes / status ----------

public sealed class ProcessDto
{
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public string Name { get; set; } = "";
    public int SessionId { get; set; }
    public string? User { get; set; }
    public long WorkingSetBytes { get; set; }
}

public sealed class DiskDto
{
    public string Name { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
}

public sealed class BatteryDto
{
    public bool Present { get; set; }
    public int? Percent { get; set; }
    public bool OnAc { get; set; }
}

public sealed class StatusResponse
{
    public string Os { get; set; } = "";            // "Windows" | "Linux" | "macOS"
    public string OsDescription { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public long UptimeSeconds { get; set; }
    public double? CpuPercent { get; set; }
    public long? RamUsedBytes { get; set; }
    public long? RamTotalBytes { get; set; }
    public List<DiskDto> Disks { get; set; } = new();
    public BatteryDto? Battery { get; set; }
    public List<string> TailnetIps { get; set; } = new();
    public List<string> LoggedOnUsers { get; set; } = new();
    public bool Locked { get; set; }
    public DateTimeOffset ServerTime { get; set; }
    // Supervision + jobs (v1.1)
    public List<SupervisedStatusDto> Supervised { get; set; } = new();
    public int JobsRunning { get; set; }
    // Fleet identity (update 5)
    public string DeviceCode { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string FleetMode { get; set; } = "Standalone";   // Standalone | Fleet
    public bool FleetConnected { get; set; }
    public bool MeshEnabled { get; set; }
    public string? MeshVirtualIp { get; set; }
    public string MeshStatus { get; set; } = "";
    /// <summary>P2P layer status line (discovery + peer sessions), same shape as MeshStatus.</summary>
    public string P2pStatus { get; set; } = "";
    // Owner policy (v1.2): capability toggles + desktop consent policy, so every
    // surface can show what the owner has switched off / gated.
    public CapabilityPolicy Capabilities { get; set; } = new();
    /// <summary>Item 45: a verified newer release waiting (Notify mode) —
    /// the app/panel/tray badge reads this.</summary>
    public string? UpdateAvailable { get; set; }
    public string ReleaseUpdateMode { get; set; } = "Notify";
    public DesktopPolicy DesktopPolicy { get; set; } = new();
}

// ---------- files ----------

public sealed class FileInfoResponse
{
    public bool Exists { get; set; }
    public long Size { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class BeginUploadRequest
{
    public string Path { get; set; } = "";
}

public sealed class BeginUploadResponse
{
    /// <summary>Bytes already present in the partial file — the client resumes from here.</summary>
    public long Received { get; set; }
}

public sealed class ChunkResponse
{
    public long Received { get; set; }
}

public sealed class CommitUploadRequest
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

// ---------- software / services / repair ----------

public sealed class SoftwarePackageDto
{
    public string Name { get; set; } = "";
    public string? Id { get; set; }              // winget id / package name / product code
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? InstallDate { get; set; }
    public long? SizeBytes { get; set; }
    public string Source { get; set; } = "";     // "registry" | "winget" | "dpkg" | "rpm" | "pacman"
}

public sealed class InstallRequest
{
    /// <summary>"manager" (winget id / distro package name) or "file" (local installer path).</summary>
    public string Kind { get; set; } = "manager";
    public string? Id { get; set; }
    public string? Path { get; set; }
    /// <summary>For EXE installers: silent args to try. Null = try "/S" then "/quiet" then "/silent".</summary>
    public string[]? SilentArgs { get; set; }
    public int TimeoutMs { get; set; } = 600_000;
}

public sealed class UninstallRequest
{
    public string Name { get; set; } = "";       // name, id or product code
    public int TimeoutMs { get; set; } = 600_000;
}

public sealed class UpgradeRequest
{
    public string? Id { get; set; }              // null = upgrade everything
    public int TimeoutMs { get; set; } = 900_000;
}

/// <summary>Result of a mutating operation. Verified is only true when the post-state was re-queried and matched.</summary>
public sealed class OperationResult
{
    public bool Success { get; set; }
    public int? ExitCode { get; set; }
    public string Output { get; set; } = "";
    public bool Verified { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ServiceDto
{
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string Status { get; set; } = "";       // Running | Stopped | Paused | ... | unknown
    public string? StartupMode { get; set; }       // Automatic | Manual | Disabled | enabled | disabled
}

public sealed class StartupModeRequest
{
    public string Mode { get; set; } = "";         // Automatic | Manual | Disabled
}

// ---------- registry (Windows head; other heads answer 501) ----------

public sealed class RegistryGetResponse
{
    public bool KeyExists { get; set; }
    public bool ValueExists { get; set; }
    public string? Kind { get; set; }
    public string? Value { get; set; }             // string rendering of the value
}

public sealed class RegistrySetRequest
{
    public string Path { get; set; } = "";         // e.g. HKLM\SOFTWARE\Foo or HKCU\Software\Bar
    public string Name { get; set; } = "";         // "" or "(Default)" = default value
    public string Kind { get; set; } = "String";   // String | ExpandString | DWord | QWord | MultiString | Binary(base64)
    public string Value { get; set; } = "";
}

// ---------- durable jobs (v1.1) ----------

/// <summary>Starts a durable job: a DETACHED process whose record and output files
/// persist under the agent's data dir (jobs/&lt;id&gt;/), so the job outlives the
/// API call — and the agent process itself. With Shell set, Command is a script
/// for that shell; otherwise Command is the executable and Args its arguments.</summary>
public sealed class JobStartRequest
{
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
    public string? Shell { get; set; }
    public string? Cwd { get; set; }
    /// <summary>Child-process environment. Values are applied at launch and then
    /// discarded — the persisted record keeps key NAMES only (see EnvKeys).</summary>
    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>One durable job's persisted record (jobs/&lt;id&gt;/job.json) and API view.
/// State: running | exited | cancelled | interrupted (was running when the agent
/// stopped; the outcome is unknowable, so it is closed out honestly, never faked).</summary>
public sealed class JobRecord
{
    public string Id { get; set; } = "";
    public string State { get; set; } = "running";
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
    public string? Shell { get; set; }
    public string? Cwd { get; set; }
    /// <summary>Environment variable NAMES the job was launched with. Values are never persisted.</summary>
    public List<string> EnvKeys { get; set; } = new();
    public int Pid { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>Output tails, filled only by the single-job GET (never by the list).</summary>
    public string? StdoutTail { get; set; }
    public string? StderrTail { get; set; }
}

// ---------- supervision (v1.1) ----------

/// <summary>Live state of one supervised process (config in esoagent.json "supervised").</summary>
public sealed class SupervisedStatusDto
{
    public string Name { get; set; } = "";
    /// <summary>running | exited | gave-up | starting</summary>
    public string State { get; set; } = "";
    public int? Pid { get; set; }
    public int Restarts { get; set; }
    public int? LastExitCode { get; set; }
    public DateTimeOffset? LastStartedAt { get; set; }
    public string Policy { get; set; } = "";
}

// ---------- inventory (v1.1) ----------

public sealed class NetworkInterfaceDto
{
    public string Name { get; set; } = "";
    public string? Mac { get; set; }
    public List<string> Ips { get; set; } = new();
    public bool IsLoopback { get; set; }
}

public sealed class ToolProbeDto
{
    public string Name { get; set; } = "";
    public bool Present { get; set; }
    /// <summary>First line of the tool's version output; null when absent.</summary>
    public string? Version { get; set; }
    public string? Path { get; set; }
}

/// <summary>One structured machine snapshot — everything the operator otherwise
/// needs a dozen probe commands to learn. Every probe is best-effort: a missing
/// tool or unreadable source yields "absent"/null, never an error.</summary>
public sealed class InventoryResponse
{
    public string Os { get; set; } = "";
    public string OsDescription { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string? CpuModel { get; set; }
    public int CpuCores { get; set; }
    public long? RamTotalBytes { get; set; }
    public long? RamFreeBytes { get; set; }
    public List<DiskDto> Volumes { get; set; } = new();
    public string? Gpu { get; set; }
    public List<NetworkInterfaceDto> NetworkInterfaces { get; set; } = new();
    public List<ToolProbeDto> Tools { get; set; } = new();
    public string AgentVersion { get; set; } = "";
    public long AgentUptimeSeconds { get; set; }
    public long SystemUptimeSeconds { get; set; }
    public List<string> InteractiveSessions { get; set; } = new();
}

// ---------- doctor (v1.1) ----------

public sealed class DoctorCheckDto
{
    public string Name { get; set; } = "";
    /// <summary>pass | warn | fail</summary>
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class DoctorResponse
{
    /// <summary>Worst of the checks: pass | warn | fail.</summary>
    public string Overall { get; set; } = "";
    public List<DoctorCheckDto> Checks { get; set; } = new();
}

// ---------- misc ----------

public sealed class ErrorResponse
{
    public string Error { get; set; } = "";
}

public sealed class RepairInfoDto
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}
