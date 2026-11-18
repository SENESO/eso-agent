namespace EsoAgent.Core;

// Platform abstractions. Core holds the protocol + shared logic; each OS head
// (Windows service / Linux daemon / a future macOS launchd head) supplies these.
// The API surface is identical regardless of which head serves it.

/// <summary>Runs processes. The default Core implementation (ProcessRunner) is fully
/// cross-platform and unrestricted — it runs anything the service account can run.</summary>
public interface IProcessRunner
{
    Task<ExecResponse> RunAsync(ExecRequest request, CancellationToken ct = default);

    /// <summary>Streaming variant: onData(streamName, text) is called as output arrives ("stdout"/"stderr").</summary>
    Task<ExecResponse> RunStreamingAsync(ExecRequest request, Func<string, string, Task> onData, CancellationToken ct = default);

    /// <summary>Starts a detached process owned by the agent (survives client disconnects
    /// and agent restarts of the connection; tracked by the agent). Returns its PID.</summary>
    int LaunchDetached(ExecRequest request);
}

/// <summary>Platform facts Core cannot get from managed APIs alone (RAM, CPU %, battery, sessions).</summary>
public interface ISystemInfo
{
    (long? Used, long? Total) GetMemory();
    Task<double?> GetCpuPercentAsync(CancellationToken ct = default);
    BatteryDto? GetBattery();
    IReadOnlyList<string> GetLoggedOnUsers();
}

/// <summary>Reboot / shutdown / sleep. Implementations shell to the platform's own tools.</summary>
public interface IPowerControl
{
    Task<OperationResult> RebootAsync(CancellationToken ct = default);
    Task<OperationResult> ShutdownAsync(CancellationToken ct = default);
    Task<OperationResult> SleepAsync(CancellationToken ct = default);
}

/// <summary>
/// Screenshot of the owner's display. A service in session 0 (Windows) or a root
/// daemon (Linux) usually cannot capture the desktop directly — implementations
/// forward to a user-session companion where one exists, and return a clean
/// "not available" error otherwise. Never fake a capture.
/// </summary>
public interface IScreenshotProvider
{
    Task<(byte[]? Png, string? Error)> CaptureAsync(CancellationToken ct = default);
}

/// <summary>Run a process inside the logged-on user's interactive session.</summary>
public interface IInteractiveExec
{
    Task<ExecResponse> RunInUserSessionAsync(ExecRequest request, CancellationToken ct = default);
}

/// <summary>Installed-software management: list / install / uninstall / upgrade.</summary>
public interface IPackageManager
{
    string ManagerName { get; }
    Task<IReadOnlyList<SoftwarePackageDto>> ListAsync(CancellationToken ct = default);
    Task<OperationResult> InstallAsync(InstallRequest request, CancellationToken ct = default);
    Task<OperationResult> UninstallAsync(UninstallRequest request, CancellationToken ct = default);
    Task<OperationResult> UpgradeAsync(UpgradeRequest request, CancellationToken ct = default);
}

/// <summary>OS services: Windows services / systemd units.</summary>
public interface IServiceControl
{
    Task<IReadOnlyList<ServiceDto>> ListAsync(CancellationToken ct = default);
    Task<OperationResult> StartAsync(string name, CancellationToken ct = default);
    Task<OperationResult> StopAsync(string name, CancellationToken ct = default);
    Task<OperationResult> RestartAsync(string name, CancellationToken ct = default);
    Task<OperationResult> SetStartupModeAsync(string name, string mode, CancellationToken ct = default);
}

/// <summary>Named, well-known maintenance operations (sfc/DISM/apt-fix/...). Anything
/// beyond this small named set simply goes through the open exec endpoint.</summary>
public interface ISystemRepair
{
    IReadOnlyList<RepairInfoDto> Available { get; }
    Task<OperationResult> RunAsync(string name, CancellationToken ct = default);
}

/// <summary>Windows registry access. Null on other heads (endpoint answers 501).</summary>
public interface IRegistryAccess
{
    RegistryGetResponse Get(string path, string name);
    (RegistryGetResponse Before, RegistryGetResponse After) Set(RegistrySetRequest request);
}

/// <summary>
/// Remote-desktop host: lives in the user-session companion (Windows tray) or the
/// session-aware helper (Linux/X11), because the service session cannot capture
/// or inject input. Null on a head with no desktop support — the endpoint then
/// answers a clean "not available", never a fake session.
/// </summary>
public interface IDesktopHost
{
    IReadOnlyCollection<string> SupportedCodecs { get; }
    Task<(IDesktopSession? Session, string? Error)> OpenAsync(DesktopHello hello, string codec, CancellationToken ct = default);

    /// <summary>Attended-session consent (v1.2): asks the LOCAL user to accept
    /// an incoming session of the given mode, waiting up to the timeout.
    /// Default: cannot prompt → false (the runner only calls this when the
    /// owner's policy requires consent, and denial is the fail-closed answer).</summary>
    Task<bool> RequestConsentAsync(string mode, TimeSpan timeout, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>One live desktop session. NextFrameAsync returns null to skip an unchanged frame.</summary>
public interface IDesktopSession : IAsyncDisposable
{
    MonitorInfo Monitor { get; }
    IReadOnlyList<MonitorInfo> AllMonitors { get; }
    string Codec { get; }
    Task<DesktopFrame?> NextFrameAsync(CancellationToken ct = default);

    /// <summary>Adaptive streaming (v1.2 item 37): the runner retunes quality /
    /// fps to measured bandwidth, never below the legibility floor
    /// (AdaptiveQuality). Default: the head cannot retune mid-session.</summary>
    void SetStreamParams(int quality, int maxFps) { }
    Task SendInputAsync(DesktopInput input, CancellationToken ct = default);
    Task<string?> GetClipboardAsync(CancellationToken ct = default);
    Task SetClipboardAsync(string text, CancellationToken ct = default);

    /// <summary>Privacy mode (v1.2): blank the device's physical screen (and lock
    /// local input where the platform supports it) while the remote session
    /// keeps streaming. Returns TRUE only when the requested state is actually
    /// in effect on the device — a head that cannot blank (or cannot prove it
    /// did) returns false, and the runner then tells the operator plainly
    /// instead of echoing a state that is not real. Default: unsupported —
    /// enabling reports false, disabling trivially true. A session ending
    /// must restore the screen (DisposeAsync).</summary>
    Task<bool> SetPrivacyAsync(bool enabled, CancellationToken ct = default) => Task.FromResult(!enabled);

    /// <summary>In-session chat (v1.2): deliver an operator chat message to the
    /// person at the device (notification / tray window, platform-dependent).
    /// Default: nowhere to show it (no-op).</summary>
    Task SendChatAsync(string text, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>In-session chat (v1.2): the latest chat message FROM the person
    /// at the device since the last call, or null. Polled by the runner like
    /// the clipboard. Default: none.</summary>
    Task<string?> GetChatAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>Everything a head must provide to Core's endpoint mapping.</summary>
public sealed class PlatformServices
{
    public required IProcessRunner ProcessRunner { get; init; }
    public required ISystemInfo SystemInfo { get; init; }
    public required IPowerControl PowerControl { get; init; }
    public required IScreenshotProvider Screenshot { get; init; }
    public required IInteractiveExec InteractiveExec { get; init; }
    public required IPackageManager Packages { get; init; }
    public required IServiceControl Services { get; init; }
    public required ISystemRepair Repair { get; init; }
    public IRegistryAccess? Registry { get; init; }
    /// <summary>Optional remote-desktop host (user-session companion). Null = desktop not available on this head.</summary>
    public IDesktopHost? DesktopHost { get; init; }
}
