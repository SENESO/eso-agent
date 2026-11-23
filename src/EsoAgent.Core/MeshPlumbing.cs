using System.Net.NetworkInformation;

namespace EsoAgent.Core;

/// <summary>
/// The tunnel's actual plumbing, verified and repaired on every mesh
/// frame — independently of the §24 fingerprint gate.
///
/// Field defect (§26, 2026-11-23): the box's eso0 lost its IPv4 address
/// (and its peer route) across a hibernation, and the applier's
/// "configuration unchanged — apply skipped" then skipped the repair
/// forever: the fingerprint records what configuration was last
/// APPLIED, not what the tunnel currently HOLDS. A frame that changes
/// nothing must still verify the address and the routes and repair
/// them when they are gone. The check is cheap (one interface
/// enumeration + at most one process spawn per peer route) and the
/// repair is additive and idempotent — an address or route that is
/// already present is left alone, never re-added.
///
/// The same step runs on Windows (netsh addressing), where the
/// supervisor's own IsApplied skip has the identical blind spot.
/// </summary>
public static class MeshPlumbing
{
    public sealed record VerifyResult(bool AddressOk, bool AddressRepaired, int RoutesOk, int RoutesRepaired, IReadOnlyList<string> Failures)
    {
        public string Note
        {
            get
            {
                var parts = new List<string>();
                parts.Add(AddressRepaired ? "repaired missing mesh address" : AddressOk ? "address ok" : "address missing (repair failed)");
                parts.Add($"routes {RoutesOk} ok" + (RoutesRepaired > 0 ? $", {RoutesRepaired} repaired" : ""));
                if (Failures.Count > 0) parts.Add("failures: " + string.Join("; ", Failures));
                return "verify: " + string.Join("; ", parts);
            }
        }
    }

    /// <summary>
    /// Verifies the tunnel plumbing for <paramref name="model"/> and
    /// repairs what is missing. The address is checked against the live
    /// interface table (no process spawn); routes are repaired by
    /// try-add — an add that fails with "already exists" proves the
    /// route is present, an add that succeeds proves it was missing,
    /// anything else is a reported failure. Never throws: every failure
    /// is reported in the result.
    /// </summary>
    public static async Task<VerifyResult> VerifyAndRepairAsync(
        WgInterfaceConfig model,
        IProcessRunner runner,
        Func<IReadOnlyList<(string InterfaceName, string Address)>>? listInterfaces = null,
        CancellationToken ct = default)
    {
        var windows = OperatingSystem.IsWindows();
        var failures = new List<string>();
        var addressOk = false;
        var addressRepaired = false;
        try
        {
            var interfaces = listInterfaces ?? DefaultListInterfaces;
            addressOk = interfaces().Any(i =>
                i.InterfaceName.Equals(MeshEngineSupervisor.InterfaceName, StringComparison.OrdinalIgnoreCase)
                && i.Address.Equals(model.VirtualIp, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { failures.Add($"address check failed: {ex.Message}"); }

        if (!addressOk)
        {
            // Additive repair: set the address (netsh set / ip addr add).
            // The command is idempotent in effect — setting the address
            // the interface already has is a no-op — but it is only run
            // when the check above says the address is missing.
            var repaired = await RunRepairAsync(runner, AddressCommands(model, windows), ct);
            if (repaired) { addressRepaired = true; addressOk = true; }
            else failures.Add($"could not restore mesh address {model.VirtualIp} on {MeshEngineSupervisor.InterfaceName}");
        }

        var routesOk = 0;
        var routesRepaired = 0;
        foreach (var prefix in WindowsMeshAddressing.RoutePrefixes(model))
        {
            var (ok, repaired) = await EnsureRouteAsync(runner, prefix, windows, ct);
            if (repaired) routesRepaired++;
            else if (ok) routesOk++;
            else failures.Add($"could not restore route {prefix} via {MeshEngineSupervisor.InterfaceName}");
        }

        return new VerifyResult(addressOk, addressRepaired, routesOk, routesRepaired, failures);
    }

    /// <summary>Live interface table: (interface name, IPv4 unicast address).</summary>
    internal static IReadOnlyList<(string InterfaceName, string Address)> DefaultListInterfaces()
    {
        var result = new List<(string, string)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    result.Add((nic.Name, addr.Address.ToString()));
                }
            }
        }
        catch { /* an unreadable table is "address missing" — the repair below reports it */ }
        return result;
    }

    /// <summary>Commands that (re)assign the model's virtual IP to the mesh interface.</summary>
    internal static List<string[]> AddressCommands(WgInterfaceConfig model, bool windows)
    {
        var iface = MeshEngineSupervisor.InterfaceName;
        if (windows)
            return new List<string[]> { WindowsMeshAddressing.ApplyCommands(iface, model)[0] };
        return new List<string[]> { new[] { "ip", "addr", "add", $"{model.VirtualIp}/32", "dev", iface } };
    }

    /// <summary>Commands that add one peer route via the mesh interface.</summary>
    internal static List<string[]> RouteCommands(string prefix, bool windows)
    {
        var iface = MeshEngineSupervisor.InterfaceName;
        if (windows)
            return new List<string[]> { new[] { "netsh", "interface", "ipv4", "add", "route", $"prefix={prefix}", $"interface={iface}", "nexthop=0.0.0.0" } };
        return new List<string[]> { new[] { "ip", "route", "add", prefix, "dev", iface } };
    }

    /// <summary>Runs the repair commands; true when at least one exited 0.</summary>
    private static async Task<bool> RunRepairAsync(IProcessRunner runner, List<string[]> commands, CancellationToken ct)
    {
        var ok = false;
        foreach (var argv in commands)
        {
            try
            {
                var result = await runner.RunAsync(
                    new ExecRequest { File = argv[0], Args = argv.Skip(1).ToArray(), TimeoutMs = 30_000 }, ct);
                if (result.ExitCode == 0) ok = true;
            }
            catch { /* counted as not-ok */ }
        }
        return ok;
    }

    /// <summary>
    /// Ensures one route exists: try-add, then classify. Exit 0 = it was
    /// missing and is now repaired; "already exists" = it was present all
    /// along; anything else = a real failure (reported, not silent).
    /// Returns (present, repaired).
    /// </summary>
    private static async Task<(bool Present, bool Repaired)> EnsureRouteAsync(IProcessRunner runner, string prefix, bool windows, CancellationToken ct)
    {
        foreach (var argv in RouteCommands(prefix, windows))
        {
            ExecResponse result;
            try
            {
                result = await runner.RunAsync(
                    new ExecRequest { File = argv[0], Args = argv.Skip(1).ToArray(), TimeoutMs = 30_000 }, ct);
            }
            catch { return (false, false); }
            if (result.ExitCode == 0) return (true, true);
            var text = (result.Stdout + "\n" + result.Stderr).ToLowerInvariant();
            if (text.Contains("already exists") || text.Contains("file exists")) return (true, false);
            return (false, false);
        }
        return (false, false);
    }
}
