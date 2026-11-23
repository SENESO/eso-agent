using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EsoAgent.Core;

/// <summary>Job state machine (v1.1). States: running → exited | cancelled |
/// interrupted. Terminal states never transition again. Pure — unit-tested.</summary>
public static class JobLifecycle
{
    public const string Running = "running";
    public const string Exited = "exited";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";

    public static bool IsTerminal(string state) => state != Running;

    public static bool CanTransition(string from, string to)
        => from == Running && to is Exited or Cancelled or Interrupted;

    /// <summary>
    /// Startup closeout: a job still marked running when the agent (re)starts can
    /// never report its real outcome — its monitor died with the old process.
    /// Close it out honestly as interrupted. Output files are preserved untouched.
    /// Terminal records are returned unchanged.
    /// </summary>
    public static JobRecord CloseOutOnStartup(JobRecord record, DateTimeOffset now)
    {
        if (record.State != Running) return record;
        return new JobRecord
        {
            Id = record.Id,
            State = Interrupted,
            Command = record.Command,
            Args = record.Args,
            Shell = record.Shell,
            Cwd = record.Cwd,
            EnvKeys = record.EnvKeys,
            Pid = record.Pid,
            StartedAt = record.StartedAt,
            EndedAt = now,
            ExitCode = null, // unknowable — never invented
        };
    }
}

/// <summary>
/// Durable jobs (v1.1): detached processes owned by the agent whose records and
/// stdout/stderr live on disk (jobs/&lt;id&gt;/job.json + stdout.log + stderr.log
/// under the data dir). Unlike /api/exec (bounded by the HTTP call) and
/// /api/launch (in-memory tracking), a job survives client disconnects AND
/// agent restarts: the process itself is detached (systemd KillMode=process /
/// a plain child on Windows — nothing kills it when the agent stops), and the
/// record is re-read from disk after a restart. Jobs found still "running" at
/// startup are closed out as interrupted (see JobLifecycle).
/// </summary>
public sealed class JobService
{
    private readonly string _jobsDir;
    private readonly AuditLog _audit;
    private readonly ConcurrentDictionary<string, RunningJob> _running = new();

    private sealed class RunningJob
    {
        public required Process Process { get; init; }
        public volatile bool CancelRequested;
    }

    public JobService(string dataDir, AuditLog audit)
    {
        _jobsDir = Path.Combine(dataDir, "jobs");
        _audit = audit;
        Directory.CreateDirectory(_jobsDir);
    }

    private string JobDir(string id) => Path.Combine(_jobsDir, id);
    private string RecordPath(string id) => Path.Combine(JobDir(id), "job.json");

    // ----- persistence -----

    private void SaveRecord(JobRecord record)
    {
        Directory.CreateDirectory(JobDir(record.Id));
        StateFile.WriteText(RecordPath(record.Id), JsonSerializer.Serialize(record, EsoJson.Options));
    }

    private JobRecord? LoadRecord(string id)
    {
        // Backup-aware (defect-8): a torn job record recovers from its
        // last-good backup; only a genuinely absent or doubly-corrupt
        // record reads as null, and corrupt bytes are left untouched.
        var status = StateFile.TryReadJson<JobRecord>(RecordPath(id), out var record);
        return status is StateFileRead.Ok or StateFileRead.RecoveredFromBackup ? record : null;
    }

    /// <summary>Closes out every job left "running" by a previous agent lifetime.
    /// Called once at startup, before the API serves traffic.</summary>
    public void CloseOutOnStartup()
    {
        var closed = 0;
        foreach (var record in LoadAll())
        {
            if (record.State != JobLifecycle.Running) continue;
            SaveRecord(JobLifecycle.CloseOutOnStartup(record, DateTimeOffset.UtcNow));
            closed++;
        }
        if (closed > 0)
            _audit.Write("jobs", $"startup closeout: {closed} job(s) marked interrupted (agent restarted while they ran)", "ok");
    }

    private List<JobRecord> LoadAll()
    {
        var result = new List<JobRecord>();
        if (!Directory.Exists(_jobsDir)) return result;
        foreach (var dir in Directory.EnumerateDirectories(_jobsDir))
        {
            var record = LoadRecord(Path.GetFileName(dir));
            if (record != null) result.Add(record);
        }
        return result.OrderByDescending(r => r.StartedAt).ToList();
    }

    // ----- API -----

    public JobRecord Start(JobStartRequest request)
    {
        var exec = BuildExecRequest(request);
        var id = Guid.NewGuid().ToString("N");
        var dir = JobDir(id);
        Directory.CreateDirectory(dir);
        var stdoutPath = Path.Combine(dir, "stdout.log");
        var stderrPath = Path.Combine(dir, "stderr.log");

        var record = new JobRecord
        {
            Id = id,
            State = JobLifecycle.Running,
            Command = request.Command,
            Args = request.Args,
            Shell = request.Shell,
            Cwd = request.Cwd,
            EnvKeys = EnvAudit.KeyNames(request.Env), // names only — values are never persisted
            StartedAt = DateTimeOffset.UtcNow,
        };

        var psi = new ProcessStartInfo
        {
            FileName = exec.File,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = exec.Stdin != null,
            CreateNoWindow = true,
        };
        foreach (var a in exec.Args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(exec.Cwd)) psi.WorkingDirectory = exec.Cwd;
        if (exec.Env != null)
            foreach (var (k, v) in exec.Env) psi.Environment[k] = v;

        var proc = new Process { StartInfo = psi };
        proc.Start();
        record.Pid = proc.Id;
        SaveRecord(record);

        // Pump output to the job's files for the life of the process. The files
        // are the durable output — they keep growing after this agent is gone
        // only while the pumps live; after an agent restart the process keeps
        // its (now broken-pipe-safe) descriptors, see the report's honest limit.
        var stdoutFile = new FileStream(stdoutPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var stderrFile = new FileStream(stderrPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _ = Task.Run(async () => { try { await proc.StandardOutput.BaseStream.CopyToAsync(stdoutFile); } catch { } finally { await stdoutFile.DisposeAsync(); } });
        _ = Task.Run(async () => { try { await proc.StandardError.BaseStream.CopyToAsync(stderrFile); } catch { } finally { await stderrFile.DisposeAsync(); } });
        if (exec.Stdin != null)
        {
            _ = Task.Run(async () =>
            {
                try { await proc.StandardInput.WriteAsync(exec.Stdin); proc.StandardInput.Close(); }
                catch { /* process may have exited already */ }
            });
        }

        var running = new RunningJob { Process = proc };
        _running[id] = running;
        _ = Task.Run(async () =>
        {
            try { await proc.WaitForExitAsync(); } catch { }
            _running.TryRemove(id, out _);
            var current = LoadRecord(id) ?? record;
            if (current.State == JobLifecycle.Running)
            {
                current.State = running.CancelRequested ? JobLifecycle.Cancelled : JobLifecycle.Exited;
                current.ExitCode = running.CancelRequested ? null : SafeExitCode(proc);
                current.EndedAt = DateTimeOffset.UtcNow;
                SaveRecord(current);
                _audit.Write("jobs", $"job {id} {current.State} (exit {(current.ExitCode?.ToString() ?? "—")})", "ok");
            }
            proc.Dispose();
        });

        _audit.Write("jobs",
            $"job {id} started: {(request.Shell != null ? $"shell {request.Shell} script" : exec.File + " " + string.Join(" ", request.Args))}{EnvAudit.Describe(request.Env)}",
            "ok");
        return record;
    }

    public IReadOnlyList<JobRecord> List() => LoadAll();

    public int RunningCount => LoadAll().Count(r => r.State == JobLifecycle.Running);

    /// <summary>One job, with output tails attached (tailLines per stream, default 100).</summary>
    public JobRecord? Get(string id, int tailLines)
    {
        var record = LoadRecord(id);
        if (record == null) return null;
        if (tailLines < 0) tailLines = 0;
        if (tailLines > 5000) tailLines = 5000;
        record.StdoutTail = TailText(Path.Combine(JobDir(id), "stdout.log"), tailLines);
        record.StderrTail = TailText(Path.Combine(JobDir(id), "stderr.log"), tailLines);
        return record;
    }

    /// <summary>Cancels a running job (kills its process tree). Returns the record
    /// after the attempt, or null when the job does not exist.</summary>
    public JobRecord? Cancel(string id)
    {
        var record = LoadRecord(id);
        if (record == null) return null;
        if (record.State != JobLifecycle.Running) return record;
        if (_running.TryGetValue(id, out var running))
        {
            running.CancelRequested = true;
            try { running.Process.Kill(entireProcessTree: true); } catch { }
            // The monitor task finalizes the record as cancelled.
        }
        else
        {
            // Not tracked by this agent lifetime (should not happen after the
            // startup closeout, but stay honest): kill by PID and finalize here.
            try
            {
                using var p = Process.GetProcessById(record.Pid);
                p.Kill(entireProcessTree: true);
            }
            catch { }
            record.State = JobLifecycle.Cancelled;
            record.EndedAt = DateTimeOffset.UtcNow;
            SaveRecord(record);
        }
        _audit.Write("jobs", $"job {id} cancel requested", "ok");
        // Give the monitor a brief moment to finalize so the caller sees the end state.
        for (int i = 0; i < 20 && LoadRecord(id)?.State == JobLifecycle.Running; i++)
            Thread.Sleep(50);
        return LoadRecord(id);
    }

    // ----- helpers -----

    private static ExecRequest BuildExecRequest(JobStartRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Shell))
        {
            var exec = ProcessRunner.ResolveShell(new ShellRequest
            {
                Shell = request.Shell,
                Script = request.Command,
                Env = request.Env,
                TimeoutMs = 0,
            });
            if (!string.IsNullOrWhiteSpace(request.Cwd)) exec.Cwd = request.Cwd;
            return exec;
        }
        return new ExecRequest
        {
            File = request.Command,
            Args = request.Args,
            Cwd = request.Cwd,
            Env = request.Env,
            TimeoutMs = 0,
        };
    }

    private static int? SafeExitCode(Process proc)
    {
        try { return proc.ExitCode; } catch { return null; }
    }

    private static string? TailText(string path, int lines)
    {
        try
        {
            if (lines == 0 || !File.Exists(path)) return lines == 0 ? "" : null;
            var all = AuditLog.TailFile(path, lines);
            return string.Join("\n", all);
        }
        catch { return null; }
    }
}
