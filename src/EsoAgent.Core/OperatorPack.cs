using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Core;

// =====================================================================
// v1.2 operator pack (items 27–36): the services that maximise the
// operator's control and awareness — deep telemetry, one-call
// diagnostics, the app-command registry with design-as-data perception,
// file-change watch, and the crash watch. Everything here runs through
// the agent's existing process/interactive-exec plumbing and is audit-
// logged like every other capability; nothing here is a side channel.
// =====================================================================

// ---------- telemetry (item 29) ----------

public sealed class ProcessTelemetryDto
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double CpuPercent { get; set; }
    public long RamBytes { get; set; }
    public long? ReadBytes { get; set; }
    public long? WriteBytes { get; set; }
}

public sealed class GpuTelemetryDto
{
    public string Name { get; set; } = "";
    public double? UtilPercent { get; set; }
    public long? VramUsedBytes { get; set; }
    public long? VramTotalBytes { get; set; }
    public double? TempC { get; set; }
}

public sealed class NetConnectionDto
{
    public string Proto { get; set; } = "";
    public string Local { get; set; } = "";
    public string Remote { get; set; } = "";
    public string State { get; set; } = "";
    public int? Pid { get; set; }
    public string? Process { get; set; }
}

public sealed class TelemetryResponse
{
    public DateTimeOffset At { get; set; }
    public double? CpuPercent { get; set; }
    public long? RamUsedBytes { get; set; }
    public long? RamTotalBytes { get; set; }
    /// <summary>Top processes by RAM (max 25).</summary>
    public List<ProcessTelemetryDto> TopProcesses { get; set; } = new();
    public List<GpuTelemetryDto> Gpus { get; set; } = new();
    public List<NetConnectionDto> Connections { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary>Pure parsers for telemetry tool output — unit-tested.</summary>
public static class TelemetryParsers
{
    /// <summary>nvidia-smi --query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu
    /// --format=csv,noheader,nounits → one GPU per line (MiB for memory).</summary>
    public static List<GpuTelemetryDto> ParseNvidiaSmi(string output)
    {
        var gpus = new List<GpuTelemetryDto>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 5) continue;
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var util);
            long.TryParse(parts[2], out var usedMiB);
            long.TryParse(parts[3], out var totalMiB);
            double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var temp);
            gpus.Add(new GpuTelemetryDto
            {
                Name = parts[0],
                UtilPercent = util,
                VramUsedBytes = usedMiB * 1024 * 1024,
                VramTotalBytes = totalMiB * 1024 * 1024,
                TempC = temp,
            });
        }
        return gpus;
    }

    /// <summary>Windows `netstat -ano` lines (TCP/UDP, with owning PID).</summary>
    public static List<NetConnectionDto> ParseNetstat(string output)
    {
        var result = new List<NetConnectionDto>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            var proto = parts[0].ToUpperInvariant();
            if (proto is not ("TCP" or "UDP")) continue;
            var conn = new NetConnectionDto { Proto = proto, Local = parts[1] };
            if (proto == "TCP" && parts.Length >= 5)
            {
                conn.Remote = parts[2];
                conn.State = parts[3];
                if (int.TryParse(parts[4], out var pid)) conn.Pid = pid;
            }
            else if (proto == "UDP" && parts.Length >= 4)
            {
                conn.Remote = parts[2];
                if (int.TryParse(parts[3], out var pid)) conn.Pid = pid;
            }
            result.Add(conn);
        }
        return result;
    }

    /// <summary>Linux `ss -tanp` / `ss -uanp` lines (state, endpoints, optional process).</summary>
    public static List<NetConnectionDto> ParseSs(string output, string proto)
    {
        var result = new List<NetConnectionDto>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("State", StringComparison.Ordinal)) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            var conn = new NetConnectionDto
            {
                Proto = proto,
                State = parts[0],
                Local = parts[3],
                Remote = parts[4],
            };
            // users:(("name",pid=123,fd=4))
            var pidAt = line.IndexOf("pid=", StringComparison.Ordinal);
            if (pidAt >= 0)
            {
                var digits = new string(line[(pidAt + 4)..].TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var pid)) conn.Pid = pid;
            }
            var nameAt = line.IndexOf("((\"", StringComparison.Ordinal);
            if (nameAt >= 0)
            {
                var end = line.IndexOf('"', nameAt + 3);
                if (end > nameAt) conn.Process = line[(nameAt + 3)..end];
            }
            result.Add(conn);
        }
        return result;
    }
}

public static class TelemetryService
{
    public static async Task<TelemetryResponse> CaptureAsync(AgentState state, CancellationToken ct = default)
    {
        var response = new TelemetryResponse { At = DateTimeOffset.UtcNow };
        var status = await Endpoints.BuildStatusAsync(state);
        response.CpuPercent = status.CpuPercent;
        response.RamUsedBytes = status.RamUsedBytes;
        response.RamTotalBytes = status.RamTotalBytes;

        // Per-process CPU needs two samples; take them 300 ms apart.
        var first = SnapshotProcesses();
        await Task.Delay(300, ct);
        var second = SnapshotProcesses();
        var elapsedCpu = 0.3 * Environment.ProcessorCount;
        foreach (var (pid, (name, cpuTime, ram, read, write)) in second)
        {
            double cpu = 0;
            if (first.TryGetValue(pid, out var before) && elapsedCpu > 0)
                cpu = Math.Max(0, (cpuTime - before.Cpu).TotalSeconds / elapsedCpu * 100);
            response.TopProcesses.Add(new ProcessTelemetryDto
            {
                Pid = pid, Name = name, CpuPercent = Math.Round(cpu, 1),
                RamBytes = ram, ReadBytes = read, WriteBytes = write,
            });
        }
        response.TopProcesses = response.TopProcesses.OrderByDescending(p => p.RamBytes).Take(25).ToList();

        // GPU via nvidia-smi when present (any platform); absence is a note, not an error.
        var smi = await RunQuietAsync(state, "nvidia-smi",
            new[] { "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu", "--format=csv,noheader,nounits" }, ct);
        if (smi is { ExitCode: 0 } && !string.IsNullOrWhiteSpace(smi.Stdout))
            response.Gpus = TelemetryParsers.ParseNvidiaSmi(smi.Stdout);
        else
            response.Notes.Add("No nvidia-smi data (no NVIDIA GPU, or the tool is not installed). Other vendors' GPUs are not probed in this version.");

        // Network connections.
        if (OperatingSystem.IsWindows())
        {
            var net = await RunQuietAsync(state, "netstat", new[] { "-ano" }, ct);
            if (net is { ExitCode: 0 }) response.Connections = TelemetryParsers.ParseNetstat(net.Stdout);
        }
        else if (OperatingSystem.IsLinux())
        {
            var tcp = await RunQuietAsync(state, "ss", new[] { "-tanp" }, ct);
            var udp = await RunQuietAsync(state, "ss", new[] { "-uanp" }, ct);
            if (tcp is { ExitCode: 0 }) response.Connections.AddRange(TelemetryParsers.ParseSs(tcp.Stdout, "TCP"));
            if (udp is { ExitCode: 0 }) response.Connections.AddRange(TelemetryParsers.ParseSs(udp.Stdout, "UDP"));
            if (response.Connections.Count == 0) response.Notes.Add("No connection data (ss/iproute2 not available).");
        }
        return response;
    }

    private static Dictionary<int, (string Name, TimeSpan Cpu, long Ram, long? Read, long? Write)> SnapshotProcesses()
    {
        var map = new Dictionary<int, (string, TimeSpan, long, long?, long?)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                long? read = null, write = null;
                if (OperatingSystem.IsLinux())
                {
                    // /proc/<pid>/io: read_bytes / write_bytes (best effort).
                    try
                    {
                        foreach (var line in File.ReadLines($"/proc/{p.Id}/io"))
                        {
                            if (line.StartsWith("read_bytes:")) read = long.Parse(line[11..].Trim());
                            else if (line.StartsWith("write_bytes:")) write = long.Parse(line[12..].Trim());
                        }
                    }
                    catch { }
                }
                map[p.Id] = (p.ProcessName, p.TotalProcessorTime, p.WorkingSet64, read, write);
            }
            catch { /* exited mid-scan */ }
            finally { p.Dispose(); }
        }
        return map;
    }

    internal static async Task<ExecResponse?> RunQuietAsync(AgentState state, string file, string[] args, CancellationToken ct, int timeoutMs = 15_000)
    {
        try
        {
            return await state.Platform.ProcessRunner.RunAsync(
                new ExecRequest { File = file, Args = args, TimeoutMs = timeoutMs }, ct);
        }
        catch { return null; }
    }
}

/// <summary>Telemetry watch (item 29, second half): the agent samples its own
/// telemetry on an interval; sustained threshold crossings are audit-logged
/// (endpoint "telemetry") and therefore land in the unified activity timeline
/// and the server's alert feed. Thresholds live in esoagent.json.</summary>
public sealed class TelemetryWatchService : BackgroundService
{
    private readonly AgentState _state;
    private readonly Dictionary<string, int> _strikes = new();

    public TelemetryWatchService(AgentState state) => _state = state;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cfg = _state.Config.TelemetryWatch;
        if (!cfg.Enabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var t = await TelemetryService.CaptureAsync(_state, ct);
                Check("cpu", t.CpuPercent, cfg.CpuPercent, v => $"CPU at {v:0}% (threshold {cfg.CpuPercent}%)");
                var ramPct = t.RamUsedBytes is { } used && t.RamTotalBytes is { } total && total > 0
                    ? 100.0 * used / total : (double?)null;
                Check("ram", ramPct, cfg.MemoryPercent, v => $"RAM at {v:0}% (threshold {cfg.MemoryPercent}%)");
                foreach (var gpu in t.Gpus)
                    Check("gpu-temp", gpu.TempC, cfg.GpuTempC, v => $"GPU '{gpu.Name}' at {v:0}°C (threshold {cfg.GpuTempC}°C)");
            }
            catch (Exception ex) { _state.Audit.Write("telemetry", "watch sample failed: " + ex.Message, "error"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, cfg.IntervalSeconds)), ct); } catch { return; }
        }
    }

    private void Check(string key, double? value, double threshold, Func<double, string> describe)
    {
        if (value is not { } v) { _strikes.Remove(key); return; }
        if (v >= threshold)
        {
            var strikes = _strikes[key] = _strikes.GetValueOrDefault(key) + 1;
            if (strikes == 2) // sustained across two samples — not a momentary spike
                _state.Audit.Write("telemetry", describe(v), "warn");
        }
        else _strikes.Remove(key);
    }
}

// ---------- one-call diagnostics (item 30) ----------

public sealed class DiagnoseResponse
{
    public DateTimeOffset At { get; set; }
    /// <summary>pass | warn | fail — the worst section wins.</summary>
    public string Overall { get; set; } = "pass";
    public DoctorResponse Doctor { get; set; } = new();
    public List<string> OsErrors { get; set; } = new();
    public List<string> Crashes { get; set; } = new();
    public List<string> FailedServices { get; set; } = new();
    public List<string> Pressure { get; set; } = new();
    public int AuditErrorCount { get; set; }
    public List<string> RecentAuditErrors { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public static class DiagnoseService
{
    public static async Task<DiagnoseResponse> BuildAsync(AgentState state, CancellationToken ct = default)
    {
        var d = new DiagnoseResponse { At = DateTimeOffset.UtcNow };
        d.Doctor = await Doctor.BuildAsync(state);
        var status = await Endpoints.BuildStatusAsync(state);

        // Resource pressure.
        if (status.RamUsedBytes is { } used && status.RamTotalBytes is { } total && total > 0)
        {
            var pct = 100.0 * used / total;
            if (pct >= 90) d.Pressure.Add($"RAM pressure: {pct:0}% used.");
            else if (pct >= 80) d.Pressure.Add($"RAM elevated: {pct:0}% used.");
        }
        foreach (var disk in status.Disks.Where(x => x.TotalBytes > 0))
        {
            var freePct = 100.0 * disk.FreeBytes / disk.TotalBytes;
            if (freePct < 10) d.Pressure.Add($"Disk '{disk.Name}' nearly full: {freePct:0.#}% free.");
            else if (freePct < 20) d.Pressure.Add($"Disk '{disk.Name}' low: {freePct:0.#}% free.");
        }
        if (status.CpuPercent is { } cpu && cpu >= 95) d.Pressure.Add($"CPU saturated: {cpu:0}%.");

        // Failed services (auto-start but not running).
        try
        {
            var services = await state.Platform.Services.ListAsync(ct);
            d.FailedServices = services
                .Where(s => s.Status != "Running" && (s.StartupMode is "Automatic" or "enabled"))
                .Select(s => $"{s.Name} ({s.Status})").Take(30).ToList();
        }
        catch (Exception ex) { d.Notes.Add("Service list unavailable: " + ex.Message); }
        if (OperatingSystem.IsLinux())
        {
            var failed = await TelemetryService.RunQuietAsync(state, "systemctl", new[] { "--failed", "--no-legend", "--no-pager" }, ct);
            if (failed is { ExitCode: 0 })
                foreach (var line in failed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!d.FailedServices.Contains(line)) d.FailedServices.Add(line);
        }

        // OS error events + crashes, per platform.
        if (OperatingSystem.IsLinux())
        {
            var journal = await TelemetryService.RunQuietAsync(state, "journalctl",
                new[] { "-p", "err", "-n", "40", "--no-pager", "-o", "short-iso" }, ct, 20_000);
            if (journal is { ExitCode: 0 }) d.OsErrors = journal.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(40).ToList();
            else d.Notes.Add("journalctl not readable from the agent session (needs journal group / root).");
            var cores = await TelemetryService.RunQuietAsync(state, "coredumpctl",
                new[] { "list", "--no-pager", "--no-legend", "-n", "20" }, ct, 20_000);
            if (cores is { ExitCode: 0 }) d.Crashes = cores.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20).ToList();
            else d.Notes.Add("coredumpctl unavailable or no core dumps recorded.");
        }
        else if (OperatingSystem.IsWindows())
        {
            var script = """
                $since = (Get-Date).AddHours(-24)
                Get-WinEvent -FilterHashtable @{LogName='System','Application'; Level=1,2; StartTime=$since} -MaxEvents 40 -ErrorAction SilentlyContinue |
                  ForEach-Object { "{0} [{1}] {2}: {3}" -f $_.TimeCreated.ToString('s'), $_.LogName, $_.ProviderName, ($_.Message -replace '\s+',' ').Substring(0, [Math]::Min(220, $_.Message.Length)) }
                """;
            var events = await RunPowerShellAsync(state, script, ct);
            if (events is { ExitCode: 0 }) d.OsErrors = events.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(40).ToList();
            else d.Notes.Add("Windows Event Log read failed from the agent session.");
            var crashScript = """
                $since = (Get-Date).AddDays(-7)
                Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000,1026; StartTime=$since} -MaxEvents 20 -ErrorAction SilentlyContinue |
                  ForEach-Object { "{0} {1}" -f $_.TimeCreated.ToString('s'), (($_.Message -replace '\s+',' ').Substring(0, [Math]::Min(260, $_.Message.Length))) }
                """;
            var crashes = await RunPowerShellAsync(state, crashScript, ct);
            if (crashes is { ExitCode: 0 }) d.Crashes = crashes.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20).ToList();
        }

        // Recent agent audit errors.
        var (_, auditTotal) = state.Audit.ReadRange(0, int.MaxValue);
        var (lines, _) = state.Audit.ReadRange(Math.Max(0, auditTotal - 1000), 1000);
        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var result = doc.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null;
                if (result is "error" or "401" or "403")
                {
                    d.AuditErrorCount++;
                    var endpoint = doc.RootElement.TryGetProperty("endpoint", out var e) ? e.GetString() : "?";
                    var summary = doc.RootElement.TryGetProperty("summary", out var s) ? s.GetString() : "";
                    d.RecentAuditErrors.Add($"{endpoint}: {summary} ({result})");
                }
            }
            catch { }
        }
        d.RecentAuditErrors = d.RecentAuditErrors.TakeLast(10).ToList();

        // Overall = worst signal.
        if (d.Doctor.Overall == "fail" || d.Crashes.Count > 0 && d.Pressure.Any(p => p.Contains("nearly full")) || d.FailedServices.Count >= 3)
            d.Overall = "fail";
        else if (d.Doctor.Overall == "warn" || d.OsErrors.Count > 0 || d.Pressure.Count > 0 || d.FailedServices.Count > 0 || d.AuditErrorCount > 0)
            d.Overall = "warn";
        return d;
    }

    internal static async Task<ExecResponse?> RunPowerShellAsync(AgentState state, string script, CancellationToken ct, int timeoutMs = 30_000)
    {
        try
        {
            return await state.Platform.ProcessRunner.RunAsync(new ExecRequest
            {
                File = "powershell",
                Args = new[] { "-NoProfile", "-NonInteractive", "-Command", script },
                TimeoutMs = timeoutMs,
            }, ct);
        }
        catch { return null; }
    }
}

// ---------- crash watch (item 31) ----------

/// <summary>
/// Watches the OS crash channels and audit-logs new application crashes
/// (endpoint "crash" → the unified timeline; the fleet server turns those
/// into alerts). Linux: journal "dumped core" lines since the last poll.
/// Windows: Application Error / .NET Runtime events (1000/1026) since the
/// last poll. Baseline is agent start — history is diagnose's job, the
/// watch is for NEW crashes only.
/// </summary>
public sealed class CrashWatchService : BackgroundService
{
    private readonly AgentState _state;
    private DateTimeOffset _since;

    public CrashWatchService(AgentState state) => _state = state;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_state.Config.CrashWatchEnabled) return;
        _since = DateTimeOffset.UtcNow;
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct); }
            catch (Exception ex) { _state.Audit.Write("crash", "watch poll failed: " + ex.Message, "error"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch { return; }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (OperatingSystem.IsLinux())
        {
            var sinceText = _since.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var journal = await TelemetryService.RunQuietAsync(_state, "journalctl",
                new[] { "--since", sinceText, "--no-pager", "-o", "short-iso", "-g", "dumped core" }, ct, 20_000);
            if (journal is { ExitCode: 0 })
            {
                foreach (var line in journal.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (line.Contains("-- No entries --")) continue;
                    _state.Audit.Write("crash", line.Length > 300 ? line[..300] : line, "crash");
                }
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            var script = $$"""
                $since = [DateTime]::Parse('{{_since.UtcDateTime:yyyy-MM-ddTHH:mm:ss}}')
                Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000,1026; StartTime=$since} -ErrorAction SilentlyContinue |
                  ForEach-Object { "{0} {1}" -f $_.TimeCreated.ToString('s'), (($_.Message -replace '\s+',' ').Substring(0, [Math]::Min(260, $_.Message.Length))) }
                """;
            var crashes = await DiagnoseService.RunPowerShellAsync(_state, script, ct);
            if (crashes is { ExitCode: 0 })
            {
                foreach (var line in crashes.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    _state.Audit.Write("crash", line, "crash");
            }
        }
        _since = now;
    }
}

// ---------- file-change watch (item 34) ----------

public sealed class WatchEventDto
{
    public long Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public string Watch { get; set; } = "";
    public string Kind { get; set; } = "";   // created | changed | deleted | renamed
    public string Path { get; set; } = "";
}

/// <summary>
/// File-change watch: FileSystemWatchers over the configured paths (plus
/// runtime-added ones). Events land in a bounded ring for followers
/// (GET /api/watch?since=) and are audit-logged in coalesced bursts, so
/// they reach the unified activity timeline without flooding it.
/// </summary>
public sealed class WatchService : IDisposable
{
    private const int RingCap = 2000;
    private readonly AgentState _state;
    private readonly object _gate = new();
    private readonly List<(FileSystemWatcher Watcher, string Name)> _watchers = new();
    private readonly List<WatchEventDto> _ring = new();
    private long _seq;
    private int _burstCount;
    private DateTimeOffset _burstWindowStart = DateTimeOffset.MinValue;
    private string _burstWatch = "";

    public WatchService(AgentState state)
    {
        _state = state;
        foreach (var w in state.Config.Watches)
            Add(w.Name, w.Path, w.Recursive, persist: false);
    }

    public List<WatchInfoDto> List()
    {
        lock (_gate)
            return _watchers.Select(w => new WatchInfoDto
            {
                Name = w.Name,
                Path = w.Watcher.Path,
                Recursive = w.Watcher.IncludeSubdirectories,
            }).ToList();
    }

    public (bool Ok, string? Error) Add(string name, string path, bool recursive, bool persist)
    {
        try
        {
            if (!Directory.Exists(path)) return (false, $"Directory not found: {path}");
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = recursive,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += (_, e) => OnEvent(name, "created", e.FullPath);
            watcher.Changed += (_, e) => OnEvent(name, "changed", e.FullPath);
            watcher.Deleted += (_, e) => OnEvent(name, "deleted", e.FullPath);
            watcher.Renamed += (_, e) => OnEvent(name, "renamed", e.FullPath);
            lock (_gate) _watchers.Add((watcher, name));
            _state.Audit.Write("watch", $"watch '{name}' started on {path} (recursive={recursive})", "ok");
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private void OnEvent(string watch, string kind, string path)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _ring.Add(new WatchEventDto { Seq = ++_seq, At = now, Watch = watch, Kind = kind, Path = path });
            if (_ring.Count > RingCap) _ring.RemoveAt(0);
            // Audit coalescing: first event of a burst writes a line; a burst
            // over 20 events/sec collapses into periodic summary lines.
            if (now - _burstWindowStart > TimeSpan.FromSeconds(1))
            {
                if (_burstCount > 20)
                    _state.Audit.Write("watch", $"watch '{_burstWatch}': {_burstCount} change events in the last burst", "ok");
                _burstWindowStart = now;
                _burstCount = 0;
                _burstWatch = watch;
                _state.Audit.Write("watch", $"{kind}: {path} (watch '{watch}')", "ok");
            }
            _burstCount++;
        }
    }

    public List<WatchEventDto> EventsSince(long since)
    {
        lock (_gate) return _ring.Where(e => e.Seq > since).ToList();
    }

    public void Dispose()
    {
        lock (_gate) { foreach (var w in _watchers) w.Watcher.Dispose(); _watchers.Clear(); }
    }
}

public sealed class WatchInfoDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Recursive { get; set; }
}

public sealed class WatchAddRequest
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Recursive { get; set; } = true;
}
