using EsoAgent.Core;

namespace EsoAgent.Linux;

/// <summary>Power actions via systemctl. Reboot/shutdown are launched detached
/// behind a short sleep so the API response reaches the client before the
/// machine actually goes down.</summary>
public sealed class LinuxPowerControl : IPowerControl
{
    private readonly IProcessRunner _runner;

    public LinuxPowerControl(IProcessRunner runner) => _runner = runner;

    public Task<OperationResult> RebootAsync(CancellationToken ct = default)
        => Task.FromResult(LaunchDelayed("sleep 2; systemctl reboot || reboot", "Reboot scheduled (in ~2 seconds)."));

    public Task<OperationResult> ShutdownAsync(CancellationToken ct = default)
        => Task.FromResult(LaunchDelayed("sleep 2; systemctl poweroff || poweroff", "Shutdown scheduled (in ~2 seconds)."));

    public async Task<OperationResult> SleepAsync(CancellationToken ct = default)
    {
        var result = await RunnerHelpers.RunSafeAsync(_runner,
            RunnerHelpers.Cmd("systemctl", 30_000, "suspend"), ct);
        return new OperationResult
        {
            Success = result.ExitCode == 0,
            ExitCode = result.ExitCode,
            Output = RunnerHelpers.Combined(result),
            Message = result.ExitCode == 0 ? "Suspend requested." : "systemctl suspend failed.",
        };
    }

    private OperationResult LaunchDelayed(string shellCommand, string message)
    {
        try
        {
            _runner.LaunchDetached(new ExecRequest
            {
                File = "/bin/sh",
                Args = new[] { "-c", shellCommand },
            });
            return new OperationResult { Success = true, Message = message };
        }
        catch (Exception ex)
        {
            return new OperationResult { Success = false, Message = ex.Message };
        }
    }
}
