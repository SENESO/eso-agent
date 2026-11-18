using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace EsoAgent.Core;

/// <summary>Registry of processes started via launch-detached, so the agent can report/kill its own children.</summary>
public static class LaunchedProcesses
{
    private static readonly ConcurrentDictionary<int, LaunchedProcessDto> _byPid = new();

    public static void Track(int pid, string file)
        => _byPid[pid] = new LaunchedProcessDto { Pid = pid, File = file, StartedAt = DateTimeOffset.UtcNow, StillRunning = true };

    public static IReadOnlyList<LaunchedProcessDto> List()
    {
        foreach (var (pid, dto) in _byPid)
        {
            try { dto.StillRunning = !Process.GetProcessById(pid).HasExited; }
            catch { dto.StillRunning = false; }
        }
        return _byPid.Values.OrderBy(d => d.StartedAt).ToList();
    }
}

/// <summary>
/// Default cross-platform IProcessRunner on System.Diagnostics.Process.
/// Unrestricted by design: it runs whatever the caller asks, with the service
/// account's full privileges — the same power as an SSH shell.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxCaptureBytes = 4 * 1024 * 1024;

    private static ProcessStartInfo BuildStartInfo(ExecRequest req, bool redirect)
    {
        var psi = new ProcessStartInfo
        {
            FileName = req.File,
            UseShellExecute = false,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            RedirectStandardInput = redirect,
            CreateNoWindow = true,
        };
        foreach (var a in req.Args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(req.Cwd)) psi.WorkingDirectory = req.Cwd;
        if (req.Env != null)
            foreach (var (k, v) in req.Env) psi.Environment[k] = v;
        return psi;
    }

    public async Task<ExecResponse> RunAsync(ExecRequest request, CancellationToken ct = default)
        => await RunInternalAsync(request, null, ct);

    public async Task<ExecResponse> RunStreamingAsync(ExecRequest request, Func<string, string, Task> onData, CancellationToken ct = default)
        => await RunInternalAsync(request, onData, ct);

    private async Task<ExecResponse> RunInternalAsync(ExecRequest request, Func<string, string, Task>? onData, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = BuildStartInfo(request, redirect: true) };
        var stdout = new CappedBuffer(MaxCaptureBytes);
        var stderr = new CappedBuffer(MaxCaptureBytes);

        proc.Start();

        var stdinTask = Task.Run(async () =>
        {
            try
            {
                if (request.Stdin != null) await proc.StandardInput.WriteAsync(request.Stdin);
                proc.StandardInput.Close();
            }
            catch { /* process may have exited already */ }
        });

        async Task Pump(StreamReader reader, string streamName, CappedBuffer buffer)
        {
            var buf = new char[4096];
            int n;
            while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                var text = new string(buf, 0, n);
                buffer.Append(text);
                if (onData != null) await onData(streamName, text);
            }
        }

        var outTask = Pump(proc.StandardOutput, "stdout", stdout);
        var errTask = Pump(proc.StandardError, "stderr", stderr);

        bool timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.TimeoutMs > 0) timeoutCts.CancelAfter(request.TimeoutMs);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
        }
        await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        _ = stdinTask;
        sw.Stop();

        return new ExecResponse
        {
            ExitCode = timedOut ? null : proc.ExitCode,
            Stdout = stdout.ToString(),
            Stderr = stderr.ToString(),
            TimedOut = timedOut,
            Truncated = stdout.Truncated || stderr.Truncated,
            DurationMs = sw.ElapsedMilliseconds,
        };
    }

    public int LaunchDetached(ExecRequest request)
    {
        var psi = BuildStartInfo(request, redirect: false);
        psi.CreateNoWindow = false;
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start process.");
        LaunchedProcesses.Track(proc.Id, request.File);
        return proc.Id;
    }

    /// <summary>
    /// Turns a shell name + script into an ExecRequest. PowerShell/bash/sh read the
    /// script from stdin (no -EncodedCommand, no quoting hacks). cmd.exe gets a temp
    /// .cmd file because it cannot read a script from stdin cleanly.
    /// </summary>
    public static ExecRequest ResolveShell(ShellRequest shell)
    {
        var name = shell.Shell.Trim().ToLowerInvariant();
        ExecRequest Base(string file, params string[] args) => new()
        {
            File = file,
            Args = args,
            Stdin = shell.Script,
            Env = shell.Env,
            TimeoutMs = shell.TimeoutMs,
        };
        return name switch
        {
            "powershell" => Base(OperatingSystem.IsWindows() ? "powershell.exe" : "powershell", "-NoProfile", "-NonInteractive", "-Command", "-"),
            "pwsh" => Base("pwsh", "-NoProfile", "-NonInteractive", "-Command", "-"),
            "bash" => Base("bash", "-s"),
            "sh" => Base("sh", "-s"),
            "cmd" => CmdRequest(shell),
            "" or "default" => OperatingSystem.IsWindows()
                ? Base("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "-")
                : Base("bash", "-s"),
            _ => throw new ArgumentException($"Unknown shell '{shell.Shell}'. Use powershell, pwsh, cmd, bash, sh or default."),
        };
    }

    private static ExecRequest CmdRequest(ShellRequest shell)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"eso-shell-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(tmp, shell.Script);
        return new ExecRequest
        {
            File = "cmd.exe",
            Args = new[] { "/D", "/C", tmp },
            Env = shell.Env,
            TimeoutMs = shell.TimeoutMs,
        };
    }

    private sealed class CappedBuffer
    {
        private readonly StringBuilder _sb = new();
        private readonly int _cap;
        public bool Truncated { get; private set; }
        public CappedBuffer(int cap) => _cap = cap;
        public void Append(string text)
        {
            if (_sb.Length >= _cap) { Truncated = true; return; }
            if (_sb.Length + text.Length > _cap) { _sb.Append(text.AsSpan(0, _cap - _sb.Length)); Truncated = true; }
            else _sb.Append(text);
        }
        public override string ToString() => _sb.ToString();
    }
}
