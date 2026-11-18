using System.Text.Json;

namespace EsoAgent.Core;

/// <summary>
/// OS patch management (v1.2). Check lists pending OS updates; apply installs
/// them through the platform's own machinery and RE-CHECKS afterwards, so the
/// reported "remaining" is measured, not assumed. Reboot-required is read from
/// the platform's own signals. Linux: apt (full path). Windows: the Windows
/// Update COM API driven through PowerShell (the same channel the software
/// layer uses) — that path is PENDING FIRST REAL WINDOWS verification.
/// </summary>
public static class PatchService
{
    public static async Task<PatchStatusResponse> CheckAsync(AgentState state, CancellationToken ct = default)
    {
        var runner = state.Platform.ProcessRunner;
        if (OperatingSystem.IsWindows()) return await CheckWindowsAsync(runner, ct);
        if (OperatingSystem.IsLinux()) return await CheckAptAsync(runner, refresh: true, ct);
        return new PatchStatusResponse { Source = "none", Error = "OS patch management is not available on this platform." };
    }

    public static async Task<PatchApplyResponse> ApplyAsync(AgentState state, PatchApplyRequest req, CancellationToken ct = default)
    {
        var runner = state.Platform.ProcessRunner;
        if (OperatingSystem.IsWindows()) return await ApplyWindowsAsync(state, runner, req, ct);
        if (OperatingSystem.IsLinux()) return await ApplyAptAsync(state, runner, req, ct);
        return new PatchApplyResponse { Success = false, Output = "OS patch management is not available on this platform." };
    }

    // ----- Linux / apt -----

    private static async Task<PatchStatusResponse> CheckAptAsync(IProcessRunner runner, bool refresh, CancellationToken ct)
    {
        if (refresh)
        {
            var update = await runner.RunAsync(new ExecRequest
            {
                File = "apt-get", Args = new[] { "update", "-qq" }, TimeoutMs = 300_000,
            }, ct);
            if (update.ExitCode != 0)
                return new PatchStatusResponse { Source = "apt", Error = $"apt-get update failed (exit {update.ExitCode}): {Tail(update.Stderr + update.Stdout)}" };
        }
        var list = await runner.RunAsync(new ExecRequest
        {
            File = "apt", Args = new[] { "list", "--upgradable" }, TimeoutMs = 120_000,
        }, ct);
        return new PatchStatusResponse
        {
            Source = "apt",
            Pending = AptPatchParser.ParseUpgradable(list.Stdout),
            RebootRequired = File.Exists("/var/run/reboot-required"),
        };
    }

    private static async Task<PatchApplyResponse> ApplyAptAsync(AgentState state, IProcessRunner runner, PatchApplyRequest req, CancellationToken ct)
    {
        var env = new Dictionary<string, string> { ["DEBIAN_FRONTEND"] = "noninteractive" };
        ExecRequest applyReq;
        if (req.Ids.Count > 0)
        {
            var args = new List<string> { "install", "-y", "--only-upgrade" };
            args.AddRange(req.Ids);
            applyReq = new ExecRequest { File = "apt-get", Args = args.ToArray(), Env = env, TimeoutMs = 1_800_000 };
        }
        else
        {
            applyReq = new ExecRequest { File = "apt-get", Args = new[] { "upgrade", "-y" }, Env = env, TimeoutMs = 1_800_000 };
        }
        var result = await runner.RunAsync(applyReq, ct);
        var after = await CheckAptAsync(runner, refresh: false, ct);
        state.Audit.Write("patch", $"apt apply ({(req.Ids.Count == 0 ? "all" : string.Join(",", req.Ids))}) exit {result.ExitCode}; remaining {after.Pending.Count}",
            result.ExitCode == 0 ? "ok" : "error");
        return new PatchApplyResponse
        {
            Success = result.ExitCode == 0,
            Output = Tail(result.Stdout + result.Stderr, 4000),
            Remaining = after.Pending.Count,
            RebootRequired = after.RebootRequired,
        };
    }

    // ----- Windows / Windows Update COM via PowerShell -----

    private const string WuSearchScript = """
        $ErrorActionPreference = 'Stop'
        try {
          $session = New-Object -ComObject Microsoft.Update.Session
          $searcher = $session.CreateUpdateSearcher()
          $result = $searcher.Search("IsInstalled=0 and IsHidden=0")
          $items = @()
          foreach ($u in $result.Updates) {
            $items += [pscustomobject]@{ id = $u.Identity.UpdateID; name = $u.Title; kind = $(if ($u.MsrcSeverity -in @('Critical','Important')) { 'security' } else { 'update' }) }
          }
          $reboot = $false
          if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { $reboot = $true }
          [pscustomobject]@{ pending = $items; rebootRequired = $reboot } | ConvertTo-Json -Depth 4 -Compress
        } catch {
          [pscustomobject]@{ error = $_.Exception.Message } | ConvertTo-Json -Compress
        }
        """;

    private const string WuInstallScript = """
        $ErrorActionPreference = 'Stop'
        try {
          $session = New-Object -ComObject Microsoft.Update.Session
          $searcher = $session.CreateUpdateSearcher()
          $result = $searcher.Search("IsInstalled=0 and IsHidden=0")
          if ($result.Updates.Count -eq 0) { [pscustomobject]@{ installed = 0 } | ConvertTo-Json -Compress; exit }
          $toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
          foreach ($u in $result.Updates) { if ($u.EulaAccepted -eq $false) { $u.AcceptEula() }; [void]$toInstall.Add($u) }
          $downloader = $session.CreateUpdateDownloader()
          $downloader.Updates = $toInstall
          [void]$downloader.Download()
          $installer = New-Object -ComObject Microsoft.Update.Installer
          $installer.Updates = $toInstall
          $res = $installer.Install()
          $reboot = $false
          if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { $reboot = $true }
          [pscustomobject]@{ installed = $toInstall.Count; resultCode = $res.ResultCode; rebootRequired = $reboot } | ConvertTo-Json -Compress
        } catch {
          [pscustomobject]@{ error = $_.Exception.Message } | ConvertTo-Json -Compress
        }
        """;

    private static async Task<PatchStatusResponse> CheckWindowsAsync(IProcessRunner runner, CancellationToken ct)
    {
        var run = await runner.RunAsync(ProcessRunner.ResolveShell(new ShellRequest
        {
            Shell = "powershell", Script = WuSearchScript, TimeoutMs = 600_000,
        }), ct);
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(run.Stdout));
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                return new PatchStatusResponse { Source = "windows-update", Error = err.GetString() };
            var pending = new List<PatchInfoDto>();
            foreach (var item in root.GetProperty("pending").EnumerateArray())
            {
                pending.Add(new PatchInfoDto
                {
                    Id = item.GetProperty("id").GetString() ?? "",
                    Name = item.GetProperty("name").GetString() ?? "",
                    Kind = item.TryGetProperty("kind", out var k) ? k.GetString() ?? "update" : "update",
                });
            }
            return new PatchStatusResponse
            {
                Source = "windows-update",
                Pending = pending,
                RebootRequired = root.TryGetProperty("rebootRequired", out var rb) && rb.GetBoolean(),
            };
        }
        catch (Exception ex)
        {
            return new PatchStatusResponse { Source = "windows-update", Error = $"Windows Update search failed to parse: {ex.Message}" };
        }
    }

    private static async Task<PatchApplyResponse> ApplyWindowsAsync(AgentState state, IProcessRunner runner, PatchApplyRequest req, CancellationToken ct)
    {
        // The COM installer path applies everything pending; named-id filtering
        // is not offered on Windows (the search script would need per-id
        // installs — reported honestly rather than faked).
        var run = await runner.RunAsync(ProcessRunner.ResolveShell(new ShellRequest
        {
            Shell = "powershell", Script = WuInstallScript, TimeoutMs = 1_800_000,
        }), ct);
        var after = await CheckWindowsAsync(runner, ct);
        var output = Tail(run.Stdout + run.Stderr, 4000);
        var success = !output.Contains("\"error\"", StringComparison.OrdinalIgnoreCase);
        state.Audit.Write("patch", $"windows-update apply exit {run.ExitCode}; remaining {after.Pending.Count}", success ? "ok" : "error");
        return new PatchApplyResponse
        {
            Success = success,
            Output = (req.Ids.Count > 0 ? "note: Windows apply installs all pending updates (per-id selection is not supported on this path).\n" : "") + output,
            Remaining = after.Pending.Count,
            RebootRequired = after.RebootRequired,
        };
    }

    private static string ExtractJson(string stdout)
    {
        var start = stdout.IndexOf('{');
        var end = stdout.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("No JSON in output.");
        return stdout[start..(end + 1)];
    }

    private static string Tail(string text, int max = 500) => text.Length <= max ? text.Trim() : "…" + text[^max..].Trim();
}
