using EsoAgent.Core;

namespace EsoAgent.Linux;

/// <summary>
/// Runs a process as the logged-on user (via runuser) so it lands in their
/// graphical session instead of the daemon's root context: DISPLAY and
/// XDG_RUNTIME_DIR are pointed at that user's session.
/// </summary>
public sealed class LinuxInteractiveExec : IInteractiveExec
{
    private readonly IProcessRunner _runner;

    public LinuxInteractiveExec(IProcessRunner runner) => _runner = runner;

    public async Task<ExecResponse> RunInUserSessionAsync(ExecRequest request, CancellationToken ct = default)
    {
        var user = await FirstInteractiveUserAsync(ct);
        if (user == null)
        {
            return new ExecResponse
            {
                ExitCode = null,
                Stderr = "No interactive user session available on this machine.",
            };
        }

        var uidResult = await RunnerHelpers.RunSafeAsync(_runner,
            RunnerHelpers.Cmd("id", 5_000, "-u", user), ct);
        var uid = uidResult.Stdout.Trim();
        if (uidResult.ExitCode != 0 || uid.Length == 0)
        {
            return new ExecResponse
            {
                ExitCode = null,
                Stderr = $"Could not resolve uid for user '{user}': {RunnerHelpers.Combined(uidResult)}",
            };
        }

        var env = request.Env != null
            ? new Dictionary<string, string>(request.Env)
            : new Dictionary<string, string>();
        env["DISPLAY"] = ":0";
        env["XDG_RUNTIME_DIR"] = $"/run/user/{uid}";

        var args = new List<string> { "-u", user, "--", request.File };
        args.AddRange(request.Args);

        // Note: stdin is forwarded to runuser, which does not reliably pass it
        // through to the target process — acceptable for the GUI-launch use
        // case this endpoint exists for; Cwd/timeout are preserved as-is.
        return await RunnerHelpers.RunSafeAsync(_runner, new ExecRequest
        {
            File = "runuser",
            Args = args.ToArray(),
            Cwd = request.Cwd,
            Env = env,
            Stdin = request.Stdin,
            TimeoutMs = request.TimeoutMs,
        }, ct);
    }

    /// <summary>Username of the first entry in `who` — the primary interactive session.</summary>
    private async Task<string?> FirstInteractiveUserAsync(CancellationToken ct)
    {
        var result = await RunnerHelpers.RunSafeAsync(_runner, RunnerHelpers.Cmd("who", 5_000), ct);
        if (result.ExitCode != 0) return null;
        var users = LinuxSystemInfo.ParseWhoUsers(result.Stdout);
        return users.Count > 0 ? users[0] : null;
    }
}
