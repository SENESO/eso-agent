using EsoAgent.Core;

namespace EsoAgent.Linux;

/// <summary>
/// The small named repair set for Linux. Anything beyond these goes through
/// the open exec endpoint by design — this list is a convenience, not a gate.
/// </summary>
public sealed class LinuxRepair : ISystemRepair
{
    private readonly IProcessRunner _runner;
    private readonly DistroFamily _family;
    private const int TimeoutMs = 600_000;

    public LinuxRepair(IProcessRunner runner)
    {
        _runner = runner;
        _family = LinuxDistro.Detect();
    }

    public IReadOnlyList<RepairInfoDto> Available => new List<RepairInfoDto>
    {
        new()
        {
            Name = "fix-broken",
            Description = _family switch
            {
                DistroFamily.Debian => "Repair broken package state: apt-get install --fix-broken.",
                DistroFamily.Rpm => "Check the package database for problems: dnf check.",
                DistroFamily.Pacman => "Verify installed package files: pacman -Qk.",
                _ => "Repair broken package state (no supported package manager detected on this machine).",
            },
        },
        new()
        {
            Name = "journal-vacuum",
            Description = "Trim the systemd journal to 200 MB (journalctl --vacuum-size=200M), reporting disk usage before and after.",
        },
        new()
        {
            Name = "disk-summary",
            Description = "Report filesystem usage (df -h) and the local package cache size.",
        },
    };

    public async Task<OperationResult> RunAsync(string name, CancellationToken ct = default)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "fix-broken": return await FixBrokenAsync(ct);
            case "journal-vacuum": return await JournalVacuumAsync(ct);
            case "disk-summary": return await DiskSummaryAsync(ct);
            default:
                return new OperationResult
                {
                    Success = false,
                    Message = $"Unknown repair '{name}'. Available: {string.Join(", ", Available.Select(a => a.Name))}.",
                };
        }
    }

    private async Task<OperationResult> FixBrokenAsync(CancellationToken ct)
    {
        ExecResponse result;
        switch (_family)
        {
            case DistroFamily.Debian:
                result = await RunnerHelpers.RunSafeAsync(_runner,
                    RunnerHelpers.Cmd("apt-get", TimeoutMs, RunnerHelpers.AptEnv(), "install", "-y", "--fix-broken"), ct);
                break;
            case DistroFamily.Rpm:
                result = await RunnerHelpers.RunSafeAsync(_runner,
                    RunnerHelpers.Cmd("dnf", TimeoutMs, "check"), ct);
                break;
            case DistroFamily.Pacman:
                result = await RunnerHelpers.RunSafeAsync(_runner,
                    RunnerHelpers.Cmd("pacman", TimeoutMs, "-Qk"), ct);
                break;
            default:
                return new OperationResult
                {
                    Success = false,
                    Message = "No supported package manager found on this machine (looked for dpkg, rpm, pacman).",
                };
        }
        return new OperationResult
        {
            Success = result.ExitCode == 0,
            ExitCode = result.ExitCode,
            Output = RunnerHelpers.Combined(result),
            Message = result.ExitCode == 0 ? "fix-broken completed." : "fix-broken failed.",
        };
    }

    private async Task<OperationResult> JournalVacuumAsync(CancellationToken ct)
    {
        var before = await RunnerHelpers.RunSafeAsync(_runner,
            RunnerHelpers.Cmd("journalctl", 30_000, "--disk-usage"), ct);
        var vacuum = await RunnerHelpers.RunSafeAsync(_runner,
            RunnerHelpers.Cmd("journalctl", TimeoutMs, "--vacuum-size=200M"), ct);
        var after = await RunnerHelpers.RunSafeAsync(_runner,
            RunnerHelpers.Cmd("journalctl", 30_000, "--disk-usage"), ct);

        var output = string.Join("\n", new[]
        {
            "Before: " + RunnerHelpers.Combined(before),
            RunnerHelpers.Combined(vacuum),
            "After: " + RunnerHelpers.Combined(after),
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new OperationResult
        {
            Success = vacuum.ExitCode == 0,
            ExitCode = vacuum.ExitCode,
            Output = output,
            Message = vacuum.ExitCode == 0 ? "Journal vacuumed to 200 MB." : "journalctl --vacuum-size failed.",
        };
    }

    private async Task<OperationResult> DiskSummaryAsync(CancellationToken ct)
    {
        var df = await RunnerHelpers.RunSafeAsync(_runner,
            RunnerHelpers.Cmd("df", 30_000, "-h"), ct);
        var parts = new List<string> { RunnerHelpers.Combined(df) };

        // The apt archive cache is the usual silent disk eater on Debian-family machines.
        if (Directory.Exists("/var/cache/apt"))
        {
            var du = await RunnerHelpers.RunSafeAsync(_runner,
                RunnerHelpers.Cmd("du", 60_000, "-sh", "/var/cache/apt"), ct);
            var duText = RunnerHelpers.Combined(du);
            if (duText.Length > 0) parts.Add("/var/cache/apt: " + duText);
        }

        return new OperationResult
        {
            Success = true,
            ExitCode = df.ExitCode,
            Output = string.Join("\n", parts),
            Message = "Disk summary collected.",
        };
    }
}
