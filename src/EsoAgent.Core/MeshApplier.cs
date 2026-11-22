using System.Runtime.InteropServices;
using System.Text;
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
///
/// Applies are IDEMPOTENT (§24): the server re-pushes the frame every
/// 30 s (the §15 resync), so an apply whose effective configuration is
/// unchanged must not touch the tunnel — on Windows the engine
/// supervisor skips its peer-replacing UAPI set, and here the wg-quick
/// bounce, the exit-node commands and the firewall plan are skipped on
/// an unchanged fingerprint. The self-healing parts still run every
/// frame: relay shim Sync (restarts dead loops), the hosts merge
/// (content-gated), and the engine's status refresh + dead-receiver
/// recovery.
/// </summary>
public sealed class MeshApplier
{
    private readonly AgentState _state;
    private readonly RelayShimSet _shims = new();
    private MeshEngineSupervisor? _engine;
    /// <summary>Fingerprint of the last frame whose platform apply
    /// converged (see <see cref="ComputeFingerprint"/>); null until the
    /// first converged apply, so a fresh agent always applies in full —
    /// the §15 resync heal depends on that.</summary>
    private string? _lastAppliedFingerprint;
    public string Status { get; private set; } = "mesh not configured";

    /// <summary>
    /// Throttle for the dead-receiver recovery restart (see
    /// ApplyWindowsCoreAsync): at most one restart per 10 minutes, so a
    /// genuinely unreachable peer can never turn into a restart loop.
    /// </summary>
    private DateTimeOffset _lastDeadReceiverRestart = DateTimeOffset.MinValue;

    public MeshApplier(AgentState state) => _state = state;

    /// <summary>Test seam: a supervisor built on fakes (transport/process/runner).</summary>
    internal MeshApplier(AgentState state, MeshEngineSupervisor engine)
    {
        _state = state;
        _engine = engine;
    }

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

        // The model first (pure): both the tunnel and the shims derive from it.
        var wg = WgIdentity.LoadOrCreate(config.DataDir);
        var useExit = config.Mesh.UseExitNode && frame.ExitNodeCode != null
                      && !frame.ExitNodeCode.Equals(_state.Device?.Code, StringComparison.OrdinalIgnoreCase)
            ? frame.ExitNodeCode : null;
        var model = WgConfigBuilder.BuildModel(wg.PrivateKeyB64, frame.YourVirtualIp, config.Mesh.ListenPort, frame.Peers, config.Mesh.RelayShimBasePort, useExit);
        if (useExit != null) notes.Add($"exit-node: routing all traffic via {frame.ExitNodeCode}");

        // Idempotency (§24): the resync re-pushes this frame every 30 s.
        // When the effective configuration is unchanged since the last
        // converged apply, the destructive steps (tunnel reload, exit-node
        // commands, firewall plan) are skipped — the tunnel must not be
        // rebuilt for a frame that changes nothing.
        var fingerprint = ComputeFingerprint(model, frame, config.Mesh.RelayShimBasePort);
        var unchanged = _lastAppliedFingerprint != null && _lastAppliedFingerprint == fingerprint;
        var coreConverged = false;

        // 0+1) Relay shims + tunnel. The shims go FIRST (Windows core does
        // this explicitly; the Linux path below mirrors it): the engine sends
        // its first handshake initiation the moment it is configured, and on
        // Windows a packet to a not-yet-bound shim port earns an ICMP Port
        // Unreachable that kills the engine's IPv4 receive routine outright
        // (wireguard-go RoutineReceiveIncoming exits on WSAECONNRESET — see
        // ApplyWindowsCoreAsync). Configuring the tunnel first is what
        // stranded the laptop mesh at rx 0 forever.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // The supervisor's own gate makes the engine part of the core
            // a status-refresh no-op when the model is unchanged; the
            // shims still sync and the dead-receiver recovery still runs.
            notes.Add(await ApplyWindowsCoreAsync(model, frame, wg, ct));
            coreConverged = _engine != null && _engine.IsApplied(model);
        }
        else
        {
            try
            {
                _shims.Sync(frame.Peers, frame.ServerRelayEndpoint, wg.PublicKeyB64, config.Mesh.ListenPort, config.Mesh.RelayShimBasePort);
                notes.Add($"relay: {_shims.Status}");
            }
            catch (Exception ex) { notes.Add($"relay: {ex.Message}"); }
            if (unchanged)
            {
                // No wg-quick down/up bounce for a frame that changes
                // nothing — that bounce is the Linux twin of the Windows
                // defect: the tunnel died every 30 s either way.
                notes.Add("tunnel: configuration unchanged — apply skipped");
                coreConverged = true;
            }
            else
            {
                try
                {
                    var (note, converged) = await ApplyTunnelAsync(model, ct);
                    notes.Add(note);
                    coreConverged = converged;
                }
                catch (Exception ex) { notes.Add($"tunnel: {ex.Message}"); }
            }
        }

        // 1b) Exit-node duty (v1.2): when THIS device is the designated exit
        // node, enable forwarding + NAT so peers' default-route traffic can
        // leave through it. Linux is automated (sysctl + iptables MASQUERADE
        // for mesh sources); Windows NAT is not automated (noted honestly —
        // the designation and routing still apply). Skipped on an unchanged
        // frame (§24): the commands are idempotent in effect, and re-running
        // them every 30 s was pure churn.
        if (frame.YouAreExitNode && !unchanged)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // sysctl, then the MASQUERADE rule: -C checks for it and
                    // -A runs ONLY when the check missed. The old loop ran
                    // -A unconditionally after -C, stacking one duplicate
                    // rule per apply (every 30 s under the resync).
                    var applied = 0;
                    if (await RunPlatformCommandAsync(new[] { "sysctl", "-w", "net.ipv4.ip_forward=1" }, ct)) applied++;
                    var rulePresent = await RunPlatformCommandAsync(
                        new[] { "iptables", "-t", "nat", "-C", "POSTROUTING", "-s", IpAllocator.DefaultCidr, "-j", "MASQUERADE" }, ct);
                    if (rulePresent) applied += 2; // check passed: the add is already satisfied
                    else if (await RunPlatformCommandAsync(
                        new[] { "iptables", "-t", "nat", "-A", "POSTROUTING", "-s", IpAllocator.DefaultCidr, "-j", "MASQUERADE" }, ct)) applied += 2;
                    notes.Add($"exit-node: forwarding+NAT {applied}/3 commands ok");
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

        // 3) Firewall plan (skipped on an unchanged frame, §24 — the plan
        // is part of the fingerprint, so a plan change re-runs it).
        if (frame.Firewall != null && !unchanged)
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

        Status = string.Join("; ", notes);
        _state.MeshStatus = Status;
        audit.Write("mesh", $"applied vip={frame.YourVirtualIp} peers={frame.Peers.Count} — {Status}", "ok");

        // Record the fingerprint only when the tunnel actually converged:
        // if the engine rejected the config or wg-quick failed, the next
        // (identical) frame must retry the full apply, not skip it.
        if (!unchanged && coreConverged) _lastAppliedFingerprint = fingerprint;
    }

    /// <summary>
    /// The effective-configuration fingerprint an apply is gated on:
    /// the engine model (identity key, virtual IP, listen port, and each
    /// peer's key/allowed-IPs/endpoint/keepalive in order — the model
    /// already folds in exit-node routing and relay shim port
    /// assignment), the relay endpoint and shim base port (the shim
    /// inputs), the exit-node designation, and the firewall plan. The
    /// hosts map is deliberately NOT part of it: the hosts step is
    /// content-gated on its own, and a hosts-only change must not
    /// rebuild the tunnel.
    /// </summary>
    internal static string ComputeFingerprint(WgInterfaceConfig model, FleetMeshFrame frame, int relayShimBasePort)
    {
        var sb = new StringBuilder();
        sb.Append(model.PrivateKeyB64).Append('|').Append(model.VirtualIp).Append('|').Append(model.ListenPort);
        foreach (var peer in model.Peers)
        {
            sb.Append("|peer:").Append(peer.PublicKeyB64)
              .Append(',').Append(string.Join(",", peer.AllowedIps))
              .Append(',').Append(peer.Endpoint ?? "-")
              .Append(',').Append(peer.PersistentKeepalive);
        }
        sb.Append("|relay:").Append(frame.ServerRelayEndpoint ?? "-");
        sb.Append("|shimbase:").Append(relayShimBasePort);
        sb.Append("|exit:").Append(frame.YouAreExitNode ? '1' : '0');
        sb.Append("|fw:").Append(frame.Firewall == null ? "-" : JsonSerializer.Serialize(frame.Firewall, EsoJson.Options));
        return sb.ToString();
    }

    /// <summary>Runs one platform command; true when it exited 0.</summary>
    private async Task<bool> RunPlatformCommandAsync(string[] argv, CancellationToken ct)
    {
        var result = await _state.Platform.ProcessRunner.RunAsync(
            new ExecRequest { File = argv[0], Args = argv.Skip(1).ToArray(), TimeoutMs = 30_000 }, ct);
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Windows mesh core: relay shims, then the engine, then — only when the
    /// engine was already running with a dead UDP receiver — one engine
    /// restart to bring the receiver back.
    ///
    /// Root cause it repairs (field-proven 2026-11-21, laptop ESO):
    /// wireguard-go's IPv4 receive routine (device/receive.go,
    /// RoutineReceiveIncoming, pinned v0.0.20250522) RETURNS — permanently —
    /// on any non-temporary receive error. On Windows, sending a UDP packet
    /// to a port with no listener earns an ICMP Port Unreachable, and the
    /// next recv on that socket fails with WSAECONNRESET ("An existing
    /// connection was forcibly closed by the remote host"), which Go reports
    /// as non-temporary. The old apply order configured the tunnel BEFORE
    /// syncing the shims, so the engine's first handshake initiation went to
    /// the not-yet-bound 127.0.0.1:51831: the v4 receiver died in the same
    /// second at every apply (engine log: "Failed to receive v4 packet ..."
    /// then "Routine: receive incoming v4 - stopped", never restarted), the
    /// engine could send but never receive (UAPI: tx 32708 B, rx 0 B,
    /// last_handshake_time_sec 0), and no restart or re-apply could fix it
    /// because every re-apply repeated the race (Sync tore the shim sockets
    /// down first). The shim's relay loop was exonerated by a live probe: a
    /// plain UdpClient received relay frames fine and forwarding genuine
    /// box handshake initiations to 127.0.0.1:51820 still produced no
    /// handshake — the packets had nowhere to land.
    ///
    /// The fix has three parts: (1) shims are synced BEFORE the engine is
    /// configured, so the loopback port is bound when the first initiation
    /// is sent; (2) RelayShimSet.Sync is idempotent, so re-applies never
    /// unbind a live shim port; (3) this recovery: if the engine predates
    /// this apply (already running, or adopted from a previous agent
    /// lifetime) and UAPI still shows it transmitting without ever having
    /// received or handshook, the receiver is dead — restart the engine
    /// once (throttled) so its routines start with the shim sockets bound.
    /// A genuinely unreachable peer shows the same UAPI signature, so the
    /// restart is throttled to one per 10 minutes; the cost of a needless
    /// restart is a few seconds of tunnel flap, and the detector only fires
    /// for a pre-existing engine, never a freshly started one (whose
    /// handshake is still in flight).
    /// </summary>
    internal async Task<string> ApplyWindowsCoreAsync(WgInterfaceConfig model, FleetMeshFrame frame, WgIdentity wg, CancellationToken ct = default)
    {
        var notes = new List<string>();

        // 0) Relay shims FIRST: the loopback ports must be bound before the
        // engine is configured and starts sending to them.
        try
        {
            _shims.Sync(frame.Peers, frame.ServerRelayEndpoint, wg.PublicKeyB64, _state.Config.Mesh.ListenPort, _state.Config.Mesh.RelayShimBasePort);
            notes.Add($"relay: {_shims.Status}");
        }
        catch (Exception ex) { notes.Add($"relay: {ex.Message}"); }

        // 1) WireGuard config + tunnel reload.
        var wasRunning = Engine.State == MeshEngineState.Running;
        try { notes.Add(await ApplyWindowsTunnelAsync(model, ct)); }
        catch (Exception ex) { notes.Add($"tunnel: {ex.Message}"); }

        // 2) Dead-receiver recovery (see the doc comment above).
        if (Engine.State == MeshEngineState.Running
            && (wasRunning || Engine.Adopted)
            && DeadReceiverSignature(Engine.LastStatus)
            && (DateTimeOffset.UtcNow - _lastDeadReceiverRestart) >= TimeSpan.FromMinutes(10))
        {
            _lastDeadReceiverRestart = DateTimeOffset.UtcNow;
            try
            {
                await Engine.StopAsync(ct);
                notes.Add(await ApplyWindowsTunnelAsync(model, ct));
                notes.Add("engine restarted to recover its dead UDP receiver (transmitting, never received, no handshake)");
            }
            catch (Exception ex) { notes.Add($"engine receiver recovery failed: {ex.Message}"); }
        }

        return string.Join("; ", notes);
    }

    /// <summary>
    /// The dead-UDP-receiver signature from a UAPI get: at least one peer
    /// has transmitted but the engine has never received anything from it
    /// and never completed a handshake. (A genuinely unreachable peer shows
    /// the same signature — the caller throttles the restart this gates.)
    /// </summary>
    internal static bool DeadReceiverSignature(UapiDeviceStatus? status)
    {
        if (status == null || !status.Ok) return false;
        return status.Peers.Any(p => p.TxBytes > 0 && p.RxBytes == 0 && p.LastHandshakeUnixSec == 0);
    }

    /// <summary>Applies the tunnel for the non-Windows-core path. The
    /// Converged half tells the idempotency gate whether this state may
    /// be recorded as applied: a failed wg-quick up must be retried by
    /// the next frame, while "not installed" / "unsupported OS" are as
    /// converged as this host can ever be.</summary>
    private async Task<(string Note, bool Converged)> ApplyTunnelAsync(WgInterfaceConfig model, CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var note = await ApplyWindowsTunnelAsync(model, ct);
            return (note, _engine != null && _engine.IsApplied(model));
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var runner = _state.Platform.ProcessRunner;
            var wgQuick = FindOnPath("wg-quick");
            if (wgQuick == null) return ("tunnel: wg-quick not installed (config generated only)", true);
            var path = _state.Config.Mesh.WgConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, WgConfigBuilder.RenderText(model), ct);
            var name = Path.GetFileNameWithoutExtension(path);
            await runner.RunAsync(new ExecRequest { File = wgQuick, Args = new[] { "down", name }, TimeoutMs = 30_000 }, ct);
            var up = await runner.RunAsync(new ExecRequest { File = wgQuick, Args = new[] { "up", name }, TimeoutMs = 60_000 }, ct);
            return up.ExitCode == 0
                ? ($"tunnel: wg-quick up {name}", true)
                : ($"tunnel: wg-quick exited {up.ExitCode}: {up.Stderr.Trim()}", false);
        }
        return ("tunnel: unsupported OS", true);
    }

    /// <summary>
    /// The Windows tunnel step (rendered .conf kept in the data dir for
    /// inspection; the engine itself is configured from the model over
    /// UAPI). Split out of <see cref="ApplyTunnelAsync"/> because that
    /// method OS-branches: the Windows core path must reach the engine
    /// supervisor directly, or on a non-Windows host it would silently take
    /// the wg-quick branch and never touch the engine.
    /// </summary>
    private async Task<string> ApplyWindowsTunnelAsync(WgInterfaceConfig model, CancellationToken ct)
    {
        var path = Path.Combine(_state.Config.DataDir, "eso0.conf");
        await File.WriteAllTextAsync(path, WgConfigBuilder.RenderText(model), ct);
        var result = await Engine.ApplyAsync(model, ct);
        return result.Summary;
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
