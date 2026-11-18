using EsoAgent.Core;

namespace EsoAgent.Linux;

/// <summary>
/// systemd service control through systemctl. Every call is wrapped so a
/// machine where systemd is not running (containers, WSL, this sandbox)
/// answers with a clean failure instead of an exception.
/// </summary>
public sealed class LinuxServiceControl : IServiceControl
{
    private readonly IProcessRunner _runner;
    private const int TimeoutMs = 30_000;

    public LinuxServiceControl(IProcessRunner runner) => _runner = runner;

    public async Task<IReadOnlyList<ServiceDto>> ListAsync(CancellationToken ct = default)
    {
        var units = await SystemctlAsync(ct, "list-units", "--type=service", "--all", "--no-legend", "--plain");
        if (units.ExitCode != 0) return Array.Empty<ServiceDto>();

        // Startup state lives in a separate listing: unit file -> enabled/disabled/static/masked.
        var startupByUnit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = await SystemctlAsync(ct, "list-unit-files", "--type=service", "--no-legend", "--plain");
        if (files.ExitCode == 0)
        {
            foreach (var raw in Lines(files.Stdout))
            {
                var tokens = Split(raw);
                if (tokens.Length >= 2) startupByUnit[StripServiceSuffix(tokens[0])] = tokens[1];
            }
        }

        var result = new List<ServiceDto>();
        foreach (var raw in Lines(units.Stdout))
        {
            var tokens = Split(raw);
            if (tokens.Length < 4) continue;
            // UNIT LOAD ACTIVE SUB DESCRIPTION...
            var name = StripServiceSuffix(tokens[0]);
            var sub = tokens[3];
            result.Add(new ServiceDto
            {
                Name = name,
                DisplayName = tokens.Length > 4 ? string.Join(" ", tokens.Skip(4)) : null,
                Status = sub switch
                {
                    "running" => "Running",
                    "dead" => "Stopped",
                    "failed" => "Failed",
                    _ => sub,
                },
                StartupMode = startupByUnit.TryGetValue(name, out var mode) ? mode : null,
            });
        }
        return result;
    }

    public Task<OperationResult> StartAsync(string name, CancellationToken ct = default)
        => ControlAsync("start", name, expectRunning: true, ct);

    public Task<OperationResult> StopAsync(string name, CancellationToken ct = default)
        => ControlAsync("stop", name, expectRunning: false, ct);

    public Task<OperationResult> RestartAsync(string name, CancellationToken ct = default)
        => ControlAsync("restart", name, expectRunning: true, ct);

    public async Task<OperationResult> SetStartupModeAsync(string name, string mode, CancellationToken ct = default)
    {
        var normalized = mode.Trim();
        bool enable;
        if (normalized.Equals("Automatic", StringComparison.OrdinalIgnoreCase)) enable = true;
        else if (normalized.Equals("Manual", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) enable = false;
        else
        {
            return new OperationResult
            {
                Success = false,
                Message = $"Unknown startup mode '{mode}'. Use Automatic, Manual or Disabled.",
            };
        }

        var result = await SystemctlAsync(ct, enable ? "enable" : "disable", name);
        var enabledResult = await SystemctlAsync(ct, "is-enabled", name);
        var state = enabledResult.Stdout.Trim();
        // systemd has no Manual/Disabled distinction beyond enable/disable
        // (masking is a stronger, separate act and is deliberately NOT used):
        // both map to "not enabled at boot", verified as any non-enabled state.
        bool verified = enable ? state == "enabled" : state != "enabled";
        return new OperationResult
        {
            Success = result.ExitCode == 0 && verified,
            ExitCode = result.ExitCode,
            Output = RunnerHelpers.Combined(result),
            Verified = verified,
            Message = (verified
                ? $"Startup mode for '{name}' set to {normalized} (systemd: {(enable ? "enabled" : "disabled")})."
                : $"Startup mode for '{name}' could not be verified (is-enabled reports '{state}').")
                + " Note: systemd distinguishes only enabled vs disabled at boot; Manual and Disabled both map to disable (no mask is applied).",
        };
    }

    private async Task<OperationResult> ControlAsync(string verb, string name, bool expectRunning, CancellationToken ct)
    {
        var result = await SystemctlAsync(ct, verb, name);
        var activeResult = await SystemctlAsync(ct, "is-active", name);
        var state = activeResult.Stdout.Trim();
        bool running = state == "active";
        bool verified = running == expectRunning;
        return new OperationResult
        {
            Success = result.ExitCode == 0 && verified,
            ExitCode = result.ExitCode,
            Output = RunnerHelpers.Combined(result),
            Verified = verified,
            Message = verified
                ? $"Service '{name}' {verb} completed (is-active: {state})."
                : $"Service '{name}' {verb} did not reach the expected state (is-active: {state}).",
        };
    }

    private Task<ExecResponse> SystemctlAsync(CancellationToken ct, params string[] args)
        => RunnerHelpers.RunSafeAsync(_runner, RunnerHelpers.Cmd("systemctl", TimeoutMs, args), ct);

    private static string StripServiceSuffix(string unit)
        => unit.EndsWith(".service", StringComparison.Ordinal) ? unit[..^".service".Length] : unit;

    private static IEnumerable<string> Lines(string text)
        => text.Replace("\r\n", "\n").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l));

    private static string[] Split(string line)
        => line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
}
