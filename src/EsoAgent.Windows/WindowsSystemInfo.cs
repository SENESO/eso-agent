using System.Runtime.InteropServices;
using EsoAgent.Core;

namespace EsoAgent.Windows;

/// <summary>
/// Windows ISystemInfo. RAM and battery come from kernel32/power P/Invokes;
/// CPU % is sampled from GetSystemTimes ~300 ms apart; logged-on users are
/// parsed from "query user" (the service runs in session 0, so managed APIs
/// alone cannot see the interactive sessions).
/// </summary>
public sealed class WindowsSystemInfo : ISystemInfo
{
    private readonly IProcessRunner _runner;

    public WindowsSystemInfo(IProcessRunner runner) => _runner = runner;

    // ---------- memory ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint DwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint DwMemoryLoad;
        public ulong UllTotalPhys;
        public ulong UllAvailPhys;
        public ulong UllTotalPageFile;
        public ulong UllAvailPageFile;
        public ulong UllTotalVirtual;
        public ulong UllAvailVirtual;
        public ulong UllAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    public (long? Used, long? Total) GetMemory()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status)) return (null, null);
        var total = (long)status.UllTotalPhys;
        var used = total - (long)status.UllAvailPhys;
        return (used, total);
    }

    // ---------- CPU ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint DwLowDateTime;
        public uint DwHighDateTime;
        public long ToLong() => ((long)DwHighDateTime << 32) + DwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime lpIdleTime, out FileTime lpKernelTime, out FileTime lpUserTime);

    public async Task<double?> GetCpuPercentAsync(CancellationToken ct = default)
    {
        if (!GetSystemTimes(out var idle0, out var kernel0, out var user0)) return null;
        await Task.Delay(300, ct);
        if (!GetSystemTimes(out var idle1, out var kernel1, out var user1)) return null;

        // Kernel time includes idle time, so busy = (kernel + user) - idle.
        long idleDelta = idle1.ToLong() - idle0.ToLong();
        long totalDelta = (kernel1.ToLong() - kernel0.ToLong()) + (user1.ToLong() - user0.ToLong());
        if (totalDelta <= 0) return null;
        var percent = (totalDelta - idleDelta) * 100.0 / totalDelta;
        return Math.Clamp(percent, 0.0, 100.0);
    }

    // ---------- battery ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;       // 0 = offline, 1 = online, 255 = unknown
        public byte BatteryFlag;        // 128 = no system battery
        public byte BatteryLifePercent; // 255 = unknown
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    public BatteryDto? GetBattery()
    {
        if (!GetSystemPowerStatus(out var status)) return null;
        bool present = status.BatteryFlag != 128;
        return new BatteryDto
        {
            Present = present,
            Percent = !present || status.BatteryLifePercent == 255 ? null : status.BatteryLifePercent,
            OnAc = status.ACLineStatus == 1,
        };
    }

    // ---------- logged-on users ----------

    public IReadOnlyList<string> GetLoggedOnUsers()
    {
        try
        {
            var result = _runner.RunAsync(new ExecRequest
            {
                File = "query.exe",
                Args = new[] { "user" },
                TimeoutMs = 5_000,
            }).GetAwaiter().GetResult();

            if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Stdout))
                return Array.Empty<string>();

            var users = new List<string>();
            foreach (var raw in result.Stdout.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("USERNAME", StringComparison.OrdinalIgnoreCase)) continue; // header
                // The current session's row starts with '>'; strip it, then the
                // username is the first whitespace-delimited token.
                if (line.StartsWith(">")) line = line[1..].TrimStart();
                var firstSpace = line.IndexOfAny(new[] { ' ', '\t' });
                var name = firstSpace < 0 ? line : line[..firstSpace];
                if (name.Length > 0) users.Add(name);
            }
            return users;
        }
        catch
        {
            // "query user" exits non-zero / is absent when nobody is logged on.
            return Array.Empty<string>();
        }
    }
}
