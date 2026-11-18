using System.Collections.Concurrent;
using System.Diagnostics;

namespace EsoAgent.Core;

/// <summary>
/// Persistent shell sessions (v1.2) — the tmux-class answer: named, long-lived
/// interactive shell processes hosted by the AGENT. They survive any client
/// disconnect (CLI, app, panel — the session belongs to the device, not to a
/// connection); output is buffered in a bounded ring with sequence numbers so
/// an attaching client asks "everything since seq N" and catches up. Sessions
/// do NOT survive an agent restart (documented, never faked).
///
/// Transport honesty: attach works over the fleet relay too, because the API
/// is plain request/response (poll for output, POST input) — no stream needed.
/// </summary>
public sealed class ShellSessionService
{
    private const int MaxChunks = 4000;
    private readonly AgentState _state;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    private sealed class Session
    {
        public required string Name { get; init; }
        public required string Shell { get; init; }
        public required Process Proc { get; init; }
        public required StreamWriter Stdin { get; init; }
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
        public readonly object Gate = new();
        public readonly List<ShellSessionChunk> Buffer = new();
        public long Seq;
    }

    public ShellSessionService(AgentState state) => _state = state;

    /// <summary>File/args for an interactive shell process (stdin stays open —
    /// unlike ResolveShell's one-shot script mode, no -NonInteractive).</summary>
    public static (string File, string[] Args) InteractiveShell(string shell)
    {
        var name = (shell ?? "").Trim().ToLowerInvariant();
        return name switch
        {
            "powershell" => (OperatingSystem.IsWindows() ? "powershell.exe" : "powershell", new[] { "-NoProfile", "-Command", "-" }),
            "pwsh" => ("pwsh", new[] { "-NoProfile", "-Command", "-" }),
            "bash" => ("bash", Array.Empty<string>()),
            "sh" => ("sh", Array.Empty<string>()),
            "cmd" => ("cmd.exe", Array.Empty<string>()),
            "" or "default" => OperatingSystem.IsWindows()
                ? ("powershell.exe", new[] { "-NoProfile", "-Command", "-" })
                : ("bash", Array.Empty<string>()),
            _ => throw new ArgumentException($"Unknown shell '{shell}'. Use powershell, pwsh, cmd, bash, sh or default."),
        };
    }

    public ShellSessionDto Create(ShellSessionCreateRequest req)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0 || name.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("Session name must be letters/digits/-/_/. only.");
        if (_sessions.TryGetValue(name, out var existing) && !existing.Proc.HasExited)
            throw new InvalidOperationException($"Session '{name}' already exists and is running.");
        var (file, args) = InteractiveShell(req.Shell);
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        var session = new Session
        {
            Name = name,
            Shell = string.IsNullOrWhiteSpace(req.Shell) ? "default" : req.Shell,
            Proc = proc,
            Stdin = proc.StandardInput,
        };
        proc.OutputDataReceived += (_, e) => Append(session, "stdout", e.Data);
        proc.ErrorDataReceived += (_, e) => Append(session, "stderr", e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _sessions[name] = session;
        _state.Audit.Write("sessions", $"persistent shell session '{name}' started ({session.Shell}, pid {proc.Id})", "ok");
        return ToDto(session);
    }

    private void Append(Session session, string stream, string? line)
    {
        if (line == null) return;
        lock (session.Gate)
        {
            session.Buffer.Add(new ShellSessionChunk { Seq = ++session.Seq, Stream = stream, Text = line });
            if (session.Buffer.Count > MaxChunks) session.Buffer.RemoveRange(0, session.Buffer.Count - MaxChunks);
        }
    }

    public IReadOnlyList<ShellSessionDto> List()
        => _sessions.Values.Select(ToDto).OrderBy(s => s.Name).ToList();

    public ShellSessionOutputResponse? GetOutput(string name, long since)
    {
        if (!_sessions.TryGetValue(name, out var session)) return null;
        lock (session.Gate)
        {
            return new ShellSessionOutputResponse
            {
                Name = name,
                Running = !session.Proc.HasExited,
                Chunks = session.Buffer.Where(c => c.Seq > since).ToList(),
                LatestSeq = session.Seq,
            };
        }
    }

    public bool SendInput(string name, string data)
    {
        if (!_sessions.TryGetValue(name, out var session) || session.Proc.HasExited) return false;
        try
        {
            session.Stdin.Write(data);
            session.Stdin.Flush();
            return true;
        }
        catch { return false; }
    }

    public bool Kill(string name)
    {
        if (!_sessions.TryRemove(name, out var session)) return false;
        try { if (!session.Proc.HasExited) session.Proc.Kill(entireProcessTree: true); } catch { }
        _state.Audit.Write("sessions", $"persistent shell session '{name}' killed", "ok");
        return true;
    }

    private ShellSessionDto ToDto(Session session)
    {
        lock (session.Gate)
        {
            return new ShellSessionDto
            {
                Name = session.Name,
                Shell = session.Shell,
                Pid = session.Proc.Id,
                Running = !session.Proc.HasExited,
                StartedAt = session.StartedAt,
                LatestSeq = session.Seq,
            };
        }
    }
}
