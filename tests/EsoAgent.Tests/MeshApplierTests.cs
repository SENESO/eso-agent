using System.Net;
using System.Net.Sockets;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Mesh applier Windows core: shims-before-engine ordering and the
/// dead-UDP-receiver recovery (field defect 2026-11-21 — see
/// MeshApplier.ApplyWindowsCoreAsync).
///
/// The ordering proof is a real in-process observation, not an assertion
/// about code layout: the fake UAPI transport tries to bind the shim's
/// loopback port at the moment the engine is first contacted. If the shim
/// was synced first (the fix), that bind fails with "address in use" —
/// the shim already holds the port. Under the old order the bind would
/// succeed, because the engine was configured before any shim existed.
///
/// NOTE: the applier owns its RelayShimSet for the life of the test object
/// and MeshApplier has no Dispose, so each test uses its own shim base
/// port; the sockets stay bound until the test process exits.
/// </summary>
public class MeshApplierTests
{
    private const string PrivB64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string PeerB64 = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=";

    /// <summary>UAPI get response with the dead-receiver signature: the engine
    /// has transmitted 32708 B, received 0 B, and never handshook.</summary>
    private const string DeadReceiverGetResponse =
        "listen_port=51820\n" +
        "public_key=202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f\n" +
        "endpoint=127.0.0.1:51900\n" +
        "last_handshake_time_sec=0\n" +
        "tx_bytes=32708\nrx_bytes=0\n" +
        "persistent_keepalive_interval=25\n" +
        "allowed_ip=10.77.0.3/32\n" +
        "errno=0\n\n";

    private static FleetMeshFrame Frame(string relayEndpoint = "127.0.0.1:51821") => new()
    {
        YourVirtualIp = "10.77.0.3",
        Peers = new()
        {
            new MeshPeerInfo { Code = "PEER1", Name = "peer", WgPublicKey = PeerB64, VirtualIp = "10.77.0.2", Relay = true },
        },
        ServerRelayEndpoint = relayEndpoint,
    };

    private sealed class Rig : IDisposable
    {
        public string Dir { get; }
        public AgentState State { get; }
        public RecordingRunner Runner { get; } = new();
        public FakeUapiTransport Transport { get; } = new();
        public FakeEngineProcess Process { get; }
        public string FakeEnginePath { get; }

        public Rig(int shimBasePort, bool transportAlive)
        {
            Dir = Path.Combine(Path.GetTempPath(), "esoagent-meshapplier-" + Guid.NewGuid().ToString("N"));
            var cfg = AgentConfig.Load(Dir);
            cfg.Mesh.Enabled = true;
            cfg.Mesh.ListenPort = 51820;
            cfg.Mesh.RelayShimBasePort = shimBasePort;
            var platform = new PlatformServices
            {
                ProcessRunner = Runner,
                SystemInfo = new StubSystemInfo(),
                PowerControl = new StubPowerControl(),
                Screenshot = new StubScreenshot(),
                InteractiveExec = new StubInteractiveExec(),
                Packages = new StubPackages(),
                Services = new StubServices(),
                Repair = new StubRepair(),
            };
            State = new AgentState
            {
                Config = cfg,
                Token = Array.Empty<byte>(),
                Audit = new AuditLog(cfg.ResolvePath(cfg.AuditLogFile)),
                KillSwitch = new KillSwitch(cfg.ResolvePath(cfg.LockFile)),
                Platform = platform,
                Files = new FileTransferService(),
                Update = new UpdateService(cfg, Runner),
            };
            Transport.Alive = transportAlive;
            Process = new FakeEngineProcess { LinkedTransport = Transport };
            // A real file the supervisor's File.Exists check accepts; never
            // executed (the process factory is fake), and no real process is
            // ever named after it, so the adopted-engine kill path is inert.
            FakeEnginePath = Path.Combine(Dir, "fake-engine.exe");
            File.WriteAllText(FakeEnginePath, "not a real engine");
        }

        public MeshEngineSupervisor BuildSupervisor(IUapiTransport transport)
            => new(
                enginePath: FakeEnginePath,
                runner: Runner,
                audit: (_, _) => { },
                processFactory: () => Process,
                transportFactory: () => transport,
                healthInterval: TimeSpan.FromHours(1),
                startupTimeout: TimeSpan.FromSeconds(5));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    /// <summary>A UAPI transport that proves, at the moment the engine is
    /// first contacted, whether the shim's loopback port is already bound.</summary>
    private sealed class OrderingTransport : IUapiTransport
    {
        private readonly FakeUapiTransport _inner;
        private readonly int _shimPort;
        public bool ShimBoundAtFirstContact { get; private set; }
        public bool Checked { get; private set; }

        public OrderingTransport(FakeUapiTransport inner, int shimPort)
        {
            _inner = inner;
            _shimPort = shimPort;
        }

        public Task<string> RequestAsync(string request, CancellationToken ct = default)
        {
            if (!Checked)
            {
                Checked = true;
                try
                {
                    using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, _shimPort));
                    ShimBoundAtFirstContact = false; // the port was free: no shim yet
                }
                catch (SocketException)
                {
                    ShimBoundAtFirstContact = true; // "address in use": the shim holds it
                }
            }
            return _inner.RequestAsync(request, ct);
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task ApplyWindowsCore_SyncsShimsBeforeEngineIsConfigured()
    {
        using var rig = new Rig(shimBasePort: 52031, transportAlive: false);
        var ordering = new OrderingTransport(rig.Transport, 52031);
        var supervisor = rig.BuildSupervisor(ordering);
        var applier = new MeshApplier(rig.State, supervisor);
        var frame = Frame();
        var wg = WgIdentity.LoadOrCreate(rig.Dir);
        var model = WgConfigBuilder.BuildModel(wg.PrivateKeyB64, "10.77.0.3", 51820, frame.Peers, 52031);

        var notes = await applier.ApplyWindowsCoreAsync(model, frame, wg);

        Assert.Contains("relay: 1 relay shim(s) active", notes);
        Assert.True(ordering.Checked);
        Assert.True(ordering.ShimBoundAtFirstContact,
            "the engine was contacted before the shim's loopback port was bound — the WSAECONNRESET race is back");
        Assert.Equal(1, rig.Process.Starts); // engine started once, no recovery restart
        Assert.Single(rig.Transport.SetRequests);
        supervisor.Dispose();
    }

    /// <summary>
    /// Dead-receiver recovery, driven as two applies: the first starts the
    /// engine healthy; the second finds it transmitting without ever having
    /// received or handshook (the field signature) and restarts it once.
    /// A third apply proves the 10-minute throttle: no second restart.
    /// </summary>
    [Fact]
    public async Task ApplyWindowsCore_DeadReceiverSignature_RestartsEngineOnce()
    {
        using var rig = new Rig(shimBasePort: 52041, transportAlive: false);
        var supervisor = rig.BuildSupervisor(rig.Transport);
        var applier = new MeshApplier(rig.State, supervisor);
        var frame = Frame();
        var wg = WgIdentity.LoadOrCreate(rig.Dir);
        var model = WgConfigBuilder.BuildModel(wg.PrivateKeyB64, "10.77.0.3", 51820, frame.Peers, 52041);

        var first = await applier.ApplyWindowsCoreAsync(model, frame, wg);
        Assert.Equal(1, rig.Process.Starts);
        Assert.Single(rig.Transport.SetRequests);
        Assert.DoesNotContain("dead UDP receiver", first);

        rig.Transport.GetResponse = DeadReceiverGetResponse;
        var second = await applier.ApplyWindowsCoreAsync(model, frame, wg);
        Assert.Equal(2, rig.Process.Starts); // the recovery restart
        Assert.Equal(1, rig.Process.Stops);
        Assert.Equal(3, rig.Transport.SetRequests.Count); // normal set + re-push after the restart
        Assert.Contains("engine restarted to recover its dead UDP receiver", second);

        var third = await applier.ApplyWindowsCoreAsync(model, frame, wg);
        Assert.Equal(2, rig.Process.Starts); // throttled: no second restart
        Assert.Equal(4, rig.Transport.SetRequests.Count); // only the normal set
        Assert.DoesNotContain("dead UDP receiver", third);
        supervisor.Dispose();
    }

    [Fact]
    public async Task ApplyWindowsCore_HealthyAdoptedEngine_NotRestarted()
    {
        using var rig = new Rig(shimBasePort: 52051, transportAlive: true);
        // Default get response: handshake done, rx > 0 — healthy.
        var supervisor = rig.BuildSupervisor(rig.Transport);
        var applier = new MeshApplier(rig.State, supervisor);
        var frame = Frame();
        var wg = WgIdentity.LoadOrCreate(rig.Dir);
        var model = WgConfigBuilder.BuildModel(wg.PrivateKeyB64, "10.77.0.3", 51820, frame.Peers, 52051);

        var notes = await applier.ApplyWindowsCoreAsync(model, frame, wg);

        Assert.True(supervisor.Adopted);
        Assert.Equal(0, rig.Process.Starts); // no restart for a healthy engine
        Assert.Single(rig.Transport.SetRequests);
        Assert.DoesNotContain("dead UDP receiver", notes);
        supervisor.Dispose();
    }

    [Theory]
    [InlineData(32708, 0, 0, true)]   // transmitting, never received, never handshook: dead receiver
    [InlineData(1360, 1188, 1795167375, false)] // healthy
    [InlineData(0, 0, 0, false)]       // never transmitted: fresh peer, not a dead receiver
    [InlineData(100, 0, 1795167375, false)] // handshook once: receiver works
    public void DeadReceiverSignature_ClassifiesCorrectly(long tx, long rx, long handshake, bool expected)
    {
        var status = new UapiDeviceStatus
        {
            Errno = 0,
            ListenPort = 51820,
            Peers = { new UapiPeerStatus { TxBytes = tx, RxBytes = rx, LastHandshakeUnixSec = handshake } },
        };
        Assert.Equal(expected, MeshApplier.DeadReceiverSignature(status));
        Assert.False(MeshApplier.DeadReceiverSignature(null));
        Assert.False(MeshApplier.DeadReceiverSignature(new UapiDeviceStatus { Errno = 5 }));
    }

    // Minimal platform stubs: ApplyWindowsCoreAsync only ever calls the
    // process runner (addressing commands); the rest are never touched.
    private sealed class StubSystemInfo : ISystemInfo
    {
        public (long? Used, long? Total) GetMemory() => (null, null);
        public Task<double?> GetCpuPercentAsync(CancellationToken ct = default) => Task.FromResult<double?>(null);
        public BatteryDto? GetBattery() => null;
        public IReadOnlyList<string> GetLoggedOnUsers() => Array.Empty<string>();
    }

    private sealed class StubPowerControl : IPowerControl
    {
        public Task<OperationResult> RebootAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> ShutdownAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> SleepAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubScreenshot : IScreenshotProvider
    {
        public Task<(byte[]? Png, string? Error)> CaptureAsync(CancellationToken ct = default)
            => Task.FromResult<(byte[]?, string?)>((null, "not available in tests"));
    }

    private sealed class StubInteractiveExec : IInteractiveExec
    {
        public Task<ExecResponse> RunInUserSessionAsync(ExecRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubPackages : IPackageManager
    {
        public string ManagerName => "test";
        public Task<IReadOnlyList<SoftwarePackageDto>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SoftwarePackageDto>>(Array.Empty<SoftwarePackageDto>());
        public Task<OperationResult> InstallAsync(InstallRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> UninstallAsync(UninstallRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> UpgradeAsync(UpgradeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubServices : IServiceControl
    {
        public Task<IReadOnlyList<ServiceDto>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ServiceDto>>(Array.Empty<ServiceDto>());
        public Task<OperationResult> StartAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> StopAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> RestartAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OperationResult> SetStartupModeAsync(string name, string mode, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubRepair : ISystemRepair
    {
        public IReadOnlyList<RepairInfoDto> Available => Array.Empty<RepairInfoDto>();
        public Task<OperationResult> RunAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
