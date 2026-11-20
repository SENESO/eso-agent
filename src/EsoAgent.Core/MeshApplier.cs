using System.Runtime.InteropServices;
using System.Text.Json;

namespace EsoAgent.Core;

/// <summary>
/// The device's WireGuard identity: keypair generated once at first run and kept
/// in the data dir (wg.key / wg.pub). The public key + device code are the
/// device's fleet identity; the private key never leaves the device.
/// </summary>
public sealed class WgIdentity
{
    public required string PrivateKeyB64 { get; init; }
    public required string PublicKeyB64 { get; init; }

    public static WgIdentity LoadOrCreate(string dataDir)
    {
        var keyPath = Path.Combine(dataDir, "wg.key");
        var pubPath = Path.Combine(dataDir, "wg.pub");
        if (File.Exists(keyPath) && File.Exists(pubPath))
        {
            return new WgIdentity
            {
                PrivateKeyB64 = File.ReadAllText(keyPath).Trim(),
                PublicKeyB64 = File.ReadAllText(pubPath).Trim(),
            };
        }
        var (priv, pub) = WgCrypto.GenerateKeyPair();
        File.WriteAllText(keyPath, priv);
        File.WriteAllText(pubPath, pub);
        return new WgIdentity { PrivateKeyB64 = priv, PublicKeyB64 = pub };
    }
}

/// <summary>
/// Applies mesh state pushed by the fleet server: persists it, regenerates the
/// WireGuard config, reloads the platform tunnel, syncs the hosts file
/// (MagicDNS-lite), applies the compiled firewall plan, and (re)starts relay
/// shims. Every step is best-effort and audited; failures are reported
/// honestly in the status, never faked. On Windows the tunnel is the embedded
/// engine (MeshEngineSupervisor — eso-mesh.exe + wintun.dll ship inside our
/// own installer; there is no external WireGuard dependency). On Linux the
/// tunnel still runs through wg-quick (see MESH-EMBED-REPORT.md for the
/// follow-up that retires that too).
/// </summary>
public sealed class MeshApplier
{
    private readonly AgentState _state;
    private readonly RelayShimSet _shims = new();
    private MeshEngineSupervisor? _engine;
    public string Status { get; private set; } = "mesh not configured";

    public MeshApplier(AgentState state) => _state = state;

    /// <summary>
    /// The embedded engine supervisor (Windows), created on first use. The
    /// engine binary ships inside our own installer next to the agent
    /// (eso-mesh.exe + wintun.dll) — no external WireGuard install exists in
    /// the product anymore (owner product law, 2026-11-20).
    /// </summary>
    private MeshEngineSupervisor Engine
    {
        get
        {
            if (_engine != null) return _engine;
            var config = _state.Config;
            var enginePath = !string.IsNullOrWhiteSpace(config.Mesh.EnginePath)
                ? config.Mesh.EnginePath!
                : Path.Combine(AppContext.BaseDirectory, "eso-mesh.exe");
            _engine = new MeshEngineSupervisor(
                enginePath,
                _state.Platform.ProcessRunner,
                audit: (message, status) => _state.Audit.Write("mesh", message, status),
                logFilePath: Path.Combine(config.DataDir, "logs", "mesh-engine.log"));
            return _engine;
        }
    }

    public async Task ApplyAsync(FleetMeshFrame frame, CancellationToken ct = default)
    {
        var config = _state.Config;
        var audit = _state.Audit;
        await File.WriteAllTextAsync(
            Path.Combine(config.DataDir, "mesh.json"),
            JsonSerializer.Serialize(frame, EsoJson.Options), ct);
        _state.MeshVirtualIp = frame.YourVirtualIp;

        if (!config.Mesh.Enabled)
        {
            // Mesh was on and is now off in config: tear the tunnel down
            // cleanly instead of leaving an engine running behind.
            if (_engine != null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { await _engine.StopAsync(ct); } catch { /* best-effort teardown */ }
            }
            Status = "mesh state received but mesh is disabled in config";
            audit.Write("mesh", $"state received (vip={frame.YourVirtualIp}, peers={frame.Peers.Count}) — mesh disabled in config", "ok");
            return;
        }

        var notes = new List<string>();

        // 1) WireGuard config + tunnel reload.
        try
        {
            var wg = WgIdentity.LoadOrCreate(config.DataDir);
            var useExit = config.Mesh.UseExitNode && frame.ExitNodeCode != null
                          && !frame.ExitNodeCode.Equals(_state.Device?.Code, StringComparison.OrdinalIgnoreCase)
                ? frame.ExitNodeCode : null;
            var model = WgConfigBuilder.BuildModel(wg.PrivateKeyB64, frame.YourVirtualIp, config.Mesh.ListenPort, frame.Peers, config.Mesh.RelayShimBasePort, useExit);
            notes.Add(await ApplyTunnelAsync(model, ct));
            if (useExit != null) notes.Add($"exit-node: routing all traffic via {frame.ExitNodeCode}");
        }
        catch (Exception ex) { notes.Add($"tunnel: {ex.Message}"); }

        // 1b) Exit-node duty (v1.2): when THIS device is the designated exit
        // node, enable forwarding + NAT so peers' default-route traffic can
        // leave through it. Linux is automated (sysctl + iptables MASQUERADE
        // for mesh sources); Windows NAT is not automated (noted honestly —
        // the designation and routing still apply).
        if (frame.YouAreExitNode)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var commands = new[]
                    {
                        new[] { "sysctl", "-w", "net.ipv4.ip_forward=1" },
                        new[] { "iptables", "-t", "nat", "-C", "POSTROUTING", "-s", IpAllocator.DefaultCidr, "-j", "MASQUERADE" },
                        new[] { "iptables", "-t", "nat", "-A", "POSTROUTING", "-s", IpAllocator.DefaultCidr, "-j", "MASQUERADE" },
                    };
                    var applied = 0;
                    foreach (var argv in commands)
                    {
                        var result = await _state.Platform.ProcessRunner.RunAsync(
                            new ExecRequest { File = argv[0], Args = argv.Skip(1).ToArray(), TimeoutMs = 30_000 }, ct);
                        if (result.ExitCode == 0) applied++;
                    }
                    notes.Add($"exit-node: forwarding+NAT {applied}/{commands.Length} commands ok");
                }
                else
                {
                    notes.Add("exit-node: this device is the exit node, but NAT is not automated on this OS (forwarding must be enabled by the owner)");
                }
            }
            catch (Exception ex) { notes.Add($"exit-node: {ex.Message}"); }
        }

        // 2) Hosts file sync (MagicDNS-lite).
        try
        {
            var hostsPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
                : "/etc/hosts";
            var existing = File.Exists(hostsPath) ? await File.ReadAllTextAsync(hostsPath, ct) : "";
            var merged = HostsFileSync.Merge(existing, frame.Hosts);
            if (merged != existing) await File.WriteAllTextAsync(hostsPath, merged, ct);
            notes.Add($"hosts: {frame.Hosts.Count} name(s)");
        }
        catch (Exception ex) { notes.Add($"hosts: {ex.Message}"); }

        // 3) Firewall plan.
        if (frame.Firewall != null)
        {
            try
            {
                var commands = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? MeshFirewallCommands.Windows(frame.Firewall)
                    : MeshFirewallCommands.Linux(frame.Firewall);
                var applied = 0;
                foreach (var argv in commands)
                {
                    var result = await _state.Platform.ProcessRunner.RunAsync(
                        new ExecRequest { File = argv[0], Args = argv.Skip(1).ToArray(), TimeoutMs = 30_000 }, ct);
                    if (result.ExitCode == 0) applied++;
                }
                notes.Add($"firewall: {applied}/{commands.Count} commands ok");
            }
            catch (Exception ex) { notes.Add($"firewall: {ex.Message}"); }
        }

        // 4) Relay shims for relayed peers.
        try
        {
            var wg = WgIdentity.LoadOrCreate(config.DataDir);
            _shims.Sync(frame.Peers, frame.ServerRelayEndpoint, wg.PublicKeyB64, config.Mesh.ListenPort, config.Mesh.RelayShimBasePort);
            notes.Add($"relay: {_shims.Status}");
        }
        catch (Exception ex) { notes.Add($"relay: {ex.Message}"); }

        Status = string.Join("; ", notes);
        _state.MeshStatus = Status;
        audit.Write("mesh", $"applied vip={frame.YourVirtualIp} peers={frame.Peers.Count} — {Status}", "ok");
    }

    private async Task<string> ApplyTunnelAsync(WgInterfaceConfig model, CancellationToken ct)
    {
        var runner = _state.Platform.ProcessRunner;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Keep the rendered .conf in the data dir for inspection/debugging;
            // the engine itself is configured from the model over UAPI.
            var path = Path.Combine(_state.Config.DataDir, "eso0.conf");
            await File.WriteAllTextAsync(path, WgConfigBuilder.RenderText(model), ct);
            var result = await Engine.ApplyAsync(model, ct);
            return result.Summary;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var wgQuick = FindOnPath("wg-quick");
            if (wgQuick == null) return "tunnel: wg-quick not installed (config generated only)";
            var path = _state.Config.Mesh.WgConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, WgConfigBuilder.RenderText(model), ct);
            var name = Path.GetFileNameWithoutExtension(path);
            await runner.RunAsync(new ExecRequest { File = wgQuick, Args = new[] { "down", name }, TimeoutMs = 30_000 }, ct);
            var up = await runner.RunAsync(new ExecRequest { File = wgQuick, Args = new[] { "up", name }, TimeoutMs = 60_000 }, ct);
            return up.ExitCode == 0 ? $"tunnel: wg-quick up {name}" : $"tunnel: wg-quick exited {up.ExitCode}: {up.Stderr.Trim()}";
        }
        return "tunnel: unsupported OS";
    }

    private static string? FindOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
