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
///
/// Sync is IDEMPOTENT: a shim whose peer key, loopback port and relay
/// endpoint are unchanged is kept as-is (same sockets, same loops) instead
/// of being torn down and rebound. Rebinding on every mesh frame used to
/// open a window in which the loopback port had no listener while the
/// engine kept sending — and on Windows that window is fatal: the
/// engine's first packet to the closed port earns an ICMP Port Unreachable,
/// and wireguard-go's IPv4 receive routine exits on the resulting
/// WSAECONNRESET and never comes back (see MeshApplier). Keeping live
/// shims across re-applies closes that window.
///
/// The loops themselves are equally defensive (field defect 2026-11-21,
/// one layer up from the engine bug): the relay loop delivers to the
/// engine's port over the SAME loopback socket the WG loop receives on,
/// so a delivery that lands before the engine's bind earns the socket an
/// ICMP Port Unreachable and the WG loop's pending receive throws
/// WSAECONNRESET. The loops therefore (a) disable SIO_UDP_CONNRESET on
/// Windows so the poison never surfaces, (b) treat a receive error as a
/// lost packet, not a death — the loop pauses briefly and receives
/// again — and (c) are restarted by Sync if one ever does exit, so a
/// dead loop self-heals on the next mesh frame instead of silently
/// stranding a direction forever.
/// </summary>
public sealed class RelayShimSet : IDisposable
{
    private sealed class Shim
    {
        public required string PeerKeyB64;
        public required UdpClient Socket;
        public required UdpClient RelaySocket;
        public required CancellationTokenSource Cts;
        /// <summary>Relay endpoint this shim forwards to (part of the keep/match key).</summary>
        public required IPEndPoint RelayEndpoint;
        /// <summary>Loopback port of the WG-facing socket (basePort + peer index).</summary>
        public required int WgPort;
        /// <summary>The WG→relay loop task; Sync restarts it if it ever completes.</summary>
        public Task? WgLoopTask;
        /// <summary>The relay→WG loop task; Sync restarts it if it ever completes.</summary>
        public Task? RelayLoopTask;
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

    /// <summary>
    /// Diagnostics/test seam: the two loop tasks of each live shim. A kept
    /// shim must keep the SAME task objects across a re-Sync while its
    /// loops are alive; a completed task is how Sync knows a loop died
    /// and must be restarted.
    /// </summary>
    internal IReadOnlyList<(Task? WgLoop, Task? RelayLoop)> ShimLoopTasks
    {
        get
        {
            lock (_lock)
            {
                return _shims.Select(s => (s.WgLoopTask, s.RelayLoopTask)).ToList();
            }
        }
    }

    /// <summary>
    /// Reconciles the live shims with the desired relayed peers. Shims whose
    /// peer key, loopback port and relay endpoint are unchanged are kept
    /// (their sockets stay bound and their loops keep running); only missing
    /// or changed shims are created, and shims for departed peers are
    /// disposed. A full teardown on every call used to unbind the loopback
    /// port while the engine was still sending to it — on Windows the
    /// resulting ICMP Port Unreachable kills the engine's IPv4 receive
    /// routine (WSAECONNRESET), so re-applies must not disturb live shims.
    /// </summary>
    public void Sync(IReadOnlyList<MeshPeerInfo> peers, string? serverRelayEndpoint, string ownPubKeyB64, int wgListenPort, int basePort)
    {
        lock (_lock)
        {
            var relayed = peers.Where(p => p.Relay).ToList();
            if (relayed.Count == 0)
            {
                StopAllLocked();
                Status = "no relayed peers";
                return;
            }
            if (string.IsNullOrWhiteSpace(serverRelayEndpoint))
            {
                StopAllLocked();
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
            catch (Exception ex) { StopAllLocked(); Status = $"bad relay endpoint: {ex.Message}"; return; }

            var ownPub = Convert.FromBase64String(ownPubKeyB64);
            var desired = relayed.Select((peer, i) => (Peer: peer, WgPort: basePort + i)).ToList();
            var kept = new List<Shim>();
            foreach (var shim in _shims)
            {
                var match = desired.FirstOrDefault(d =>
                    d.Peer.WgPublicKey == shim.PeerKeyB64 &&
                    d.WgPort == shim.WgPort &&
                    relayEp.Equals(shim.RelayEndpoint));
                if (match.Peer != null && !kept.Contains(shim))
                {
                    kept.Add(shim); // unchanged: keep the live sockets and loops
                    // …but a loop that died anyway (its receive error was
                    // fatal before the loops became resilient, or the
                    // socket broke under it) is restarted in place — the
                    // sockets stay bound, only the dead direction resumes.
                    EnsureLoopsRunningLocked(shim, relayEp, ownPub, wgListenPort);
                }
                else
                {
                    try { shim.Cts.Cancel(); shim.Socket.Dispose(); shim.RelaySocket.Dispose(); } catch { }
                }
            }
            _shims.Clear();
            _shims.AddRange(kept);
            foreach (var (peer, wgPort) in desired)
            {
                if (_shims.Any(s => s.PeerKeyB64 == peer.WgPublicKey && s.WgPort == wgPort && relayEp.Equals(s.RelayEndpoint)))
                    continue; // already kept
                UdpClient? socket = null;
                UdpClient? relaySocket = null;
                try
                {
                    socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, wgPort));
                    relaySocket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    DisableUdpConnReset(socket);
                    DisableUdpConnReset(relaySocket);
                    var cts = new CancellationTokenSource();
                    var shim = new Shim { PeerKeyB64 = peer.WgPublicKey, Socket = socket, RelaySocket = relaySocket, Cts = cts, RelayEndpoint = relayEp, WgPort = wgPort };
                    _shims.Add(shim);
                    EnsureLoopsRunningLocked(shim, relayEp, ownPub, wgListenPort);
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
    /// Starts whichever of the shim's loops is not running. Called when a
    /// shim is created and again on every Sync that keeps it, so a loop
    /// that exited is restarted in place (same sockets, same bindings).
    /// Caller must hold <see cref="_lock"/>.
    /// </summary>
    private void EnsureLoopsRunningLocked(Shim shim, IPEndPoint relayEp, byte[] ownPub, int wgListenPort)
    {
        if (LoopNeedsRestart(shim.WgLoopTask))
            shim.WgLoopTask = RunWgLoopAsync(shim, relayEp, ownPub, shim.Cts.Token);
        if (LoopNeedsRestart(shim.RelayLoopTask))
            shim.RelayLoopTask = RunRelayLoopAsync(shim, relayEp, wgListenPort, shim.Cts.Token);
    }

    /// <summary>A loop needs (re)starting when it was never started or has run to completion for any reason.</summary>
    internal static bool LoopNeedsRestart(Task? loopTask) => loopTask == null || loopTask.IsCompleted;

    /// <summary>
    /// Whether a shim loop should keep receiving after a receive error.
    /// A socket error — above all WSAECONNRESET, the ICMP Port Unreachable
    /// surfacing on a Windows UDP socket that recently sent to a closed
    /// port — costs one packet, not the loop: the socket stays usable.
    /// Cancellation and disposal are the loop's real ends (Sync tore the
    /// shim down, or the set is being disposed).
    /// </summary>
    internal static bool ShouldContinueAfterReceiveError(Exception ex, bool cancellationRequested)
    {
        if (cancellationRequested) return false;
        if (ex is OperationCanceledException) return false;
        if (ex is ObjectDisposedException) return false;
        return true;
    }

    /// <summary>Pause after a receive error before receiving again — keeps a persistently broken socket from spinning.</summary>
    private static readonly TimeSpan ReceiveErrorPause = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Windows: SIO_UDP_CONNRESET OFF, so a send to a not-yet-bound port
    /// does not poison this socket's next receive with WSAECONNRESET.
    /// This is the shim-side twin of the engine's problem (see
    /// MeshApplier): the relay loop sends to the engine's port over the
    /// WG loop's receive socket, and during the startup window that port
    /// may not be bound yet. Best-effort — the resilient loops above are
    /// the second line of defence where this is unavailable.
    /// </summary>
    private static void DisableUdpConnReset(UdpClient client)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SioUdpConnReset = -1744830452; // 0x9800000C
        try { client.Client.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null); } catch { }
    }

    /// <summary>
    /// WireGuard side: packets arriving on the loopback socket come from the
    /// local WireGuard — wrap them and send them to the relay over the
    /// relay-facing socket. Also sends the one-off registration frame (empty
    /// payload) so the relay learns our endpoint for our pubkey (re-sent on
    /// a restart, so the relay re-learns our endpoint then too).
    /// </summary>
    private async Task RunWgLoopAsync(Shim shim, IPEndPoint relayEp, byte[] ownPub, CancellationToken ct)
    {
        var peerPub = Convert.FromBase64String(shim.PeerKeyB64);
        try { await shim.RelaySocket.SendAsync(RelayFraming.Wrap(ownPub, peerPub, ReadOnlySpan<byte>.Empty), relayEp, ct); } catch { }
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await shim.Socket.ReceiveAsync(ct); }
            catch (Exception ex)
            {
                if (!ShouldContinueAfterReceiveError(ex, ct.IsCancellationRequested)) break;
                // Lost packet, live loop: pause briefly and receive again.
                try { await Task.Delay(ReceiveErrorPause, ct); } catch { break; }
                continue;
            }
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
            catch (Exception ex)
            {
                if (!ShouldContinueAfterReceiveError(ex, ct.IsCancellationRequested)) break;
                // Lost packet, live loop: pause briefly and receive again.
                try { await Task.Delay(ReceiveErrorPause, ct); } catch { break; }
                continue;
            }
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
