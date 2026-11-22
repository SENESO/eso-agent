using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EsoAgent.Core;

// P2P product-run harness. Two modes:
//   node  — one product P2P node as its own process (real TcpP2pTransport),
//           driven over a small loopback control channel.
//   drive — spawns nodes + the real fleet server/agent/client binaries and
//           runs the verification legs, writing results.json.
// The node mode composes ONLY product components; the two harness-local
// helpers (file trust, dial blocker) stand in for the fleet approval feed
// and for a middlebox, exactly as the unit tests' in-memory versions do.

string Arg(string name, string? fallback = null)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? "";
}
if (args.Length > 0 && args[0] == "node") return await NodeMode();
if (args.Length > 0 && args[0] == "drive") return await DriveMode();
Console.Error.WriteLine("usage: P2pProductRun node|drive ...");
return 2;

// ---------------------------------------------------------------- node mode

async Task<int> NodeMode()
{
    var dir = Arg("--dir");
    var port = int.Parse(Arg("--port"));
    var bootstrap = Arg("--bootstrap");
    var blockDial = Arg("--block-dial");
    var capture = Arg("--capture");
    Directory.CreateDirectory(dir);

    var cfg = AgentConfig.Load(dir);
    var identity = DeviceIdentity.LoadOrCreate(cfg);
    IP2pTransport transport = TcpP2pTransport.Instance;
    if (int.TryParse(blockDial, out var blocked) && blocked > 0)
        transport = new BlockDialTransport(transport, blocked);

    var node = new P2pNode(new P2pNodeOptions
    {
        Identity = identity,
        Transport = transport,
        Trust = new FileTrust(dir),
        AdvertiseHost = "127.0.0.1",
        ListenPort = port,
        Bootstrap = bootstrap.Length > 0 ? new List<string> { bootstrap } : new List<string>(),
        PeerCacheFile = Path.Combine(dir, "p2p-peers.json"),
        RelayCaptureFile = capture.Length > 0 ? capture : null,
        Audit = (endpoint, summary, result) => Console.WriteLine($"[audit] {endpoint} {summary} -> {result}"),
    });

    var statsLock = new object();
    long payloads = 0;
    string lastHash = "";
    long lastBytes = 0;
    node.OnPayloadReceived = (from, payload, result) =>
    {
        lock (statsLock)
        {
            payloads++;
            lastHash = P2pHex.Sha256Hex(payload);
            lastBytes = payload.Length;
        }
        Console.WriteLine($"[payload] {payload.Length} bytes from {from}");
        return Task.CompletedTask;
    };

    await node.StartAsync();
    await File.WriteAllTextAsync(Path.Combine(dir, "ready.json"),
        new JsonObject { ["code"] = identity.Code, ["spki"] = identity.PublicKeyBase64, ["port"] = port }.ToJsonString());
    Console.WriteLine($"[node] {identity.Code} listening :{port}");

    var connector = new P2pConnector(node);
    var listener = new TcpListener(IPAddress.Loopback, port + 1000);
    listener.Start();
    var shutdown = new TaskCompletionSource();
    _ = Task.Run(async () =>
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Task.Run(async () =>
            {
                using var c = client;
                var stream = c.GetStream();
                var reader = new StreamReader(stream, Encoding.UTF8);
                var line = await reader.ReadLineAsync();
                if (line == null) return;
                var req = JsonNode.Parse(line) as JsonObject ?? new JsonObject();
                var resp = new JsonObject();
                try
                {
                    switch (P2pJson.S(req, "cmd"))
                    {
                        case "resolve":
                        {
                            var target = await node.ResolveAsync(P2pJson.S(req, "code") ?? "");
                            resp["found"] = target != null;
                            if (target != null) resp["endpoint"] = target.Endpoint;
                            break;
                        }
                        case "transfer":
                        {
                            var payload = P2pPayload.Generate(P2pJson.I(req, "seed"), P2pJson.I(req, "bytes"));
                            var (result, path, via) = await connector.TransferAsync(P2pJson.S(req, "code") ?? "", payload);
                            resp["result"] = result;
                            resp["path"] = path.ToString();
                            if (via != null) resp["via"] = via;
                            resp["payloadSha256"] = P2pHex.Sha256Hex(payload);
                            break;
                        }
                        case "announce":
                            resp["stored"] = await node.AnnounceAsync();
                            break;
                        case "stats":
                            lock (statsLock)
                            {
                                resp["code"] = identity.Code;
                                resp["table"] = node.Table.Count;
                                resp["sessionsAccepted"] = node.SessionsAccepted;
                                resp["sessionsRefused"] = node.SessionsRefused;
                                resp["relayBytes"] = node.RelayForwardedBytes;
                                resp["relayFrames"] = node.RelayForwardedFrames;
                                resp["payloads"] = payloads;
                                resp["lastPayloadSha256"] = lastHash;
                                resp["lastPayloadBytes"] = lastBytes;
                            }
                            break;
                        case "shutdown":
                            resp["bye"] = true;
                            _ = Task.Run(async () => { await Task.Delay(200); shutdown.TrySetResult(); });
                            break;
                        default:
                            resp["error"] = "unknown cmd";
                            break;
                    }
                }
                catch (Exception ex) { resp["error"] = ex.Message; }
                var bytes = Encoding.UTF8.GetBytes(resp.ToJsonString() + "\n");
                await stream.WriteAsync(bytes);
            });
        }
    });

    await shutdown.Task;
    listener.Stop();
    await node.StopAsync();
    Console.WriteLine("[node] stopped");
    return 0;
}

// --------------------------------------------------------------- drive mode

async Task<int> DriveMode()
{
    var work = Arg("--work");
    var selfDll = Path.GetFullPath(Arg("--self"));
    var serverDll = Path.GetFullPath(Arg("--server-dll"));
    var agentDll = Path.GetFullPath(Arg("--agent-dll"));
    var clientDll = Path.GetFullPath(Arg("--client-dll"));
    Directory.CreateDirectory(work);
    var results = new JsonObject();
    var legs = new JsonObject();
    results["legs"] = legs;
    var processes = new List<Process>();
    var sw = new Stopwatch();

    void Leg(string name, bool pass, string detail, double ms = 0)
    {
        legs[name] = new JsonObject { ["pass"] = pass, ["detail"] = detail, ["ms"] = Math.Round(ms) };
        Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}: {detail}");
    }

    async Task<JsonObject> ControlAsync(int port, JsonObject req, int timeoutMs = 60000)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        await client.ConnectAsync(IPAddress.Loopback, port + 1000, cts.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(req.ToJsonString() + "\n"), cts.Token);
        var reader = new StreamReader(stream, Encoding.UTF8);
        var line = await reader.ReadLineAsync(cts.Token);
        return JsonNode.Parse(line ?? "{}") as JsonObject ?? new JsonObject();
    }

    Process StartProc(string file, string arguments, string logName)
    {
        var log = new StreamWriter(Path.Combine(work, logName), append: true) { AutoFlush = true };
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine("[err] " + e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        processes.Add(p);
        return p;
    }

    async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(250);
        }
        return condition();
    }

    // ---- swarm setup: four product nodes A B C D on loopback, real TCP.
    const int PA = 61001, PB = 61002, PC = 61003, PD = 61004;
    var nodeDirs = new Dictionary<string, string>();
    var identities = new Dictionary<string, DeviceIdentity>();
    foreach (var (name, _) in new[] { ("A", PA), ("B", PB), ("C", PC), ("D", PD) })
    {
        var dir = Path.Combine(work, "node-" + name);
        Directory.CreateDirectory(dir);
        nodeDirs[name] = dir;
        identities[name] = DeviceIdentity.LoadOrCreate(AgentConfig.Load(dir));
    }
    // Everyone approves everyone (the fleet's approval, as a file the node
    // processes re-read on every trust check).
    var approved = new JsonObject();
    foreach (var kv in identities) approved[kv.Value.Code] = kv.Value.PublicKeyBase64;
    foreach (var dir in nodeDirs.Values)
    {
        await File.WriteAllTextAsync(Path.Combine(dir, "approved.json"), approved.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(dir, "self-approved"), "yes");
    }
    var codeB = identities["B"].Code;
    var codeC = identities["C"].Code;

    async Task StartNode(string name, int port, string? bootstrap, int blockDial = 0, string? captureFile = null)
    {
        var argsLine = $"\"{selfDll}\" node --dir \"{nodeDirs[name]}\" --port {port}";
        if (bootstrap != null) argsLine += $" --bootstrap {bootstrap}";
        if (blockDial > 0) argsLine += $" --block-dial {blockDial}";
        if (captureFile != null) argsLine += $" --capture \"{captureFile}\"";
        StartProc("dotnet", argsLine, $"node-{name}.log");
        var ready = Path.Combine(nodeDirs[name], "ready.json");
        if (!await WaitForAsync(() => File.Exists(ready), 30000))
            throw new Exception($"node {name} never became ready");
    }

    try
    {
        var captureB = Path.Combine(work, "relay-capture-b.bin");
        var captureA = Path.Combine(work, "relay-capture-a.bin");
        await StartNode("B", PB, null, captureFile: captureB);
        await StartNode("A", PA, $"127.0.0.1:{PB}", captureFile: captureA);
        await StartNode("C", PC, $"127.0.0.1:{PB}");
        // D's dials to C are refused (middlebox): D can only reach C via relay.
        await StartNode("D", PD, $"127.0.0.1:{PA}", blockDial: PC);

        // ---- L1: discovery by code. No fleet server of this run exists yet —
        // the fleet leg starts much later, and nothing here talks to any hub.
        sw.Restart();
        var r1 = await ControlAsync(PA, new JsonObject { ["cmd"] = "resolve", ["code"] = codeC });
        var r2 = await ControlAsync(PA, new JsonObject { ["cmd"] = "resolve", ["code"] = codeB });
        var r3 = await ControlAsync(PD, new JsonObject { ["cmd"] = "resolve", ["code"] = codeB });
        sw.Stop();
        Leg("L1-discovery-no-fleet",
            P2pJson.B(r1, "found") && P2pJson.B(r2, "found") && P2pJson.B(r3, "found"),
            $"A->C {P2pJson.S(r1, "endpoint")}, A->B {P2pJson.S(r2, "endpoint")}, D->B {P2pJson.S(r3, "endpoint")}",
            sw.Elapsed.TotalMilliseconds);

        // ---- L2: session over peer relay (D cannot dial C directly).
        sw.Restart();
        var t = await ControlAsync(PD, new JsonObject { ["cmd"] = "transfer", ["code"] = codeC, ["seed"] = 777, ["bytes"] = 256 * 1024 });
        sw.Stop();
        var tres = t["result"] as JsonObject ?? new JsonObject();
        var cStats = await ControlAsync(PC, new JsonObject { ["cmd"] = "stats" });
        Leg("L2-peer-relay-session",
            P2pJson.B(tres, "ok") && P2pJson.S(t, "path") == "PeerRelay"
                && P2pJson.S(cStats, "lastPayloadSha256") == P2pJson.S(t, "payloadSha256"),
            $"ok={P2pJson.B(tres, "ok")} path={P2pJson.S(t, "path")} via={P2pJson.S(t, "via")} " +
            $"C payloads={P2pJson.L(cStats, "payloads")} hashMatch={P2pJson.S(cStats, "lastPayloadSha256") == P2pJson.S(t, "payloadSha256")}",
            sw.Elapsed.TotalMilliseconds);

        // ---- L3: the relay saw ciphertext only.
        var capture = new List<byte>();
        foreach (var f in new[] { captureA, captureB })
            if (File.Exists(f)) capture.AddRange(await File.ReadAllBytesAsync(f));
        var payload = P2pPayload.Generate(777, 256 * 1024);
        var hits = ScanWindows(capture.ToArray(), payload);
        Leg("L3-relay-plaintext-scan", hits == 0 && capture.Count > payload.Length / 2,
            $"capture {capture.Count} bytes scanned, {hits} plaintext window hits");

        // ---- L5: peer-cache restart. D stops (cache saved), restarts with
        // NO bootstrap, and must still resolve C from the cache alone.
        await ControlAsync(PD, new JsonObject { ["cmd"] = "shutdown" });
        await Task.Delay(1000);
        File.Delete(Path.Combine(nodeDirs["D"], "ready.json"));
        await StartNode("D", PD, null, blockDial: PC);
        var r5 = await ControlAsync(PD, new JsonObject { ["cmd"] = "resolve", ["code"] = codeC });
        Leg("L5-peer-cache-restart", P2pJson.B(r5, "found"),
            $"D restarted bootstrap-less, resolve C -> {P2pJson.S(r5, "endpoint")}");

        // ---- L4a: swarm peers down -> connector reports the fleet fallback.
        foreach (var p in new[] { PA, PB, PC })
            await ControlAsync(p, new JsonObject { ["cmd"] = "shutdown" });
        await Task.Delay(1000);
        var t4 = await ControlAsync(PD, new JsonObject { ["cmd"] = "transfer", ["code"] = codeC, ["seed"] = 778, ["bytes"] = 4096 });
        var t4res = t4["result"] as JsonObject ?? new JsonObject();
        Leg("L4a-fallback-signal",
            P2pJson.S(t4, "path") == "None" && P2pJson.S(t4res, "fallback") == "fleet",
            $"path={P2pJson.S(t4, "path")} fallback={P2pJson.S(t4res, "fallback")} error={P2pJson.S(t4res, "error")}");
        await ControlAsync(PD, new JsonObject { ["cmd"] = "shutdown" });
    }
    catch (Exception ex)
    {
        Leg("swarm", false, ex.Message);
    }

    // ---- L4b: the fleet path itself, untouched, with the swarm down:
    // real fleet server + real Linux agent + operator-signed command.
    try
    {
        var fleetDir = Path.Combine(work, "fleet-server");
        var agentDir = Path.Combine(work, "fleet-agent");
        Directory.CreateDirectory(fleetDir);
        Directory.CreateDirectory(agentDir);
        const int fleetPort = 18740;

        async Task<(int Exit, string Out)> RunCaptureAsync(string dll, string argsLine, int timeoutMs = 30000)
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" {argsLine}")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (-1, ""); }
            return (p.ExitCode, await outTask);
        }

        // Operator keypair (harness-held; the demo server gets its public half).
        using var opKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var opSpki = Convert.ToBase64String(opKey.ExportSubjectPublicKeyInfo());
        var opKeyFile = Path.Combine(work, "operator.key");
        await File.WriteAllTextAsync(opKeyFile, opKey.ExportPkcs8PrivateKeyPem());

        await File.WriteAllTextAsync(Path.Combine(fleetDir, "esoagent.json"),
            new JsonObject { ["port"] = fleetPort }.ToJsonString());
        await RunCaptureAsync(serverDll, $"--data-dir \"{fleetDir}\" set-operator-key {opSpki}");
        var fingerprint = (await RunCaptureAsync(serverDll, $"--data-dir \"{fleetDir}\" fingerprint")).Out.Trim();

        var agentIdentity = DeviceIdentity.LoadOrCreate(AgentConfig.Load(agentDir));
        // The installers seed the agent's API token on first install; a bare
        // data dir without one is not a supported state, so seed it here too.
        await File.WriteAllTextAsync(Path.Combine(agentDir, "token"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await File.WriteAllTextAsync(Path.Combine(agentDir, "esoagent.json"), new JsonObject
        {
            ["port"] = 18737,
            ["fleet"] = new JsonObject
            {
                ["serverUrl"] = $"wss://127.0.0.1:{fleetPort}/fleet/connect",
                ["serverFingerprint"] = fingerprint,
            },
            ["mesh"] = new JsonObject { ["enabled"] = false },
            ["p2p"] = new JsonObject { ["enabled"] = false },
        }.ToJsonString());

        StartProc("dotnet", $"\"{serverDll}\" --data-dir \"{fleetDir}\"", "fleet-server.log");
        if (!await WaitForAsync(() =>
        {
            try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, fleetPort); return true; }
            catch { return false; }
        }, 30000)) throw new Exception("demo fleet server never listened");

        StartProc("dotnet", $"\"{agentDll}\" --data-dir \"{agentDir}\"", "fleet-agent.log");
        var enrolled = await WaitForAsync(() =>
        {
            var list = RunCaptureAsync(serverDll, $"--data-dir \"{fleetDir}\" list").GetAwaiter().GetResult().Out;
            return list.Contains(agentIdentity.Code);
        }, 60000);
        if (!enrolled) throw new Exception("demo agent never enrolled (see fleet-agent.log)");
        await RunCaptureAsync(serverDll, $"--data-dir \"{fleetDir}\" approve {agentIdentity.Code}");

        var opConfig = Path.Combine(work, "operator.json");
        await File.WriteAllTextAsync(opConfig, new JsonObject
        {
            // host/port are required by the CLI's config validation even when
            // every command is fleet-targeted (production configs carry the
            // paired device's endpoint here).
            ["host"] = "127.0.0.1",
            ["port"] = 18737,
            ["fleetUrl"] = $"https://127.0.0.1:{fleetPort}",
            ["fleetFingerprint"] = fingerprint,
            ["operatorKeyFile"] = opKeyFile,
        }.ToJsonString());

        // The signed command round-trip: poll until the agent has the
        // approval + operator key from its state frame and answers.
        string statusOut = "";
        var answered = await WaitForAsync(() =>
        {
            var (exit, outText) = RunCaptureAsync(clientDll,
                $"--config \"{opConfig}\" --device {agentIdentity.Code} status", 20000).GetAwaiter().GetResult();
            statusOut = outText;
            return exit == 0 && outText.Trim().Length > 20;
        }, 150000);
        Leg("L4b-fleet-path-roundtrip", answered,
            answered ? $"operator-signed status answered for {agentIdentity.Code} ({statusOut.Trim().Split('\n')[0][..Math.Min(80, statusOut.Trim().Split('\n')[0].Length)]})"
                     : "operator status never answered (see cli/fleet logs)");
    }
    catch (Exception ex)
    {
        Leg("L4b-fleet-path-roundtrip", false, ex.Message);
    }
    finally
    {
        foreach (var p in processes) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }
    }

    results["when"] = DateTimeOffset.UtcNow.ToString("o");
    results["host"] = Environment.MachineName;
    await File.WriteAllTextAsync(Path.Combine(work, "results.json"), results.ToJsonString());
    var allPass = legs.All(kv => P2pJson.B(kv.Value as JsonObject ?? new JsonObject(), "pass"));
    Console.WriteLine(allPass ? "ALL LEGS PASS" : "SOME LEGS FAILED");
    return allPass ? 0 : 1;
}

static int ScanWindows(byte[] capture, byte[] plaintext)
{
    var windows = new HashSet<string>();
    for (int i = 0; i + 16 <= capture.Length; i++)
        windows.Add(P2pHex.Sha256Hex(capture.AsSpan(i, 16)));
    int hits = 0;
    for (int i = 0; i + 16 <= plaintext.Length; i++)
        if (windows.Contains(P2pHex.Sha256Hex(plaintext.AsSpan(i, 16)))) hits++;
    return hits;
}

// ------------------------------------------------------------- node helpers

sealed class FileTrust : IP2pTrust
{
    private readonly string _dir;
    public FileTrust(string dir) => _dir = dir;
    public bool SelfApproved => File.Exists(Path.Combine(_dir, "self-approved"));
    public bool IsApprovedPeer(string code, string spkiB64)
    {
        try
        {
            var path = Path.Combine(_dir, "approved.json");
            if (!File.Exists(path)) return false;
            var map = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return map != null && (string?)map[code] == spkiB64;
        }
        catch { return false; }
    }
}

/// <summary>Models a middlebox: dials to one port are refused, all else passes.</summary>
sealed class BlockDialTransport : IP2pTransport
{
    private readonly IP2pTransport _inner;
    private readonly int _blockedPort;
    public BlockDialTransport(IP2pTransport inner, int blockedPort) { _inner = inner; _blockedPort = blockedPort; }
    public async Task<Stream> DialAsync(string host, int port, CancellationToken ct)
    {
        if (port == _blockedPort) throw new SocketException((int)SocketError.ConnectionRefused);
        return await _inner.DialAsync(host, port, ct);
    }
    public IP2pListener Listen(string host, int port) => _inner.Listen(host, port);
}
