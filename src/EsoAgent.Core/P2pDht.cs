using System.Text.Json.Nodes;

namespace EsoAgent.Core;

/// <summary>How to reach one peer. Id is the peer's 256-bit Kademlia ID (SHA-256 of its SPKI).</summary>
public sealed record P2pPeerInfo(string IdHex, string Code, string SpkiB64, string Host, int Port)
{
    public string Endpoint => Host + ":" + Port;
    public JsonObject ToJson() => new()
    {
        ["id"] = IdHex, ["code"] = Code, ["spki"] = SpkiB64, ["host"] = Host, ["port"] = Port,
    };
    public static P2pPeerInfo FromJson(JsonObject o) => new(P2pJson.S(o, "id"), P2pJson.S(o, "code"), P2pJson.S(o, "spki"), P2pJson.S(o, "host"), P2pJson.I(o, "port"));

    /// <summary>Builds the peer entry for a verified device record (endpoint + key from the record itself).</summary>
    public static P2pPeerInfo FromRecord(JsonObject rec)
    {
        var spki = Convert.FromBase64String(P2pJson.S(rec, "spki"));
        return new P2pPeerInfo(P2pHex.Of(P2pIdentity.NodeIdOf(spki)), P2pJson.S(rec, "code"), P2pJson.S(rec, "spki"), P2pJson.S(rec, "host"), P2pJson.I(rec, "port"));
    }
}

public static class P2pXor
{
    /// <summary>Index of the first differing bit between two 256-bit IDs (= the k-bucket index).</summary>
    public static int BucketIndex(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < 32; i++)
        {
            var x = (byte)(a[i] ^ b[i]);
            if (x != 0) return i * 8 + (7 - BitLead(x));
        }
        return 256; // identical
    }
    private static int BitLead(byte b) { int n = 0; while ((b & 0x80) == 0) { b = (byte)(b << 1); n++; } return n; }

    public static int CompareDistance(ReadOnlySpan<byte> target, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < 32; i++)
        {
            var da = a[i] ^ target[i];
            var db = b[i] ^ target[i];
            if (da != db) return da < db ? -1 : 1;
        }
        return 0;
    }
}

/// <summary>Classic Kademlia routing table: 256 buckets, at most K peers each, oldest dropped when full.</summary>
public sealed class P2pRoutingTable
{
    public const int K = 8;
    private readonly byte[] _selfId;
    private readonly List<P2pPeerInfo>[] _buckets = new List<P2pPeerInfo>[257];
    private readonly object _lock = new();

    public P2pRoutingTable(byte[] selfId)
    {
        _selfId = selfId;
        for (int i = 0; i < _buckets.Length; i++) _buckets[i] = new List<P2pPeerInfo>();
    }

    public int Count { get { lock (_lock) return _buckets.Sum(b => b.Count); } }

    public void Add(P2pPeerInfo p)
    {
        if (p.IdHex == P2pHex.Of(_selfId)) return;
        if (p.IdHex.Length != 64) return; // only fully-identified peers enter the table
        var idx = P2pXor.BucketIndex(_selfId, P2pHex.Parse(p.IdHex));
        lock (_lock)
        {
            var bucket = _buckets[idx];
            bucket.RemoveAll(x => x.IdHex == p.IdHex);
            bucket.Add(p);
            while (bucket.Count > K) bucket.RemoveAt(0);
        }
    }

    public List<P2pPeerInfo> Closest(byte[] target, int count)
    {
        List<P2pPeerInfo> all;
        lock (_lock) all = _buckets.SelectMany(b => b).ToList();
        all.Sort((a, b) => P2pXor.CompareDistance(target, P2pHex.Parse(a.IdHex), P2pHex.Parse(b.IdHex)));
        return all.Take(count).ToList();
    }

    public List<P2pPeerInfo> All() { lock (_lock) return _buckets.SelectMany(b => b).ToList(); }
}

/// <summary>
/// Local DHT value store. Device records are verified before they are
/// stored AND before they are served; blob values are stored unverified
/// on purpose — exactly like torrent pieces, their integrity is the
/// receiver's job (signed manifest), not the holder's.
/// </summary>
public sealed class P2pValueStore
{
    private readonly Dictionary<string, JsonObject> _values = new();
    private readonly object _lock = new();
    public int RejectedRecords;

    public bool Store(string keyHex, JsonObject value, long nowUnix, out string reason)
    {
        reason = "";
        if (P2pJson.S(value, "kind") == "device-record")
        {
            var (ok, why) = P2pRecords.VerifyDeviceRecord(value, nowUnix);
            if (!ok) { Interlocked.Increment(ref RejectedRecords); reason = why; return false; }
        }
        lock (_lock)
        {
            // A device record replaces an older one for the same key only if its expiry is later.
            if (_values.TryGetValue(keyHex, out var old) && P2pJson.S(old, "kind") == "device-record"
                && P2pJson.L(old, "expires") > P2pJson.L(value, "expires"))
                return true;
            // Clone on the way in: the caller's object is parented to its
            // request frame, and a JsonNode can only ever have one parent —
            // storing it as-is would make it unservable ("already has a parent").
            _values[keyHex] = value.DeepClone() as JsonObject ?? value;
        }
        return true;
    }

    public JsonObject? Get(string keyHex, long nowUnix)
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(keyHex, out var v)) return null;
            if (P2pJson.S(v, "kind") == "device-record")
            {
                var (ok, _) = P2pRecords.VerifyDeviceRecord(v, nowUnix);
                if (!ok) { _values.Remove(keyHex); return null; }
            }
            // Clone on the way out, for the same single-parent reason:
            // serving the stored object itself would parent it to the
            // response and poison it for every later serve.
            return v.DeepClone() as JsonObject ?? v;
        }
    }

    public bool Has(string keyHex) { lock (_lock) return _values.ContainsKey(keyHex); }
    public bool Remove(string keyHex) { lock (_lock) return _values.Remove(keyHex); }
    /// <summary>Raw peek for diagnostics/tests: the stored value without the serve-time verification Get applies.</summary>
    public JsonObject? Peek(string keyHex) { lock (_lock) return _values.TryGetValue(keyHex, out var v) ? v.DeepClone() as JsonObject ?? v : null; }
    public int Count { get { lock (_lock) return _values.Count; } }
    public List<string> Keys() { lock (_lock) return _values.Keys.ToList(); }
}

/// <summary>One-shot RPC: dial, send one JSON frame, read one JSON frame, close.</summary>
public static class P2pRpc
{
    public static async Task<JsonObject?> CallAsync(IP2pTransport transport, P2pPeerInfo peer, JsonObject request, int timeoutMs = 5000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var stream = await transport.DialAsync(peer.Host, peer.Port, cts.Token);
            await P2pWire.WriteJsonAsync(stream, request, cts.Token);
            return await P2pWire.ReadJsonAsync(stream, cts.Token);
        }
        catch { return null; }
    }

    public static JsonObject Base(string rpc, P2pPeerInfo self) => new()
    {
        ["kind"] = "rpc",
        ["rpc"] = rpc,
        ["from"] = self.ToJson(),
    };
}

/// <summary>The iterative Kademlia operations, run from a node's own routing table.</summary>
public sealed class P2pDhtOps
{
    public const int Alpha = 3;
    public const int Replication = 3;
    private readonly P2pNode _node;

    public P2pDhtOps(P2pNode node) => _node = node;

    private P2pPeerInfo Self => _node.SelfPeer;
    private P2pRoutingTable Table => _node.Table;
    private IP2pTransport Transport => _node.Transport;

    private sealed class Shortlist
    {
        private readonly string _selfIdHex;
        public Shortlist(string selfIdHex) => _selfIdHex = selfIdHex;
        public readonly List<P2pPeerInfo> Peers = new();
        public readonly HashSet<string> Queried = new();
        public void Merge(IEnumerable<P2pPeerInfo> peers, byte[] target)
        {
            foreach (var p in peers)
            {
                if (p.IdHex == _selfIdHex) continue;
                if (!Peers.Any(x => x.IdHex == p.IdHex)) Peers.Add(p);
            }
            Peers.Sort((a, b) => P2pXor.CompareDistance(target, P2pHex.Parse(a.IdHex), P2pHex.Parse(b.IdHex)));
        }
        public List<P2pPeerInfo> NextBatch() => Peers.Where(p => !Queried.Contains(p.IdHex)).Take(Alpha).ToList();
    }

    /// <summary>Iterative FIND_NODE: returns the K closest known peers to the target.</summary>
    public async Task<List<P2pPeerInfo>> LookupNodeAsync(byte[] target)
    {
        var sl = new Shortlist(Self.IdHex);
        sl.Merge(Table.Closest(target, P2pRoutingTable.K), target);
        for (int round = 0; round < 24; round++)
        {
            var batch = sl.NextBatch();
            if (batch.Count == 0) break;
            var results = await Task.WhenAll(batch.Select(async p =>
            {
                sl.Queried.Add(p.IdHex);
                var req = P2pRpc.Base("find_node", Self);
                req["target"] = P2pHex.Of(target);
                var resp = await P2pRpc.CallAsync(Transport, p, req);
                if (resp == null) return Enumerable.Empty<P2pPeerInfo>();
                Table.Add(p);
                return NodesOf(resp);
            }));
            foreach (var r in results) sl.Merge(r, target);
        }
        return sl.Peers.Take(P2pRoutingTable.K).ToList();
    }

    /// <summary>
    /// Iterative FIND_VALUE. Collects every distinct value the swarm returns
    /// (not just the first) so the caller can discard poisoned or damaged
    /// copies and fall back to another holder — the torrent behaviour.
    /// </summary>
    public async Task<(List<JsonObject> Values, int NodesQueried)> LookupValueAllAsync(byte[] key)
    {
        var keyHex = P2pHex.Of(key);
        var values = new List<JsonObject>();
        var seen = new HashSet<string>();
        void Collect(JsonObject? v)
        {
            if (v == null) return;
            var s = v.ToJsonString();
            if (seen.Add(P2pHex.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(s)))) values.Add(v);
        }
        Collect(_node.Store.Get(keyHex, Now()));
        var sl = new Shortlist(Self.IdHex);
        sl.Merge(Table.Closest(key, P2pRoutingTable.K), key);
        int queried = 0;
        for (int round = 0; round < 24; round++)
        {
            var batch = sl.NextBatch();
            if (batch.Count == 0) break;
            var results = await Task.WhenAll(batch.Select(async p =>
            {
                sl.Queried.Add(p.IdHex);
                Interlocked.Increment(ref queried);
                var req = P2pRpc.Base("find_value", Self);
                req["key"] = keyHex;
                var resp = await P2pRpc.CallAsync(Transport, p, req);
                if (resp == null) return (null, Enumerable.Empty<P2pPeerInfo>());
                Table.Add(p);
                return (resp["value"] as JsonObject, NodesOf(resp));
            }));
            foreach (var (v, nodes) in results) { Collect(v); sl.Merge(nodes, key); }
        }
        return (values, queried);
    }

    /// <summary>Store a value at the Replication closest nodes to its key (and locally if we are among them).</summary>
    public async Task<int> StoreAsync(byte[] key, JsonObject value)
    {
        var keyHex = P2pHex.Of(key);
        var closest = await LookupNodeAsync(key);
        int stored = 0;
        var targets = closest.Take(Replication).ToList();
        if (targets.Count == 0 || P2pXor.CompareDistance(key, P2pHex.Parse(targets[^1].IdHex), _node.NodeId) >= 0)
        {
            if (_node.Store.Store(keyHex, value, Now(), out _)) stored++;
        }
        foreach (var p in targets)
        {
            var req = P2pRpc.Base("store", Self);
            req["key"] = keyHex;
            req["value"] = value.DeepClone();
            var resp = await P2pRpc.CallAsync(Transport, p, req);
            if (resp != null && P2pJson.B(resp, "stored")) stored++;
        }
        return stored;
    }

    public static IEnumerable<P2pPeerInfo> NodesOf(JsonObject resp)
    {
        if (resp["nodes"] is JsonArray arr)
            foreach (var n in arr) if (n is JsonObject o) yield return P2pPeerInfo.FromJson(o);
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
