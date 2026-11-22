using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EsoAgent.Core;

/// <summary>
/// An encrypted session between two device identities. Key agreement is
/// ECDH over the identity keys themselves (see P2pIdentity), salted per
/// session by both sides' nonces and run through HKDF; frames are
/// AES-GCM under the derived keys with the session ID as AAD.
///
/// The session-open is signed by the initiator over its canonical form
/// and verified by the responder before the ack is signed — a relay in
/// the middle can carry the handshake but cannot impersonate either
/// side or read a single frame. (The lab relied on ECDH possession
/// alone; the product signs the open explicitly so a refusal is
/// immediate and auditable, and so the responder can apply the
/// approval gate before any key agreement happens.)
///
/// Frame plaintext layout: [type byte][body].
///   0x01 data chunk: [u32 index][chunk bytes]
///   0x02 echo request / 0x03 echo reply: [u32 seq][payload]
///   0x10 manifest (JSON)   0x20 retransmit request: [u32 index]
///   0x30 receipt (JSON)    0x31 abort (JSON)
/// </summary>
public sealed class P2pSessionChannel : IDisposable
{
    public const byte TChunk = 0x01, TEchoReq = 0x02, TEchoRep = 0x03, TManifest = 0x10, TRetransmit = 0x20, TReceipt = 0x30, TAbort = 0x31;

    private readonly Stream _stream;
    private readonly AesGcm _sendGcm, _recvGcm;
    private readonly byte[] _sessionId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private P2pSessionChannel(Stream stream, byte[] sessionId, byte[] sendKey, byte[] recvKey)
    {
        _stream = stream;
        _sessionId = sessionId;
        _sendGcm = new AesGcm(sendKey, 16);
        _recvGcm = new AesGcm(recvKey, 16);
    }

    public Stream RawStream => _stream;

    public static (byte[] SendKey, byte[] RecvKey) DirectionKeys(DeviceIdentity id, byte[] peerSpki, byte[] aNonce, byte[] cNonce, bool initiator)
    {
        var salt = aNonce.Concat(cNonce).ToArray();
        var material = P2pIdentity.DeriveSessionKey(id.Key, peerSpki, salt, "esoagent-p2p-session-v1", 64);
        var first = material[..32];
        var second = material[32..];
        return initiator ? (first, second) : (second, first);
    }

    public static string AckCanonical(string sessionId, string aNonceB64, string cNonceB64)
        => string.Join('\n', "esoagent-p2p-session-ack", sessionId, aNonceB64, cNonceB64);

    public static string OpenCanonical(string sessionId, string fromCode, string toCode, string aNonceB64, string purpose)
        => string.Join('\n', "esoagent-p2p-session-open", sessionId, fromCode, toCode, aNonceB64, purpose);

    /// <summary>Builds the session-open frame, signed by the initiator.</summary>
    public static JsonObject BuildOpen(DeviceIdentity id, P2pPeerInfo target, P2pPeerInfo? via, string purpose, byte[] sessionId, byte[] aNonce)
    {
        var sessionIdHex = P2pHex.Of(sessionId);
        var open = new JsonObject
        {
            ["kind"] = "session-open",
            ["sessionId"] = sessionIdHex,
            ["fromCode"] = id.Code,
            ["fromSpki"] = id.PublicKeyBase64,
            ["toCode"] = target.Code,
            ["toHost"] = target.Host,
            ["toPort"] = target.Port,
            ["viaCode"] = via?.Code,
            ["aNonce"] = P2pJson.B64(aNonce),
            ["purpose"] = purpose,
        };
        var canonical = OpenCanonical(sessionIdHex, id.Code, target.Code, P2pJson.B64(aNonce), purpose);
        open["sig"] = id.SignBase64(Encoding.UTF8.GetBytes(canonical));
        return open;
    }

    /// <summary>
    /// Verifies a session-open: the claimed code must be the code of the
    /// claimed key (binding), and the signature must verify with that key.
    /// Returns "" when valid, else the refusal reason.
    /// </summary>
    public static string VerifyOpen(JsonObject open)
    {
        try
        {
            var fromCode = P2pJson.S(open, "fromCode");
            var fromSpki = P2pJson.S(open, "fromSpki");
            if (DeviceIdentity.CodeFromPublicKey(Convert.FromBase64String(fromSpki)) != fromCode)
                return "code-key-binding";
            var canonical = OpenCanonical(P2pJson.S(open, "sessionId"), fromCode, P2pJson.S(open, "toCode"), P2pJson.S(open, "aNonce"), P2pJson.S(open, "purpose"));
            if (!DeviceIdentity.VerifyWithPublicKey(fromSpki, Encoding.UTF8.GetBytes(canonical), P2pJson.S(open, "sig")))
                return "signature";
            return "";
        }
        catch (Exception ex) { return "malformed:" + ex.GetType().Name; }
    }

    /// <summary>Initiator side: dial (directly or via a relay peer), handshake, return the channel.</summary>
    public static async Task<P2pSessionChannel> OpenAsync(
        IP2pTransport transport, DeviceIdentity id, P2pPeerInfo target, byte[] targetSpki, P2pPeerInfo? via, string purpose, int timeoutMs = 15000)
    {
        var sessionId = RandomNumberGenerator.GetBytes(16);
        var sessionIdHex = P2pHex.Of(sessionId);
        var aNonce = RandomNumberGenerator.GetBytes(16);
        var dial = via ?? target;
        using var cts = new CancellationTokenSource(timeoutMs);
        var stream = await transport.DialAsync(dial.Host, dial.Port, cts.Token);
        try
        {
            var open = BuildOpen(id, target, via, purpose, sessionId, aNonce);
            await P2pWire.WriteJsonAsync(stream, open, cts.Token);
            var ack = await P2pWire.ReadJsonAsync(stream, cts.Token)
                      ?? throw new IOException("connection closed during session open");
            if (P2pJson.S(ack, "kind") == "session-refused")
                throw new IOException("session refused: " + P2pJson.S(ack, "reason"));
            if (P2pJson.S(ack, "kind") != "session-ack") throw new IOException("unexpected handshake reply: " + P2pJson.S(ack, "kind"));
            var cNonce = P2pJson.B64(ack, "cNonce");
            var canonical = AckCanonical(sessionIdHex, P2pJson.B64(aNonce), P2pJson.B64(cNonce));
            if (!DeviceIdentity.VerifyWithPublicKey(Convert.ToBase64String(targetSpki), Encoding.UTF8.GetBytes(canonical), P2pJson.S(ack, "sig")))
                throw new IOException("session ack signature does not verify — possible impersonation");
            var (sendKey, recvKey) = DirectionKeys(id, targetSpki, aNonce, cNonce, initiator: true);
            return new P2pSessionChannel(stream, sessionId, sendKey, recvKey);
        }
        catch { try { stream.Dispose(); } catch { } throw; }
    }

    /// <summary>Responder side: given an accepted (already verified) open frame, sign and send the ack, return the channel.</summary>
    public static async Task<P2pSessionChannel> AcceptAsync(DeviceIdentity id, Stream stream, JsonObject open)
    {
        var aNonce = P2pJson.B64(open, "aNonce");
        var cNonce = RandomNumberGenerator.GetBytes(16);
        var sessionIdHex = P2pJson.S(open, "sessionId");
        var canonical = AckCanonical(sessionIdHex, P2pJson.B64(aNonce), P2pJson.B64(cNonce));
        var ack = new JsonObject
        {
            ["kind"] = "session-ack",
            ["sessionId"] = sessionIdHex,
            ["cNonce"] = P2pJson.B64(cNonce),
            ["sig"] = id.SignBase64(Encoding.UTF8.GetBytes(canonical)),
        };
        await P2pWire.WriteJsonAsync(stream, ack);
        var fromSpki = P2pJson.B64(open, "fromSpki");
        var (sendKey, recvKey) = DirectionKeys(id, fromSpki, aNonce, cNonce, initiator: false);
        return new P2pSessionChannel(stream, P2pHex.Parse(sessionIdHex), sendKey, recvKey);
    }

    public async Task SendFrameAsync(byte type, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var plain = new byte[1 + body.Length];
        plain[0] = type;
        body.CopyTo(plain.AsMemory(1));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        _sendGcm.Encrypt(nonce, plain, cipher, tag, _sessionId);
        var frame = new byte[12 + cipher.Length + 16];
        nonce.CopyTo(frame, 0);
        cipher.CopyTo(frame, 12);
        tag.CopyTo(frame, 12 + cipher.Length);
        await _sendLock.WaitAsync(ct);
        try { await P2pWire.WriteFrameAsync(_stream, frame, ct); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Throws CryptographicException when a frame was tampered with (GCM tag mismatch).</summary>
    public async Task<(byte Type, byte[] Body)> RecvFrameAsync(CancellationToken ct = default)
    {
        var frame = await P2pWire.ReadFrameAsync(_stream, ct) ?? throw new IOException("connection closed");
        if (frame.Length < 12 + 16 + 1) throw new InvalidDataException("short session frame");
        var nonce = frame[..12];
        var cipher = frame[12..^16];
        var tag = frame[^16..];
        var plain = new byte[cipher.Length];
        _recvGcm.Decrypt(nonce, cipher, tag, plain, _sessionId);
        return (plain[0], plain[1..]);
    }

    public void Dispose() { try { _stream.Dispose(); } catch { } }
}

/// <summary>Deterministic payload so sender, receiver and audits can all regenerate the same bytes from a seed (tests, diagnostics).</summary>
public static class P2pPayload
{
    public static byte[] Generate(int seed, int bytes)
    {
        var buf = new byte[bytes];
        new Random(seed).NextBytes(buf);
        return buf;
    }
}

public static class P2pTransfer
{
    public const int ChunkSize = 64 * 1024;

    /// <summary>Sender side of a stream transfer. Chunks go out in order; retransmit requests are served until the receipt arrives.</summary>
    public static async Task<JsonObject> SendStreamAsync(P2pSessionChannel ch, DeviceIdentity id, byte[] payload, int paceMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var manifest = P2pRecords.MakeManifest(id, "stream-" + Guid.NewGuid().ToString("N")[..8], payload, ChunkSize);
        await ch.SendFrameAsync(P2pSessionChannel.TManifest, Encoding.UTF8.GetBytes(manifest.ToJsonString()), ct);
        int chunkCount = P2pRecords.ManifestChunkHashes(manifest).Length;
        int retransmits = 0;
        async Task SendChunk(int i)
        {
            var off = i * ChunkSize;
            var len = Math.Min(ChunkSize, payload.Length - off);
            var body = new byte[4 + len];
            BitConverter.TryWriteBytes(body.AsSpan(0, 4), (uint)i);
            payload.AsSpan(off, len).CopyTo(body.AsSpan(4));
            await ch.SendFrameAsync(P2pSessionChannel.TChunk, body, ct);
        }
        for (int i = 0; i < chunkCount; i++)
        {
            await SendChunk(i);
            if (paceMs > 0) await Task.Delay(paceMs, ct);
        }
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            using var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rcts.CancelAfter(5000);
            (byte type, byte[] body) frame;
            try { frame = await ch.RecvFrameAsync(rcts.Token); }
            catch (OperationCanceledException) { continue; }
            if (frame.type == P2pSessionChannel.TRetransmit)
            {
                var idx = (int)BitConverter.ToUInt32(frame.body, 0);
                retransmits++;
                await SendChunk(idx);
            }
            else if (frame.type == P2pSessionChannel.TReceipt)
            {
                sw.Stop();
                var receipt = JsonNode.Parse(Encoding.UTF8.GetString(frame.body)) as JsonObject ?? new JsonObject();
                return new JsonObject
                {
                    ["ok"] = P2pJson.B(receipt, "ok"),
                    ["bytes"] = payload.Length,
                    ["chunks"] = chunkCount,
                    ["retransmits"] = retransmits,
                    ["elapsedMs"] = sw.ElapsedMilliseconds,
                    ["throughputMiBs"] = Math.Round(payload.Length / 1048576.0 / sw.Elapsed.TotalSeconds, 2),
                    ["receipt"] = receipt,
                };
            }
            else if (frame.type == P2pSessionChannel.TAbort)
            {
                return new JsonObject { ["ok"] = false, ["error"] = "aborted by receiver: " + Encoding.UTF8.GetString(frame.body) };
            }
        }
        return new JsonObject { ["ok"] = false, ["error"] = "receipt timeout", ["retransmits"] = retransmits };
    }

    /// <summary>
    /// Receiver side of a stream transfer. Every chunk is checked twice:
    /// AES-GCM tag on decrypt, then plaintext SHA-256 against the signed
    /// manifest. Returns the transfer result AND the assembled payload
    /// (null when the transfer did not complete verified).
    /// </summary>
    public static async Task<(JsonObject Result, byte[]? Payload)> ReceiveStreamAsync(P2pSessionChannel ch, string expectedFromCode, CancellationToken ct)
    {
        var detections = new JsonArray();
        var (mtype, mbody) = await ch.RecvFrameAsync(ct);
        if (mtype != P2pSessionChannel.TManifest) throw new IOException("first frame is not a manifest");
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(mbody)) as JsonObject ?? throw new IOException("bad manifest");
        var (mok, mwhy) = P2pRecords.VerifyManifest(manifest, expectedFromCode);
        if (!mok) throw new IOException("manifest does not verify: " + mwhy);
        var hashes = P2pRecords.ManifestChunkHashes(manifest);
        var chunks = new byte[hashes.Length][];
        int received = 0;
        long bytes = 0;
        // Chunks arrive on one connection in send order, so every chunk frame
        // occupies an arrival slot: slot n carries chunk n during the first
        // pass. A frame that fails GCM cannot tell us its index — the slot
        // can. (Retransmissions arrive after the first pass, when every slot
        // is past; a failure then falls back to the smallest missing index.)
        int arrival = 0;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (received < hashes.Length && DateTime.UtcNow < deadline)
        {
            (byte type, byte[] body) frame;
            try { frame = await ch.RecvFrameAsync(ct); }
            catch (CryptographicException)
            {
                // A frame arrived damaged: GCM caught it before any content was trusted.
                var damaged = arrival < hashes.Length
                    ? arrival
                    : Enumerable.Range(0, hashes.Length).First(i => chunks[i] == null);
                if (arrival < hashes.Length) arrival++;
                detections.Add(new JsonObject { ["index"] = damaged, ["layer"] = "aes-gcm" });
                await SendRetransmit(ch, damaged, ct);
                continue;
            }
            if (frame.type != P2pSessionChannel.TChunk) continue;
            if (arrival < hashes.Length) arrival++;
            var idx = (int)BitConverter.ToUInt32(frame.body, 0);
            var data = frame.body[4..];
            if (idx < 0 || idx >= hashes.Length) continue;
            if (P2pHex.Sha256Hex(data) != hashes[idx])
            {
                detections.Add(new JsonObject { ["index"] = idx, ["layer"] = "manifest-sha256" });
                await SendRetransmit(ch, idx, ct);
                continue;
            }
            if (chunks[idx] == null) { chunks[idx] = data; received++; bytes += data.Length; }
        }
        var result = new JsonObject
        {
            ["chunksExpected"] = hashes.Length,
            ["chunksReceived"] = received,
            ["bytesReceived"] = bytes,
            ["detections"] = detections,
        };
        if (received == hashes.Length)
        {
            var full = new byte[P2pJson.L(manifest, "payloadBytes")];
            int pos = 0;
            foreach (var c in chunks) { c!.CopyTo(full, pos); pos += c.Length; }
            var payloadOk = P2pHex.Sha256Hex(full) == P2pJson.S(manifest, "payloadSha256");
            result["payloadHashOk"] = payloadOk;
            result["ok"] = payloadOk;
            await ch.SendFrameAsync(P2pSessionChannel.TReceipt, Encoding.UTF8.GetBytes(result.ToJsonString()), ct);
            return (result, payloadOk ? full : null);
        }
        result["ok"] = false;
        result["error"] = "transfer incomplete at deadline";
        return (result, null);
    }

    private static Task SendRetransmit(P2pSessionChannel ch, int idx, CancellationToken ct)
    {
        var body = new byte[4];
        BitConverter.TryWriteBytes(body, (uint)idx);
        return ch.SendFrameAsync(P2pSessionChannel.TRetransmit, body, ct);
    }

    /// <summary>Echo RTT probe over an open session: returns the per-sample round-trip milliseconds.</summary>
    public static async Task<List<double>> EchoAsync(P2pSessionChannel ch, int samples, CancellationToken ct)
    {
        var rtts = new List<double>();
        var payload = new byte[64];
        new Random(1234).NextBytes(payload);
        for (int i = 0; i < samples; i++)
        {
            var body = new byte[4 + payload.Length];
            BitConverter.TryWriteBytes(body.AsSpan(0, 4), (uint)i);
            payload.CopyTo(body.AsSpan(4));
            var sw = Stopwatch.StartNew();
            await ch.SendFrameAsync(P2pSessionChannel.TEchoReq, body, ct);
            var (type, _) = await ch.RecvFrameAsync(ct);
            sw.Stop();
            if (type != P2pSessionChannel.TEchoRep) throw new IOException("unexpected echo reply type");
            rtts.Add(sw.Elapsed.TotalMilliseconds);
        }
        return rtts;
    }
}

/// <summary>
/// The DHT blob path (the pure torrent mechanism): the sender publishes a
/// signed manifest and the payload chunks as DHT values; holders store and
/// serve them without being able to vouch for them; the receiver verifies
/// every chunk against the manifest before assembly. In "enc" mode chunks
/// are AES-GCM ciphertext under a key only sender and receiver can derive,
/// so holders additionally learn nothing. In "plain" mode chunks are
/// world-readable — exactly like torrent pieces — and the manifest hashes
/// are the only integrity there is.
/// </summary>
public static class P2pBlob
{
    public static async Task<(string BlobId, JsonObject Result)> PutAsync(P2pNode node, DeviceIdentity id, byte[] receiverSpki, string toCode, byte[] payload, bool encrypted)
    {
        var blobId = Guid.NewGuid().ToString("N")[..12];
        var manifest = P2pRecords.MakeManifest(id, blobId, payload, P2pTransfer.ChunkSize);
        byte[]? blobKey = null;
        if (encrypted)
        {
            var salt = P2pHex.Sha256(Encoding.UTF8.GetBytes("blob:" + blobId));
            blobKey = P2pIdentity.DeriveSessionKey(id.Key, receiverSpki, salt, "esoagent-p2p-blob-v1", 32);
        }
        var ops = new P2pDhtOps(node);
        await ops.StoreAsync(P2pRecords.ManifestKey(blobId), manifest);
        int chunkCount = P2pRecords.ManifestChunkHashes(manifest).Length;
        int storedTotal = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            var off = i * P2pTransfer.ChunkSize;
            var len = Math.Min(P2pTransfer.ChunkSize, payload.Length - off);
            var plain = payload.AsSpan(off, len).ToArray();
            byte[] data;
            if (encrypted)
            {
                using var gcm = new AesGcm(blobKey!, 16);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var cipher = new byte[plain.Length];
                var tag = new byte[16];
                gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(blobId));
                data = nonce.Concat(cipher).Concat(tag).ToArray();
            }
            else data = plain;
            var val = new JsonObject
            {
                ["kind"] = "chunk",
                ["blob"] = blobId,
                ["index"] = i,
                ["enc"] = encrypted,
                ["data"] = P2pJson.B64(data),
            };
            storedTotal += await ops.StoreAsync(P2pRecords.ChunkKey(blobId, i), val);
        }
        return (blobId, new JsonObject
        {
            ["blob"] = blobId,
            ["toCode"] = toCode,
            ["encrypted"] = encrypted,
            ["bytes"] = payload.Length,
            ["chunks"] = chunkCount,
            ["storeAcks"] = storedTotal,
        });
    }

    public static async Task<JsonObject> GetAsync(P2pNode node, string fromCode, byte[] fromSpki, string blobId)
    {
        var ops = new P2pDhtOps(node);
        var (manifests, _) = await ops.LookupValueAllAsync(P2pRecords.ManifestKey(blobId));
        JsonObject? manifest = null;
        int badManifests = 0;
        foreach (var m in manifests)
        {
            var (ok, _) = P2pRecords.VerifyManifest(m, fromCode);
            if (ok) { manifest = m; break; }
            badManifests++;
        }
        if (manifest == null)
            return new JsonObject { ["ok"] = false, ["error"] = "no valid manifest", ["badManifests"] = badManifests };
        var encrypted = false;
        var hashes = P2pRecords.ManifestChunkHashes(manifest);
        var chunks = new byte[hashes.Length][];
        var detections = new JsonArray();
        byte[]? blobKey = null;
        // Encryption mode is discovered from the first chunk value; derive the key then.
        for (int i = 0; i < hashes.Length; i++)
        {
            var (candidates, _) = await ops.LookupValueAllAsync(P2pRecords.ChunkKey(blobId, i));
            bool done = false;
            foreach (var cand in candidates)
            {
                if (P2pJson.S(cand, "kind") != "chunk" || P2pJson.I(cand, "index") != i) continue;
                encrypted = P2pJson.B(cand, "enc");
                var raw = P2pJson.B64(cand, "data");
                byte[] plain;
                if (encrypted)
                {
                    blobKey ??= P2pIdentity.DeriveSessionKey(node.Identity.Key, fromSpki, P2pHex.Sha256(Encoding.UTF8.GetBytes("blob:" + blobId)), "esoagent-p2p-blob-v1", 32);
                    try
                    {
                        using var gcm = new AesGcm(blobKey, 16);
                        var cipher = raw[12..^16];
                        plain = new byte[cipher.Length];
                        gcm.Decrypt(raw[..12], cipher, raw[^16..], plain, Encoding.UTF8.GetBytes(blobId));
                    }
                    catch (CryptographicException)
                    {
                        detections.Add(new JsonObject { ["index"] = i, ["layer"] = "aes-gcm" });
                        continue;
                    }
                }
                else plain = raw;
                if (P2pHex.Sha256Hex(plain) != hashes[i])
                {
                    detections.Add(new JsonObject { ["index"] = i, ["layer"] = "manifest-sha256" });
                    continue;
                }
                chunks[i] = plain;
                done = true;
                break;
            }
            if (!done)
                return new JsonObject { ["ok"] = false, ["error"] = $"chunk {i} unverifiable from every holder", ["detections"] = detections };
        }
        var full = new byte[P2pJson.L(manifest, "payloadBytes")];
        int pos = 0;
        foreach (var c in chunks) { c!.CopyTo(full, pos); pos += c.Length; }
        var okFull = P2pHex.Sha256Hex(full) == P2pJson.S(manifest, "payloadSha256");
        return new JsonObject
        {
            ["ok"] = okFull,
            ["blob"] = blobId,
            ["encrypted"] = encrypted,
            ["bytes"] = full.Length,
            ["chunks"] = hashes.Length,
            ["detections"] = detections,
            ["payloadHashOk"] = okFull,
        };
    }
}
