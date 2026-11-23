using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EsoAgent.Core;

/// <summary>
/// The approval gate for P2P sessions. Discovery (DHT records, lookups)
/// is public by design — records are self-authenticating and reveal only
/// what a device signed about itself. CONNECTING is gated: a node holds
/// sessions only when it is itself approved, and only with peers the
/// operator has approved. Implementations: the agent's fleet trust
/// (P2pNodeService — the approved set arrives in the fleet state frame),
/// the standalone config list, and test/harness feeds.
/// </summary>
public interface IP2pTrust
{
    /// <summary>True when THIS device may hold sessions (fleet: enrollment Approved; standalone: always).</summary>
    bool SelfApproved { get; }
    /// <summary>True when the peer with this code AND this public key is approved. Both must match.</summary>
    bool IsApprovedPeer(string code, string spkiB64);
}

/// <summary>Simple trust set: self-approved flag + an exact code→key map. Used by
/// standalone mode, tests and the product-run harness.</summary>
public sealed class StaticP2pTrust : IP2pTrust
{
    private readonly Dictionary<string, string> _peers = new(StringComparer.OrdinalIgnoreCase);
    public bool SelfApproved { get; set; } = true;

    public StaticP2pTrust() { }
    public StaticP2pTrust(IEnumerable<P2pTrustedPeer> peers)
    {
        foreach (var p in peers) _peers[p.Code] = p.PublicKey;
    }

    public void SetPeer(string code, string spkiB64) { lock (_peers) _peers[code] = spkiB64; }
    public void RemovePeer(string code) { lock (_peers) _peers.Remove(code); }

    public bool IsApprovedPeer(string code, string spkiB64)
    {
        lock (_peers) return _peers.TryGetValue(code, out var spki) && spki == spkiB64;
    }
}

public sealed class P2pNodeOptions
{
    public required DeviceIdentity Identity { get; init; }
    public required IP2pTransport Transport { get; init; }
    public required IP2pTrust Trust { get; init; }
    /// <summary>Host this device advertises in its DHT record.</summary>
    public string AdvertiseHost { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = P2pDefaults.ListenPort;
    /// <summary>Port advertised in the record when different from ListenPort (port forwards). 0 = ListenPort.</summary>
    public int AdvertisePort { get; set; }
    /// <summary>host:port bootstrap entries, tried in order when the peer cache yields nobody.</summary>
    public List<string> Bootstrap { get; set; } = new();
    /// <summary>Peer cache file (routing-table persistence between runs). Null = no persistence.</summary>
    public string? PeerCacheFile { get; set; }
    /// <summary>Diagnostics: append every relayed A→C frame to this file (ciphertext; exists for secrecy audits). Null = off.</summary>
    public string? RelayCaptureFile { get; set; }
    /// <summary>Audit sink with AuditLog.Write's shape (source, summary, result). Null = silent.</summary>
    public Action<string, string, string>? Audit { get; set; }
    /// <summary>Device-record lifetime; the node re-announces at a third of it.</summary>
    public long RecordTtlSeconds { get; set; } = 3600;
}

/// <summary>
/// One P2P node: a Kademlia participant, a session endpoint and a
/// potential relay, all on one listener (the first frame on a connection
/// decides: "rpc" = one-shot DHT message, "session-open" = the start of
/// an encrypted session or a relay pipe).
///
/// Every inbound RPC teaches the node its sender (standard Kademlia:
/// any message is proof of a live peer) — that is what lets a node with
/// no bootstrap recover from a single inbound contact.
/// </summary>
public sealed class P2pNode : IAsyncDisposable
{
    private readonly P2pNodeOptions _opts;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Stream, byte> _liveStreams = new();
    private readonly object _cacheLock = new();
    private DateTime _lastCacheSave = DateTime.MinValue;
    private int _lastAnnouncedTableSize = -1;
    private IP2pListener? _listener;
    private Task? _acceptLoop;
    private Task? _maintenance;

    public P2pNode(P2pNodeOptions opts)
    {
        _opts = opts;
        Identity = opts.Identity;
        NodeId = P2pIdentity.NodeIdOf(Convert.FromBase64String(opts.Identity.PublicKeyBase64));
        Table = new P2pRoutingTable(NodeId);
        Store = new P2pValueStore();
        Transport = opts.Transport;
    }

    public DeviceIdentity Identity { get; }
    public byte[] NodeId { get; }
    public P2pRoutingTable Table { get; }
    public P2pValueStore Store { get; }
    public IP2pTransport Transport { get; }
    public IP2pTrust Trust => _opts.Trust;
    public P2pDhtOps Ops => new(this);

    public int AdvertisePort => _opts.AdvertisePort > 0 ? _opts.AdvertisePort : _opts.ListenPort;
    public P2pPeerInfo SelfPeer => new(P2pHex.Of(NodeId), Identity.Code, Identity.PublicKeyBase64, _opts.AdvertiseHost, AdvertisePort);

    // Live counters (diagnostics + tests).
    public long RelayForwardedBytes;
    public int RelayForwardedFrames;
    public int SessionsAccepted;
    public int SessionsRefused;
    public int PayloadsReceived;

    /// <summary>Called for each verified payload received over a "transfer" session:
    /// (sender code, payload bytes, the receiver-side transfer result).</summary>
    public Func<string, byte[], JsonObject, Task>? OnPayloadReceived { get; set; }

    public async Task StartAsync()
    {
        _listener = Transport.Listen(_opts.AdvertiseHost, _opts.ListenPort);
        _acceptLoop = Task.Run(AcceptLoopAsync);
        LoadPeerCache();
        await JoinSwarmAsync();
        await AnnounceAsync();
        _maintenance = Task.Run(MaintenanceLoopAsync);
    }

    public async Task StopAsync()
    {
        try { _cts.Cancel(); } catch { }
        if (_listener != null) { try { await _listener.DisposeAsync(); } catch { } }
        foreach (var s in _liveStreams.Keys) { try { s.Dispose(); } catch { } }
        SavePeerCache(force: true);
        try { if (_acceptLoop != null) await _acceptLoop; } catch { }
        try { if (_maintenance != null) await _maintenance; } catch { }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ---------------- join / announce / cache ----------------

    /// <summary>
    /// Join order (the lab's measured bootstrap legs): remembered peers
    /// first (any one live contact is enough), then the configured
    /// bootstrap entries in order, any one suffices. A cold node with
    /// neither stays silent until first contact — measured behaviour,
    /// not an error.
    /// </summary>
    public async Task JoinSwarmAsync()
    {
        // Leg 2: peer cache. Ping what we remember; whoever answers repopulates the table.
        foreach (var cached in Table.All().Take(P2pRoutingTable.K))
        {
            var resp = await P2pRpc.CallAsync(Transport, cached, P2pRpc.Base("ping", SelfPeer), 3000);
            if (resp != null && resp["self"] is JsonObject selfObj)
            {
                Table.Add(P2pPeerInfo.FromJson(selfObj));
                await Ops.LookupNodeAsync(NodeId);
                return;
            }
        }
        // No cached peer answered (stale entries may still sit in the table —
        // they prove nothing). Fall through to the bootstrap entries.
        // Leg 1: bootstrap entries.
        foreach (var entry in _opts.Bootstrap)
        {
            var parts = entry.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var port)) continue;
            var bootPeer = new P2pPeerInfo("", "", "", parts[0], port);
            var resp = await P2pRpc.CallAsync(Transport, bootPeer, P2pRpc.Base("ping", SelfPeer), 5000);
            if (resp != null && resp["self"] is JsonObject selfObj)
            {
                Table.Add(P2pPeerInfo.FromJson(selfObj));
                await Ops.LookupNodeAsync(NodeId);
                return;
            }
        }
    }

    public async Task<int> AnnounceAsync()
    {
        var rec = P2pRecords.MakeDeviceRecord(Identity, _opts.AdvertiseHost, AdvertisePort, _opts.RecordTtlSeconds);
        var key = P2pRecords.DeviceKey(Identity.Code);
        var stored = await Ops.StoreAsync(key, rec);
        // A device is authoritative for its own record: always hold it
        // locally too. Replication alone can place every copy on nodes
        // that later die, and then the device is undiscoverable until
        // its next republication — holding a local copy means anyone
        // who can reach the device can always resolve it.
        Store.Store(P2pHex.Of(key), rec, P2pDhtOps.Now(), out _);
        _lastAnnouncedTableSize = Table.Count;
        return stored;
    }

    private async Task MaintenanceLoopAsync()
    {
        var reannounceEvery = TimeSpan.FromSeconds(Math.Max(60, _opts.RecordTtlSeconds / 3));
        var lastAnnounce = DateTime.UtcNow;
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token); }
            catch { return; }
            try
            {
                // Re-announce when the record nears expiry, or promptly when
                // the table grew (a node that joined empty announces to nobody;
                // once it learns peers its record must actually reach them).
                if (DateTime.UtcNow - lastAnnounce >= reannounceEvery || (Table.Count > 0 && _lastAnnouncedTableSize == 0))
                {
                    await AnnounceAsync();
                    lastAnnounce = DateTime.UtcNow;
                }
                SavePeerCache(force: false);
            }
            catch { /* maintenance never kills the node */ }
        }
    }

    private void LoadPeerCache()
    {
        var file = _opts.PeerCacheFile;
        if (file == null) return;
        // Backup-aware (defect-8): a torn cache recovers the last-good
        // peer set from its backup instead of cold-starting empty; a
        // doubly-corrupt cache is still just a cold start, never a crash.
        var status = StateFile.TryReadJson<List<P2pPeerInfo>>(file, out var peers, new JsonSerializerOptions());
        if (peers == null) return;
        foreach (var p in peers) if (IsSelfConsistent(p)) Table.Add(p);
    }

    private void SavePeerCache(bool force)
    {
        var file = _opts.PeerCacheFile;
        if (file == null) return;
        lock (_cacheLock)
        {
            if (!force && DateTime.UtcNow - _lastCacheSave < TimeSpan.FromSeconds(15)) return;
            _lastCacheSave = DateTime.UtcNow;
            try
            {
                StateFile.WriteText(file, JsonSerializer.Serialize(Table.All()));
            }
            catch { }
        }
    }

    /// <summary>A peer entry is only believable when its code is the code of its
    /// key and its ID is that key's hash — the same binding records enforce.</summary>
    public static bool IsSelfConsistent(P2pPeerInfo p)
    {
        try
        {
            if (p.IdHex.Length != 64 || p.SpkiB64.Length == 0) return false;
            var spki = Convert.FromBase64String(p.SpkiB64);
            return DeviceIdentity.CodeFromPublicKey(spki) == p.Code
                   && P2pHex.Of(P2pIdentity.NodeIdOf(spki)) == p.IdHex;
        }
        catch { return false; }
    }

    // ---------------- lookups ----------------

    /// <summary>Resolve a device code to its verified DHT record (endpoint + key), or null.</summary>
    public async Task<P2pPeerInfo?> ResolveAsync(string code)
    {
        var (values, _) = await Ops.LookupValueAllAsync(P2pRecords.DeviceKey(code));
        foreach (var v in values)
        {
            var (ok, _) = P2pRecords.VerifyDeviceRecord(v, P2pDhtOps.Now());
            if (ok && P2pJson.S(v, "code") == code)
            {
                var peer = P2pPeerInfo.FromRecord(v);
                Table.Add(peer);
                return peer;
            }
        }
        return null;
    }

    // ---------------- connection handling ----------------

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            (Stream Stream, string Remote) conn;
            try { conn = await _listener!.AcceptAsync(_cts.Token); }
            catch { return; }
            _liveStreams[conn.Stream] = 0;
            _ = Task.Run(async () =>
            {
                try { await HandleConnectionAsync(conn.Stream); }
                catch { }
                finally
                {
                    _liveStreams.TryRemove(conn.Stream, out _);
                    try { conn.Stream.Dispose(); } catch { }
                }
            });
        }
    }

    private async Task HandleConnectionAsync(Stream stream)
    {
        var first = await P2pWire.ReadJsonAsync(stream, _cts.Token);
        if (first == null) return;
        var kind = P2pJson.S(first, "kind");
        if (kind == "rpc") await HandleRpcAsync(stream, first);
        else if (kind == "session-open") await HandleSessionOpenAsync(stream, first);
    }

    private async Task HandleRpcAsync(Stream stream, JsonObject req)
    {
        var resp = new JsonObject { ["ok"] = true };
        try
        {
            if (req["from"] is JsonObject fromObj)
            {
                var from = P2pPeerInfo.FromJson(fromObj);
                if (IsSelfConsistent(from)) Table.Add(from);
            }
            var rpc = P2pJson.S(req, "rpc");
            switch (rpc)
            {
                case "ping":
                    resp["self"] = SelfPeer.ToJson();
                    break;
                case "find_node":
                {
                    var target = P2pHex.Parse(P2pJson.S(req, "target"));
                    resp["nodes"] = new JsonArray(Table.Closest(target, P2pRoutingTable.K).Select(p => (JsonNode)p.ToJson()).ToArray());
                    break;
                }
                case "find_value":
                {
                    var keyHex = P2pJson.S(req, "key");
                    var value = Store.Get(keyHex, P2pDhtOps.Now());
                    if (value != null) resp["value"] = value;
                    resp["nodes"] = new JsonArray(Table.Closest(P2pHex.Parse(keyHex), P2pRoutingTable.K).Select(p => (JsonNode)p.ToJson()).ToArray());
                    break;
                }
                case "store":
                {
                    var keyHex = P2pJson.S(req, "key");
                    var value = req["value"] as JsonObject ?? new JsonObject();
                    var stored = Store.Store(keyHex, value, P2pDhtOps.Now(), out var reason);
                    resp["stored"] = stored;
                    if (!stored) resp["reason"] = reason;
                    break;
                }
                default:
                    resp = new JsonObject { ["ok"] = false, ["error"] = "unknown rpc " + rpc };
                    break;
            }
        }
        catch (Exception ex) { resp = new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
        try { await P2pWire.WriteJsonAsync(stream, resp); } catch { }
    }

    // ---------------- sessions + relay ----------------

    private async Task RefuseSessionAsync(Stream stream, JsonObject open, string reason)
    {
        Interlocked.Increment(ref SessionsRefused);
        _opts.Audit?.Invoke("p2p", $"refused session from {P2pJson.S(open, "fromCode")} for {P2pJson.S(open, "toCode")}: {reason}", "refused");
        try
        {
            await P2pWire.WriteJsonAsync(stream, new JsonObject { ["kind"] = "session-refused", ["reason"] = reason });
        }
        catch { }
    }

    private async Task HandleSessionOpenAsync(Stream stream, JsonObject open)
    {
        var toCode = P2pJson.S(open, "toCode");
        var viaCode = P2pJson.S(open, "viaCode");
        if (viaCode == Identity.Code && toCode != Identity.Code)
        {
            await RelaySessionAsync(stream, open);
            return;
        }
        if (toCode != Identity.Code) return; // not for us, not via us — drop

        // The approval gate, applied BEFORE any key agreement:
        // signature + binding first (cheap), then the trust checks.
        var verifyError = P2pSessionChannel.VerifyOpen(open);
        if (verifyError.Length > 0) { await RefuseSessionAsync(stream, open, "open-" + verifyError); return; }
        if (!_opts.Trust.SelfApproved) { await RefuseSessionAsync(stream, open, "this device is not approved"); return; }
        var fromCode = P2pJson.S(open, "fromCode");
        var fromSpki = P2pJson.S(open, "fromSpki");
        if (!_opts.Trust.IsApprovedPeer(fromCode, fromSpki)) { await RefuseSessionAsync(stream, open, "peer not approved"); return; }

        Interlocked.Increment(ref SessionsAccepted);
        _opts.Audit?.Invoke("p2p", $"session accepted from {fromCode} ({P2pJson.S(open, "purpose")})", "ok");
        using var ch = await P2pSessionChannel.AcceptAsync(Identity, stream, open);
        var purpose = P2pJson.S(open, "purpose");
        if (purpose == "transfer")
        {
            var (result, payload) = await P2pTransfer.ReceiveStreamAsync(ch, fromCode, _cts.Token);
            if (payload != null)
            {
                Interlocked.Increment(ref PayloadsReceived);
                if (OnPayloadReceived != null) await OnPayloadReceived(fromCode, payload, result);
            }
        }
        else if (purpose == "echo")
        {
            try
            {
                while (true)
                {
                    var (type, body) = await ch.RecvFrameAsync(_cts.Token);
                    if (type == P2pSessionChannel.TEchoReq) await ch.SendFrameAsync(P2pSessionChannel.TEchoRep, body, _cts.Token);
                }
            }
            catch { /* peer closed */ }
        }
    }

    /// <summary>
    /// The relay pipe. The relay reads the (plaintext, routing-only) open
    /// frame, verifies it, dials the target AT THE ENDPOINT THE TARGET
    /// SIGNED FOR in its DHT record (never an arbitrary address from the
    /// open — a relay must not become a blind proxy), forwards the ack,
    /// then copies frames in both directions. Session frames are opaque
    /// ciphertext to the relay: it counts them, and — only when the
    /// diagnostics capture is configured — writes the A→C bytes to the
    /// capture file, which is what a secrecy audit scans.
    ///
    /// Gate: the initiator must be an approved peer (signature-verified),
    /// this device must be approved, and the target must resolve to an
    /// approved peer whose record endpoint matches the open's target.
    /// </summary>
    private async Task RelaySessionAsync(Stream inStream, JsonObject open)
    {
        var verifyError = P2pSessionChannel.VerifyOpen(open);
        if (verifyError.Length > 0) { await RefuseSessionAsync(inStream, open, "open-" + verifyError); return; }
        if (!_opts.Trust.SelfApproved) { await RefuseSessionAsync(inStream, open, "relay device is not approved"); return; }
        var fromCode = P2pJson.S(open, "fromCode");
        if (!_opts.Trust.IsApprovedPeer(fromCode, P2pJson.S(open, "fromSpki")))
        { await RefuseSessionAsync(inStream, open, "peer not approved"); return; }
        var toCode = P2pJson.S(open, "toCode");
        var target = await ResolveAsync(toCode);
        if (target == null) { await RefuseSessionAsync(inStream, open, "target not resolvable"); return; }
        if (!_opts.Trust.IsApprovedPeer(toCode, target.SpkiB64))
        { await RefuseSessionAsync(inStream, open, "target not approved"); return; }
        if (target.Host != P2pJson.S(open, "toHost") || target.Port != P2pJson.I(open, "toPort"))
        { await RefuseSessionAsync(inStream, open, "target endpoint does not match its signed record"); return; }

        Stream outStream;
        try { outStream = await Transport.DialAsync(target.Host, target.Port, _cts.Token); }
        catch (Exception ex) { await RefuseSessionAsync(inStream, open, "relay cannot reach target: " + ex.Message); return; }
        _liveStreams[outStream] = 0;
        try
        {
            var fwd = new JsonObject();
            foreach (var kv in open) fwd[kv.Key] = kv.Value?.DeepClone();
            fwd["relayed"] = true;
            await P2pWire.WriteJsonAsync(outStream, fwd, _cts.Token);
            var ack = await P2pWire.ReadJsonAsync(outStream, _cts.Token) ?? throw new IOException("no ack from target");
            await P2pWire.WriteJsonAsync(inStream, ack, _cts.Token);
            if (P2pJson.S(ack, "kind") != "session-ack") return; // e.g. the target refused — the initiator has the frame

            _opts.Audit?.Invoke("p2p", $"relaying session {fromCode} -> {toCode}", "ok");
            Stream? capture = null;
            var captureLock = new SemaphoreSlim(1, 1);
            if (_opts.RelayCaptureFile != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_opts.RelayCaptureFile))!);
                    capture = new FileStream(_opts.RelayCaptureFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                }
                catch { capture = null; }
            }
            var a2c = Task.Run(async () =>
            {
                while (true)
                {
                    var frame = await P2pWire.ReadFrameAsync(inStream, _cts.Token) ?? throw new IOException("initiator closed");
                    Interlocked.Add(ref RelayForwardedBytes, frame.Length);
                    Interlocked.Increment(ref RelayForwardedFrames);
                    if (capture != null)
                    {
                        await captureLock.WaitAsync(_cts.Token);
                        try { await capture.WriteAsync(frame, _cts.Token); await capture.FlushAsync(_cts.Token); }
                        finally { captureLock.Release(); }
                    }
                    await P2pWire.WriteFrameAsync(outStream, frame, _cts.Token);
                }
            });
            var c2a = Task.Run(async () =>
            {
                while (true)
                {
                    var frame = await P2pWire.ReadFrameAsync(outStream, _cts.Token) ?? throw new IOException("target closed");
                    await P2pWire.WriteFrameAsync(inStream, frame, _cts.Token);
                }
            });
            try { await Task.WhenAny(a2c, c2a); } catch { }
            if (capture != null) { try { await capture.DisposeAsync(); } catch { } }
        }
        finally
        {
            _liveStreams.TryRemove(outStream, out _);
            try { outStream.Dispose(); } catch { }
        }
    }
}
