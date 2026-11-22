using System.Net;
using Microsoft.Extensions.Hosting;

namespace EsoAgent.Core;

/// <summary>
/// The agent's P2P node, hosted alongside the fleet link. The node carries
/// discovery (DHT records by device code) and encrypted peer sessions
/// with peer relay, so neither job depends on the fleet hub; the hub
/// remains bootstrap + fallback (P2pConnector reports "no peer path" and
/// the caller takes the fleet path as before).
///
/// Trust: with the fleet enabled, the gate is the fleet's own approval —
/// this device must be Approved (a persisted signer key from an earlier
/// approval counts until a state frame says otherwise), and a peer must
/// be in the approved set the state frame delivers. Standalone devices
/// use the owner's explicit trusted-peer list instead.
/// </summary>
public sealed class P2pNodeService : BackgroundService
{
    private readonly AgentState _state;

    public P2pNodeService(AgentState state) => _state = state;

    /// <summary>The fleet-backed approval gate. Reads AgentState live, so a
    /// state frame that approves (or a rejection that clears the set) takes
    /// effect on the next session attempt — no restart, no cached copy.</summary>
    public sealed class FleetTrust : IP2pTrust
    {
        private readonly AgentState _state;
        public FleetTrust(AgentState state) => _state = state;

        public bool SelfApproved =>
            _state.FleetEnrollmentState == "Approved"
            || _state.OperatorPublicKeySpki != null
            || _state.PanelPublicKeySpki != null;

        public bool IsApprovedPeer(string code, string spkiB64)
            => _state.ApprovedPeers.TryGetValue(code, out var spki) && spki == spkiB64;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = _state.Config;
        var device = _state.Device;
        if (device == null || cfg.P2p.ListenPort <= 0)
        {
            _state.P2pStatus = "p2p disabled";
            return;
        }
        IP2pTrust trust = cfg.Fleet is { Enabled: true }
            ? new FleetTrust(_state)
            : new StaticP2pTrust(cfg.P2p.TrustedPeers);
        var node = new P2pNode(new P2pNodeOptions
        {
            Identity = device,
            Transport = TcpP2pTransport.Instance,
            Trust = trust,
            AdvertiseHost = cfg.P2p.AdvertiseHost ?? PickAdvertiseHost(),
            ListenPort = cfg.P2p.ListenPort,
            AdvertisePort = cfg.P2p.AdvertisePort,
            Bootstrap = cfg.P2p.Bootstrap,
            PeerCacheFile = cfg.ResolvePath("p2p-peers.json"),
            RelayCaptureFile = cfg.P2p.RelayCaptureFile,
            Audit = _state.Audit.Write,
        });
        node.OnPayloadReceived = async (fromCode, payload, result) =>
        {
            try
            {
                var dir = cfg.ResolvePath("p2p-inbox");
                Directory.CreateDirectory(dir);
                var name = $"{fromCode}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.bin";
                await File.WriteAllBytesAsync(Path.Combine(dir, name), payload);
                _state.Audit.Write("p2p", $"received {payload.Length} bytes from {fromCode} -> p2p-inbox/{name}", "ok");
            }
            catch (Exception ex)
            {
                _state.Audit.Write("p2p", $"failed to store payload from {fromCode}: {ex.Message}", "error");
            }
        };
        _state.P2p = node;
        try
        {
            await node.StartAsync();
            _state.Audit.Write("p2p", $"node listening on :{cfg.P2p.ListenPort} as {device.Code}, advertising {node.SelfPeer.Endpoint}", "ok");
        }
        catch (Exception ex)
        {
            _state.P2pStatus = "p2p failed to start: " + ex.Message;
            _state.Audit.Write("p2p", "node failed to start: " + ex.Message, "error");
            return;
        }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _state.P2pStatus = Describe(node);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _state.P2pStatus = "p2p stopped";
            try { await node.StopAsync(); } catch { }
        }
    }

    private static string Describe(P2pNode node)
        => $"listening {node.SelfPeer.Endpoint} · {node.Table.Count} peers known · " +
           $"{node.SessionsAccepted} sessions accepted, {node.SessionsRefused} refused · " +
           $"relayed {node.RelayForwardedBytes} bytes";

    /// <summary>The address other devices should dial: the tailnet address when
    /// this machine has one (reachable across the whole fleet), else the mesh
    /// virtual IP once assigned, else the first interface IPv4, else loopback.</summary>
    private string PickAdvertiseHost()
    {
        var ips = NetHelpers.DiscoverInterfaceIps();
        var tailnet = ips.FirstOrDefault(ip =>
            IPAddress.TryParse(ip, out var a) && a.GetAddressBytes() is var b && b[0] == 100 && (b[1] & 0xC0) == 0x40);
        if (tailnet != null) return tailnet;
        if (!string.IsNullOrWhiteSpace(_state.MeshVirtualIp)) return _state.MeshVirtualIp!;
        return ips.FirstOrDefault(ip => IPAddress.TryParse(ip, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
               ?? "127.0.0.1";
    }
}
