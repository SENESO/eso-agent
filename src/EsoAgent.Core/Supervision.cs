using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Core;

/// <summary>One supervised process definition (esoagent.json "supervised").</summary>
public sealed class SupervisedProcessConfig
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
    public string? Cwd { get; set; }
    /// <summary>always | on-failure | never</summary>
    public string Restart { get; set; } = "on-failure";
    /// <summary>Maximum restarts before giving up. Null = unlimited.</summary>
    public int? MaxRestarts { get; set; }
}

/// <summary>Restart decision — pure, unit-tested.</summary>
public static class RestartPolicy
{
    public static bool ShouldRestart(string policy, int? exitCode, int restartCount, int? maxRestarts)
    {
        if (maxRestarts is { } max && restartCount >= max) return false;
        return policy.Trim().ToLowerInvariant() switch
        {
            "always" => true,
            "on-failure" => exitCode != 0, // null (killed / unknown) counts as failure
            _ => false,                    // "never" and anything unrecognized
        };
    }
}

/// <summary>
/// Supervision (v1.1): the agent keeps config-declared long-lived processes
/// alive. A background loop checks every ~15 s; an exited process is restarted
/// per its policy (always | on-failure | never, capped by maxRestarts), and
/// every start/restart is audit-logged. State is exposed via /api/supervised
/// and folded into /api/status.
///
/// Honest scope: supervision tracks the processes THIS agent lifetime started.
/// A supervised process that outlives an agent restart (detached by design) is
/// not re-adopted — the new lifetime starts a fresh instance. Adoption would
/// need PID+start-time fingerprinting and is deliberately not faked here.
/// </summary>
public sealed class SupervisorService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private readonly AgentState _state;
    private readonly List<Entry> _entries = new();
    private readonly object _gate = new();

    private sealed class Entry
    {
        public required SupervisedProcessConfig Config { get; init; }
        public Process? Process;
        public string State = "starting";
        public int Restarts;
        public int? LastExitCode;
        public DateTimeOffset? LastStartedAt;
    }

    public SupervisorService(AgentState state)
    {
        _state = state;
        foreach (var cfg in state.Config.Supervised)
            if (!string.IsNullOrWhiteSpace(cfg.Name) && !string.IsNullOrWhiteSpace(cfg.Command))
                _entries.Add(new Entry { Config = cfg });
    }

    public IReadOnlyList<SupervisedStatusDto> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Select(e => new SupervisedStatusDto
            {
                Name = e.Config.Name,
                State = e.State,
                Pid = e.Process is { HasExited: false } p ? p.Id : null,
                Restarts = e.Restarts,
                LastExitCode = e.LastExitCode,
                LastStartedAt = e.LastStartedAt,
                Policy = e.Config.Restart,
            }).ToList();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_entries.Count == 0) return;
        // Start everything once, promptly, then settle into the check cadence.
        Tick();
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Tick();
        }
        catch (OperationCanceledException) { /* agent stopping; supervised processes are left running (detached by design) */ }
    }

    /// <summary>One supervision pass: start what's not running, restart what the
    /// policy demands. Internal so the logic stays in one auditable place.</summary>
    internal void Tick()
    {
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                var proc = entry.Process;
                if (proc != null && !proc.HasExited)
                {
                    entry.State = "running";
                    continue;
                }
                if (proc != null)
                {
                    // Just observed the exit.
                    entry.LastExitCode = SafeExitCode(proc);
                    proc.Dispose();
                    entry.Process = null;
                    if (!RestartPolicy.ShouldRestart(entry.Config.Restart, entry.LastExitCode, entry.Restarts, entry.Config.MaxRestarts))
                    {
                        // "gave-up" only when the policy wanted a restart but the
                        // maxRestarts cap forbade it; otherwise the process simply ended.
                        var wantsRestart = RestartPolicy.ShouldRestart(entry.Config.Restart, entry.LastExitCode, entry.Restarts, null);
                        entry.State = wantsRestart ? "gave-up" : "exited";
                        continue;
                    }
                    entry.Restarts++;
                    Start(entry, restarted: true);
                }
                else if (entry.State == "starting")
                {
                    Start(entry, restarted: false);
                }
                // entries in exited/gave-up states are left alone.
            }
        }
    }

    private void Start(Entry entry, bool restarted)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = entry.Config.Command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            foreach (var a in entry.Config.Args) psi.ArgumentList.Add(a);
            if (!string.IsNullOrWhiteSpace(entry.Config.Cwd)) psi.WorkingDirectory = entry.Config.Cwd;
            var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            entry.Process = proc;
            entry.State = "running";
            entry.LastStartedAt = DateTimeOffset.UtcNow;
            _state.Audit.Write("supervise",
                restarted
                    ? $"supervised '{entry.Config.Name}' restarted (#{entry.Restarts}) after exit {(entry.LastExitCode?.ToString() ?? "—")}, new pid {proc.Id}"
                    : $"supervised '{entry.Config.Name}' started, pid {proc.Id}",
                "ok");
        }
        catch (Exception ex)
        {
            entry.State = "gave-up";
            _state.Audit.Write("supervise", $"supervised '{entry.Config.Name}' failed to start: {ex.Message}", "error");
        }
    }

    private static int? SafeExitCode(Process proc)
    {
        try { return proc.ExitCode; } catch { return null; }
    }
}
