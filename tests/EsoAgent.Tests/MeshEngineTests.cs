using System.Net.Sockets;
using System.Text;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

// Embedded mesh engine (2026-11-20): the structured config model, the UAPI
// translation/parsing seam, the Windows addressing plan, the supervisor
// lifecycle (with fakes — no real sidecar in the sandbox), and a live
// unix-socket round-trip of the Linux UAPI transport against a fake engine.

public class WgModelTests
{
    private static List<MeshPeerInfo> SamplePeers() => new()
    {
        new() { Code = "AAAA-1111", Name = "direct-peer", WgPublicKey = "PUB1", VirtualIp = "10.77.0.3", Endpoint = "192.168.1.5:51820", ApprovedSubnets = new() { "192.168.1.0/24" } },
        new() { Code = "BBBB-2222", Name = "relayed-peer", WgPublicKey = "PUB2", VirtualIp = "10.77.0.4", Relay = true },
    };

    [Fact]
    public void Build_IsRenderOfModel_ByteIdentical()
    {
        var peers = SamplePeers();
        var viaBuild = WgConfigBuilder.Build("PRIVKEY", "10.77.0.2", 51820, peers, relayShimBasePort: 51900);
        var model = WgConfigBuilder.BuildModel("PRIVKEY", "10.77.0.2", 51820, peers, relayShimBasePort: 51900);
        Assert.Equal(viaBuild, WgConfigBuilder.RenderText(model));
    }

    [Fact]
    public void Model_CarriesRelayEndpointAndAllowedIps()
    {
        var model = WgConfigBuilder.BuildModel("PRIVKEY", "10.77.0.2", 51820, SamplePeers(), relayShimBasePort: 51900);
        Assert.Equal("10.77.0.2", model.VirtualIp);
        Assert.Equal(51820, model.ListenPort);
        Assert.Equal(new[] { "10.77.0.3/32", "192.168.1.0/24" }, model.Peers[0].AllowedIps);
        Assert.Equal("192.168.1.5:51820", model.Peers[0].Endpoint);
        Assert.Equal("127.0.0.1:51900", model.Peers[1].Endpoint); // relay shim, index increments per relay peer
        Assert.Equal(25, model.Peers[0].PersistentKeepalive);
    }

    [Fact]
    public void Model_ExitNodePeerCarriesDefaultRoute()
    {
        var peers = new List<MeshPeerInfo>
        {
            new() { Code = "EXIT-0001", Name = "exit", WgPublicKey = "PUBX", VirtualIp = "10.77.0.9", Endpoint = "203.0.113.7:51820" },
        };
        var model = WgConfigBuilder.BuildModel("PRIVKEY", "10.77.0.2", 51820, peers, exitNodeCode: "EXIT-0001");
        Assert.Equal("0.0.0.0/0", model.Peers[0].AllowedIps[0]);
    }

    [Fact]
    public void Parser_RoundTripsBuilderOutput()
    {
        var text = WgConfigBuilder.Build("PRIVKEY", "10.77.0.2", 51820, SamplePeers(), relayShimBasePort: 51900);
        var parsed = WgConfigParser.Parse(text);
        Assert.Equal("PRIVKEY", parsed.PrivateKeyB64);
        Assert.Equal("10.77.0.2", parsed.VirtualIp);
        Assert.Equal(51820, parsed.ListenPort);
        Assert.Equal(2, parsed.Peers.Count);
        Assert.Equal("PUB1", parsed.Peers[0].PublicKeyB64);
        Assert.Equal(new[] { "10.77.0.3/32", "192.168.1.0/24" }, parsed.Peers[0].AllowedIps);
        Assert.Equal("192.168.1.5:51820", parsed.Peers[0].Endpoint);
        Assert.Equal("127.0.0.1:51900", parsed.Peers[1].Endpoint);
        Assert.Equal(25, parsed.Peers[1].PersistentKeepalive);
    }
}

public class UapiProtocolTests
{
    // Two real-shaped keys (32 bytes each) in base64 and their hex forms.
    private const string PrivB64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="; // bytes 00..1f
    private const string PrivHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private const string PeerB64 = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8="; // bytes 20..3f
    private const string PeerHex = "202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f";

    [Fact]
    public void KeyToHex_Converts()
    {
        Assert.Equal(PrivHex, UapiProtocol.KeyToHex(PrivB64));
        Assert.Equal(PeerHex, UapiProtocol.KeyToHex(PeerB64));
    }

    [Fact]
    public void KeyToHex_RejectsBadKeys()
    {
        Assert.Throws<ArgumentException>(() => UapiProtocol.KeyToHex("not-base64!!"));
        Assert.Throws<ArgumentException>(() => UapiProtocol.KeyToHex(Convert.ToBase64String(new byte[16])));
    }

    [Fact]
    public void SetRequest_FullReplacementShape()
    {
        var model = new WgInterfaceConfig
        {
            PrivateKeyB64 = PrivB64,
            VirtualIp = "10.77.0.2",
            ListenPort = 51820,
            Peers = new()
            {
                new WgPeerConfig
                {
                    PublicKeyB64 = PeerB64,
                    AllowedIps = new() { "10.77.0.3/32", "192.168.1.0/24" },
                    Endpoint = "192.168.1.5:51820",
                    PersistentKeepalive = 25,
                },
                new WgPeerConfig
                {
                    PublicKeyB64 = PrivB64,
                    AllowedIps = new() { "10.77.0.4" }, // bare IP must normalize to /32
                    Endpoint = null,
                    PersistentKeepalive = 0,
                },
            },
        };
        var request = UapiProtocol.BuildSetRequest(model);
        Assert.StartsWith("set=1\n", request);
        Assert.Contains($"private_key={PrivHex}\n", request);
        Assert.Contains("listen_port=51820\n", request);
        Assert.Contains("replace_peers=true\n", request);
        Assert.Contains($"public_key={PeerHex}\n", request);
        Assert.Contains("replace_allowed_ips=true\n", request);
        Assert.Contains("allowed_ip=10.77.0.3/32\n", request);
        Assert.Contains("allowed_ip=192.168.1.0/24\n", request);
        Assert.Contains("allowed_ip=10.77.0.4/32\n", request);
        Assert.Contains("endpoint=192.168.1.5:51820\n", request);
        Assert.Contains("persistent_keepalive_interval=25\n", request);
        Assert.EndsWith("\n\n", request); // blank-line terminator
        // Exactly one endpoint line: the endpoint-less peer emits none.
        Assert.Equal(1, request.Split('\n').Count(l => l.StartsWith("endpoint=")));
    }

    [Fact]
    public void GetResponse_ParsesDeviceAndPeers()
    {
        // Real wireguard-go get layout (device/uapi.go): the device section is
        // private_key + listen_port + fwmark — there is no device public_key
        // line; every public_key= line opens a peer block.
        var response = string.Join("\n",
            "private_key=" + PrivHex,
            "listen_port=51820",
            "fwmark=0",
            "public_key=" + PeerHex,
            "preshared_key=" + new string('0', 64),
            "endpoint=192.168.1.5:51820",
            "last_handshake_time_sec=1795167375",
            "last_handshake_time_nsec=123000000",
            "tx_bytes=1360",
            "rx_bytes=1188",
            "persistent_keepalive_interval=25",
            "allowed_ip=10.77.0.3/32",
            "public_key=" + PrivHex,
            "endpoint=127.0.0.1:51900",
            "last_handshake_time_sec=0",
            "tx_bytes=0",
            "rx_bytes=0",
            "persistent_keepalive_interval=25",
            "allowed_ip=10.77.0.4/32",
            "errno=0",
            "");
        var status = UapiProtocol.ParseGetResponse(response);
        Assert.True(status.Ok);
        Assert.Equal(51820, status.ListenPort);
        Assert.Equal(2, status.Peers.Count);
        Assert.Equal(PeerHex, status.Peers[0].PublicKeyHex);
        Assert.Equal(1795167375, status.Peers[0].LastHandshakeUnixSec);
        Assert.Equal(1360, status.Peers[0].TxBytes);
        Assert.Equal(1188, status.Peers[0].RxBytes);
        Assert.Equal(new[] { "10.77.0.3/32" }, status.Peers[0].AllowedIps);
        Assert.Equal(0, status.Peers[1].LastHandshakeUnixSec);
        Assert.Contains("1 handshake(s)", status.Summarize());
    }

    [Fact]
    public void Errno_ParsesFailure()
    {
        Assert.Equal(22, UapiProtocol.ParseErrno("errno=22\n\n"));
        Assert.False(UapiProtocol.IsSuccess("errno=22\n\n"));
        Assert.True(UapiProtocol.IsSuccess("errno=0\n\n"));
        Assert.Equal(-1, UapiProtocol.ParseErrno(""));
    }
}

public class WindowsMeshAddressingTests
{
    private static WgInterfaceConfig Model() => new()
    {
        PrivateKeyB64 = "PRIV",
        VirtualIp = "10.77.0.2",
        ListenPort = 51820,
        Peers = new()
        {
            new WgPeerConfig { PublicKeyB64 = "P1", AllowedIps = new() { "10.77.0.3/32", "192.168.1.0/24" } },
            new WgPeerConfig { PublicKeyB64 = "P2", AllowedIps = new() { "10.77.0.3/32", "10.77.0.2/32", "10.77.0.4" } }, // dup + own IP + bare IP
        },
    };

    [Fact]
    public void RoutePrefixes_DedupesSkipsOwnAndNormalizes()
    {
        Assert.Equal(new[] { "10.77.0.3/32", "192.168.1.0/24", "10.77.0.4/32" },
            WindowsMeshAddressing.RoutePrefixes(Model()));
    }

    [Fact]
    public void ApplyCommands_SetAddressThenDeleteAddRoutes()
    {
        var commands = WindowsMeshAddressing.ApplyCommands("eso0", Model());
        var set = commands[0];
        Assert.Equal("netsh", set[0]);
        Assert.Contains("address=10.77.0.2", set);
        Assert.Contains("name=eso0", set);
        // 1 set + (delete+add) per distinct route
        Assert.Equal(1 + 3 * 2, commands.Count);
        Assert.Contains(commands, c => c.Contains("add") && c.Contains("prefix=192.168.1.0/24"));
        Assert.Contains(commands, c => c.Contains("add") && c.Contains("prefix=10.77.0.4/32"));
    }

    [Fact]
    public void RemoveCommands_MirrorApply()
    {
        var commands = WindowsMeshAddressing.RemoveCommands("eso0", Model());
        Assert.Equal(3 + 1, commands.Count); // route deletes + address delete
        Assert.Contains(commands, c => c.Contains("delete") && c.Contains("prefix=10.77.0.3/32"));
        Assert.Contains(commands[^1], c => c == "address=10.77.0.2");
    }
}

// ── Supervisor lifecycle, driven by fakes ────────────────────────────────────

internal sealed class FakeEngineProcess : IMeshEngineProcess
{
    public bool Running { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public bool ThrowOnStart { get; set; }
    /// <summary>The fake engine's control channel: alive exactly while the
    /// process runs (a dead engine answers no UAPI probes).</summary>
    public FakeUapiTransport? LinkedTransport { get; set; }
    public bool HasExited => !Running;
    public int? ExitCode => Running ? null : 1;

    public void Start()
    {
        if (ThrowOnStart) throw new InvalidOperationException("cannot start");
        Starts++;
        Running = true;
        if (LinkedTransport != null) LinkedTransport.Alive = true;
    }

    public Task StopAsync(TimeSpan grace, CancellationToken ct = default)
    {
        Stops++;
        Running = false;
        if (LinkedTransport != null) LinkedTransport.Alive = false;
        return Task.CompletedTask;
    }

    public void Crash()
    {
        Running = false;
        if (LinkedTransport != null) LinkedTransport.Alive = false;
    }

    public void Dispose() { }
}

/// <summary>An in-memory engine: answers get with a canned status, records set requests.</summary>
internal sealed class FakeUapiTransport : IUapiTransport
{
    public bool Alive { get; set; } = true;
    public List<string> Requests { get; } = new();
    public List<string> SetRequests => Requests.Where(r => r.StartsWith("set=1")).ToList();

    public const string DefaultGetResponse =
        "listen_port=51820\n" +
        "public_key=202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f\n" +
        "endpoint=192.168.1.5:51820\n" +
        "last_handshake_time_sec=1795167375\n" +
        "tx_bytes=1360\nrx_bytes=1188\n" +
        "persistent_keepalive_interval=25\n" +
        "allowed_ip=10.77.0.3/32\n" +
        "errno=0\n\n";

    /// <summary>Overridable per test (e.g. the dead-receiver signature: tx &gt; 0, rx 0, no handshake).</summary>
    public string GetResponse { get; set; } = DefaultGetResponse;

    public Task<string> RequestAsync(string request, CancellationToken ct = default)
    {
        if (!Alive) throw new SocketException((int)SocketError.NotConnected);
        Requests.Add(request);
        return Task.FromResult(request.StartsWith("get=1") ? GetResponse : "errno=0\n\n");
    }

    public void Dispose() { }
}

internal sealed class RecordingRunner : IProcessRunner
{
    public List<string[]> Commands { get; } = new();
    public int ExitCode { get; set; }

    public Task<ExecResponse> RunAsync(ExecRequest request, CancellationToken ct = default)
    {
        Commands.Add(new[] { request.File }.Concat(request.Args).ToArray());
        return Task.FromResult(new ExecResponse { ExitCode = ExitCode });
    }

    public Task<ExecResponse> RunStreamingAsync(ExecRequest request, Func<string, string, Task> onData, CancellationToken ct = default)
        => RunAsync(request, ct);

    public int LaunchDetached(ExecRequest request) => 4242;
}

public class MeshEngineSupervisorTests
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

    private sealed class Rig
    {
        public FakeUapiTransport Transport { get; } = new();
        public FakeEngineProcess Process { get; }
        public RecordingRunner Runner { get; } = new();
        public List<string> Audits { get; } = new();
        public MeshEngineSupervisor Supervisor { get; }

        public Rig(int maxRestarts = 10, bool transportAlive = true)
        {
            Transport.Alive = transportAlive;
            Process = new FakeEngineProcess { LinkedTransport = Transport };
            Supervisor = new MeshEngineSupervisor(
                enginePath: "/nonexistent-dir/eso-mesh.exe",
                runner: Runner,
                audit: (m, s) => Audits.Add($"{s}:{m}"),
                processFactory: () => Process,
                transportFactory: () => Transport,
                maxRestarts: maxRestarts,
                healthInterval: TimeSpan.FromHours(1), // tests drive CheckHealthAsync manually
                startupTimeout: TimeSpan.FromSeconds(5));
        }

        /// <summary>A supervisor whose engine path exists on disk (the spawn
        /// path checks for the binary): the test assembly stands in for it.
        /// The transport starts dead and comes alive when the process starts,
        /// so Apply takes the spawn path rather than the adoption path.</summary>
        public MeshEngineSupervisor SpawnSupervisor(int maxRestarts = 10)
        {
            Transport.Alive = false;
            return new MeshEngineSupervisor(
                enginePath: typeof(MeshEngineSupervisorTests).Assembly.Location,
                runner: Runner,
                audit: (m, s) => Audits.Add(m),
                processFactory: () => Process,
                transportFactory: () => Transport,
                maxRestarts: maxRestarts,
                healthInterval: TimeSpan.FromHours(1),
                startupTimeout: TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Apply_StartsEnginePushesConfigAppliesAddressing()
    {
        var rig = new Rig();
        var supervisor = rig.SpawnSupervisor();

        var result = await supervisor.ApplyAsync(Model());
        Assert.True(result.Ok, result.Summary);
        Assert.Equal(1, rig.Process.Starts);
        Assert.Single(rig.Transport.SetRequests);
        Assert.Contains("private_key=", rig.Transport.SetRequests[0]);
        Assert.Equal(MeshEngineState.Running, supervisor.State);
        Assert.NotNull(result.Status);
        Assert.Contains("1 handshake(s)", result.Summary);
        // Addressing: set address + delete/add route for the one peer route.
        Assert.Equal(3, rig.Runner.Commands.Count);
        supervisor.Dispose();
    }

    [Fact]
    public async Task Apply_MissingEngineBinary_FailsHonestly()
    {
        var rig = new Rig(transportAlive: false);
        var result = await rig.Supervisor.ApplyAsync(Model());
        Assert.False(result.Ok);
        Assert.Contains("not found", result.Summary);
        Assert.Equal(MeshEngineState.Stopped, rig.Supervisor.State);
        rig.Supervisor.Dispose();
    }

    [Fact]
    public async Task Apply_AdoptsAlreadyRunningEngine()
    {
        // Transport answers but our process was never started: a previous
        // agent lifetime left an engine up. Apply must adopt, not spawn.
        var rig = new Rig();
        var supervisor = new MeshEngineSupervisor(
            enginePath: "/nonexistent-dir/eso-mesh.exe",
            runner: rig.Runner,
            audit: (m, s) => rig.Audits.Add(m),
            processFactory: () => rig.Process,
            transportFactory: () => rig.Transport,
            healthInterval: TimeSpan.FromHours(1),
            startupTimeout: TimeSpan.FromSeconds(5));
        var result = await supervisor.ApplyAsync(Model());
        Assert.True(result.Ok, result.Summary);
        Assert.Equal(0, rig.Process.Starts);
        Assert.True(supervisor.Adopted);
        Assert.Contains(rig.Audits, a => a.Contains("adopted"));
        supervisor.Dispose();
    }

    [Fact]
    public async Task HealthCheck_RestartsCrashedEngineAndReconfigures()
    {
        var rig = new Rig();
        var supervisor = rig.SpawnSupervisor();
        await supervisor.ApplyAsync(Model());
        Assert.Equal(1, rig.Process.Starts);

        rig.Process.Crash();
        await supervisor.CheckHealthForTests();
        Assert.Equal(2, rig.Process.Starts);
        Assert.Equal(1, supervisor.RestartCount);
        Assert.Equal(MeshEngineState.Running, supervisor.State);
        Assert.Equal(2, rig.Transport.SetRequests.Count); // config re-pushed after restart
        supervisor.Dispose();
    }

    [Fact]
    public async Task HealthCheck_GivesUpAfterRestartBudget()
    {
        var rig = new Rig();
        var supervisor = rig.SpawnSupervisor(maxRestarts: 1);
        await supervisor.ApplyAsync(Model());
        rig.Process.Crash();
        await supervisor.CheckHealthForTests(); // restart #1 (budget now spent)
        Assert.Equal(MeshEngineState.Running, supervisor.State);
        rig.Process.Crash();
        await supervisor.CheckHealthForTests(); // budget exhausted
        Assert.Equal(MeshEngineState.GaveUp, supervisor.State);
        Assert.Equal(2, rig.Process.Starts); // no third start
        supervisor.Dispose();
    }

    [Fact]
    public async Task Stop_StopsEngineAndRemovesAddressing()
    {
        var rig = new Rig();
        var supervisor = rig.SpawnSupervisor();
        await supervisor.ApplyAsync(Model());
        var commandsBefore = rig.Runner.Commands.Count;
        await supervisor.StopAsync();
        Assert.Equal(1, rig.Process.Stops);
        Assert.Equal(MeshEngineState.Stopped, supervisor.State);
        Assert.True(rig.Runner.Commands.Count > commandsBefore); // removal commands ran
        Assert.Contains(rig.Runner.Commands[^1], c => c == "address=10.77.0.2");
        supervisor.Dispose();
    }
}

/// <summary>CheckHealthAsync is internal; the test assembly already has
/// InternalsVisibleTo — this wrapper just keeps call sites tidy.</summary>
internal static class SupervisorTestExtensions
{
    public static Task CheckHealthForTests(this MeshEngineSupervisor supervisor)
        => supervisor.CheckHealthAsync();
}

public class UnixSocketUapiTransportTests
{
    /// <summary>
    /// A fake engine on a real unix socket: proves the transport + framing
    /// round-trip end-to-end (the same seam the Linux sidecar will use).
    /// </summary>
    [Fact]
    public async Task RoundTrip_GetAndSet()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-uapi-" + Guid.NewGuid().ToString("N")));
        var socketPath = Path.Combine(dir.FullName, "eso0.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(4);

        var seen = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                using var conn = await listener.AcceptAsync(cts.Token);
                using var stream = new NetworkStream(conn, ownsSocket: false);
                var buffer = new byte[4096];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\n\n"))
                {
                    var read = await stream.ReadAsync(buffer, cts.Token);
                    if (read == 0) break;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                seen.Add(request.ToString());
                var reply = request.ToString().StartsWith("get=1")
                    ? "listen_port=51820\nerrno=0\n\n"
                    : "errno=0\n\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), cts.Token);
            }
        }, cts.Token);

        try
        {
            using var transport = new UnixSocketUapiTransport(socketPath);
            var get = await transport.RequestAsync(UapiProtocol.BuildGetRequest(), cts.Token);
            Assert.Equal(0, UapiProtocol.ParseErrno(get));
            Assert.Equal(51820, UapiProtocol.ParseGetResponse(get).ListenPort);

            var model = new WgInterfaceConfig
            {
                PrivateKeyB64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
                VirtualIp = "10.77.0.2",
                ListenPort = 51820,
            };
            var set = await transport.RequestAsync(UapiProtocol.BuildSetRequest(model), cts.Token);
            Assert.True(UapiProtocol.IsSuccess(set));
            Assert.Contains(seen, r => r.StartsWith("set=1"));
        }
        finally
        {
            cts.Cancel();
            try { await server; } catch (OperationCanceledException) { }
            dir.Delete(recursive: true);
        }
    }
}
