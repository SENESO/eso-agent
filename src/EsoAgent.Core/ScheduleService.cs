using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Core;

/// <summary>
/// Device scheduled tasks (v1.2) — the device's own cron. Schedules live in
/// esoagent.json ("schedules"); a ~30 s tick runs whatever
/// <see cref="SchedulePolicy"/> says is due, through the same unrestricted
/// process runner as everything else. Last-run state persists in
/// schedules-state.json so an agent restart does not re-fire a daily task.
/// Every run is audit-logged (endpoint "schedule") — and audit lines are what
/// the fleet activity shipper carries into the unified timeline, so schedule
/// results appear there with no extra channel.
/// </summary>
public sealed class ScheduleService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly AgentState _state;
    // Swappable as a whole (ReplaceSchedules): readers take the reference
    // once and iterate that snapshot, so a live edit never races a tick.
    private List<ScheduledTaskConfig> _schedules;
    private readonly Dictionary<string, RuntimeState> _runtime = new();
    private readonly HashSet<string> _running = new();
    private readonly object _gate = new();

    private sealed class RuntimeState
    {
        public DateTimeOffset? LastRunAt { get; set; }
        public int? LastExitCode { get; set; }
        public string? LastError { get; set; }
    }

    private sealed class StateFile
    {
        public Dictionary<string, RuntimeState> Schedules { get; set; } = new();
    }

    public ScheduleService(AgentState state)
    {
        _state = state;
        _schedules = state.Config.Schedules
            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Command))
            .ToList();
        LoadState();
    }

    private string StatePath => _state.Config.ResolvePath("schedules-state.json");

    private void LoadState()
    {
        // Through the shared StateFile helper (defect-8; fully qualified
        // — this class has its own nested StateFile DTO): a torn state
        // file recovers from its last-good backup; a doubly-corrupt one
        // is quarantined (bytes preserved) instead of silently reset.
        var status = EsoAgent.Core.StateFile.TryReadJson<StateFile>(StatePath, out var file);
        if (file != null)
            foreach (var (name, rt) in file.Schedules) _runtime[name] = rt;
        else if (status == StateFileRead.Corrupt)
            EsoAgent.Core.StateFile.QuarantineCorrupt(StatePath);
    }

    private void SaveState()
    {
        try
        {
            EsoAgent.Core.StateFile.WriteText(StatePath,
                JsonSerializer.Serialize(new StateFile { Schedules = _runtime }, EsoJson.Options));
        }
        catch { }
    }

    /// <summary>The live schedule definitions (the editor's view — Snapshot()
    /// is the state view). Returned as a copy; mutate via ReplaceSchedules.</summary>
    public IReadOnlyList<ScheduledTaskConfig> Configs()
    {
        lock (_gate) return _schedules.ToList();
    }

    /// <summary>Replaces the whole schedule set live (the write path behind
    /// PUT /api/schedules; the caller validates + persists config first).
    /// Runtime state for names that survive is kept; removed names are
    /// forgotten so a re-added task starts clean.</summary>
    public void ReplaceSchedules(IReadOnlyList<ScheduledTaskConfig> next)
    {
        var filtered = next
            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Command))
            .ToList();
        lock (_gate)
        {
            _schedules = filtered;
            var names = filtered.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _runtime.Keys.Where(k => !names.Contains(k)).ToList())
                _runtime.Remove(stale);
        }
    }

    /// <summary>Runs one schedule immediately (the Tasks UI's Run-now).
    /// False when the name is unknown or that task is already running.</summary>
    public Task<bool> RunNowAsync(string name)
    {
        ScheduledTaskConfig? schedule;
        lock (_gate)
        {
            schedule = _schedules.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (schedule == null || _running.Contains(schedule.Name)) return Task.FromResult(false);
            _running.Add(schedule.Name);
        }
        _ = Task.Run(() => RunOneAsync(schedule));
        return Task.FromResult(true);
    }

    public IReadOnlyList<ScheduleStateDto> Snapshot()
    {
        lock (_gate)
        {
            return _schedules.Select(s =>
            {
                _runtime.TryGetValue(s.Name, out var rt);
                return new ScheduleStateDto
                {
                    Name = s.Name,
                    Enabled = s.Enabled,
                    Description = SchedulePolicy.Describe(s),
                    LastRunAt = rt?.LastRunAt,
                    LastExitCode = rt?.LastExitCode,
                    LastError = rt?.LastError,
                };
            }).ToList();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No early return on an empty list: schedules can be added live via
        // the API, and the loop must already be running when they arrive.
        using var timer = new PeriodicTimer(TickInterval);
        // First pass shortly after start (interval schedules that never ran are due).
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch { return; }
        Tick();
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Tick();
        }
        catch (OperationCanceledException) { /* agent stopping */ }
    }

    private void Tick()
    {
        var now = DateTimeOffset.Now; // schedules think in device-local time
        foreach (var schedule in _schedules.ToList())
        {
            bool due;
            lock (_gate)
            {
                if (_running.Contains(schedule.Name)) continue;
                _runtime.TryGetValue(schedule.Name, out var rt);
                due = SchedulePolicy.IsDue(schedule, rt?.LastRunAt, now);
                if (due) _running.Add(schedule.Name);
            }
            if (!due) continue;
            _ = Task.Run(() => RunOneAsync(schedule));
        }
    }

    private async Task RunOneAsync(ScheduledTaskConfig schedule)
    {
        var startedAt = DateTimeOffset.Now;
        int? exitCode = null;
        string? error = null;
        try
        {
            ExecRequest req;
            if (schedule.Shell != null)
            {
                req = ProcessRunner.ResolveShell(new ShellRequest
                {
                    Shell = schedule.Shell, Script = schedule.Command,
                    Env = schedule.Env, TimeoutMs = schedule.TimeoutMs,
                });
                req.Cwd = schedule.Cwd;
            }
            else
            {
                req = new ExecRequest
                {
                    File = schedule.Command, Args = schedule.Args,
                    Cwd = schedule.Cwd, Env = schedule.Env, TimeoutMs = schedule.TimeoutMs,
                };
            }
            var result = await _state.Platform.ProcessRunner.RunAsync(req);
            exitCode = result.ExitCode;
            if (result.TimedOut) error = "timed out";
            _state.Audit.Write("schedule",
                $"scheduled '{schedule.Name}' ran ({SchedulePolicy.Describe(schedule)}): {(schedule.Shell != null ? $"[{schedule.Shell}] " : "")}{schedule.Command}",
                result.TimedOut ? "timeout" : $"exit {result.ExitCode}");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _state.Audit.Write("schedule", $"scheduled '{schedule.Name}' failed to start: {ex.Message}", "error");
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(schedule.Name);
                var rt = _runtime.TryGetValue(schedule.Name, out var existing) ? existing : new RuntimeState();
                rt.LastRunAt = startedAt;
                rt.LastExitCode = exitCode;
                rt.LastError = error;
                _runtime[schedule.Name] = rt;
                SaveState();
            }
        }
    }
}
