using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// The P2P productization proofs, ported from the lab suite
/// (experiments/p2p-lab) onto the product stack. The lab ran its swarm
/// as processes on the box because this sandbox intercepts .NET loopback
/// TCP; the product tests run the same stack over the in-memory
/// transport, which exercises everything except the socket itself.
/// The multi-process product run lives in experiments/p2p-product-run.
/// </summary>
internal static class P2pTestKit
{
    public static DeviceIdentity NewIdentity()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        return new DeviceIdentity
        {
            Key = key,
            Code = DeviceIdentity.CodeFromPublicKey(spki),
            PublicKeyBase64 = Convert.ToBase64String(spki),
        };
    }

    public static StaticP2pTrust TrustAll(params DeviceIdentity[] ids)
    {
        var trust = new StaticP2pTrust();
        foreach (var id in ids) trust.SetPeer(id.Code, id.PublicKeyBase64);
        return trust;
    }

    public static async Task<P2pNode> StartNodeAsync(
        IP2pTransport transport, DeviceIdentity id, int port, IP2pTrust trust,
        string? bootstrap = null, string? cacheFile = null, string? relayCapture = null,
        IP2pTransport? nodeTransport = null)
    {
        var node = new P2pNode(new P2pNodeOptions
        {
            Identity = id,
            Transport = nodeTransport ?? transport,
            Trust = trust,
            AdvertiseHost = "127.0.0.1",
            ListenPort = port,
            Bootstrap = bootstrap != null ? new List<string> { bootstrap } : new List<string>(),
            PeerCacheFile = cacheFile,
            RelayCaptureFile = relayCapture,
        });
        await node.StartAsync();
        return node;
    }

    public static string TempDir()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-p2p-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Polls a condition until it holds or the timeout expires (swarm convergence).</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return await condition();
    }
}

/// <summary>Models a middlebox rule: this node's dials to the blocked ports are
/// refused, everything else passes. Real networks do this with NAT/firewalls;
/// it is how a direct path can fail while a relay path works.</summary>
internal sealed class FilteredTransport : IP2pTransport
{
    private readonly IP2pTransport _inner;
    private readonly HashSet<int> _blockedDialPorts;
    public FilteredTransport(IP2pTransport inner, params int[] blockedDialPorts)
    {
        _inner = inner;
        _blockedDialPorts = new HashSet<int>(blockedDialPorts);
    }
    public async Task<Stream> DialAsync(string host, int port, CancellationToken ct)
    {
        if (_blockedDialPorts.Contains(port))
            throw new SocketException((int)SocketError.ConnectionRefused);
        return await _inner.DialAsync(host, port, ct);
    }
    public IP2pListener Listen(string host, int port) => _inner.Listen(host, port);
}

/// <summary>Flips one byte in one chosen length-prefixed frame on the write
/// side of streams dialed to a target port — first transmission only, the
/// lab's relay-tamper moved to the transport seam.</summary>
internal sealed class TamperTransport : IP2pTransport
{
    private readonly IP2pTransport _inner;
    private readonly int _targetPort;
    private readonly int _frameToFlip;
    public TamperTransport(IP2pTransport inner, int targetPort, int frameToFlip)
    {
        _inner = inner;
        _targetPort = targetPort;
        _frameToFlip = frameToFlip;
    }
    public async Task<Stream> DialAsync(string host, int port, CancellationToken ct)
    {
        var s = await _inner.DialAsync(host, port, ct);
        return port == _targetPort ? new TamperStream(s, _frameToFlip) : s;
    }
    public IP2pListener Listen(string host, int port) => _inner.Listen(host, port);

    private sealed class TamperStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _frameToFlip;
        private readonly MemoryStream _pending = new();
        private int _framesSeen;
        private bool _flipped;

        public TamperStream(Stream inner, int frameToFlip) { _inner = inner; _frameToFlip = frameToFlip; }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            _pending.Write(buffer.Span);
            var bytes = _pending.ToArray();
            int pos = 0;
            while (bytes.Length - pos >= 4)
            {
                var len = BitConverter.ToInt32(bytes, pos);
                if (len < 0 || bytes.Length - pos - 4 < len) break; // incomplete frame — wait for more
                var frame = bytes.AsSpan(pos, 4 + len).ToArray();
                if (_framesSeen == _frameToFlip && !_flipped && len > 0)
                {
                    frame[^1] ^= 0xFF; // last payload byte: inside the GCM tag / ciphertext tail
                    _flipped = true;
                }
                _framesSeen++;
                await _inner.WriteAsync(frame, ct);
                pos += 4 + len;
            }
            var rest = bytes.AsSpan(pos).ToArray();
            _pending.SetLength(0);
            _pending.Write(rest);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

public class P2pRecordTests
{
    [Fact]
    public void DeviceRecord_Verifies_WhenHonest()
    {
        var id = P2pTestKit.NewIdentity();
        var rec = P2pRecords.MakeDeviceRecord(id, "127.0.0.1", 8742);
        var (ok, reason) = P2pRecords.VerifyDeviceRecord(rec, P2pDhtOps.Now());
        Assert.True(ok, reason);
    }

    [Fact]
    public void ForgedRecords_AllRejected_ByVerificationAndByTheStore()
    {
        var victim = P2pTestKit.NewIdentity();
        var attacker = P2pTestKit.NewIdentity();
        var now = P2pDhtOps.Now();
        var honest = P2pRecords.MakeDeviceRecord(victim, "127.0.0.1", 8742);

        // 1. Wrong binding: victim's code, attacker's key (signed by the attacker).
        var wrongBinding = P2pRecords.MakeDeviceRecord(attacker, "127.0.0.1", 8742);
        wrongBinding["code"] = victim.Code;
        // 2. Wrong signer: victim's code + key, signature produced by the attacker.
        var wrongSigner = honest.DeepClone() as JsonObject;
        var canonical = P2pRecords.CanonicalDeviceRecord(victim.Code, victim.PublicKeyBase64, "127.0.0.1", 8742, P2pJson.L(honest, "expires"));
        wrongSigner!["sig"] = attacker.SignBase64(Encoding.UTF8.GetBytes(canonical));
        // 3. Expired.
        var expired = P2pRecords.MakeDeviceRecord(victim, "127.0.0.1", 8742, ttlSeconds: -10);
        // 4. Endpoint altered after signing.
        var altered = honest.DeepClone() as JsonObject;
        altered!["host"] = "10.9.9.9";

        var store = new P2pValueStore();
        var key = P2pHex.Of(P2pRecords.DeviceKey(victim.Code));
        foreach (var forged in new[] { wrongBinding, wrongSigner, expired, altered })
        {
            var (ok, _) = P2pRecords.VerifyDeviceRecord(forged, now);
            Assert.False(ok);
            Assert.False(store.Store(key, forged, now, out _));
        }
        Assert.Equal(4, store.RejectedRecords);
        Assert.Null(store.Get(key, now)); // nothing poisoned the registry
        Assert.True(store.Store(key, honest, now, out _));
        Assert.NotNull(store.Get(key, now));
    }

    [Fact]
    public void SessionOpen_ForgeryRejected_BeforeTrust()
    {
        var victim = P2pTestKit.NewIdentity();
        var attacker = P2pTestKit.NewIdentity();
        var target = new P2pPeerInfo(P2pHex.Of(P2pIdentity.NodeIdOf(Convert.FromBase64String(victim.PublicKeyBase64))),
            victim.Code, victim.PublicKeyBase64, "127.0.0.1", 8742);

        // Attacker signs an open claiming the victim's code but its own key.
        var openA = P2pSessionChannel.BuildOpen(attacker, target, null, "transfer",
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        openA["fromCode"] = victim.Code;
        Assert.Equal("code-key-binding", P2pSessionChannel.VerifyOpen(openA));

        // Attacker claims the victim's code AND key, but signs with its own key.
        var impostor = new DeviceIdentity { Key = attacker.Key, Code = victim.Code, PublicKeyBase64 = victim.PublicKeyBase64 };
        var openB = P2pSessionChannel.BuildOpen(impostor, target, null, "transfer",
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        Assert.Equal("signature", P2pSessionChannel.VerifyOpen(openB));

        // The honest open verifies.
        var openC = P2pSessionChannel.BuildOpen(victim, target, null, "transfer",
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        Assert.Equal("", P2pSessionChannel.VerifyOpen(openC));
    }
}

public class P2pSwarmTests
{
    private const int Base = 51100;

    private static async Task<(InMemoryP2pTransport Transport, DeviceIdentity[] Ids, P2pNode[] Nodes)> SwarmAsync(int count, int basePort)
    {
        var transport = new InMemoryP2pTransport();
        var ids = Enumerable.Range(0, count).Select(_ => P2pTestKit.NewIdentity()).ToArray();
        var trust = P2pTestKit.TrustAll(ids);
        var nodes = new P2pNode[count];
        for (int i = 0; i < count; i++)
        {
            nodes[i] = await P2pTestKit.StartNodeAsync(transport, ids[i], basePort + i, trust,
                bootstrap: i == 0 ? null : $"127.0.0.1:{basePort}");
        }
        return (transport, ids, nodes);
    }

    [Fact]
    public async Task Discovery_ByCode_FindsEveryDevice()
    {
        var (_, ids, nodes) = await SwarmAsync(4, Base);
        try
        {
            // Every node resolves every other node's code (the lab measured 25/25).
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    if (i == j) continue;
                    var found = await P2pTestKit.UntilAsync(async () => await nodes[i].ResolveAsync(ids[j].Code) != null, 5000);
                    Assert.True(found, $"node {i} never resolved {ids[j].Code}");
                }
        }
        finally { foreach (var n in nodes) await n.StopAsync(); }
    }

    [Fact]
    public async Task DirectTransfer_PayloadVerified_AndDelivered()
    {
        var (_, ids, nodes) = await SwarmAsync(2, Base + 100);
        try
        {
            var received = new TaskCompletionSource<byte[]>();
            nodes[1].OnPayloadReceived = (from, payload, result) =>
            {
                received.TrySetResult(payload);
                return Task.CompletedTask;
            };
            var payload = P2pPayload.Generate(42, 256 * 1024);
            var connector = new P2pConnector(nodes[0]);
            var (result, path, _) = await connector.TransferAsync(ids[1].Code, payload);
            Assert.True(P2pJson.B(result, "ok"), result.ToJsonString());
            Assert.Equal(P2pPath.Direct, path);
            var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(payload, got);
        }
        finally { foreach (var n in nodes) await n.StopAsync(); }
    }

    [Fact]
    public async Task RelayTransfer_WhenDirectIsBlocked_UsesPeerRelay()
    {
        var transport = new InMemoryP2pTransport();
        var a = P2pTestKit.NewIdentity();
        var b = P2pTestKit.NewIdentity();
        var c = P2pTestKit.NewIdentity();
        var trust = P2pTestKit.TrustAll(a, b, c);
        // A's dials to C are refused (middlebox); A->B and B->C are fine.
        var aTransport = new FilteredTransport(transport, Base + 202);
        var nodeB = await P2pTestKit.StartNodeAsync(transport, b, Base + 201, trust);
        var nodeC = await P2pTestKit.StartNodeAsync(transport, c, Base + 202, trust, bootstrap: $"127.0.0.1:{Base + 201}");
        var nodeA = await P2pTestKit.StartNodeAsync(transport, a, Base + 200, trust, bootstrap: $"127.0.0.1:{Base + 201}", nodeTransport: aTransport);
        try
        {
            var received = new TaskCompletionSource<byte[]>();
            nodeC.OnPayloadReceived = (from, payload, result) => { received.TrySetResult(payload); return Task.CompletedTask; };
            var payload = P2pPayload.Generate(7, 128 * 1024);
            var connector = new P2pConnector(nodeA);
            var (result, path, via) = await connector.TransferAsync(c.Code, payload);
            Assert.True(P2pJson.B(result, "ok"), result.ToJsonString());
            Assert.Equal(P2pPath.PeerRelay, path);
            Assert.Equal(b.Code, via);
            Assert.True(nodeB.RelayForwardedBytes > 0);
            Assert.Equal(payload, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { await nodeA.StopAsync(); await nodeB.StopAsync(); await nodeC.StopAsync(); }
    }
}

public class P2pIntegrityTests
{
    private const int Base = 51300;

    [Fact]
    public async Task RelayTamper_GcmDetects_Retransmits_AndTransferCompletes()
    {
        var transport = new InMemoryP2pTransport();
        var a = P2pTestKit.NewIdentity();
        var b = P2pTestKit.NewIdentity();
        var c = P2pTestKit.NewIdentity();
        var trust = P2pTestKit.TrustAll(a, b, c);
        var nodeB = await P2pTestKit.StartNodeAsync(transport, b, Base + 1, trust);
        var nodeC = await P2pTestKit.StartNodeAsync(transport, c, Base + 2, trust, bootstrap: $"127.0.0.1:{Base + 1}");
        // A's streams: frame 0 = session open, 1 = manifest, 2 = chunk 0, 3 = chunk 1 -> flip frame 3.
        // A also cannot dial C directly, so the session runs A -> B(relay) -> C.
        var aTransport = new TamperTransport(new FilteredTransport(transport, Base + 2), Base + 1, frameToFlip: 3);
        var nodeA = await P2pTestKit.StartNodeAsync(transport, a, Base, trust, bootstrap: $"127.0.0.1:{Base + 1}", nodeTransport: aTransport);
        try
        {
            JsonObject? receiveResult = null;
            var received = new TaskCompletionSource();
            nodeC.OnPayloadReceived = (from, payload, result) => { receiveResult = result; received.TrySetResult(); return Task.CompletedTask; };
            var payload = P2pPayload.Generate(99, 192 * 1024);
            var connector = new P2pConnector(nodeA);
            var (result, path, _) = await connector.TransferAsync(c.Code, payload);
            Assert.True(P2pJson.B(result, "ok"), result.ToJsonString());
            Assert.Equal(P2pPath.PeerRelay, path);
            Assert.True(P2pJson.I(result, "retransmits") >= 1, "the damaged chunk was never retransmitted");
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var detections = receiveResult!["detections"] as JsonArray;
            Assert.NotNull(detections);
            Assert.Contains(detections!, d => (string?)d?["layer"] == "aes-gcm" && (int?)d?["index"] == 1);
        }
        finally { await nodeA.StopAsync(); await nodeB.StopAsync(); await nodeC.StopAsync(); }
    }

    [Fact]
    public async Task BlobChunkSwap_IsCaughtByTheManifest_NotByGcm()
    {
        var transport = new InMemoryP2pTransport();
        var ids = Enumerable.Range(0, 4).Select(_ => P2pTestKit.NewIdentity()).ToArray();
        var trust = P2pTestKit.TrustAll(ids);
        var nodes = new P2pNode[4];
        for (int i = 0; i < 4; i++)
            nodes[i] = await P2pTestKit.StartNodeAsync(transport, ids[i], Base + 100 + i, trust,
                bootstrap: i == 0 ? null : $"127.0.0.1:{Base + 100}");
        try
        {
            var sender = nodes[0];
            var receiver = nodes[3];
            var payload = P2pPayload.Generate(1234, 200 * 1024); // 4 chunks at 64 KiB
            var (blobId, put) = await P2pBlob.PutAsync(sender, ids[0],
                Convert.FromBase64String(ids[3].PublicKeyBase64), ids[3].Code, payload, encrypted: true);
            Assert.Equal(4, P2pJson.I(put, "chunks"));
            Assert.True(P2pJson.I(put, "storeAcks") > 0, put.ToJsonString());

            var chunk1Key = P2pHex.Of(P2pRecords.ChunkKey(blobId, 1));
            var chunk2Key = P2pHex.Of(P2pRecords.ChunkKey(blobId, 2));
            JsonObject? chunk2Value = null;
            foreach (var n in nodes)
            {
                var v = n.Store.Peek(chunk2Key);
                if (v != null) { chunk2Value = v; break; }
            }
            Assert.NotNull(chunk2Value);

            // Swap chunk 1's stored DATA for chunk 2's on every holder (the
            // value keeps chunk 1's key and index — only the bytes move, like
            // a torrent piece mix-up), and drop the receiver's own clean copy.
            var chunk2Data = P2pJson.S(chunk2Value!, "data")!;
            var originals = new Dictionary<P2pNode, JsonObject>();
            foreach (var n in nodes)
            {
                var v = n.Store.Peek(chunk1Key);
                if (v == null) continue;
                originals[n] = v;
                var swapped = (v.DeepClone() as JsonObject)!;
                swapped["data"] = chunk2Data;
                n.Store.Store(chunk1Key, swapped, P2pDhtOps.Now(), out _);
            }
            Assert.True(originals.Count >= 2, "the blob was not replicated");
            receiver.Store.Remove(chunk1Key);

            var damaged = await P2pBlob.GetAsync(receiver, ids[0].Code, Convert.FromBase64String(ids[0].PublicKeyBase64), blobId);
            Assert.False(P2pJson.B(damaged, "ok"), "swapped chunk accepted");
            var detections = damaged["detections"] as JsonArray;
            Assert.NotNull(detections);
            // The swapped chunk is GENUINE ciphertext (of chunk 2): AES-GCM
            // decrypts it cleanly. Only the signed manifest's per-chunk hash
            // can catch this — the lab's headline integrity result.
            Assert.Contains(detections!, d => (string?)d?["layer"] == "manifest-sha256" && (int?)d?["index"] == 1);
            Assert.DoesNotContain(detections!, d => (string?)d?["layer"] == "aes-gcm" && (int?)d?["index"] == 1);

            // Holders serve the real chunk again -> the same get completes.
            foreach (var (n, original) in originals)
                n.Store.Store(chunk1Key, original, P2pDhtOps.Now(), out _);
            var healed = await P2pBlob.GetAsync(receiver, ids[0].Code, Convert.FromBase64String(ids[0].PublicKeyBase64), blobId);
            Assert.True(P2pJson.B(healed, "ok"), healed.ToJsonString());
            Assert.True(P2pJson.B(healed, "payloadHashOk"));
        }
        finally { foreach (var n in nodes) await n.StopAsync(); }
    }

    [Fact]
    public async Task BlobFlips_AreCaught_ByGcmWhenEncrypted_AndByManifestWhenPlain()
    {
        var transport = new InMemoryP2pTransport();
        var ids = Enumerable.Range(0, 4).Select(_ => P2pTestKit.NewIdentity()).ToArray();
        var trust = P2pTestKit.TrustAll(ids);
        var nodes = new P2pNode[4];
        for (int i = 0; i < 4; i++)
            nodes[i] = await P2pTestKit.StartNodeAsync(transport, ids[i], Base + 200 + i, trust,
                bootstrap: i == 0 ? null : $"127.0.0.1:{Base + 200}");
        try
        {
            var sender = nodes[0];
            var receiver = nodes[3];
            foreach (var encrypted in new[] { true, false })
            {
                var payload = P2pPayload.Generate(encrypted ? 555 : 556, 100 * 1024);
                var (blobId, put) = await P2pBlob.PutAsync(sender, ids[0],
                    Convert.FromBase64String(ids[3].PublicKeyBase64), ids[3].Code, payload, encrypted);
                Assert.True(P2pJson.I(put, "storeAcks") > 0, put.ToJsonString());
                var chunk0Key = P2pHex.Of(P2pRecords.ChunkKey(blobId, 0));
                foreach (var n in nodes)
                {
                    var v = n.Store.Peek(chunk0Key);
                    if (v == null) continue;
                    var raw = Convert.FromBase64String(P2pJson.S(v, "data")!);
                    raw[encrypted ? 20 : 5] ^= 0xFF;
                    v["data"] = Convert.ToBase64String(raw);
                    n.Store.Store(chunk0Key, v, P2pDhtOps.Now(), out _);
                }
                receiver.Store.Remove(chunk0Key);
                var get = await P2pBlob.GetAsync(receiver, ids[0].Code, Convert.FromBase64String(ids[0].PublicKeyBase64), blobId);
                Assert.False(P2pJson.B(get, "ok"), $"flip accepted (encrypted={encrypted})");
                var detections = get["detections"] as JsonArray;
                Assert.NotNull(detections);
                var expectedLayer = encrypted ? "aes-gcm" : "manifest-sha256";
                Assert.Contains(detections!, d => (string?)d?["layer"] == expectedLayer && (int?)d?["index"] == 0);
            }
        }
        finally { foreach (var n in nodes) await n.StopAsync(); }
    }

    [Fact]
    public async Task RelaySeesCiphertextOnly_PlaintextScanFindsNothing()
    {
        var transport = new InMemoryP2pTransport();
        var a = P2pTestKit.NewIdentity();
        var b = P2pTestKit.NewIdentity();
        var c = P2pTestKit.NewIdentity();
        var trust = P2pTestKit.TrustAll(a, b, c);
        var captureFile = Path.Combine(P2pTestKit.TempDir(), "relay-capture.bin");
        var aTransport = new FilteredTransport(transport, Base + 302);
        var nodeB = await P2pTestKit.StartNodeAsync(transport, b, Base + 301, trust, relayCapture: captureFile);
        var nodeC = await P2pTestKit.StartNodeAsync(transport, c, Base + 302, trust, bootstrap: $"127.0.0.1:{Base + 301}");
        var nodeA = await P2pTestKit.StartNodeAsync(transport, a, Base + 300, trust, bootstrap: $"127.0.0.1:{Base + 301}", nodeTransport: aTransport);
        try
        {
            var payload = P2pPayload.Generate(31337, 256 * 1024);
            var connector = new P2pConnector(nodeA);
            var (result, path, _) = await connector.TransferAsync(c.Code, payload);
            Assert.True(P2pJson.B(result, "ok"), result.ToJsonString());
            Assert.Equal(P2pPath.PeerRelay, path);
            var capture = await File.ReadAllBytesAsync(captureFile);
            Assert.True(capture.Length > payload.Length / 2, "the relay captured almost nothing");
            Assert.Equal(0, ScanForPlaintext(capture, payload));
        }
        finally { await nodeA.StopAsync(); await nodeB.StopAsync(); await nodeC.StopAsync(); }
    }

    /// <summary>Rolling 16-byte window scan (the lab's method): every window of
    /// the plaintext is hashed; the capture's window set must contain none.</summary>
    private static int ScanForPlaintext(byte[] capture, byte[] plaintext)
    {
        var captureWindows = new HashSet<string>();
        for (int i = 0; i + 16 <= capture.Length; i++)
            captureWindows.Add(P2pHex.Sha256Hex(capture.AsSpan(i, 16)));
        int hits = 0;
        for (int i = 0; i + 16 <= plaintext.Length; i++)
            if (captureWindows.Contains(P2pHex.Sha256Hex(plaintext.AsSpan(i, 16)))) hits++;
        return hits;
    }
}

public class P2pResilienceTests
{
    private const int Base = 51500;

    [Fact]
    public async Task RelayDeath_FailsFast_AndNewSessionRecoversViaSecondRelay()
    {
        var transport = new InMemoryP2pTransport();
        var a = P2pTestKit.NewIdentity();
        var b1 = P2pTestKit.NewIdentity();
        var b2 = P2pTestKit.NewIdentity();
        var c = P2pTestKit.NewIdentity();
        var trust = P2pTestKit.TrustAll(a, b1, b2, c);
        var aTransport = new FilteredTransport(transport, Base + 3);
        var nodeB1 = await P2pTestKit.StartNodeAsync(transport, b1, Base + 1, trust);
        var nodeC = await P2pTestKit.StartNodeAsync(transport, c, Base + 3, trust, bootstrap: $"127.0.0.1:{Base + 1}");
        var nodeA = await P2pTestKit.StartNodeAsync(transport, a, Base, trust, bootstrap: $"127.0.0.1:{Base + 1}", nodeTransport: aTransport);
        P2pNode? nodeB2 = null;
        try
        {
            var connector = new P2pConnector(nodeA);
            var payload = P2pPayload.Generate(2024, 2 * 1024 * 1024);
            var transfer = connector.TransferAsync(c.Code, payload, paceMs: 60);
            var flowing = await P2pTestKit.UntilAsync(() => nodeB1.RelayForwardedFrames >= 4, 15000);
            Assert.True(flowing, "the relay never started forwarding");
            await nodeB1.StopAsync(); // the relay dies mid-transfer
            var (dead, deadPath, _) = await transfer.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(P2pJson.B(dead, "ok"), "a transfer over a dead relay reported success");

            // A second relay joins; a fresh session completes through it.
            // Note the discovery leg: A's middlebox blocks ALL A->C traffic,
            // DHT queries included, and the only reachable copy of C's record
            // died with B1 — so C's republication tick (the maintenance loop
            // re-announces at TTL/3; invoked directly here) is what restores
            // the record onto B2, after which A resolves C through B2.
            nodeB2 = await P2pTestKit.StartNodeAsync(transport, b2, Base + 2, trust, bootstrap: $"127.0.0.1:{Base}");
            await nodeC.AnnounceAsync();
            var (healed, healedPath, via) = await connector.TransferAsync(c.Code, P2pPayload.Generate(2025, 64 * 1024));
            Assert.True(P2pJson.B(healed, "ok"), healed.ToJsonString());
            Assert.Equal(P2pPath.PeerRelay, healedPath);
            Assert.Equal(b2.Code, via);
        }
        finally
        {
            await nodeA.StopAsync();
            if (nodeB2 != null) await nodeB2.StopAsync();
            await nodeC.StopAsync();
        }
    }

    [Fact]
    public async Task ColdStart_NoBootstrapNoCache_FindsNothing_UntilOneInboundContact()
    {
        var transport = new InMemoryP2pTransport();
        var x = P2pTestKit.NewIdentity();
        var y = P2pTestKit.NewIdentity();
        var trust = P2pTestKit.TrustAll(x, y);
        // X: no bootstrap entries, no peer cache — the lab's leg 3, first half.
        var nodeX = await P2pTestKit.StartNodeAsync(transport, x, Base + 100, trust);
        P2pNode? nodeY = null;
        try
        {
            Assert.Equal(0, nodeX.Table.Count);
            Assert.Null(await nodeX.ResolveAsync(y.Code));

            // One inbound contact: Y joins via X. X learns Y from the contact
            // alone (no bootstrap on X's side, nothing configured).
            nodeY = await P2pTestKit.StartNodeAsync(transport, y, Base + 101, trust, bootstrap: $"127.0.0.1:{Base + 100}");
            Assert.True(nodeX.Table.Count >= 1);
            var found = await nodeX.ResolveAsync(y.Code);
            Assert.NotNull(found);
            Assert.Equal(y.Code, found!.Code);
        }
        finally
        {
            await nodeX.StopAsync();
            if (nodeY != null) await nodeY.StopAsync();
        }
    }

    [Fact]
    public async Task PeerCache_RestartWithoutBootstrap_RejoinsFromCache()
    {
        // The cache leg the lab designed but never got to run (its leg 2):
        // a node that learned peers, stopped, and restarts with NO bootstrap
        // must rejoin from the persisted cache alone.
        var transport = new InMemoryP2pTransport();
        var a = P2pTestKit.NewIdentity();
        var b = P2pTestKit.NewIdentity();
        var trust = P2pTestKit.TrustAll(a, b);
        var cacheFile = Path.Combine(P2pTestKit.TempDir(), "p2p-peers.json");
        var nodeB = await P2pTestKit.StartNodeAsync(transport, b, Base + 201, trust);
        var nodeA = await P2pTestKit.StartNodeAsync(transport, a, Base + 200, trust,
            bootstrap: $"127.0.0.1:{Base + 201}", cacheFile: cacheFile);
        P2pNode? nodeA2 = null;
        try
        {
            Assert.True(nodeA.Table.Count >= 1);
            await nodeA.StopAsync(); // the cache is written on stop
            Assert.True(File.Exists(cacheFile), "peer cache was not persisted");
            Assert.Contains(b.Code, await File.ReadAllTextAsync(cacheFile));

            nodeA2 = await P2pTestKit.StartNodeAsync(transport, a, Base + 200, trust,
                bootstrap: null, cacheFile: cacheFile); // no bootstrap this time
            Assert.True(nodeA2.Table.Count >= 1, "restart learned nobody from the cache");
            var found = await nodeA2.ResolveAsync(b.Code);
            Assert.NotNull(found);
            Assert.Equal(b.Code, found!.Code);
        }
        finally
        {
            if (nodeA2 != null) await nodeA2.StopAsync();
            await nodeB.StopAsync();
        }
    }

    [Fact]
    public async Task ApprovalGate_UnapprovedPeerIsRefused_ApprovedPeerConnects()
    {
        var transport = new InMemoryP2pTransport();
        var a = P2pTestKit.NewIdentity();
        var b = P2pTestKit.NewIdentity();
        var trustA = P2pTestKit.TrustAll(a, b);
        var trustB = new StaticP2pTrust(); // B approves NOBODY yet
        var nodeB = await P2pTestKit.StartNodeAsync(transport, b, Base + 301, trustB);
        var nodeA = await P2pTestKit.StartNodeAsync(transport, a, Base + 300, trustA, bootstrap: $"127.0.0.1:{Base + 301}");
        try
        {
            var connector = new P2pConnector(nodeA);
            var refused = await connector.ConnectAsync(b.Code, "transfer");
            Assert.False(refused.Ok);
            Assert.Equal(P2pPath.None, refused.Path);
            Assert.Contains(refused.Attempts, t => t.Path == P2pPath.Direct && t.Error != null && t.Error.Contains("not approved"));
            Assert.True(nodeB.SessionsRefused >= 1);

            // Approval lands (the fleet's Pending -> Approved moment): the same
            // attempt now connects directly.
            trustB.SetPeer(a.Code, a.PublicKeyBase64);
            var accepted = await connector.ConnectAsync(b.Code, "transfer");
            Assert.True(accepted.Ok, string.Join("; ", accepted.Attempts.Select(t => t.Error)));
            Assert.Equal(P2pPath.Direct, accepted.Path);
            accepted.Channel!.Dispose();
        }
        finally { await nodeA.StopAsync(); await nodeB.StopAsync(); }
    }

    [Fact]
    public async Task ApprovalGate_SelfUnapproved_RefusesEvenApprovedPeers()
    {
        var transport = new InMemoryP2pTransport();
        var a = P2pTestKit.NewIdentity();
        var b = P2pTestKit.NewIdentity();
        var trustA = P2pTestKit.TrustAll(a, b);
        var trustB = new StaticP2pTrust();
        trustB.SetPeer(a.Code, a.PublicKeyBase64);
        trustB.SelfApproved = false; // B itself is still Pending on the fleet
        var nodeB = await P2pTestKit.StartNodeAsync(transport, b, Base + 401, trustB);
        var nodeA = await P2pTestKit.StartNodeAsync(transport, a, Base + 400, trustA, bootstrap: $"127.0.0.1:{Base + 401}");
        try
        {
            var connector = new P2pConnector(nodeA);
            var refused = await connector.ConnectAsync(b.Code, "transfer");
            Assert.False(refused.Ok);
            Assert.Contains(refused.Attempts, t => t.Error != null && t.Error.Contains("not approved"));
        }
        finally { await nodeA.StopAsync(); await nodeB.StopAsync(); }
    }
}
