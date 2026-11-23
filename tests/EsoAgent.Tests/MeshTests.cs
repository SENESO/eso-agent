using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

public class WgCryptoTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void X25519_CrossCheckedVector()
    {
        // Input pair from RFC 7748 (§5.2/§6.1 values). The expected output was
        // cross-checked against a second, independent implementation of the
        // RFC 7748 ladder (Python, written directly from the RFC pseudocode) —
        // both produce 6381401e…. The basepoint case below is the RFC's own
        // published §6.1 result.
        var scalar = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var u = Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        var expected = Hex("6381401e6cdb0d7f21b69a3ca4659c06334519717557654b7b9f42a38e27d124");
        Assert.Equal(expected, WgCrypto.X25519(scalar, u));
    }

    [Fact]
    public void X25519_Basepoint_GivesKnownPublicKey()
    {
        // Same scalar with u = basepoint 9 yields the RFC 7748 §6.1 public key.
        var priv = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        Assert.Equal(
            Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"),
            WgCrypto.PublicKeyFromPrivate(priv));
    }

    [Fact]
    public void KeyAgreement_BothSidesDeriveTheSameSecret()
    {
        var (privA, pubA) = WgCrypto.GenerateKeyPair();
        var (privB, pubB) = WgCrypto.GenerateKeyPair();
        var secretA = WgCrypto.X25519(Convert.FromBase64String(privA), Convert.FromBase64String(pubB));
        var secretB = WgCrypto.X25519(Convert.FromBase64String(privB), Convert.FromBase64String(pubA));
        Assert.Equal(secretA, secretB);
        // WireGuard key format: standard base64 of 32 bytes (44 chars, one pad).
        Assert.Equal(44, privA.Length);
        Assert.Equal(44, pubA.Length);
        Assert.EndsWith("=", pubA);
    }
}

public class IpAllocatorTests
{
    [Fact]
    public void AllocatesLowestFreeFromDot2()
    {
        Assert.Equal("10.77.0.2", IpAllocator.Allocate(Array.Empty<string>()));
        Assert.Equal("10.77.0.3", IpAllocator.Allocate(new[] { "10.77.0.2" }));
        Assert.Equal("10.77.0.2", IpAllocator.Allocate(new[] { "10.77.0.3" })); // fills the gap
        Assert.Equal("10.77.0.4", IpAllocator.Allocate(new[] { "10.77.0.2", "10.77.0.3" }));
    }

    [Fact]
    public void ServerAddress_IsReserved()
    {
        // .1 belongs to the server itself and is never handed out.
        var used = new List<string>();
        for (int i = 0; i < 5; i++) used.Add(IpAllocator.Allocate(used));
        Assert.DoesNotContain("10.77.0.1", used);
        Assert.Equal(5, used.Distinct().Count());
    }
}

public class WgConfigBuilderTests
{
    [Fact]
    public void BuildsInterfaceAndPeers_WithSubnetsAndRelayEndpoint()
    {
        var peers = new List<MeshPeerInfo>
        {
            new() { Code = "AAAA-1111", Name = "direct-peer", WgPublicKey = "PUB1", VirtualIp = "10.77.0.3", Endpoint = "192.168.1.5:51820", ApprovedSubnets = new() { "192.168.1.0/24" } },
            new() { Code = "BBBB-2222", Name = "relayed-peer", WgPublicKey = "PUB2", VirtualIp = "10.77.0.4", Relay = true },
        };
        var config = WgConfigBuilder.Build("PRIVKEY", "10.77.0.2", 51820, peers, relayShimBasePort: 51900);
        Assert.Contains("PrivateKey = PRIVKEY", config);
        Assert.Contains("Address = 10.77.0.2/32", config);
        Assert.Contains("ListenPort = 51820", config);
        Assert.Contains("PublicKey = PUB1", config);
        Assert.Contains("AllowedIPs = 10.77.0.3/32, 192.168.1.0/24", config);
        Assert.Contains("Endpoint = 192.168.1.5:51820", config);
        Assert.Contains("Endpoint = 127.0.0.1:51900", config); // relayed peer goes through the local shim
        Assert.Contains("PersistentKeepalive = 25", config);
    }
}

public class AclCompilerTests
{
    private static readonly AclCompiler.DeviceView A = new("AAAA-1111", "10.77.0.2", new[] { "servers" });
    private static readonly AclCompiler.DeviceView B = new("BBBB-2222", "10.77.0.3", Array.Empty<string>());
    private static readonly AclCompiler.DeviceView C = new("CCCC-3333", "10.77.0.4", new[] { "servers" });

    [Fact]
    public void DefaultPosture_OpensAgentAndP2pPorts_IcmpEcho_AndReturnTraffic()
    {
        // §27: the default posture carries what the mesh data plane itself
        // needs — the agent port, the P2P port, ICMP echo for diagnostics,
        // and return traffic for connections the device initiated (a bare
        // RST reply to an outbound SYN was dropped by the old plan's DROP).
        // The allow-list stays explicit: named ports only, mesh sources only.
        var plans = AclCompiler.Compile(new[] { A, B }, Array.Empty<AclRule>(), agentPort: 8737);
        var planB = plans["BBBB-2222"];
        Assert.Contains(planB.AllowInbound, r => r.FromCidr == "10.77.0.2/32" && r.Port == 8737 && r.Protocol == "tcp" && !r.ReturnTraffic);
        Assert.Contains(planB.AllowInbound, r => r.FromCidr == "10.77.0.2/32" && r.Port == 8742 && r.Protocol == "tcp" && !r.ReturnTraffic);
        Assert.Contains(planB.AllowInbound, r => r.FromCidr == "10.77.0.2/32" && r.Port == 8 && r.Protocol == "icmp");
        Assert.Contains(planB.AllowInbound, r => r.FromCidr == "10.77.0.2/32" && r.ReturnTraffic && r.PortRange == "49152-65535");
        // Nothing else: no other ports, no self-rules.
        Assert.DoesNotContain(planB.AllowInbound, r => r is { Protocol: "tcp", ReturnTraffic: false } && r.Port is not (8737 or 8742));
        Assert.DoesNotContain(planB.AllowInbound, r => r.Protocol != "tcp" && r.Protocol != "icmp");
        Assert.DoesNotContain(plans["AAAA-1111"].AllowInbound, r => r.FromCidr == "10.77.0.2/32");
    }

    [Fact]
    public void GroupRule_ExpandsToMembers_WithPorts()
    {
        var rules = new[] { new AclRule { From = "group:servers", To = "BBBB-2222", Ports = new() { 22, 3389 } } };
        var plans = AclCompiler.Compile(new[] { A, B, C }, rules, agentPort: 8737);
        var planB = plans["BBBB-2222"];
        Assert.Contains(planB.AllowInbound, r => r.FromCidr == "10.77.0.2/32" && r.Port == 22);
        Assert.Contains(planB.AllowInbound, r => r.FromCidr == "10.77.0.4/32" && r.Port == 3389);
        // The rule targets B only — A's plan gains nothing beyond the default.
        Assert.DoesNotContain(plans["AAAA-1111"].AllowInbound, r => r.Port == 22);
    }

    [Fact]
    public void EmptyPorts_MeansAnyPort()
    {
        var rules = new[] { new AclRule { From = "AAAA-1111", To = "BBBB-2222" } };
        var plans = AclCompiler.Compile(new[] { A, B }, rules, agentPort: 8737);
        Assert.Contains(plans["BBBB-2222"].AllowInbound, r => r.FromCidr == "10.77.0.2/32" && r.Port == 0);
    }

    [Fact]
    public void FirewallCommands_Shape()
    {
        var plan = new MeshFirewallPlan
        {
            DeviceCode = "BBBB-2222",
            AllowInbound = new() { new MeshFirewallRule { FromCidr = "10.77.0.2/32", Port = 8737 } },
        };
        var linux = MeshFirewallCommands.Linux(plan);
        Assert.Contains(linux, c => c.Contains("ESOAGENT_MESH"));
        Assert.Contains(linux, c => c.Any(a => a == "--dport"));
        var windows = MeshFirewallCommands.Windows(plan);
        Assert.Contains(windows, c => c.Any(a => a.StartsWith("name=EsoAgentMesh")));
        Assert.Contains(windows, c => c.Any(a => a == "remoteip=10.77.0.2/32"));
    }
}

public class HostsFileSyncTests
{
    [Fact]
    public void Merge_AddsBlock_Idempotently_AndRemovesWhenEmpty()
    {
        var existing = "127.0.0.1 localhost\n::1 localhost\n";
        var map = new Dictionary<string, string> { ["laptop"] = "10.77.0.2", ["server"] = "10.77.0.3" };
        var merged = HostsFileSync.Merge(existing, map);
        Assert.Contains("127.0.0.1 localhost", merged);
        Assert.Contains("10.77.0.2 laptop", merged);
        Assert.Contains(HostsFileSync.BeginMarker, merged);
        // Idempotent: merging the merged content with the same map changes nothing.
        Assert.Equal(merged, HostsFileSync.Merge(merged, map));
        // Updating the map replaces the block, doesn't append a second one.
        var updated = HostsFileSync.Merge(merged, new Dictionary<string, string> { ["laptop"] = "10.77.0.9" });
        Assert.Contains("10.77.0.9 laptop", updated);
        Assert.DoesNotContain("10.77.0.2 laptop", updated);
        Assert.Equal(1, updated.Split(HostsFileSync.BeginMarker).Length - 1);
        // Empty map removes the block entirely, keeping the owner's own lines.
        var cleared = HostsFileSync.Merge(merged, new Dictionary<string, string>());
        Assert.DoesNotContain("esoagent-mesh", cleared);
        Assert.Contains("127.0.0.1 localhost", cleared);
    }
}

public class RelayFramingTests
{
    [Fact]
    public void WrapParse_RoundTrips()
    {
        var src = Convert.FromBase64String(WgCrypto.GenerateKeyPair().PublicKey);
        var dst = Convert.FromBase64String(WgCrypto.GenerateKeyPair().PublicKey);
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = RelayFraming.Wrap(src, dst, payload);
        Assert.True(RelayFraming.TryParse(frame, out var srcB64, out var dstB64, out var parsed));
        Assert.Equal(Convert.ToBase64String(src), srcB64);
        Assert.Equal(Convert.ToBase64String(dst), dstB64);
        Assert.Equal(payload, parsed);
    }

    [Fact]
    public void RegistrationFrame_HasEmptyPayload()
    {
        var src = new byte[32]; var dst = new byte[32];
        var frame = RelayFraming.Wrap(src, dst, ReadOnlySpan<byte>.Empty);
        Assert.True(RelayFraming.TryParse(frame, out _, out _, out var payload));
        Assert.Empty(payload);
        Assert.False(RelayFraming.TryParse(new byte[10], out _, out _, out _)); // too short
    }
}
