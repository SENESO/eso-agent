using System.Net;
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
}
