using EsoAgent.Core;

namespace EsoAgent.Linux;

/// <summary>Distro families EsoAgent knows how to drive, detected by the package tool on disk.</summary>
internal enum DistroFamily
{
    Unknown,
    Debian,   // dpkg / apt
    Rpm,      // rpm / dnf
    Pacman,   // pacman
}

internal static class LinuxDistro
{
    /// <summary>Detection order is fixed (dpkg, then rpm, then pacman) so a machine with
    /// several tools installed still gets one deterministic answer.</summary>
    public static DistroFamily Detect()
    {
        if (File.Exists("/usr/bin/dpkg-query")) return DistroFamily.Debian;
        if (File.Exists("/usr/bin/rpm")) return DistroFamily.Rpm;
        if (File.Exists("/usr/bin/pacman")) return DistroFamily.Pacman;
        return DistroFamily.Unknown;
    }
}

/// <summary>Small shared helpers for the Linux services: exception-safe process runs
/// (systemctl &amp; friends must fail cleanly, never crash the daemon, when systemd
/// is not PID 1 or a tool is missing) and stdout+stderr combination.</summary>
internal static class RunnerHelpers
{
    public static async Task<ExecResponse> RunSafeAsync(IProcessRunner runner, ExecRequest request, CancellationToken ct = default)
    {
        try { return await runner.RunAsync(request, ct); }
        catch (Exception ex)
        {
            // e.g. Win32Exception when the binary does not exist, or systemctl
            // refusing to talk to a non-systemd init — surface it as a failed run.
            return new ExecResponse { ExitCode = null, Stderr = ex.Message };
        }
    }

    public static ExecRequest Cmd(string file, int timeoutMs, params string[] args)
        => new() { File = file, Args = args, TimeoutMs = timeoutMs };

    public static ExecRequest Cmd(string file, int timeoutMs, Dictionary<string, string>? env, params string[] args)
        => new() { File = file, Args = args, TimeoutMs = timeoutMs, Env = env };

    /// <summary>stdout followed by stderr, trimmed — the shape OperationResult.Output expects.</summary>
    public static string Combined(ExecResponse r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Stdout)) parts.Add(r.Stdout.Trim());
        if (!string.IsNullOrWhiteSpace(r.Stderr)) parts.Add(r.Stderr.Trim());
        return string.Join("\n", parts);
    }

    public static Dictionary<string, string> AptEnv()
        => new() { ["DEBIAN_FRONTEND"] = "noninteractive" };
}
