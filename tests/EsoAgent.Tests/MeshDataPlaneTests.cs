using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// Mesh data-plane defects (§26/§27, 2026-11-23): the apply layer holds
/// (handshakes no longer reset) but no traffic flows — ping lost, TCP
/// timeouts — for three separately-measured causes: (1) the applier's
/// §24 skip never re-verifies the tunnel's actual plumbing, so a VIP /
/// route lost across a hibernation is never repaired; (2) the firewall
/// plan admits only TCP/8737, so ping, :8742 and even the RST reply to
/// an outbound SYN are dropped by the plan's own DROP; (3) the agent
/// API binds no mesh VIP, so the one permitted flow has no listener.
/// These tests pin the fixes; the live field proof (traffic actually
/// flowing over the mesh) is the owner's machines' job.
/// </summary>
/// <summary>Process runner scripted per exact command line (shared by the §27 tests).</summary>
internal sealed class ScriptedRunner : IProcessRunner
{
    public List<string[]> Commands { get; } = new();
    public Dictionary<string, (int Exit, string Out, string Err)> Script { get; } = new();
    public (int Exit, string Out, string Err) Default { get; set; } = (0, "", "");

    public Task<ExecResponse> RunAsync(ExecRequest request, CancellationToken ct = default)
    {
        var argv = new[] { request.File }.Concat(request.Args).ToArray();
        Commands.Add(argv);
        var resp = Script.TryGetValue(string.Join(" ", argv), out var r) ? r : Default;
        return Task.FromResult(new ExecResponse { ExitCode = resp.Exit, Stdout = resp.Out, Stderr = resp.Err });
    }

    public Task<ExecResponse> RunStreamingAsync(ExecRequest request, Func<string, string, Task> onData, CancellationToken ct = default)
        => RunAsync(request, ct);

    public int LaunchDetached(ExecRequest request) => 4242;
}

public class MeshPlumbingTests
{
    private const string PrivB64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string PeerB64 = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=";

    private static WgInterfaceConfig Model() => new()
    {
        PrivateKeyB64 = PrivB64,
        VirtualIp = "10.77.0.2",
        ListenPort = 51820,
        Peers = new()
        {
            new WgPeerConfig { PublicKeyB64 = PeerB64, AllowedIps = new() { "10.77.0.3/32" }, Endpoint = "192.168.1.5:51820", PersistentKeepalive = 25 },
        },
    };

    private static List<(string, string)> IfacesWithVip() => new() { ("eso0", "10.77.0.2") };
    private static List<(string, string)> IfacesWithoutVip() => new() { ("eth0", "192.168.1.10") };

    [Fact]
    public async Task Verify_AllPresent_RepairsNothing()
    {
        var runner = new ScriptedRunner();
        // The route try-add fails with "already exists": present, not repaired.
        runner.Script["ip route add 10.77.0.3/32 dev eso0"] = (2, "", "RTNETLINK answers: File exists");
        var result = await MeshPlumbing.VerifyAndRepairAsync(Model(), runner, () => IfacesWithVip());
        Assert.True(result.AddressOk);
        Assert.False(result.AddressRepaired);
        Assert.Equal(1, result.RoutesOk);
        Assert.Equal(0, result.RoutesRepaired);
        Assert.Empty(result.Failures);
        Assert.DoesNotContain(runner.Commands, c => c.Contains("addr"));
    }

    [Fact]
    public async Task Verify_MissingAddress_IssuesAddrAdd_AndReportsRepair()
    {
        var runner = new ScriptedRunner();
        runner.Script["ip route add 10.77.0.3/32 dev eso0"] = (2, "", "RTNETLINK answers: File exists");
        var result = await MeshPlumbing.VerifyAndRepairAsync(Model(), runner, () => IfacesWithoutVip());
        Assert.True(result.AddressOk);
        Assert.True(result.AddressRepaired);
        Assert.Contains(runner.Commands, c => string.Join(" ", c) == "ip addr add 10.77.0.2/32 dev eso0");
        Assert.Contains("repaired missing mesh address", result.Note);
    }

    [Fact]
    public async Task Verify_MissingRoute_AddSucceeds_IsRepaired()
    {
        var runner = new ScriptedRunner(); // default: every command exits 0
        var result = await MeshPlumbing.VerifyAndRepairAsync(Model(), runner, () => IfacesWithVip());
        Assert.Equal(0, result.RoutesOk);
        Assert.Equal(1, result.RoutesRepaired);
        Assert.Contains(runner.Commands, c => string.Join(" ", c) == "ip route add 10.77.0.3/32 dev eso0");
    }

    [Fact]
    public async Task Verify_RouteAddFailsForReal_Reasons_ReportsFailure()
    {
        var runner = new ScriptedRunner();
        runner.Script["ip route add 10.77.0.3/32 dev eso0"] = (1, "", "RTNETLINK answers: Network is unreachable");
        var result = await MeshPlumbing.VerifyAndRepairAsync(Model(), runner, () => IfacesWithVip());
        Assert.Equal(0, result.RoutesOk);
        Assert.Equal(0, result.RoutesRepaired);
        Assert.Single(result.Failures);
        Assert.Contains("could not restore route 10.77.0.3/32", result.Failures[0]);
    }

    [Fact]
    public async Task Verify_AddressRepairFails_ReportsFailure()
    {
        var runner = new ScriptedRunner();
        runner.Default = (1, "", "Cannot find device \"eso0\"");
        var result = await MeshPlumbing.VerifyAndRepairAsync(Model(), runner, () => IfacesWithoutVip());
        Assert.False(result.AddressOk);
        Assert.False(result.AddressRepaired);
        Assert.Contains("could not restore mesh address", result.Failures[0]);
    }

    [Fact]
    public void CommandShapes_Windows_UseNetsh_Linux_UseIp()
    {
        var winAddr = MeshPlumbing.AddressCommands(Model(), windows: true);
        Assert.Single(winAddr);
        Assert.Equal("netsh", winAddr[0][0]);
        Assert.Contains("10.77.0.2", string.Join(" ", winAddr[0]));

        var winRoute = MeshPlumbing.RouteCommands("10.77.0.3/32", windows: true);
        Assert.Contains(winRoute[0], a => a == "prefix=10.77.0.3/32");
        Assert.Contains(winRoute[0], a => a == "interface=eso0");

        var linAddr = MeshPlumbing.AddressCommands(Model(), windows: false);
        Assert.Equal(new[] { "ip", "addr", "add", "10.77.0.2/32", "dev", "eso0" }, linAddr[0]);

        var linRoute = MeshPlumbing.RouteCommands("10.77.0.3/32", windows: false);
        Assert.Equal(new[] { "ip", "route", "add", "10.77.0.3/32", "dev", "eso0" }, linRoute[0]);
    }
}

public class MeshFirewallRenderingTests
{
    private static MeshFirewallPlan PlanWith(params MeshFirewallRule[] rules) => new()
    {
        DeviceCode = "BBBB-2222",
        AllowInbound = rules.ToList(),
    };

    private static MeshFirewallRule Tcp(int port) => new() { FromCidr = "10.77.0.2/32", Port = port, Protocol = "tcp" };
    private static MeshFirewallRule IcmpEcho() => new() { FromCidr = "10.77.0.2/32", Port = 8, Protocol = "icmp" };
    private static MeshFirewallRule Return() => new() { FromCidr = "10.77.0.2/32", Port = 49152, Protocol = "tcp", ReturnTraffic = true, PortRange = "49152-65535" };

    [Fact]
    public void Linux_ReturnTraffic_EmitsConntrackAccept_AndNoPerRuleAllow()
    {
        var linux = MeshFirewallCommands.Linux(PlanWith(Tcp(8737), Return()));
        Assert.Contains(linux, c => c.Contains("--ctstate") && c.Contains("ESTABLISHED,RELATED"));
        // The return rule itself must not also render as a --dport allow.
        Assert.DoesNotContain(linux, c => c.Contains("--dport") && c.Contains("49152"));
        // The conntrack rule comes before the DROP.
        var ct = linux.FindIndex(c => c.Contains("--ctstate"));
        var drop = linux.FindIndex(c => c.Contains("DROP"));
        Assert.True(ct >= 0 && drop > ct);
    }

    [Fact]
    public void Linux_NoReturnRule_NoConntrackRule()
    {
        var linux = MeshFirewallCommands.Linux(PlanWith(Tcp(8737)));
        Assert.DoesNotContain(linux, c => c.Contains("--ctstate"));
    }

    [Fact]
    public void Linux_IcmpEcho_RendersType8_NeverAsPort()
    {
        var linux = MeshFirewallCommands.Linux(PlanWith(IcmpEcho()));
        Assert.Contains(linux, c => c.Contains("-p") && c.Contains("icmp") && c.Contains("--icmp-type") && c.Contains("8"));
        Assert.DoesNotContain(linux, c => c.Contains("--dport"));
    }

    [Fact]
    public void Windows_IcmpEcho_RendersIcmpv4Type8_WithNoLocalPort()
    {
        var windows = MeshFirewallCommands.Windows(PlanWith(IcmpEcho()));
        var rule = windows.Single(c => c.Any(a => a.StartsWith("remoteip=")));
        Assert.Contains(rule, a => a == "protocol=icmpv4:8,any");
        Assert.DoesNotContain(rule, a => a.StartsWith("localport="));
    }

    [Fact]
    public void Windows_ReturnTraffic_RendersEphemeralRange()
    {
        var windows = MeshFirewallCommands.Windows(PlanWith(Return()));
        var rule = windows.Single(c => c.Any(a => a.StartsWith("remoteip=")));
        Assert.Contains(rule, a => a == "localport=49152-65535");
        Assert.Contains(rule, a => a == "protocol=TCP");
    }

    [Fact]
    public void Windows_PlainTcpRule_Unchanged()
    {
        var windows = MeshFirewallCommands.Windows(PlanWith(Tcp(8737)));
        var rule = windows.Single(c => c.Any(a => a.StartsWith("remoteip=")));
        Assert.Contains(rule, a => a == "protocol=TCP");
        Assert.Contains(rule, a => a == "localport=8737");
    }
}

public class MeshVipBindTests
{
    [Fact]
    public void ResolveBindAddresses_AutoPath_AddsMeshVip()
    {
        var cfg = new AgentConfig { BindAddresses = new() };
        var addrs = NetHelpers.ResolveBindAddresses(cfg, new[] { "10.77.0.2" });
        Assert.Contains("127.0.0.1", addrs);
        Assert.Contains("10.77.0.2", addrs);
        Assert.Equal(1, addrs.Count(a => a == "10.77.0.2")); // no duplicates
    }

    [Fact]
    public void ResolveBindAddresses_ExplicitPath_StillAddsMeshVip_AndIgnoresGarbage()
    {
        var cfg = new AgentConfig { BindAddresses = new() { "100.64.0.5" } };
        var addrs = NetHelpers.ResolveBindAddresses(cfg, new[] { "10.77.0.2", "not-an-ip", "", "100.64.0.5" });
        Assert.Contains("100.64.0.5", addrs);
        Assert.Contains("10.77.0.2", addrs);
        Assert.DoesNotContain("not-an-ip", addrs);
        Assert.Equal(1, addrs.Count(a => a == "100.64.0.5"));
    }

    [Fact]
    public void ResolveBindAddresses_NoExtras_Unchanged()
    {
        var cfg = new AgentConfig { BindAddresses = new() };
        var addrs = NetHelpers.ResolveBindAddresses(cfg);
        Assert.Contains("127.0.0.1", addrs);
        Assert.DoesNotContain("10.77.0.2", addrs);
    }

    [Fact]
    public void MeshFrameStore_TryReadVip_RoundTrips_AndToleratesBadFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "esoagent-vip-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            Assert.Null(MeshFrameStore.TryReadVip(dir)); // missing file
            File.WriteAllText(Path.Combine(dir, "mesh.json"),
                "{\"type\":\"mesh\",\"yourVirtualIp\":\"10.77.0.2\",\"peers\":[]}");
            Assert.Equal("10.77.0.2", MeshFrameStore.TryReadVip(dir));
            File.WriteAllText(Path.Combine(dir, "mesh.json"), "{\"type\":\"mesh\",\"peers\":[]}");
            Assert.Null(MeshFrameStore.TryReadVip(dir)); // no VIP field
            File.WriteAllText(Path.Combine(dir, "mesh.json"), "{\"type\":\"mesh\",\"yourVirtualIp\":\"\"}");
            Assert.Null(MeshFrameStore.TryReadVip(dir)); // empty VIP
            File.WriteAllText(Path.Combine(dir, "mesh.json"), "{\"type\":\"mesh\",\"yourVirtualIp\":");
            Assert.Null(MeshFrameStore.TryReadVip(dir)); // torn JSON
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}

/// <summary>
/// The §26 regression, end to end through ApplyAsync on this (Linux)
/// test host: the first apply converges (wg-quick absent, "not
/// installed"); the interface then "loses" its address between frames
/// (the hibernation case); the second, IDENTICAL frame must still
/// repair the address even though the fingerprint gate skips the
/// tunnel — the §24 skip must not skip state verification.
/// </summary>
public class MeshPlumbingRegressionTests
{
    private const string PeerB64 = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=";

    private sealed class Rig : IDisposable
    {
        public string Dir { get; }
        public AgentState State { get; }
        public ScriptedRunner Runner { get; } = new();
        public List<(string, string)> Interfaces { get; set; } = new() { ("eso0", "10.77.0.3") };

        public Rig()
        {
            Dir = Path.Combine(Path.GetTempPath(), "esoagent-meshplumb-" + Guid.NewGuid().ToString("N"));
            var cfg = AgentConfig.Load(Dir);
            cfg.Mesh.Enabled = true;
            cfg.Mesh.ListenPort = 51820;
            cfg.Mesh.RelayShimBasePort = 52071;
            // Route try-adds fail with "already exists": routes present.
            Runner.Script["ip route add 10.77.0.2/32 dev eso0"] = (2, "", "RTNETLINK answers: File exists");
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

    private static FleetMeshFrame Frame() => new()
    {
        YourVirtualIp = "10.77.0.3",
        Peers = new()
        {
            new MeshPeerInfo { Code = "PEER1", Name = "peer", WgPublicKey = PeerB64, VirtualIp = "10.77.0.2", Endpoint = "192.168.1.5:51820" },
        },
        ServerRelayEndpoint = "127.0.0.1:51821",
    };

    [Fact]
    public async Task ApplyAsync_UnchangedFrame_StillRepairsLostAddress()
    {
        using var rig = new Rig();
        var applier = new MeshApplier(rig.State) { InterfaceLister = () => rig.Interfaces };

        await applier.ApplyAsync(Frame());
        Assert.DoesNotContain("configuration unchanged", applier.Status);
        Assert.DoesNotContain("repaired missing mesh address", applier.Status);

        // Hibernation strips the address; the frame is unchanged.
        rig.Interfaces = new() { ("eth0", "192.168.1.10") };
        rig.Runner.Commands.Clear();
        await applier.ApplyAsync(Frame());

        Assert.Contains("configuration unchanged — apply skipped", applier.Status);
        Assert.Contains("repaired missing mesh address", applier.Status);
        Assert.Contains(rig.Runner.Commands, c => string.Join(" ", c) == "ip addr add 10.77.0.3/32 dev eso0");
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
