using System.Net;
using System.Net.Sockets;

namespace EsoAgent.Core;

/// <summary>
/// Device-side relay shim set: two UDP sockets per relayed peer. The
/// WG-facing socket is bound to loopback (127.0.0.1:&lt;basePort+i&gt;) so the
/// generated WireGuard config can point each relayed peer's Endpoint there;
/// the relay-facing socket is bound to the wildcard address on an ephemeral
/// port and carries the framed traffic to and from the server relay.
/// The two sides must be separate sockets: a UDP socket bound to loopback
/// cannot send to a remote relay endpoint, so a shared socket silently
/// strands every relayed packet on the device. Incoming relay packets
/// ([srcPub][payload]) are delivered to the local WireGuard listen port.
/// The shim sees only WireGuard ciphertext, exactly like the relay. All UDP
/// failures are swallowed into the status string — a sandbox without UDP
/// must never crash the agent.
/// </summary>
public sealed class RelayShimSet : IDisposable
{
    private sealed class Shim
    {
        public required string PeerKeyB64;
        public required UdpClient Socket;
        public required UdpClient RelaySocket;
        public required CancellationTokenSource Cts;
    }

    private readonly List<Shim> _shims = new();
    private readonly object _lock = new();
    public string Status { get; private set; } = "not started";

    /// <summary>
    /// Diagnostics/test seam: the local endpoint each live shim's two sockets
    /// are bound to — WG-facing (loopback, basePort+i) and relay-facing
    /// (wildcard, ephemeral). The relay-facing endpoint must never be
    /// loopback: that binding is what made remote relays unreachable.
    /// </summary>
    internal IReadOnlyList<(IPEndPoint WgFacing, IPEndPoint RelayFacing)> ShimSocketEndpoints
    {
        get
        {
            lock (_lock)
            {
                return _shims
                    .Select(s => ((IPEndPoint)s.Socket.Client.LocalEndPoint!,
                                  (IPEndPoint)s.RelaySocket.Client.LocalEndPoint!))
                    .ToList();
            }
        }
    }

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
                UdpClient? socket = null;
                UdpClient? relaySocket = null;
                try
                {
                    socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, basePort + i));
                    relaySocket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    var cts = new CancellationTokenSource();
                    var shim = new Shim { PeerKeyB64 = peer.WgPublicKey, Socket = socket, RelaySocket = relaySocket, Cts = cts };
                    _shims.Add(shim);
                    _ = RunWgLoopAsync(shim, relayEp, ownPub, cts.Token);
                    _ = RunRelayLoopAsync(shim, relayEp, wgListenPort, cts.Token);
                }
                catch (Exception ex)
                {
                    try { socket?.Dispose(); } catch { }
                    try { relaySocket?.Dispose(); } catch { }
                    Status = $"shim bind failed: {ex.Message}";
                    return;
                }
            }
            Status = $"{_shims.Count} relay shim(s) active";
        }
    }

    /// <summary>
    /// WireGuard side: packets arriving on the loopback socket come from the
    /// local WireGuard — wrap them and send them to the relay over the
    /// relay-facing socket. Also sends the one-off registration frame (empty
    /// payload) so the relay learns our endpoint for our pubkey.
    /// </summary>
    private async Task RunWgLoopAsync(Shim shim, IPEndPoint relayEp, byte[] ownPub, CancellationToken ct)
    {
        var peerPub = Convert.FromBase64String(shim.PeerKeyB64);
        try { await shim.RelaySocket.SendAsync(RelayFraming.Wrap(ownPub, peerPub, ReadOnlySpan<byte>.Empty), relayEp, ct); } catch { }
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await shim.Socket.ReceiveAsync(ct); }
            catch { break; }
            try
            {
                var frame = RelayFraming.Wrap(ownPub, peerPub, result.Buffer);
                await shim.RelaySocket.SendAsync(frame, relayEp, ct);
            }
            catch { /* UDP blocked or transient — keep the shim alive */ }
        }
    }

    /// <summary>
    /// Relay side: packets arriving on the relay-facing socket from the relay
    /// are [32B srcPub][payload] — deliver the payload to the local WireGuard
    /// listen port over the loopback socket. Anything not from the relay
    /// endpoint is dropped.
    /// </summary>
    private async Task RunRelayLoopAsync(Shim shim, IPEndPoint relayEp, int wgListenPort, CancellationToken ct)
    {
        var wgLocal = new IPEndPoint(IPAddress.Loopback, wgListenPort);
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await shim.RelaySocket.ReceiveAsync(ct); }
            catch { break; }
            try
            {
                if (!result.RemoteEndPoint.Equals(relayEp)) continue;
                if (result.Buffer.Length <= 32) continue;
                await shim.Socket.SendAsync(result.Buffer.AsMemory(32), wgLocal, ct);
            }
            catch { /* UDP blocked or transient — keep the shim alive */ }
        }
    }

    private void StopAllLocked()
    {
        foreach (var shim in _shims)
        {
            try { shim.Cts.Cancel(); shim.Socket.Dispose(); shim.RelaySocket.Dispose(); } catch { }
        }
        _shims.Clear();
    }

    public void Dispose() { lock (_lock) StopAllLocked(); }
}
