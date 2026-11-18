using System.Net;
using System.Net.Sockets;

namespace EsoAgent.Core;

/// <summary>
/// Device-side relay shim set: one loopback UDP socket per relayed peer, so the
/// generated WireGuard config can point each relayed peer's Endpoint at
/// 127.0.0.1:&lt;basePort+i&gt;. Outgoing packets are wrapped with RelayFraming
/// and sent to the server relay; incoming relay packets ([srcPub][payload]) are
/// delivered to the local WireGuard listen port. The shim sees only WireGuard
/// ciphertext, exactly like the relay. All UDP failures are swallowed into the
/// status string — a sandbox without UDP must never crash the agent.
/// </summary>
public sealed class RelayShimSet : IDisposable
{
    private sealed class Shim
    {
        public required string PeerKeyB64;
        public required UdpClient Socket;
        public required CancellationTokenSource Cts;
    }

    private readonly List<Shim> _shims = new();
    private readonly object _lock = new();
    public string Status { get; private set; } = "not started";

    public void Sync(IReadOnlyList<MeshPeerInfo> peers, string? serverRelayEndpoint, string ownPubKeyB64, int wgListenPort, int basePort)
    {
        lock (_lock)
        {
            StopAllLocked();
            var relayed = peers.Where(p => p.Relay).ToList();
            if (relayed.Count == 0) { Status = "no relayed peers"; return; }
            if (string.IsNullOrWhiteSpace(serverRelayEndpoint))
            {
                Status = "relay endpoint not provided by server";
                return;
            }
            IPEndPoint relayEp;
            try
            {
                var parts = serverRelayEndpoint.Split(':');
                var ip = Dns.GetHostAddresses(parts[0]).FirstOrDefault() ?? IPAddress.Parse(parts[0]);
                relayEp = new IPEndPoint(ip, int.Parse(parts[1]));
            }
            catch (Exception ex) { Status = $"bad relay endpoint: {ex.Message}"; return; }

            var ownPub = Convert.FromBase64String(ownPubKeyB64);
            for (int i = 0; i < relayed.Count; i++)
            {
                var peer = relayed[i];
                try
                {
                    var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, basePort + i));
                    var cts = new CancellationTokenSource();
                    var shim = new Shim { PeerKeyB64 = peer.WgPublicKey, Socket = socket, Cts = cts };
                    _shims.Add(shim);
                    _ = RunShimAsync(shim, relayEp, ownPub, wgListenPort, cts.Token);
                }
                catch (Exception ex)
                {
                    Status = $"shim bind failed: {ex.Message}";
                    return;
                }
            }
            Status = $"{_shims.Count} relay shim(s) active";
        }
    }

    private async Task RunShimAsync(Shim shim, IPEndPoint relayEp, byte[] ownPub, int wgListenPort, CancellationToken ct)
    {
        var peerPub = Convert.FromBase64String(shim.PeerKeyB64);
        var wgLocal = new IPEndPoint(IPAddress.Loopback, wgListenPort);
        // Registration packet (empty payload) so the relay learns our endpoint for our pubkey.
        try { await shim.Socket.SendAsync(RelayFraming.Wrap(ownPub, peerPub, ReadOnlySpan<byte>.Empty), relayEp, ct); } catch { }
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await shim.Socket.ReceiveAsync(ct); }
            catch { break; }
            try
            {
                if (result.RemoteEndPoint.Equals(relayEp))
                {
                    // From the relay: [32B srcPub][payload] → deliver payload to local WireGuard.
                    if (result.Buffer.Length <= 32) continue;
                    await shim.Socket.SendAsync(result.Buffer.AsMemory(32), wgLocal, ct);
                }
                else
                {
                    // From local WireGuard → wrap and relay to the peer.
                    var frame = RelayFraming.Wrap(ownPub, peerPub, result.Buffer);
                    await shim.Socket.SendAsync(frame, relayEp, ct);
                }
            }
            catch { /* UDP blocked or transient — keep the shim alive */ }
        }
    }

    private void StopAllLocked()
    {
        foreach (var shim in _shims)
        {
            try { shim.Cts.Cancel(); shim.Socket.Dispose(); } catch { }
        }
        _shims.Clear();
    }

    public void Dispose() { lock (_lock) StopAllLocked(); }
}
