using EsoAgent.Core;
using Microsoft.Win32;

namespace EsoAgent.Windows;

/// <summary>
/// Windows ISystemRepair: the small named set of well-known maintenance
/// operations. sfc/DISM get long timeouts (they legitimately run for many
/// minutes); anything beyond this set goes through the open exec endpoint.
/// </summary>
public sealed class WindowsRepair : ISystemRepair
{
    private const int LongTimeoutMs = 1_800_000; // 30 minutes

    private readonly IProcessRunner _runner;

    public WindowsRepair(IProcessRunner runner) => _runner = runner;

    public IReadOnlyList<RepairInfoDto> Available { get; } = new[]
    {
        new RepairInfoDto { Name = "sfc", Description = "System File Checker (sfc /scannow)" },
        new RepairInfoDto { Name = "dism", Description = "DISM RestoreHealth" },
        new RepairInfoDto { Name = "windows-update", Description = "Trigger Windows Update scan + report last result times" },
        new RepairInfoDto { Name = "disk-cleanup", Description = "Temp/recycle cleanup size summary" },
    };

    public async Task<OperationResult> RunAsync(string name, CancellationToken ct = default)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "sfc":
            {
                var r = await _runner.RunAsync(new ExecRequest
                {
                    File = "sfc.exe",
                    Args = new[] { "/scannow" },
                    TimeoutMs = LongTimeoutMs,
                }, ct);
                return FromExec(r, "sfc /scannow completed with no integrity violations reported by exit code.");
            }
            case "dism":
            {
                var r = await _runner.RunAsync(new ExecRequest
                {
                    File = "DISM.exe",
                    Args = new[] { "/Online", "/Cleanup-Image", "/RestoreHealth" },
                    TimeoutMs = LongTimeoutMs,
                }, ct);
                return FromExec(r, "DISM RestoreHealth completed.");
            }
            case "windows-update":
                return await WindowsUpdateAsync(ct);
            case "disk-cleanup":
                return await DiskCleanupSummaryAsync(ct);
            default:
                return new OperationResult
                {
                    Success = false,
                    Output = "",
                    Message = $"Unknown repair '{name}'. Available: {string.Join(", ", Available.Select(a => a.Name))}.",
                };
        }
    }

    private async Task<OperationResult> WindowsUpdateAsync(CancellationToken ct)
    {
        // Trigger a scan; UsoClient returns immediately and works in the background.
        var scan = await _runner.RunAsync(new ExecRequest
        {
            File = "UsoClient.exe",
            Args = new[] { "StartScan" },
            TimeoutMs = 60_000,
        }, ct);

        // Report the last recorded scan/install result times from the registry.
        string? installTime = null, detectTime = null;
        try
        {
            using var results = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results", writable: false);
            using (var install = results?.OpenSubKey("Install", writable: false))
                installTime = install?.GetValue("LastSuccessTime") as string;
            using (var detect = results?.OpenSubKey("Detect", writable: false))
                detectTime = detect?.GetValue("LastSuccessTime") as string;
        }
        catch { /* times stay null and are reported as not recorded */ }

        var output =
            $"Windows Update scan triggered (UsoClient StartScan, exit {scan.ExitCode?.ToString() ?? "unknown"}).\n" +
            $"Last successful install: {installTime ?? "not recorded"}\n" +
            $"Last successful detection: {detectTime ?? "not recorded"}";
        return new OperationResult
        {
            Success = true,
            ExitCode = scan.ExitCode,
            Output = output,
            Verified = false,
            Message = "Scan triggered; result times above are the last recorded values (the new scan runs in the background).",
        };
    }

    private async Task<OperationResult> DiskCleanupSummaryAsync(CancellationToken ct)
    {
        // Report-only summary: sizes of the temp folders and the Recycle Bin.
        // Nothing is deleted here — deletion is an explicit exec decision.
        const string script = """
            function Get-FolderSize([string]$Path) {
                if (-not (Test-Path -LiteralPath $Path)) { return 0 }
                $sum = (Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
                        Measure-Object -Property Length -Sum).Sum
                if ($null -eq $sum) { return 0 }
                return [long]$sum
            }
            $userTemp = Get-FolderSize $env:TEMP
            $winTemp  = Get-FolderSize 'C:\Windows\Temp'
            $recycle  = Get-FolderSize 'C:\$Recycle.Bin'
            "User temp ($env:TEMP): $userTemp bytes ({0:N1} MB)" -f ($userTemp / 1MB)
            "Windows temp (C:\Windows\Temp): $winTemp bytes ({0:N1} MB)" -f ($winTemp / 1MB)
            "Recycle Bin (C:\`$Recycle.Bin): $recycle bytes ({0:N1} MB)" -f ($recycle / 1MB)
            "Total reclaimable (approx): $($userTemp + $winTemp + $recycle) bytes ({0:N1} MB)" -f (($userTemp + $winTemp + $recycle) / 1MB)
            """;

        var request = ProcessRunner.ResolveShell(new ShellRequest
        {
            Shell = "powershell",
            Script = script,
            TimeoutMs = 120_000,
        });
        var r = await _runner.RunAsync(request, ct);
        return new OperationResult
        {
            Success = r.ExitCode == 0,
            ExitCode = r.ExitCode,
            Output = string.Join("\n", new[] { r.Stdout.Trim(), r.Stderr.Trim() }.Where(s => s.Length > 0)),
            Verified = false,
            Message = "Size summary only — nothing was deleted.",
        };
    }

    private static OperationResult FromExec(ExecResponse r, string okMessage)
        => new()
        {
            Success = r.ExitCode == 0,
            ExitCode = r.ExitCode,
            Output = string.Join("\n", new[] { r.Stdout.Trim(), r.Stderr.Trim() }.Where(s => s.Length > 0)),
            Verified = false,
            Message = r.ExitCode == 0 ? okMessage : $"Command failed with exit code {r.ExitCode?.ToString() ?? "unknown (timed out)"}; see Output.",
        };
}
