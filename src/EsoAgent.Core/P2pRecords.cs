using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EsoAgent.Core;

/// <summary>
/// Key agreement over the product device identity. The ECDSA P-256
/// device key doubles as the ECDH key (same curve point, same private
/// scalar, re-imported as an ECDiffieHellman object), so a device needs
/// no second keypair to bind: the SPKI that derives its device code is
/// the same key it signs records with and agrees session keys from.
/// </summary>
public static class P2pIdentity
{
    /// <summary>The Kademlia node ID for a device public key: SHA-256(SPKI) in full.
    /// A node ID is bound to the key — you cannot claim an ID whose code you cannot sign for.</summary>
    public static byte[] NodeIdOf(ReadOnlySpan<byte> spki) => SHA256.HashData(spki);

    public static ECDiffieHellman CreateEcdh(ECDsa key)
    {
        var parms = key.ExportParameters(true);
        return ECDiffieHellman.Create(parms);
    }

    public static ECDiffieHellmanPublicKey PeerPublicKey(byte[] spki)
    {
        using var tmp = ECDsa.Create();
        tmp.ImportSubjectPublicKeyInfo(spki, out _);
        var q = tmp.ExportParameters(false).Q;
        var parms = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = q };
        return ECDiffieHellman.Create(parms).PublicKey;
    }

    /// <summary>Per-session key material from static-static ECDH + HKDF over a session salt.</summary>
    public static byte[] DeriveSessionKey(ECDsa key, byte[] peerSpki, byte[] salt, string info, int bytes = 32)
    {
        using var ecdh = CreateEcdh(key);
        using var peer = PeerPublicKey(peerSpki);
        var secret = ecdh.DeriveKeyMaterial(peer);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, bytes, salt, Encoding.UTF8.GetBytes(info));
    }
}

/// <summary>
/// The two signed value types the DHT carries.
///
/// Device record: "code XXXX-XXXX can be reached at host:port, key = SPKI",
/// signed by the device key. A node accepts a record only if ALL of these
/// hold: the code is exactly the code derived from the record's own SPKI
/// (binding), the signature verifies over the canonical record body with
/// that SPKI, and the record has not expired. This is what makes the
/// registry poison-proof without a central server: forging a record for
/// someone else's code requires their private key.
///
/// Blob manifest: "payload P (from device F) consists of N chunks; chunk i
/// has plaintext SHA-256 h_i; the whole payload hashes to H", signed by
/// the sender. This is the torrent mechanism — the same role as the
/// infohash + piece hashes in a .torrent file — carried as a DHT value
/// instead of a file.
/// </summary>
public static class P2pRecords
{
    public static byte[] DeviceKey(string code) => P2pHex.Sha256(Encoding.UTF8.GetBytes("record:" + code));
    public static byte[] ManifestKey(string blobId) => P2pHex.Sha256(Encoding.UTF8.GetBytes("manifest:" + blobId));
    public static byte[] ChunkKey(string blobId, int index) => P2pHex.Sha256(Encoding.UTF8.GetBytes($"chunk:{blobId}:{index}"));

    public static string CanonicalDeviceRecord(string code, string spkiB64, string host, int port, long expiresUnix)
        => string.Join('\n', "esoagent-p2p-device-record", code, spkiB64, host, port.ToString(), expiresUnix.ToString());

    public static JsonObject MakeDeviceRecord(DeviceIdentity id, string host, int port, long ttlSeconds = 3600)
    {
        var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSeconds;
        var canonical = CanonicalDeviceRecord(id.Code, id.PublicKeyBase64, host, port, expires);
        return new JsonObject
        {
            ["kind"] = "device-record",
            ["code"] = id.Code,
            ["spki"] = id.PublicKeyBase64,
            ["host"] = host,
            ["port"] = port,
            ["expires"] = expires,
            ["sig"] = id.SignBase64(Encoding.UTF8.GetBytes(canonical)),
        };
    }

    /// <summary>Returns (ok, reason). reason is "" when ok.</summary>
    public static (bool Ok, string Reason) VerifyDeviceRecord(JsonObject rec, long nowUnix)
    {
        try
        {
            if (P2pJson.S(rec, "kind") != "device-record") return (false, "kind");
            var code = P2pJson.S(rec, "code");
            var spkiB64 = P2pJson.S(rec, "spki");
            var spki = Convert.FromBase64String(spkiB64);
            if (DeviceIdentity.CodeFromPublicKey(spki) != code) return (false, "code-key-binding");
            var expires = P2pJson.L(rec, "expires");
            if (expires <= nowUnix) return (false, "expired");
            var canonical = CanonicalDeviceRecord(code, spkiB64, P2pJson.S(rec, "host"), P2pJson.I(rec, "port"), expires);
            if (!DeviceIdentity.VerifyWithPublicKey(spkiB64, Encoding.UTF8.GetBytes(canonical), P2pJson.S(rec, "sig")))
                return (false, "signature");
            return (true, "");
        }
        catch (Exception ex) { return (false, "malformed:" + ex.GetType().Name); }
    }

    public static string CanonicalManifest(string blobId, string fromCode, long payloadBytes, string payloadSha256Hex, string[] chunkSha256Hex)
        => string.Join('\n', "esoagent-p2p-blob-manifest", blobId, fromCode, payloadBytes.ToString(), payloadSha256Hex, string.Join(',', chunkSha256Hex));

    public static JsonObject MakeManifest(DeviceIdentity from, string blobId, byte[] payload, int chunkSize)
    {
        var chunks = new List<string>();
        for (int off = 0; off < payload.Length; off += chunkSize)
        {
            var len = Math.Min(chunkSize, payload.Length - off);
            chunks.Add(P2pHex.Sha256Hex(payload.AsSpan(off, len)));
        }
        if (payload.Length == 0) chunks.Add(P2pHex.Sha256Hex(ReadOnlySpan<byte>.Empty));
        var payloadHash = P2pHex.Sha256Hex(payload);
        var canonical = CanonicalManifest(blobId, from.Code, payload.Length, payloadHash, chunks.ToArray());
        return new JsonObject
        {
            ["kind"] = "blob-manifest",
            ["blob"] = blobId,
            ["fromCode"] = from.Code,
            ["fromSpki"] = from.PublicKeyBase64,
            ["payloadBytes"] = payload.Length,
            ["payloadSha256"] = payloadHash,
            ["chunkSize"] = chunkSize,
            ["chunkSha256"] = new JsonArray(chunks.Select(h => (JsonNode)h).ToArray()),
            ["sig"] = from.SignBase64(Encoding.UTF8.GetBytes(canonical)),
        };
    }

    public static (bool Ok, string Reason) VerifyManifest(JsonObject m, string expectedFromCode)
    {
        try
        {
            if (P2pJson.S(m, "kind") != "blob-manifest") return (false, "kind");
            if (P2pJson.S(m, "fromCode") != expectedFromCode) return (false, "from-code");
            var spkiB64 = P2pJson.S(m, "fromSpki");
            if (DeviceIdentity.CodeFromPublicKey(Convert.FromBase64String(spkiB64)) != expectedFromCode)
                return (false, "code-key-binding");
            var chunks = (m["chunkSha256"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToArray() ?? Array.Empty<string>();
            var canonical = CanonicalManifest(P2pJson.S(m, "blob"), expectedFromCode, P2pJson.L(m, "payloadBytes"), P2pJson.S(m, "payloadSha256"), chunks);
            if (!DeviceIdentity.VerifyWithPublicKey(spkiB64, Encoding.UTF8.GetBytes(canonical), P2pJson.S(m, "sig")))
                return (false, "signature");
            return (true, "");
        }
        catch (Exception ex) { return (false, "malformed:" + ex.GetType().Name); }
    }

    public static string[] ManifestChunkHashes(JsonObject m)
        => (m["chunkSha256"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToArray() ?? Array.Empty<string>();
}
