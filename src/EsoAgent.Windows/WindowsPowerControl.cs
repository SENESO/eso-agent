using EsoAgent.Core;

namespace EsoAgent.Windows;

/// <summary>
/// Windows IPowerControl: shells to the platform's own tools (shutdown.exe /
/// rundll32 powrprof) through the shared process runner. A short delay (/t 3)
/// lets the API response flush before the machine goes down.
/// </summary>
public sealed class WindowsPowerControl : IPowerControl
{
    private readonly IProcessRunner _runner;

    public WindowsPowerControl(IProcessRunner runner) => _runner = runner;

    public Task<OperationResult> RebootAsync(CancellationToken ct = default)
        => RunAsync(new ExecRequest { File = "shutdown.exe", Args = new[] { "/r", "/t", "3" }, TimeoutMs = 15_000 }, ct);

    public Task<OperationResult> ShutdownAsync(CancellationToken ct = default)
        => RunAsync(new ExecRequest { File = "shutdown.exe", Args = new[] { "/s", "/t", "3" }, TimeoutMs = 15_000 }, ct);

    public Task<OperationResult> SleepAsync(CancellationToken ct = default)
        => RunAsync(new ExecRequest { File = "rundll32.exe", Args = new[] { "powrprof.dll,SetSuspendState", "0,1,0" }, TimeoutMs = 15_000 }, ct);

    private async Task<OperationResult> RunAsync(ExecRequest request, CancellationToken ct)
    {
        try
        {
            var r = await _runner.RunAsync(request, ct);
            var output = string.Join("\n", new[] { r.Stdout.Trim(), r.Stderr.Trim() }.Where(s => s.Length > 0));
            return new OperationResult
            {
                Success = r.ExitCode == 0,
                ExitCode = r.ExitCode,
                Output = output,
                Message = r.ExitCode == 0 ? "Command accepted." : $"Command failed with exit code {r.ExitCode?.ToString() ?? "unknown (timed out)"}.",
            };
        }
        catch (Exception ex)
        {
            return new OperationResult { Success = false, Output = "", Message = ex.Message };
        }
    }
}
