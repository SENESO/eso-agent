using System.Net.WebSockets;
using System.Security.Cryptography;
using EsoAgent.Core;
using EsoAgent.Server;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// State-frame resync (v1.2.8; field defect 2026-11-21, shim-fix report
/// §10.1). The connect-time state frame was the only one a device ever
/// received: when the fleet server woke from hibernation it accepted
/// the laptop's WebSocket but the frame never arrived, the device
/// looped on its 60 s "no state frame" self-heal, and only a manual
/// server restart brought the mesh up. The server now re-pushes the
/// frame to every connected device on a timer. These tests cover the
/// tick itself — the live delivery is proven on the fleet (a live
/// WebSocket pair cannot run in this sandbox).
/// </summary>
public class StateResyncTests
{
    private static FleetServerState NewState(string dir) => new()
    {
        Config = new AgentConfig(),
        Registry = new FleetRegistry(dir),
        Audit = new AuditLog(Path.Combine(dir, "audit.log")),
        OwnerAuth = new OwnerAuthStore(),
        PanelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256),
    };

    private static string TempDir()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-resync-" + Guid.NewGuid().ToString("N")));
        return dir.FullName;
    }

    [Fact]
    public async Task Resync_WithNoConnections_PushesToNobody()
    {
        var dir = TempDir();
        try
        {
            var state = NewState(dir);
            Assert.Equal(0, await StateFrameResyncService.ResyncOnceAsync(state));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Resync_UnknownCodeAndDeadSocket_AreSkippedWithoutThrowing()
    {
        var dir = TempDir();
        try
        {
            var state = NewState(dir);

            // A connection for a device the registry has never seen.
            state.Connections["ZZZZ-ZZZZ"] = new DeviceConnection
            {
                Code = "ZZZZ-ZZZZ",
                Socket = new ClientWebSocket(),
            };

            // A registered (Pending) device whose socket is dead (never
            // connected): the send fails — the tick must skip it and move
            // on, not throw.
            var record = state.Registry.UpsertHello(new FleetHelloFrame
            {
                Code = "TEST-DEV1",
                Name = "test device",
                PublicKey = Convert.ToBase64String(new byte[32]),
            });
            Assert.NotNull(record);
            state.Connections[record!.Code] = new DeviceConnection
            {
                Code = record.Code,
                Socket = new ClientWebSocket(),
            };

            Assert.Equal(0, await StateFrameResyncService.ResyncOnceAsync(state));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ResyncInterval_SitsInsideTheClientSelfHealWindow()
    {
        // The client drops a connection that produced no state frame
        // within 60 s (FleetClientService). The resync must land well
        // inside that window — and not so fast it spams connected
        // devices with frames.
        Assert.True(StateFrameResyncService.Interval < TimeSpan.FromSeconds(60),
            $"interval {StateFrameResyncService.Interval} must beat the client's 60 s window");
        Assert.True(StateFrameResyncService.Interval >= TimeSpan.FromSeconds(10),
            $"interval {StateFrameResyncService.Interval} is too chatty");
    }
}
