using System.Security.Cryptography;
using System.Text.Json;
using EsoAgent.Core;
using EsoAgent.Server;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// The productization wiring around the P2P layer: the shipped config
/// defaults (bootstrap set with the box demoted to one-of-several), the
/// explicit-config-wins rule, and the state frame carrying the
/// approved-peer set that feeds the P2P trust gate.
/// </summary>
public sealed class P2pConfigTests
{
    [Fact]
    public void Defaults_P2pOn_BoxIsOneBootstrapOfSeveral()
    {
        var p2p = new AgentConfig().P2p;

        Assert.True(p2p.Enabled);
        Assert.Equal(P2pDefaults.ListenPort, p2p.ListenPort);
        Assert.True(p2p.Bootstrap.Count >= 2, "the bootstrap set must not be a single point");
        Assert.Contains(P2pDefaults.Bootstrap, b => b.Contains("100.98.165.106")); // the box, tailnet route
        Assert.Contains(P2pDefaults.Bootstrap, b => b.Contains("10.77.0.2"));       // the box, mesh route
    }

    [Fact]
    public void Load_ExplicitP2pSectionWins()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-p2p-cfg-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "esoagent.json"),
                """{ "p2p": { "enabled": false, "listenPort": 9999, "bootstrap": ["192.0.2.1:8742"] } }""");
            var cfg = AgentConfig.Load(dir);
            Assert.False(cfg.P2p.Enabled);
            Assert.Equal(9999, cfg.P2p.ListenPort);
            Assert.Equal(new[] { "192.0.2.1:8742" }, cfg.P2p.Bootstrap);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Load_NoP2pSection_KeepsDefaults()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-p2p-cfg-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "esoagent.json"), "{ \"port\": 9998 }");
            var cfg = AgentConfig.Load(dir);
            Assert.True(cfg.P2p.Enabled);
            Assert.Equal(P2pDefaults.ListenPort, cfg.P2p.ListenPort);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public sealed class P2pStateFrameTests
{
    private static FleetServerState NewState()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-p2p-frame-" + Guid.NewGuid().ToString("N"))).FullName;
        return new FleetServerState
        {
            Config = new AgentConfig(),
            Registry = new FleetRegistry(dir),
            Audit = new AuditLog(Path.Combine(dir, "audit.log")),
            OwnerAuth = new OwnerAuthStore(),
            PanelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256),
        };
    }

    private static DeviceRecord Hello(FleetServerState state, string code, string name, string publicKey)
    {
        var record = state.Registry.UpsertHello(new FleetHelloFrame
        {
            Code = code,
            Name = name,
            Os = "test",
            Version = "test",
            PublicKey = publicKey,
        });
        Assert.NotNull(record);
        return record!;
    }

    [Fact]
    public void ApprovedFrame_CarriesTheOtherApprovedDevices_Only()
    {
        var state = NewState();
        var keyA = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var keyB = Convert.ToBase64String(new byte[] { 4, 5, 6 });
        var keyC = Convert.ToBase64String(new byte[] { 7, 8, 9 });
        var a = Hello(state, "AAAA-0001", "alpha", keyA);
        var b = Hello(state, "BBBB-0002", "bravo", keyB);
        var c = Hello(state, "CCCC-0003", "charlie", keyC);
        state.Registry.SetState(a.Code, "Approved");
        state.Registry.SetState(b.Code, "Approved");
        // charlie stays Pending.

        var frame = FleetConnectEndpoint.BuildStateFrame(state, a);
        Assert.Equal("Approved", frame.State);
        Assert.NotNull(frame.PanelPublicKey); // signers ride along as before
        var peers = Assert.Single(frame.ApprovedPeers);
        Assert.Equal(b.Code, peers.Code);
        Assert.Equal(keyB, peers.PublicKey);

        // A Pending device's frame carries no peer set at all — discovery
        // rights are earned at approval, same as everything else.
        var pendingFrame = FleetConnectEndpoint.BuildStateFrame(state, c);
        Assert.Equal("Pending", pendingFrame.State);
        Assert.Empty(pendingFrame.ApprovedPeers);
        Assert.Null(pendingFrame.PanelPublicKey);
    }

    [Fact]
    public void StateFrame_OldShape_StillDeserializes()
    {
        // Frames from an older server (no ApprovedPeers field) must load with
        // an empty set: the device simply holds no P2P sessions until the
        // server is updated — honest degradation, never a crash.
        var json = """{"state":"Approved","deviceCode":"ABCD-EFGH"}""";
        var frame = JsonSerializer.Deserialize<FleetStateFrame>(json, EsoJson.Options);
        Assert.NotNull(frame);
        Assert.NotNull(frame!.ApprovedPeers);
        Assert.Empty(frame.ApprovedPeers);
    }
}
