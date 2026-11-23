using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EsoAgent.Core;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Server;

/// <summary>
/// Mesh coordination (update 8): builds per-device mesh state frames from the
/// registry (peers, virtual IPs, approved subnets, MagicDNS-lite hosts map,
/// compiled ACL firewall plan) and pushes them to connected approved devices
/// whenever anything changes. The heavy lifting (WireGuard itself) stays in
/// the platform tooling on each device — the server only coordinates.
/// </summary>
public static class FleetMesh
{
    /// <summary>The agent API port used by the default ACL posture. Devices run 8737 unless reconfigured.</summary>
    public const int AgentPort = 8737;

    public static FleetMeshFrame BuildFrame(FleetServerState state, DeviceRecord self)
    {
        var devices = state.Registry.All();
        var approved = devices.Where(d => d.State == "Approved").ToList();
        var peers = approved
            .Where(d => !d.Code.Equals(self.Code, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(d.WgPublicKey)
                        && !string.IsNullOrWhiteSpace(d.VirtualIp))
            .Select(d => new MeshPeerInfo
            {
                Code = d.Code,
                Name = d.Name,
                WgPublicKey = d.WgPublicKey,
                VirtualIp = d.VirtualIp!,
                Endpoint = d.WgEndpoint,
                Relay = string.IsNullOrWhiteSpace(d.WgEndpoint),
                ApprovedSubnets = d.ApprovedSubnets,
                ExitNode = d.ExitNode,
            })
            .ToList();
        var exitNode = approved.FirstOrDefault(d => d.ExitNode);
        var hosts = approved
            .Where(d => !string.IsNullOrWhiteSpace(d.VirtualIp))
            .ToDictionary(d => d.Name, d => d.VirtualIp!, StringComparer.OrdinalIgnoreCase);
        var views = approved
            .Where(d => !string.IsNullOrWhiteSpace(d.VirtualIp))
            .Select(d => new AclCompiler.DeviceView(d.Code, d.VirtualIp!, d.Groups))
            .ToList();
        var plans = AclCompiler.Compile(views, state.Registry.AclRules(), AgentPort, P2pDefaults.ListenPort);
        return new FleetMeshFrame
        {
            YourVirtualIp = self.VirtualIp ?? "",
            Peers = peers,
            Hosts = hosts,
            Firewall = plans.GetValueOrDefault(self.Code),
            ServerRelayEndpoint = state.Registry.RelayEndpoint,
            ExitNodeCode = exitNode?.Code,
            ExitNodeVirtualIp = exitNode?.VirtualIp,
            YouAreExitNode = self.ExitNode,
        };
    }

    public static async Task PushToDeviceAsync(FleetServerState state, string code)
    {
        var record = state.Registry.Find(code);
        if (record == null || record.State != "Approved") return;
        if (!state.Connections.TryGetValue(record.Code, out var conn)) return;
        try { await conn.SendAsync(BuildFrame(state, record)); } catch { }
    }

    /// <summary>Pushes fresh mesh state to every connected approved device (after any mesh-relevant change).</summary>
    public static async Task PushToAllAsync(FleetServerState state)
    {
        foreach (var record in state.Registry.All().Where(d => d.State == "Approved"))
            await PushToDeviceAsync(state, record.Code);
    }
}

/// <summary>
/// DERP-style UDP relay: forwards WireGuard ciphertext between devices keyed
/// by WireGuard public keys (RelayFraming). Learns pubkey→endpoint from every
/// framed packet (including zero-payload registrations). The relay never sees
/// plaintext — WireGuard crypto is end-to-end between devices.
/// NOTE: this sandbox blocks UDP entirely; the service logs and idles if the
/// socket fails, and live relay verification is PENDING FIRST REAL ENVIRONMENT.
/// </summary>
public sealed class UdpRelayService : BackgroundService
{
    private readonly FleetServerState _state;
    private readonly ConcurrentDictionary<string, IPEndPoint> _endpoints = new();
    public long Forwarded { get; private set; }
    public long Dropped { get; private set; }

    public UdpRelayService(FleetServerState state) => _state = state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _state.Registry.RelayUdpPort;
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
        catch (Exception ex)
        {
            _state.Audit.Write("mesh/relay", $"UDP relay disabled: cannot bind :{port} ({ex.Message})", "error");
            return;
        }
        _state.Audit.Write("mesh/relay", $"UDP relay listening on :{port}", "ok");
        using (udp)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch { continue; }
                if (!RelayFraming.TryParse(result.Buffer, out var src, out var dst, out var payload))
                {
                    Dropped++;
                    continue;
                }
                _endpoints[src] = result.RemoteEndPoint; // learn/refresh sender endpoint
                if (payload.Length == 0) continue;        // registration/keepalive only
                if (!_endpoints.TryGetValue(dst, out var target))
                {
                    Dropped++; // destination never registered — nothing to do (sender retries via WG)
                    continue;
                }
                // Forward as [srcPub][payload] so the receiving shim can hand it to WireGuard.
                var srcBytes = Convert.FromBase64String(src);
                var outgoing = new byte[32 + payload.Length];
                srcBytes.CopyTo(outgoing, 0);
                payload.CopyTo(outgoing, 32);
                try
                {
                    await udp.SendAsync(outgoing, target, stoppingToken);
                    Forwarded++;
                }
                catch
                {
                    Dropped++; // e.g. UDP blocked (sandbox) — counted, never fatal
                }
            }
        }
    }
}
