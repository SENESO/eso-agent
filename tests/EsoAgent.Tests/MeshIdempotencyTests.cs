using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

public class WgConfigEqualityTests
{
    private const string PrivB64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string PeerB64 = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=";
    private const string Peer2B64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private static WgInterfaceConfig Model() => new()
    {
        PrivateKeyB64 = PrivB64,
        VirtualIp = "10.77.0.2",
        ListenPort = 51820,
        Peers = new()
        {
            new WgPeerConfig { PublicKeyB64 = PeerB64, AllowedIps = new() { "10.77.0.3/32" }, Endpoint = "192.168.1.5:51820", PersistentKeepalive = 25 },
            new WgPeerConfig { PublicKeyB64 = Peer2B64, AllowedIps = new() { "10.77.0.4/32" }, Endpoint = null, PersistentKeepalive = 25 },
        },
    };

    [Fact]
    public void IdenticalModels_AreEqual()
    {
        Assert.True(WgConfigEquality.Equals(Model(), Model()));
        var m = Model();
        Assert.True(WgConfigEquality.Equals(m, m));
        Assert.True(WgConfigEquality.Equals(null, null));
        Assert.False(WgConfigEquality.Equals(Model(), null));
        Assert.False(WgConfigEquality.Equals(null, Model()));
    }

    [Fact]
    public void DisplayOnlyFields_DoNotAffectEquality()
    {
        var a = Model();
        var b = Model();
        b.Peers[0].Name = "renamed";
        b.Peers[0].Code = "ZZZZ-9999";
        Assert.True(WgConfigEquality.Equals(a, b)); // names/codes never reach the engine
    }

    [Fact]
    public void EngineVisibleChanges_BreakEquality()
    {
        var endpoint = Model(); endpoint.Peers[0].Endpoint = "192.168.1.6:51820";
        Assert.False(WgConfigEquality.Equals(Model(), endpoint));

        var allowed = Model(); allowed.Peers[0].AllowedIps.Add("192.168.1.0/24");
        Assert.False(WgConfigEquality.Equals(Model(), allowed));

        var keepalive = Model(); keepalive.Peers[0].PersistentKeepalive = 0;
        Assert.False(WgConfigEquality.Equals(Model(), keepalive));

        var key = Model(); key.Peers[0].PublicKeyB64 = Peer2B64;
        Assert.False(WgConfigEquality.Equals(Model(), key));

        var vip = Model(); vip.VirtualIp = "10.77.0.9";
        Assert.False(WgConfigEquality.Equals(Model(), vip));

        var port = Model(); port.ListenPort = 51821;
        Assert.False(WgConfigEquality.Equals(Model(), port));

        var reordered = Model(); reordered.Peers.Reverse(); // shim indices follow peer order
        Assert.False(WgConfigEquality.Equals(Model(), reordered));

        var removed = Model(); removed.Peers.RemoveAt(1);
        Assert.False(WgConfigEquality.Equals(Model(), removed));
    }
}

public class MeshApplierFingerprintTests
{
    private const string PrivB64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string PeerB64 = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=";

    private static FleetMeshFrame Frame(string endpoint = "192.168.1.5:51820", string relay = "127.0.0.1:51821") => new()
    {
        YourVirtualIp = "10.77.0.3",
        Peers = new()
        {
            new MeshPeerInfo { Code = "PEER1", Name = "peer", WgPublicKey = PeerB64, VirtualIp = "10.77.0.2", Endpoint = endpoint },
        },
        ServerRelayEndpoint = relay,
    };

    private static string Fingerprint(FleetMeshFrame frame)
    {
        var model = WgConfigBuilder.BuildModel(PrivB64, frame.YourVirtualIp, 51820, frame.Peers, 51821);
        return MeshApplier.ComputeFingerprint(model, frame, 51821);
    }

    [Fact]
    public void IdenticalFrames_FingerprintsMatch()
    {
        Assert.Equal(Fingerprint(Frame()), Fingerprint(Frame()));
    }

    [Fact]
    public void EffectiveChanges_ChangeTheFingerprint()
    {
        var baseline = Fingerprint(Frame());
        Assert.NotEqual(baseline, Fingerprint(Frame(endpoint: "192.168.1.6:51820")));
        Assert.NotEqual(baseline, Fingerprint(Frame(relay: "127.0.0.1:51999")));

        var withPeer = Frame();
        withPeer.Peers.Add(new MeshPeerInfo { Code = "PEER2", Name = "two", WgPublicKey = PrivB64, VirtualIp = "10.77.0.4", Endpoint = "192.168.1.7:51820" });
        Assert.NotEqual(baseline, Fingerprint(withPeer));

        var exit = Frame(); exit.YouAreExitNode = true;
        Assert.NotEqual(baseline, Fingerprint(exit));

        var fw = Frame();
        fw.Firewall = new MeshFirewallPlan
        {
            DeviceCode = "SELF",
            AllowInbound = new() { new MeshFirewallRule { FromCidr = "10.77.0.0/16", Port = 8737, Protocol = "tcp" } },
        };
        Assert.NotEqual(baseline, Fingerprint(fw));
    }

    [Fact]
    public void HostsOnlyChange_KeepsTheFingerprint()
    {
        // The hosts step is content-gated on its own; a new name in the
        // map must not rebuild the tunnel.
        var a = Frame();
        var b = Frame();
        b.Hosts["new-device"] = "10.77.0.9";
        Assert.Equal(Fingerprint(a), Fingerprint(b));
    }
}

/// <summary>
/// The Linux half of the gate, driven through the full ApplyAsync on
/// this (Linux) test host: wg-quick is absent here, so the tunnel step
/// converges as "not installed" — enough to record the fingerprint and
/// prove the second identical apply skips the tunnel step entirely,
/// while a changed frame applies again.
/// </summary>
public class MeshApplierLinuxGateTests
{
    private const string PeerB64 = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=";

    private sealed class Rig : IDisposable
    {
        public string Dir { get; }
        public AgentState State { get; }
        public RecordingRunner Runner { get; } = new();

        public Rig()
        {
            Dir = Path.Combine(Path.GetTempPath(), "esoagent-meshgate-" + Guid.NewGuid().ToString("N"));
            var cfg = AgentConfig.Load(Dir);
            cfg.Mesh.Enabled = true;
            cfg.Mesh.ListenPort = 51820;
            cfg.Mesh.RelayShimBasePort = 52071;
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
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static FleetMeshFrame Frame(string endpoint = "192.168.1.5:51820") => new()
    {
        YourVirtualIp = "10.77.0.3",
        Peers = new()
        {
            new MeshPeerInfo { Code = "PEER1", Name = "peer", WgPublicKey = PeerB64, VirtualIp = "10.77.0.2", Endpoint = endpoint },
        },
        ServerRelayEndpoint = "127.0.0.1:51821",
    };

    [Fact]
    public async Task ApplyAsync_IdenticalFrame_SkipsTunnelOnSecondApply_ChangedFrameAppliesAgain()
    {
        using var rig = new Rig();
        var applier = new MeshApplier(rig.State);

        await applier.ApplyAsync(Frame());
        Assert.Contains("tunnel:", applier.Status);
        Assert.DoesNotContain("configuration unchanged", applier.Status);
        Assert.True(File.Exists(Path.Combine(rig.Dir, "mesh.json")));

        await applier.ApplyAsync(Frame());
        Assert.Contains("tunnel: configuration unchanged — apply skipped", applier.Status);

        await applier.ApplyAsync(Frame(endpoint: "192.168.1.6:51820"));
        Assert.DoesNotContain("configuration unchanged", applier.Status);
    }

    // Minimal platform stubs (the Linux apply path only ever reaches the
    // process runner; the rest are never touched).
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

/// <summary>
/// Mesh apply idempotency (field defect 2026-11-22 evening, BUILD-REPORT
/// §23/§24). The fleet server re-pushes the mesh frame every 30 s (the
/// §15 resync — that stays). What an apply DID with an unchanged frame
/// was the defect: the supervisor pushed a full-replacement UAPI set
/// (replace_peers=true) on every tick, and wireguard-go answers a peer
/// replacement by destroying every peer's handshake state and byte
/// counters — the tunnel re-handshook forever and never carried
/// traffic (field audit: "0 handshake(s), tx 148 B / rx 0 B" on every
/// apply; 148 B is exactly one handshake initiation). These tests pin
/// the fix: an unchanged effective configuration is a no-op for the
/// engine; a changed one is applied exactly once; a replaced engine
/// (crash/restart, or a fresh agent with no local state — the §15
/// hibernate heal) is configured in full again.
/// </summary>
public class MeshEngineSupervisorIdempotencyTests
{
    private const string PrivB64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string PeerB64 = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=";

    private static WgInterfaceConfig Model(string endpoint = "192.168.1.5:51820") => new()
    {
        PrivateKeyB64 = PrivB64,
        VirtualIp = "10.77.0.2",
        ListenPort = 51820,
        Peers = new()
        {
            new WgPeerConfig { PublicKeyB64 = PeerB64, AllowedIps = new() { "10.77.0.3/32" }, Endpoint = endpoint, PersistentKeepalive = 25 },
        },
    };

    private sealed class Rig
    {
        public FakeUapiTransport Transport { get; } = new();
        public FakeEngineProcess Process { get; }
        public RecordingRunner Runner { get; } = new();
        public MeshEngineSupervisor Supervisor { get; }

        public Rig()
        {
            Transport.Alive = false; // spawn path: the process start brings the transport alive
            Process = new FakeEngineProcess { LinkedTransport = Transport };
            Supervisor = new MeshEngineSupervisor(
                enginePath: typeof(MeshEngineSupervisorIdempotencyTests).Assembly.Location,
                runner: Runner,
                audit: (_, _) => { },
                processFactory: () => Process,
                transportFactory: () => Transport,
                healthInterval: TimeSpan.FromHours(1),
                startupTimeout: TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RepeatedApply_SameModel_SetsEngineOnce()
    {
        var rig = new Rig();
        var first = await rig.Supervisor.ApplyAsync(Model());
        Assert.True(first.Ok, first.Summary);
        Assert.Single(rig.Transport.SetRequests);
        var commandsAfterFirst = rig.Runner.Commands.Count;

        for (var i = 0; i < 4; i++)
        {
            var again = await rig.Supervisor.ApplyAsync(Model());
            Assert.True(again.Ok, again.Summary);
            Assert.Contains("unchanged", again.Summary);
        }

        Assert.Single(rig.Transport.SetRequests); // zero reconfigurations across the re-applies
        Assert.Equal(1, rig.Process.Starts);
        Assert.Equal(commandsAfterFirst, rig.Runner.Commands.Count); // addressing not re-run either
        Assert.Equal(MeshEngineState.Running, rig.Supervisor.State);
        rig.Supervisor.Dispose();
    }

    [Fact]
    public async Task ChangedModel_AppliesExactlyOnceMore()
    {
        var rig = new Rig();
        await rig.Supervisor.ApplyAsync(Model());
        await rig.Supervisor.ApplyAsync(Model());
        Assert.Single(rig.Transport.SetRequests);

        var changed = await rig.Supervisor.ApplyAsync(Model(endpoint: "192.168.1.6:51820"));
        Assert.True(changed.Ok, changed.Summary);
        Assert.Equal(2, rig.Transport.SetRequests.Count);
        Assert.Contains("endpoint=192.168.1.6:51820", rig.Transport.SetRequests[1]);

        await rig.Supervisor.ApplyAsync(Model(endpoint: "192.168.1.6:51820"));
        Assert.Equal(2, rig.Transport.SetRequests.Count); // the new model is now the applied one
        rig.Supervisor.Dispose();
    }

    [Fact]
    public async Task EngineReplaced_SameModel_IsAppliedAgainInFull()
    {
        // The gate must key on the engine INSTANCE, not just the model:
        // after a crash the new engine holds nothing, so the very same
        // model has to be pushed again — exactly once.
        var rig = new Rig();
        await rig.Supervisor.ApplyAsync(Model());
        Assert.Single(rig.Transport.SetRequests);

        rig.Process.Crash();
        var healed = await rig.Supervisor.ApplyAsync(Model());
        Assert.True(healed.Ok, healed.Summary);
        Assert.Equal(2, rig.Process.Starts);
        Assert.Equal(2, rig.Transport.SetRequests.Count);

        await rig.Supervisor.ApplyAsync(Model());
        Assert.Equal(2, rig.Transport.SetRequests.Count); // and it is a no-op again afterwards
        rig.Supervisor.Dispose();
    }

    [Fact]
    public async Task FreshSupervisor_SameModel_AppliesInFull()
    {
        // The §15 hibernate case from the device's side: an agent whose
        // local state is gone (fresh supervisor, nothing applied yet)
        // must take the re-pushed frame in full — the resync heal must
        // not regress behind the idempotency gate.
        var first = new Rig();
        await first.Supervisor.ApplyAsync(Model());
        Assert.Single(first.Transport.SetRequests);
        first.Supervisor.Dispose();

        var fresh = new Rig();
        var result = await fresh.Supervisor.ApplyAsync(Model());
        Assert.True(result.Ok, result.Summary);
        Assert.Single(fresh.Transport.SetRequests);
        Assert.DoesNotContain("unchanged", result.Summary);
        fresh.Supervisor.Dispose();
    }
}
