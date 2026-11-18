using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace EsoAgent.Core;

/// <summary>PATH lookup + version-output parsing — pure helpers, unit-tested.</summary>
public static class ToolchainProbes
{
    /// <summary>Finds an executable on a PATH string. On Windows also tries the
    /// usual executable extensions. Returns the full path or null.</summary>
    public static string? FindOnPath(string fileName, string? pathValue, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(pathValue)) return null;
        var extensions = isWindows ? new[] { "", ".exe", ".cmd", ".bat" } : new[] { "" };
        foreach (var dir in pathValue.Split(isWindows ? ';' : ':'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), fileName + ext);
                try { if (File.Exists(candidate)) return candidate; } catch { }
            }
        }
        return null;
    }

    public static string? FindOnPath(string fileName)
        => FindOnPath(fileName, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());

    /// <summary>First non-empty line of a tool's version output (stdout preferred).</summary>
    public static string? FirstLine(params string?[] outputs)
    {
        foreach (var output in outputs)
        {
            if (string.IsNullOrWhiteSpace(output)) continue;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }
        }
        return null;
    }
}

/// <summary>
/// Inventory (v1.1): one structured snapshot of the machine — OS/build, CPU,
/// RAM, volumes, GPU (best-effort), network interfaces + IPs, toolchain probes
/// (dotnet/git/python/node/java/VS: present + version, or absent — never an
/// error), agent version + uptimes, interactive sessions (best-effort).
/// </summary>
public static class InventoryBuilder
{
    public static async Task<InventoryResponse> BuildAsync(AgentState state, CancellationToken ct = default)
    {
        var info = state.Platform.SystemInfo;
        var (used, total) = info.GetMemory();
        var runner = state.Platform.ProcessRunner;

        var volumes = new List<DiskDto>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady)
                    volumes.Add(new DiskDto { Name = d.Name, TotalBytes = d.TotalSize, FreeBytes = d.AvailableFreeSpace });
            }
            catch { }
        }

        var interfaces = new List<NetworkInterfaceDto>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var props = nic.GetIPProperties();
                var dto = new NetworkInterfaceDto
                {
                    Name = nic.Name,
                    Mac = nic.GetPhysicalAddress().ToString() is { Length: > 0 } mac ? mac : null,
                    IsLoopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                    Ips = props.UnicastAddresses.Select(a => a.Address.ToString()).ToList(),
                };
                interfaces.Add(dto);
            }
        }
        catch { /* best-effort */ }

        var tools = await ProbeToolsAsync(runner, ct);

        return new InventoryResponse
        {
            Os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
            OsDescription = RuntimeInformation.OSDescription,
            Hostname = Environment.MachineName,
            CpuModel = CpuModel(),
            CpuCores = Environment.ProcessorCount,
            RamTotalBytes = total,
            RamFreeBytes = used != null && total != null ? total - used : null,
            Volumes = volumes,
            Gpu = await ProbeGpuAsync(runner, ct),
            NetworkInterfaces = interfaces,
            Tools = tools,
            AgentVersion = ProductInfo.Version,
            AgentUptimeSeconds = (long)(DateTimeOffset.UtcNow - state.StartedAt).TotalSeconds,
            SystemUptimeSeconds = SystemUptimeSeconds(),
            InteractiveSessions = SafeSessions(info),
        };
    }

    private static List<string> SafeSessions(ISystemInfo info)
    {
        try { return info.GetLoggedOnUsers().ToList(); } catch { return new List<string>(); }
    }

    private static string? CpuModel()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                string? armModel = null;
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    var idx = line.IndexOf(':');
                    if (idx < 0) continue;
                    var key = line[..idx].Trim();
                    var value = line[(idx + 1)..].Trim();
                    if (key.Equals("model name", StringComparison.OrdinalIgnoreCase))
                        return value;
                    // ARM boards report "Model : Raspberry Pi ..." (exact case);
                    // x86's lowercase "model : 17" is a stepping number, not a name.
                    if (key == "Model") armModel ??= value;
                }
                return armModel;
            }
            if (OperatingSystem.IsWindows())
                return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        }
        catch { }
        return null;
    }

    private static async Task<string?> ProbeGpuAsync(IProcessRunner runner, CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var res = await runner.RunAsync(new ExecRequest { File = "lspci", TimeoutMs = 5000 }, ct);
                foreach (var line in (res.Stdout ?? "").Split('\n'))
                    if (line.Contains("VGA compatible controller") || line.Contains("3D controller"))
                        return line.Split(": ", 2).Last().Trim();
                return null;
            }
            if (OperatingSystem.IsWindows())
            {
                var res = await runner.RunAsync(new ExecRequest
                {
                    File = "powershell.exe",
                    Args = new[] { "-NoProfile", "-NonInteractive", "-Command", "(Get-CimInstance Win32_VideoController | Select-Object -First 1).Name" },
                    TimeoutMs = 15000,
                }, ct);
                return ToolchainProbes.FirstLine(res.Stdout);
            }
        }
        catch { /* best-effort: no GPU info is a valid answer */ }
        return null;
    }

    private static async Task<List<ToolProbeDto>> ProbeToolsAsync(IProcessRunner runner, CancellationToken ct)
    {
        async Task<ToolProbeDto> Probe(string displayName, string[] fileCandidates, string[] versionArgs)
        {
            string? found = null;
            foreach (var candidate in fileCandidates)
            {
                found = ToolchainProbes.FindOnPath(candidate);
                if (found != null) break;
            }
            if (found == null) return new ToolProbeDto { Name = displayName, Present = false };
            try
            {
                var res = await runner.RunAsync(new ExecRequest { File = found, Args = versionArgs, TimeoutMs = 5000 }, ct);
                return new ToolProbeDto
                {
                    Name = displayName,
                    Present = true,
                    Path = found,
                    Version = ToolchainProbes.FirstLine(res.Stdout, res.Stderr),
                };
            }
            catch
            {
                // On PATH but not runnable (broken shim etc.) — report present, no version.
                return new ToolProbeDto { Name = displayName, Present = true, Path = found };
            }
        }

        async Task<ToolProbeDto> ProbeVisualStudio()
        {
            const string display = "Visual Studio";
            if (!OperatingSystem.IsWindows()) return new ToolProbeDto { Name = display, Present = false };
            var vswhere = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere)) return new ToolProbeDto { Name = display, Present = false };
            try
            {
                var nameRes = await runner.RunAsync(new ExecRequest { File = vswhere, Args = new[] { "-latest", "-property", "displayName" }, TimeoutMs = 5000 }, ct);
                var verRes = await runner.RunAsync(new ExecRequest { File = vswhere, Args = new[] { "-latest", "-property", "installationVersion" }, TimeoutMs = 5000 }, ct);
                var name = ToolchainProbes.FirstLine(nameRes.Stdout);
                if (name == null) return new ToolProbeDto { Name = display, Present = false };
                var version = ToolchainProbes.FirstLine(verRes.Stdout);
                return new ToolProbeDto { Name = display, Present = true, Path = vswhere, Version = version != null ? $"{name} ({version})" : name };
            }
            catch { return new ToolProbeDto { Name = display, Present = false }; }
        }

        var probes = await Task.WhenAll(
            Probe("dotnet", new[] { "dotnet" }, new[] { "--version" }),
            Probe("git", new[] { "git" }, new[] { "--version" }),
            Probe("python", OperatingSystem.IsWindows() ? new[] { "python", "python3" } : new[] { "python3", "python" }, new[] { "--version" }),
            Probe("node", new[] { "node" }, new[] { "--version" }),
            Probe("java", new[] { "java" }, new[] { "-version" }),
            ProbeVisualStudio());
        return probes.ToList();
    }

    private static long SystemUptimeSeconds()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/uptime"))
            {
                var text = File.ReadAllText("/proc/uptime").Split(' ')[0];
                if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var s)) return (long)s;
            }
        }
        catch { }
        return Environment.TickCount64 / 1000;
    }
}
