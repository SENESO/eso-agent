using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Tunnel definitions (UI overhaul Phase 4): the App's Tunnels view and the
/// CLI both build a TunnelSpec, and a bad spec must fail at validation —
/// never as a half-bound listener. The live forwarding itself needs real
/// sockets on both ends and is proven on the fleet, not in this sandbox.
/// </summary>
public class TunnelSpecTests
{
    private static TunnelSpec Tcp() => new()
    {
        LocalPort = 13389,
        Device = "ABCD-1234",
        Target = new TunnelTarget { Host = "127.0.0.1", Port = 3389 },
    };

    [Fact]
    public void Validate_AcceptsTcpAndSocksSpecs()
    {
        Assert.Null(TunnelSpec.Validate(Tcp()));
        var socks = Tcp();
        socks.Socks = true;
        socks.Target = null;
        Assert.Null(TunnelSpec.Validate(socks));
    }

    [Fact]
    public void Validate_RejectsBadPortMissingDeviceAndMissingTarget()
    {
        var bad = Tcp(); bad.LocalPort = 0;
        Assert.NotNull(TunnelSpec.Validate(bad));
        bad = Tcp(); bad.Device = " ";
        Assert.NotNull(TunnelSpec.Validate(bad));
        bad = Tcp(); bad.Target = null;
        Assert.NotNull(TunnelSpec.Validate(bad));
    }

    [Fact]
    public void Validate_RejectsUdpSocks()
    {
        var spec = Tcp();
        spec.Socks = true;
        spec.Udp = true;
        spec.Target = null;
        Assert.NotNull(TunnelSpec.Validate(spec));
    }

    [Fact]
    public void Describe_NamesPortsTargetAndDevice()
    {
        Assert.Equal("TCP 127.0.0.1:13389 → 127.0.0.1:3389 via ABCD-1234", Tcp().Describe());
        var udp = Tcp(); udp.Udp = true;
        Assert.StartsWith("UDP ", udp.Describe());
    }
}
