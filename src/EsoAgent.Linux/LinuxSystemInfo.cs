using System.Globalization;
using EsoAgent.Core;

namespace EsoAgent.Linux;

/// <summary>Linux system facts from /proc and /sys — no shelling out except for
/// the logged-on user list, which is exactly what `who` already computes.</summary>
public sealed class LinuxSystemInfo : ISystemInfo
{
    private readonly IProcessRunner _runner;

    public LinuxSystemInfo(IProcessRunner runner) => _runner = runner;

    public (long? Used, long? Total) GetMemory()
    {
        try
        {
            long? totalKiB = null, availableKiB = null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                // "MemTotal:       16384000 kB"
                var parts = line.Split(':', 2);
                if (parts.Length != 2) continue;
                var valueText = parts[1].Trim().Split(' ')[0];
                if (!long.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kib)) continue;
                if (parts[0] == "MemTotal") totalKiB = kib;
                else if (parts[0] == "MemAvailable") availableKiB = kib;
                if (totalKiB != null && availableKiB != null) break;
            }
            if (totalKiB == null) return (null, null);
            long total = totalKiB.Value * 1024;
            long? used = availableKiB != null ? (totalKiB.Value - availableKiB.Value) * 1024 : null;
            return (used, total);
        }
        catch { return (null, null); }
    }

    public async Task<double?> GetCpuPercentAsync(CancellationToken ct = default)
    {
        try
        {
            var first = ReadCpuTimes();
            if (first == null) return null;
            await Task.Delay(300, ct);
            var second = ReadCpuTimes();
            if (second == null) return null;

            long totalDelta = second.Value.Total - first.Value.Total;
            long idleDelta = second.Value.Idle - first.Value.Idle;
            if (totalDelta <= 0) return null;
            return (1.0 - (double)idleDelta / totalDelta) * 100.0;
        }
        catch { return null; }
    }

    /// <summary>Reads the aggregate "cpu" line of /proc/stat: (idle+iowait, total of all fields).</summary>
    private static (long Idle, long Total)? ReadCpuTimes()
    {
        foreach (var line in File.ReadLines("/proc/stat"))
        {
            if (!line.StartsWith("cpu ", StringComparison.Ordinal)) continue;
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // fields[0]="cpu", then user nice system idle iowait irq softirq steal guest guest_nice
            if (fields.Length < 6) return null;
            long total = 0;
            for (int i = 1; i < fields.Length; i++)
                if (long.TryParse(fields[i], out var v)) total += v;
            long idle = long.Parse(fields[4], CultureInfo.InvariantCulture)
                      + long.Parse(fields[5], CultureInfo.InvariantCulture); // idle + iowait
            return (idle, total);
        }
        return null;
    }

    public BatteryDto? GetBattery()
    {
        var battery = new BatteryDto { Present = false };
        try
        {
            var root = "/sys/class/power_supply";
            if (!Directory.Exists(root)) return battery;
            foreach (var entry in Directory.GetDirectories(root))
            {
                // AC adapters expose an "online" file (1 = plugged in).
                var onlinePath = Path.Combine(entry, "online");
                if (File.Exists(onlinePath) &&
                    File.ReadAllText(onlinePath).Trim() == "1")
                    battery.OnAc = true;

                var typePath = Path.Combine(entry, "type");
                if (!File.Exists(typePath)) continue;
                if (File.ReadAllText(typePath).Trim() != "Battery") continue;

                battery.Present = true;
                var capacityPath = Path.Combine(entry, "capacity");
                if (File.Exists(capacityPath) &&
                    int.TryParse(File.ReadAllText(capacityPath).Trim(), out var pct))
                    battery.Percent = pct;
            }
        }
        catch { /* sysfs is best-effort; report what we have */ }
        return battery;
    }

    public IReadOnlyList<string> GetLoggedOnUsers()
    {
        try
        {
            // ISystemInfo is synchronous; the runner is not. There is no sync
            // context in the daemon, so blocking on this short call is safe.
            var result = _runner.RunAsync(RunnerHelpers.Cmd("who", 5_000))
                .ConfigureAwait(false).GetAwaiter().GetResult();
            if (result.ExitCode != 0) return Array.Empty<string>();
            return ParseWhoUsers(result.Stdout);
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>First whitespace-separated column of each `who` line, de-duplicated.</summary>
    internal static IReadOnlyList<string> ParseWhoUsers(string whoOutput)
    {
        var users = new List<string>();
        foreach (var raw in whoOutput.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var user = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
            if (!users.Contains(user)) users.Add(user);
        }
        return users;
    }
}
