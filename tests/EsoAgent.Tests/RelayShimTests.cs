using System.Net;
using System.Net.Sockets;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Relay shim socket topology. Regression coverage for the defect where the
/// shim ran both sides over one loopback-bound UDP socket: a socket bound to
/// 127.0.0.1 cannot send to a remote relay endpoint, so relayed WireGuard
/// traffic silently never left the device whenever the relay was not on the
/// same host. These tests assert the bindings only (no UDP traffic — the
/// sandbox blocks sendto outright), which is exactly the property that was
/// broken.
/// </summary>
public class RelayShimTests
{
    private static string PubKey(byte fill) =>
        Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());

    private static MeshPeerInfo RelayedPeer(byte fill) => new()
    {
        Code = "PEER1",
        Name = "peer",
        WgPublicKey = PubKey(fill),
        VirtualIp = "10.77.0.9",
        Relay = true,
    };

    [Fact]
    public void ShimSockets_RelayFacingIsWildcard_NotLoopback()
    {
        using var shims = new RelayShimSet();
        shims.Sync(
            new[] { RelayedPeer(7) },
            "127.0.0.1:51821",
            PubKey(3),
            wgListenPort: 51920,
            basePort: 51931);

        Assert.Equal("1 relay shim(s) active", shims.Status);
        var endpoints = shims.ShimSocketEndpoints;
        var (wgFacing, relayFacing) = Assert.Single(endpoints);

        // WG-facing: loopback on the configured base port, as the generated
        // WireGuard config expects.
        Assert.Equal(IPAddress.Loopback, wgFacing.Address);
        Assert.Equal(51931, wgFacing.Port);

        // Relay-facing: wildcard bind with an ephemeral port. A loopback
        // binding here is the original defect — it cannot reach a relay on
        // any other host.
        Assert.Equal(IPAddress.Any, relayFacing.Address);
        Assert.NotEqual(IPAddress.Loopback, relayFacing.Address);
        Assert.NotEqual(0, relayFacing.Port);
    }

    [Fact]
    public void ShimSockets_TwoPeers_GetSequentialLoopbackPorts_AndSeparateRelaySockets()
    {
        using var shims = new RelayShimSet();
        shims.Sync(
            new[] { RelayedPeer(7), RelayedPeer(9) },
            "127.0.0.1:51821",
            PubKey(3),
            wgListenPort: 51920,
            basePort: 51941);

        Assert.Equal("2 relay shim(s) active", shims.Status);
        var endpoints = shims.ShimSocketEndpoints;
        Assert.Equal(2, endpoints.Count);
        Assert.Equal(51941, endpoints[0].WgFacing.Port);
        Assert.Equal(51942, endpoints[1].WgFacing.Port);
        Assert.All(endpoints, e => Assert.Equal(IPAddress.Any, e.RelayFacing.Address));
        Assert.NotEqual(endpoints[0].RelayFacing.Port, endpoints[1].RelayFacing.Port);
    }

    [Fact]
    public void MeshOptions_DefaultShimBasePort_ClearsTheServerRelayPort()
    {
        // The fleet server's UDP relay binds 51821 by default; the shim base
        // port must not equal it, or a co-hosted agent's loopback shim bind
        // collides with the relay's wildcard bind ("Address already in use").
        Assert.Equal(51831, new MeshOptions().RelayShimBasePort);
    }

    /// <summary>
    /// Idempotent Sync (field defect 2026-11-21): re-applying the same mesh
    /// state must NOT tear the shim sockets down. The old Sync disposed and
    /// rebound every shim on every frame, opening a window in which the
    /// engine's packets to the loopback port hit a closed socket — on
    /// Windows the ICMP Port Unreachable kills the engine's IPv4 receive
    /// routine outright (WSAECONNRESET). The observable proof that a shim
    /// survived is its relay-facing socket: a recreated shim gets a fresh
    /// ephemeral port, a kept shim keeps its port.
    /// </summary>
    [Fact]
    public void Sync_TwiceWithSamePeers_KeepsSockets()
    {
        using var shims = new RelayShimSet();
        var peers = new[] { RelayedPeer(7) };
        shims.Sync(peers, "127.0.0.1:51821", PubKey(3), wgListenPort: 51920, basePort: 51961);
        var first = Assert.Single(shims.ShimSocketEndpoints);

        shims.Sync(peers, "127.0.0.1:51821", PubKey(3), wgListenPort: 51920, basePort: 51961);
        Assert.Equal("1 relay shim(s) active", shims.Status);
        var second = Assert.Single(shims.ShimSocketEndpoints);

        Assert.Equal(first.WgFacing, second.WgFacing);
        Assert.Equal(first.RelayFacing, second.RelayFacing); // same ephemeral port = same socket kept
    }

    [Fact]
    public void Sync_PeerRemoved_DropsOnlyItsShim()
    {
        using var shims = new RelayShimSet();
        shims.Sync(new[] { RelayedPeer(7), RelayedPeer(9) }, "127.0.0.1:51821", PubKey(3), wgListenPort: 51920, basePort: 51971);
        var before = shims.ShimSocketEndpoints;
        Assert.Equal(2, before.Count);

        shims.Sync(new[] { RelayedPeer(7) }, "127.0.0.1:51821", PubKey(3), wgListenPort: 51920, basePort: 51971);
        var after = Assert.Single(shims.ShimSocketEndpoints);
        Assert.Equal(before[0], after); // the surviving peer's shim is untouched
    }

    [Fact]
    public void Sync_PeerAdded_KeepsExistingShim()
    {
        using var shims = new RelayShimSet();
        shims.Sync(new[] { RelayedPeer(7) }, "127.0.0.1:51821", PubKey(3), wgListenPort: 51920, basePort: 51981);
        var first = Assert.Single(shims.ShimSocketEndpoints);

        shims.Sync(new[] { RelayedPeer(7), RelayedPeer(9) }, "127.0.0.1:51821", PubKey(3), wgListenPort: 51920, basePort: 51981);
        var endpoints = shims.ShimSocketEndpoints;
        Assert.Equal(2, endpoints.Count);
        Assert.Contains(first, endpoints); // the first shim survived the add
        Assert.Equal(51981, endpoints[0].WgFacing.Port);
        Assert.Equal(51982, endpoints[1].WgFacing.Port);
    }

    [Fact]
    public void Sync_RelayEndpointChanged_RecreatesShim()
    {
        using var shims = new RelayShimSet();
        shims.Sync(new[] { RelayedPeer(7) }, "127.0.0.1:51821", PubKey(3), wgListenPort: 51920, basePort: 51991);
        var first = Assert.Single(shims.ShimSocketEndpoints);

        // Same peer, different relay: the old shim is useless (it forwards to
        // the wrong relay) and must be replaced.
        shims.Sync(new[] { RelayedPeer(7) }, "127.0.0.1:51822", PubKey(3), wgListenPort: 51920, basePort: 51991);
        var second = Assert.Single(shims.ShimSocketEndpoints);
        Assert.Equal(51991, second.WgFacing.Port);
        Assert.NotEqual(first.RelayFacing, second.RelayFacing); // fresh socket, fresh ephemeral port
    }

    // ---- Loop resilience (v1.2.8; field defect 2026-11-21, shim-fix
    // report §10.2). The WG loop died permanently on its first receive
    // exception: the relay loop delivers to the engine's port over the WG
    // loop's own receive socket, so a delivery landing before the
    // engine's bind earned the socket an ICMP Port Unreachable and the
    // pending receive threw WSAECONNRESET — the loop broke, forwarding
    // stopped forever, and the idempotent Sync kept the dead shim. The
    // loops now treat a receive error as a lost packet, and Sync
    // restarts a loop that ever does exit.

    [Fact]
    public void ReceiveErrorPolicy_SocketErrorsAreLostPackets_NotDeath()
    {
        // WSAECONNRESET above all: the packet is lost, the socket is fine.
        Assert.True(RelayShimSet.ShouldContinueAfterReceiveError(
            new SocketException((int)SocketError.ConnectionReset), cancellationRequested: false));
        Assert.True(RelayShimSet.ShouldContinueAfterReceiveError(
            new SocketException((int)SocketError.TimedOut), cancellationRequested: false));
        Assert.True(RelayShimSet.ShouldContinueAfterReceiveError(
            new SocketException((int)SocketError.NetworkUnreachable), cancellationRequested: false));
    }

    [Fact]
    public void ReceiveErrorPolicy_CancellationAndDisposalEndTheLoop()
    {
        Assert.False(RelayShimSet.ShouldContinueAfterReceiveError(
            new OperationCanceledException(), cancellationRequested: false));
        Assert.False(RelayShimSet.ShouldContinueAfterReceiveError(
            new TaskCanceledException(), cancellationRequested: false));
        Assert.False(RelayShimSet.ShouldContinueAfterReceiveError(
            new ObjectDisposedException("socket"), cancellationRequested: false));
        // Even a plain socket error ends the loop once teardown cancelled it.
        Assert.False(RelayShimSet.ShouldContinueAfterReceiveError(
            new SocketException((int)SocketError.ConnectionReset), cancellationRequested: true));
    }

    [Fact]
    public void LoopRestartDecision_CoversTaskStates()
    {
        Assert.True(RelayShimSet.LoopNeedsRestart(null));
        Assert.True(RelayShimSet.LoopNeedsRestart(Task.CompletedTask));
        Assert.True(RelayShimSet.LoopNeedsRestart(Task.FromException(new InvalidOperationException("boom"))));
        Assert.True(RelayShimSet.LoopNeedsRestart(Task.FromCanceled(new CancellationToken(canceled: true))));
        var running = Task.Delay(TimeSpan.FromSeconds(30));
        Assert.False(RelayShimSet.LoopNeedsRestart(running));
    }

    [Fact]
    public void Sync_TwiceWithSamePeers_KeepsLoopTasks()
    {
        using var shims = new RelayShimSet();
        var peers = new[] { RelayedPeer(7) };
        shims.Sync(peers, "127.0.0.1:51821", PubKey(3), wgListenPort: 51920, basePort: 52001);
        var first = Assert.Single(shims.ShimLoopTasks);
        Assert.NotNull(first.WgLoop);
        Assert.NotNull(first.RelayLoop);
        Assert.False(first.WgLoop!.IsCompleted);
        Assert.False(first.RelayLoop!.IsCompleted);

        // A re-Sync restarts only DEAD loops; live ones keep their tasks.
        shims.Sync(peers, "127.0.0.1:51821", PubKey(3), wgListenPort: 51920, basePort: 52001);
        var second = Assert.Single(shims.ShimLoopTasks);
        Assert.Same(first.WgLoop, second.WgLoop);
        Assert.Same(first.RelayLoop, second.RelayLoop);
    }
}
