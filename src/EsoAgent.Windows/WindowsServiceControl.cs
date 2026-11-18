using System.Text.RegularExpressions;
using EsoAgent.Core;

namespace EsoAgent.Windows;

/// <summary>
/// Windows IServiceControl via sc.exe through the shared process runner.
/// Mutations are verified by re-querying the service state afterwards;
/// Verified is only true when the post-state matches the intent.
/// </summary>
public sealed class WindowsServiceControl : IServiceControl
{
    private readonly IProcessRunner _runner;

    public WindowsServiceControl(IProcessRunner runner) => _runner = runner;

    // ---------- list ----------

    public async Task<IReadOnlyList<ServiceDto>> ListAsync(CancellationToken ct = default)
    {
        var r = await ScAsync(new[] { "query", "state=", "all" }, 60_000, ct);
        var services = ParseQueryOutput(r.Stdout);
        if (services.Count == 0 && !string.IsNullOrWhiteSpace(r.Stderr))
            services = ParseQueryOutput(r.Stderr);

        // sc qc per service is slow; only enrich startup modes for a sane count.
        if (services.Count is > 0 and <= 200)
        {
            using var throttle = new SemaphoreSlim(8);
            await Task.WhenAll(services.Select(async svc =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    var qc = await ScAsync(new[] { "qc", svc.Name }, 15_000, ct);
                    svc.StartupMode = ParseStartType(qc.Stdout);
                }
                catch { /* leave StartupMode null for this service */ }
                finally { throttle.Release(); }
            }));
        }
        return services;
    }

    // ---------- mutations ----------

    public async Task<OperationResult> StartAsync(string name, CancellationToken ct = default)
    {
        var r = await ScAsync(new[] { "start", name }, 60_000, ct);
        var state = await QueryStateAsync(name, ct);
        return Result(r, state == "Running", $"Service '{name}' state after start: {state ?? "unknown"}.");
    }

    public async Task<OperationResult> StopAsync(string name, CancellationToken ct = default)
    {
        var r = await ScAsync(new[] { "stop", name }, 60_000, ct);
        var state = await QueryStateAsync(name, ct);
        return Result(r, state == "Stopped", $"Service '{name}' state after stop: {state ?? "unknown"}.");
    }

    public async Task<OperationResult> RestartAsync(string name, CancellationToken ct = default)
    {
        var stop = await ScAsync(new[] { "stop", name }, 60_000, ct);

        // Poll until Stopped (up to 30 s) before starting again.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string? state;
        do
        {
            state = await QueryStateAsync(name, ct);
            if (state == "Stopped") break;
            await Task.Delay(500, ct);
        } while (DateTime.UtcNow < deadline);

        var start = await ScAsync(new[] { "start", name }, 60_000, ct);
        var finalState = await QueryStateAsync(name, ct);
        var combined = new ExecResponse
        {
            ExitCode = start.ExitCode,
            Stdout = string.Join("\n", new[] { stop.Stdout.Trim(), start.Stdout.Trim() }.Where(s => s.Length > 0)),
            Stderr = string.Join("\n", new[] { stop.Stderr.Trim(), start.Stderr.Trim() }.Where(s => s.Length > 0)),
        };
        return Result(combined, finalState == "Running", $"Service '{name}' state after restart: {finalState ?? "unknown"}.");
    }

    public async Task<OperationResult> SetStartupModeAsync(string name, string mode, CancellationToken ct = default)
    {
        var scMode = mode.Trim().ToLowerInvariant() switch
        {
            "automatic" or "auto" => "auto",
            "manual" or "demand" => "demand",
            "disabled" => "disabled",
            _ => null,
        };
        if (scMode == null)
            return new OperationResult { Success = false, Output = "", Message = $"Unknown startup mode '{mode}'. Use Automatic, Manual or Disabled." };

        // sc.exe syntax is literally: sc config <name> start= <mode> (space after '=').
        // Passing "start=" and the mode as separate ArgumentList entries produces exactly that.
        var r = await ScAsync(new[] { "config", name, "start=", scMode }, 30_000, ct);

        var qc = await ScAsync(new[] { "qc", name }, 15_000, ct);
        var actual = ParseStartType(qc.Stdout);
        var expected = scMode switch { "auto" => "Automatic", "demand" => "Manual", _ => "Disabled" };
        var verified = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        return new OperationResult
        {
            Success = r.ExitCode == 0,
            ExitCode = r.ExitCode,
            Output = Combine(r),
            Verified = verified,
            Message = verified ? $"Startup mode verified as {actual}." : $"Startup mode after change: {actual ?? "unknown"} (expected {expected}).",
        };
    }

    // ---------- parsing helpers ----------

    private Task<ExecResponse> ScAsync(string[] args, int timeoutMs, CancellationToken ct)
        => _runner.RunAsync(new ExecRequest { File = "sc.exe", Args = args, TimeoutMs = timeoutMs }, ct);

    private async Task<string?> QueryStateAsync(string name, CancellationToken ct)
    {
        var r = await ScAsync(new[] { "query", name }, 15_000, ct);
        var parsed = ParseQueryOutput(r.Stdout);
        return parsed.FirstOrDefault()?.Status;
    }

    /// <summary>
    /// Parses "sc.exe query" output blocks:
    ///   SERVICE_NAME: Foo
    ///   DISPLAY_NAME: Foo Service
    ///           STATE              : 4  RUNNING
    /// </summary>
    internal static List<ServiceDto> ParseQueryOutput(string output)
    {
        var services = new List<ServiceDto>();
        ServiceDto? current = null;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            {
                current = new ServiceDto { Name = line["SERVICE_NAME:".Length..].Trim(), Status = "unknown" };
                services.Add(current);
            }
            else if (current != null && line.StartsWith("DISPLAY_NAME:", StringComparison.OrdinalIgnoreCase))
            {
                current.DisplayName = line["DISPLAY_NAME:".Length..].Trim();
            }
            else if (current != null && line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
            {
                var state = ParseStateValue(line);
                if (state != null) current.Status = state;
            }
        }
        return services;
    }

    private static string? ParseStateValue(string stateLine)
    {
        // "STATE : 4  RUNNING" → token after the number is the state name.
        var colon = stateLine.IndexOf(':');
        if (colon < 0) return null;
        var parts = stateLine[(colon + 1)..].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        return parts[1] switch
        {
            "RUNNING" => "Running",
            "STOPPED" => "Stopped",
            "PAUSED" => "Paused",
            "START_PENDING" => "StartPending",
            "STOP_PENDING" => "StopPending",
            "CONTINUE_PENDING" => "ContinuePending",
            "PAUSE_PENDING" => "PausePending",
            var other => char.ToUpperInvariant(other[0]) + other[1..].ToLowerInvariant(),
        };
    }

    /// <summary>Parses the START_TYPE line of "sc.exe qc": 2 AUTO_START, 3 DEMAND_START, 4 DISABLED.</summary>
    internal static string? ParseStartType(string qcOutput)
    {
        foreach (var raw in qcOutput.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("START_TYPE", StringComparison.OrdinalIgnoreCase)) continue;
            var m = Regex.Match(line, @":\s*(\d+)");
            if (!m.Success) return null;
            return m.Groups[1].Value switch
            {
                "2" => "Automatic",
                "3" => "Manual",
                "4" => "Disabled",
                _ => null, // 0 BOOT_START / 1 SYSTEM_START — not a user-facing startup mode
            };
        }
        return null;
    }

    private static OperationResult Result(ExecResponse r, bool verified, string message)
        => new()
        {
            // The mutation counts as successful only when the re-queried state matches.
            Success = verified,
            ExitCode = r.ExitCode,
            Output = Combine(r),
            Verified = verified,
            Message = message,
        };

    private static string Combine(ExecResponse r)
        => string.Join("\n", new[] { r.Stdout.Trim(), r.Stderr.Trim() }.Where(s => s.Length > 0));
}
