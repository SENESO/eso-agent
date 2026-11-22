using System.Diagnostics;

namespace EsoAgent.Core;

/// <summary>Lifecycle states of the embedded mesh engine.</summary>
public enum MeshEngineState
{
    Stopped,
    Starting,
    Running,
    Restarting,
    GaveUp,
    Stopping,
}

/// <summary>One running engine process, abstracted so the supervisor's
/// lifecycle logic is unit-testable without a real sidecar.</summary>
public interface IMeshEngineProcess : IDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    void Start();
    /// <summary>Stops the process: brief graceful wait, then kill. The engine
    /// has no shutdown handshake on Windows (wireguard-go exits on process
    /// termination); adapter cleanup is the supervisor's job afterwards.</summary>
    Task StopAsync(TimeSpan grace, CancellationToken ct = default);
}

/// <summary>The production engine process: eso-mesh.exe &lt;iface&gt;, output
/// appended to a log file in the data dir, held in a kill-on-close job so the
/// engine can never outlive the agent.</summary>
public sealed class SystemMeshEngineProcess : IMeshEngineProcess
{
    private readonly string _exePath;
    private readonly string _interfaceName;
    private readonly string? _logFilePath;
    private Process? _process;
    private KillOnCloseJob? _job;
    private FileStream? _logStream;

    public SystemMeshEngineProcess(string exePath, string interfaceName, string? logFilePath)
    {
        _exePath = exePath;
        _interfaceName = interfaceName;
        _logFilePath = logFilePath;
    }

    public bool HasExited => _process == null || _process.HasExited;

    public int? ExitCode
    {
        get
        {
            try { return _process is { HasExited: true } p ? p.ExitCode : null; }
            catch { return null; }
        }
    }

    public int? Pid => _process is { HasExited: false } p ? p.Id : null;

    public void Start()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(_interfaceName);
        // The engine must find wintun.dll: it sits next to eso-mesh.exe, and a
        // process's own directory is first in its DLL search order.
        psi.WorkingDirectory = Path.GetDirectoryName(_exePath) ?? AppContext.BaseDirectory;
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        _process = proc;
        _job = KillOnCloseJob.TryCreateAndAssign(proc);
        PumpOutput(proc);
    }

    private void PumpOutput(Process proc)
    {
        if (_logFilePath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
            _logStream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(_logStream) { AutoFlush = true };
            writer.WriteLine($"--- eso-mesh started {DateTimeOffset.UtcNow:O} pid={proc.Id} ---");
            _ = CopyLinesAsync(proc.StandardError, writer);
            _ = CopyLinesAsync(proc.StandardOutput, writer);
        }
        catch { /* logging is best-effort; the engine itself matters */ }

        static async Task CopyLinesAsync(StreamReader reader, TextWriter writer)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    try { await writer.WriteLineAsync(line); } catch { return; }
                }
            }
            catch { /* reader closed with the process */ }
        }
    }

    public async Task StopAsync(TimeSpan grace, CancellationToken ct = default)
    {
        var proc = _process;
        if (proc == null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(grace);
                try { await proc.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { /* already killed; exit is imminent */ }
            }
        }
        catch { /* already gone */ }
        finally
        {
            _job?.Dispose(); _job = null;
            try { _logStream?.Dispose(); } catch { /* best-effort */ }
            _logStream = null;
        }
    }

    public void Dispose()
    {
        _job?.Dispose();
        try { _logStream?.Dispose(); } catch { /* best-effort */ }
        try { _process?.Dispose(); } catch { /* best-effort */ }
    }
}

/// <summary>Outcome of one engine apply (start/adopt + configure + address).</summary>
public sealed record MeshEngineApplyResult(bool Ok, string Summary, UapiDeviceStatus? Status);

/// <summary>
/// Owns the embedded mesh engine's lifecycle on Windows: starts (or adopts) the
/// eso-mesh sidecar, converges it to the desired configuration over UAPI,
/// applies the adapter addressing, watches it on a health cadence and restarts
/// it per the shared restart policy, and tears everything down cleanly on stop
/// (engine process, routes, address, and — if the process died hard — the
/// Wintun adapter itself).
///
/// Adoption: if an engine from a previous agent lifetime is still answering on
/// the UAPI pipe (agent restart while the tunnel was up), it is adopted —
/// reconfigured and monitored — instead of fighting it for the adapter name.
/// It becomes owned the first time it dies and we respawn it.
///
/// Platform note: the supervisor is OS-agnostic except for the addressing plan
/// and adapter cleanup, which are Windows-only today because the Windows
/// agent is where the external dependency lived. The UAPI transports include
/// the Linux unix-socket variant, so a future Linux pass only needs a Linux
/// addressing plan behind the same supervisor.
/// </summary>
public sealed class MeshEngineSupervisor : IDisposable
{
    public const string InterfaceName = "eso0";

    private readonly string _enginePath;
    private readonly string _installDir;
    private readonly IProcessRunner _runner;
    private readonly Func<IMeshEngineProcess> _processFactory;
    private readonly Func<IUapiTransport> _transportFactory;
    private readonly Func<string, WgInterfaceConfig, List<string[]>> _applyAddressing;
    private readonly Func<string, WgInterfaceConfig, List<string[]>> _removeAddressing;
    private readonly Action<string, string> _audit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _maxRestarts;
    private readonly TimeSpan _healthInterval;
    private readonly TimeSpan _startupTimeout;

    private IMeshEngineProcess? _process;
    private bool _adopted;
    private WgInterfaceConfig? _desired;
    /// <summary>The model the CURRENT engine instance is known to hold
    /// (set only after a successful UAPI set on this instance; cleared
    /// whenever a new instance starts or is adopted, and on stop). The
    /// idempotency gate keys on it — see ApplyAsync.</summary>
    private WgInterfaceConfig? _appliedModel;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _restarts;

    public MeshEngineState State { get; private set; } = MeshEngineState.Stopped;
    public UapiDeviceStatus? LastStatus { get; private set; }
    public int RestartCount => _restarts;
    public bool Adopted => _adopted;

    /// <summary>True when the live engine instance already holds exactly
    /// this configuration (a set for it succeeded on this instance and
    /// the instance has not been replaced since).</summary>
    public bool IsApplied(WgInterfaceConfig model)
        => _appliedModel != null && WgConfigEquality.Equals(_appliedModel, model);

    public MeshEngineSupervisor(
        string enginePath,
        IProcessRunner runner,
        Action<string, string>? audit = null,
        Func<IMeshEngineProcess>? processFactory = null,
        Func<IUapiTransport>? transportFactory = null,
        Func<string, WgInterfaceConfig, List<string[]>>? applyAddressing = null,
        Func<string, WgInterfaceConfig, List<string[]>>? removeAddressing = null,
        int maxRestarts = 10,
        TimeSpan? healthInterval = null,
        TimeSpan? startupTimeout = null,
        string? logFilePath = null)
    {
        _enginePath = enginePath;
        _installDir = Path.GetDirectoryName(enginePath) ?? AppContext.BaseDirectory;
        _runner = runner;
        _audit = audit ?? ((_, _) => { });
        _processFactory = processFactory ?? (() => new SystemMeshEngineProcess(enginePath, InterfaceName, logFilePath));
        _transportFactory = transportFactory ?? (() => new NamedPipeUapiTransport(InterfaceName));
        _applyAddressing = applyAddressing ?? WindowsMeshAddressing.ApplyCommands;
        _removeAddressing = removeAddressing ?? WindowsMeshAddressing.RemoveCommands;
        _maxRestarts = maxRestarts;
        _healthInterval = healthInterval ?? TimeSpan.FromSeconds(15);
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>One-line status for the agent's status surfaces.</summary>
    public string Status => State switch
    {
        MeshEngineState.Running => $"embedded engine running{(_adopted ? " (adopted)" : "")}; {LastStatus?.Summarize() ?? "status pending"}",
        MeshEngineState.GaveUp => $"embedded engine gave up after {_restarts} restart(s)",
        _ => $"embedded engine {State.ToString().ToLowerInvariant()}",
    };

    /// <summary>
    /// Converges the engine to <paramref name="model"/>: starts or adopts the
    /// engine if needed, pushes the configuration via UAPI, (re)applies the
    /// adapter addressing, and reads the status back. Idempotent: when the
    /// live engine instance already holds exactly this model, the set and
    /// the addressing are skipped (a set is a full peer replacement — see
    /// the gate in the body) and only the status is refreshed.
    /// </summary>
    public async Task<MeshEngineApplyResult> ApplyAsync(WgInterfaceConfig model, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _desired = model;
            var ensured = await EnsureEngineAsync(ct);
            if (!ensured.Ok) return ensured;

            if (IsApplied(model))
            {
                // Idempotent apply (field defect 2026-11-22 evening, §24):
                // the live engine already holds exactly this configuration,
                // and a UAPI set is NOT a no-op on wireguard-go —
                // BuildSetRequest replaces the peer list wholesale
                // (replace_peers=true), which destroys every peer's
                // handshake state, session keys and byte counters. The
                // fleet resync re-pushes the mesh frame every 30 s, so an
                // unconditional set kept the tunnel permanently
                // re-handshaking: handshakes completed, then the next tick
                // wiped them before TCP could cross (field audit read
                // "0 handshake(s), tx 148 B / rx 0 B" on every apply —
                // 148 B is one handshake initiation, sent into a peer
                // table that had just been zeroed). Skip the set and the
                // addressing churn; refresh the status only, so status
                // surfaces (and the applier's dead-receiver check) keep
                // seeing live counters.
                var current = await ReadStatusAsync(ct);
                LastStatus = current;
                State = MeshEngineState.Running;
                EnsureMonitor();
                return new MeshEngineApplyResult(true,
                    $"tunnel: embedded engine up; configuration unchanged — apply skipped; {current?.Summarize() ?? "status pending"}", current);
            }

            var setResponse = await UapiAsync(UapiProtocol.BuildSetRequest(model), ct);
            if (setResponse == null || !UapiProtocol.IsSuccess(setResponse))
                return new MeshEngineApplyResult(false,
                    $"tunnel: engine rejected the configuration (errno={(setResponse == null ? "no response" : UapiProtocol.ParseErrno(setResponse).ToString())})", LastStatus);
            _appliedModel = model;

            var addressing = await RunCommandsAsync(_applyAddressing(InterfaceName, model), ct);
            var status = await ReadStatusAsync(ct);
            LastStatus = status;
            State = MeshEngineState.Running;
            EnsureMonitor();
            var summary = $"tunnel: embedded engine up; addressing {addressing}; {status?.Summarize() ?? "status pending"}";
            return new MeshEngineApplyResult(true, summary, status);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Full teardown: engine process, routes, address, adapter.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            State = MeshEngineState.Stopping;
            StopMonitor();
            var model = _desired;
            _desired = null;
            _appliedModel = null;

            if (_process != null)
            {
                await _process.StopAsync(TimeSpan.FromSeconds(3), ct);
                _process.Dispose();
                _process = null;
            }
            else if (_adopted)
            {
                KillAdoptedEngine();
            }
            _adopted = false;

            if (model != null)
                await RunCommandsAsync(_removeAddressing(InterfaceName, model), ct);
            // If the engine died ungracefully its adapter can linger; remove it
            // through wintun.dll's own API (no-op when the adapter is gone).
            WintunAdapter.TryDelete(InterfaceName, _installDir);
            State = MeshEngineState.Stopped;
            _audit("embedded engine stopped; tunnel, routes and adapter removed", "ok");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// One health pass (the monitor loop calls this on the health cadence;
    /// internal so tests can drive it directly): restarts a dead owned engine,
    /// respawns when an adopted engine disappears, refreshes LastStatus.
    /// </summary>
    internal async Task CheckHealthAsync(CancellationToken ct = default)
    {
        var desired = _desired;
        if (desired == null || State is MeshEngineState.Stopped or MeshEngineState.Stopping or MeshEngineState.GaveUp) return;
        if (!await _gate.WaitAsync(0, ct)) return; // an apply/stop is in flight; it owns the state
        try
        {
            if (_desired == null) return;
            var dead = _process != null ? _process.HasExited : _adopted && !await ProbeEngineAsync(ct);
            if (!dead)
            {
                LastStatus = await ReadStatusAsync(ct) ?? LastStatus;
                return;
            }

            var exitCode = _process?.ExitCode;
            if (!RestartPolicy.ShouldRestart("always", exitCode, _restarts, _maxRestarts))
            {
                State = MeshEngineState.GaveUp;
                _audit($"embedded engine exited (code {(exitCode?.ToString() ?? "—")}) and the restart budget ({_maxRestarts}) is exhausted", "error");
                StopMonitor();
                return;
            }
            _restarts++;
            State = MeshEngineState.Restarting;
            _audit($"embedded engine exited (code {(exitCode?.ToString() ?? "—")}); restart #{_restarts}", "error");
            _process?.Dispose();
            _process = null;
            _adopted = false;

            var ensured = await EnsureEngineAsync(ct);
            if (!ensured.Ok)
            {
                State = MeshEngineState.GaveUp;
                _audit($"embedded engine restart failed: {ensured.Summary}", "error");
                StopMonitor();
                return;
            }
            var setResponse = await UapiAsync(UapiProtocol.BuildSetRequest(desired), ct);
            if (setResponse == null || !UapiProtocol.IsSuccess(setResponse))
            {
                State = MeshEngineState.GaveUp;
                _audit("embedded engine restarted but rejected the configuration", "error");
                StopMonitor();
                return;
            }
            _appliedModel = desired; // the restarted engine now holds it — later applies may skip
            await RunCommandsAsync(_applyAddressing(InterfaceName, desired), ct);
            LastStatus = await ReadStatusAsync(ct);
            State = MeshEngineState.Running;
        }
        finally { _gate.Release(); }
    }

    private async Task<MeshEngineApplyResult> EnsureEngineAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false }) return new MeshEngineApplyResult(true, "already running", LastStatus);
        if (_process != null) { _process.Dispose(); _process = null; } // exited; the health pass will count the restart if it was unexpected
        if (_adopted && await ProbeEngineAsync(ct)) return new MeshEngineApplyResult(true, "adopted engine alive", LastStatus);

        // Adoption probe: a previous agent lifetime may have left an engine
        // running (it is not in our kill-on-close job). If it answers, adopt.
        _adopted = false;
        if (await ProbeEngineAsync(ct))
        {
            _adopted = true;
            _appliedModel = null; // a new instance for us: what it holds is unknown, so the next apply must set it
            _audit("adopted the mesh engine left running by a previous agent lifetime", "ok");
            return new MeshEngineApplyResult(true, "adopted running engine", LastStatus);
        }

        if (!File.Exists(_enginePath))
            return new MeshEngineApplyResult(false,
                $"tunnel: embedded mesh engine not found at {_enginePath} (repair/reinstall EsoAgent — no external WireGuard is used)", LastStatus);

        State = MeshEngineState.Starting;
        IMeshEngineProcess proc;
        try
        {
            proc = _processFactory();
            proc.Start();
        }
        catch (Exception ex)
        {
            State = MeshEngineState.Stopped;
            return new MeshEngineApplyResult(false, $"tunnel: embedded mesh engine failed to start: {ex.Message}", LastStatus);
        }
        _process = proc;
        _appliedModel = null; // a fresh engine holds nothing yet

        // Wait for the UAPI pipe to come up (adapter creation happens first).
        var deadline = DateTimeOffset.UtcNow + _startupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (proc.HasExited)
            {
                State = MeshEngineState.Stopped;
                return new MeshEngineApplyResult(false,
                    $"tunnel: embedded mesh engine exited during startup (code {(proc.ExitCode?.ToString() ?? "—")}); see logs/mesh-engine.log in the data dir", LastStatus);
            }
            if (await ProbeEngineAsync(ct)) return new MeshEngineApplyResult(true, "engine started", LastStatus);
            await Task.Delay(250, ct);
        }
        State = MeshEngineState.Stopped;
        return new MeshEngineApplyResult(false, "tunnel: embedded mesh engine did not open its control pipe in time", LastStatus);
    }

    private async Task<bool> ProbeEngineAsync(CancellationToken ct)
    {
        var response = await UapiAsync(UapiProtocol.BuildGetRequest(), ct);
        return response != null && UapiProtocol.ParseErrno(response) == 0;
    }

    private async Task<UapiDeviceStatus?> ReadStatusAsync(CancellationToken ct)
    {
        var response = await UapiAsync(UapiProtocol.BuildGetRequest(), ct);
        return response == null ? null : UapiProtocol.ParseGetResponse(response);
    }

    private async Task<string?> UapiAsync(string request, CancellationToken ct)
    {
        try
        {
            using var transport = _transportFactory();
            return await transport.RequestAsync(request, ct);
        }
        catch { return null; }
    }

    private async Task<string> RunCommandsAsync(List<string[]> commands, CancellationToken ct)
    {
        var ok = 0;
        foreach (var argv in commands)
        {
            try
            {
                var result = await _runner.RunAsync(
                    new ExecRequest { File = argv[0], Args = argv.Skip(1).ToArray(), TimeoutMs = 30_000 }, ct);
                if (result.ExitCode == 0) ok++;
            }
            catch { /* counted as not-ok; the summary reports the ratio */ }
        }
        return $"{ok}/{commands.Count} commands ok";
    }

    /// <summary>
    /// Stops an adopted engine (started by a previous agent lifetime, so we
    /// hold no process handle): kill only processes whose executable IS our
    /// bundled engine in our install dir — never anything else.
    /// </summary>
    private void KillAdoptedEngine()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_enginePath)))
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path != null && string.Equals(path, _enginePath, StringComparison.OrdinalIgnoreCase))
                        proc.Kill(entireProcessTree: true);
                }
                catch { /* per-process best effort */ }
                finally { proc.Dispose(); }
            }
        }
        catch { /* best-effort */ }
    }

    private void EnsureMonitor()
    {
        if (_monitorTask is { IsCompleted: false }) return;
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;
        _monitorTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_healthInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                    await CheckHealthAsync(token);
            }
            catch (OperationCanceledException) { /* stopping */ }
        });
    }

    private void StopMonitor()
    {
        try { _monitorCts?.Cancel(); } catch { /* best-effort */ }
        _monitorCts = null;
        _monitorTask = null;
    }

    public void Dispose()
    {
        StopMonitor();
        _process?.Dispose();
        _gate.Dispose();
    }
}
