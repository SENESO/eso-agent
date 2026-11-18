using System.Security.Cryptography;

namespace EsoAgent.Core;

/// <summary>
/// A device's permanent identity, created on first run and kept for the life of
/// the install: an ECDSA P-256 keypair (device.key / device.pub in the data dir)
/// and a short human device code derived from the public key fingerprint:
/// SHA-256(SPKI)[0..5) in Crockford base32 → 8 chars, grouped XXXX-XXXX.
/// The code is stable because the key is stable; reinstalling (new key) = new code.
/// </summary>
public sealed class DeviceIdentity
{
    public required ECDsa Key { get; init; }
    public required string Code { get; init; }
    public required string PublicKeyBase64 { get; init; }

    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string CodeFromPublicKey(ReadOnlySpan<byte> spkiBytes)
    {
        var hash = SHA256.HashData(spkiBytes);
        // 5 bytes = 40 bits = exactly 8 base32 chars.
        ulong value = 0;
        for (int i = 0; i < 5; i++) value = (value << 8) | hash[i];
        Span<char> chars = stackalloc char[8];
        for (int i = 7; i >= 0; i--) { chars[i] = Crockford[(int)(value & 31)]; value >>= 5; }
        return new string(chars[..4]) + "-" + new string(chars[4..]);
    }

    public static bool LooksLikeCode(string value)
    {
        var v = value.Trim().ToUpperInvariant();
        return v.Length == 9 && v[4] == '-' && v.All(c => c == '-' || Crockford.Contains(c));
    }

    public static DeviceIdentity LoadOrCreate(AgentConfig cfg)
    {
        var keyPath = cfg.ResolvePath("device.key");
        ECDsa key;
        if (File.Exists(keyPath))
        {
            key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(keyPath));
        }
        else
        {
            key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
            try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }
        var spki = key.ExportSubjectPublicKeyInfo();
        return new DeviceIdentity
        {
            Key = key,
            Code = CodeFromPublicKey(spki),
            PublicKeyBase64 = Convert.ToBase64String(spki),
        };
    }

    /// <summary>Signs data with the device key (SHA-256) — used in the fleet enrollment challenge-response.</summary>
    public string SignBase64(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(Key.SignData(data, HashAlgorithmName.SHA256));

    public static bool VerifyWithPublicKey(string publicKeyBase64, ReadOnlySpan<byte> data, string signatureBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(data, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }
}

/// <summary>
/// Operator command signing. The operator (the assistant's client, or the fleet
/// panel's own key) signs every relayed command; devices verify the signature
/// against the public key(s) pinned at enrollment approval. The relay server
/// therefore cannot forge commands — a compromised relay can drop or delay
/// traffic, never create it.
/// </summary>
public static class OperatorSigning
{
    public static string CanonicalCommand(string commandId, long issuedAtUnix, string method, string path, ReadOnlySpan<byte> body)
        => string.Join('\n', "fleet-command", commandId, issuedAtUnix.ToString(), method.ToUpperInvariant(), path, HmacAuth.Sha256Hex(body));

    public static string SignCommand(ECDsa key, string commandId, long issuedAtUnix, string method, string path, ReadOnlySpan<byte> body)
    {
        var canonical = System.Text.Encoding.UTF8.GetBytes(CanonicalCommand(commandId, issuedAtUnix, method, path, body));
        return Convert.ToBase64String(key.SignData(canonical, HashAlgorithmName.SHA256));
    }

    public static bool VerifyCommand(string publicKeyBase64, string signatureBase64, string commandId, long issuedAtUnix, string method, string path, ReadOnlySpan<byte> body, long nowUnix)
    {
        if (Math.Abs(nowUnix - issuedAtUnix) > HmacAuth.MaxSkewSeconds) return false;
        var canonical = System.Text.Encoding.UTF8.GetBytes(CanonicalCommand(commandId, issuedAtUnix, method, path, body));
        return DeviceIdentity.VerifyWithPublicKey(publicKeyBase64, canonical, signatureBase64);
    }

    public static string CanonicalAdmin(string action, string commandId, long issuedAtUnix, string payloadJson)
        => string.Join('\n', "fleet-admin", action, commandId, issuedAtUnix.ToString(),
            HmacAuth.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(payloadJson)));

    public static string SignAdmin(ECDsa key, string action, string commandId, long issuedAtUnix, string payloadJson)
    {
        var canonical = System.Text.Encoding.UTF8.GetBytes(CanonicalAdmin(action, commandId, issuedAtUnix, payloadJson));
        return Convert.ToBase64String(key.SignData(canonical, HashAlgorithmName.SHA256));
    }

    public static bool VerifyAdmin(string publicKeyBase64, string signatureBase64, string action, string commandId, long issuedAtUnix, string payloadJson, long nowUnix)
    {
        if (Math.Abs(nowUnix - issuedAtUnix) > HmacAuth.MaxSkewSeconds) return false;
        var canonical = System.Text.Encoding.UTF8.GetBytes(CanonicalAdmin(action, commandId, issuedAtUnix, payloadJson));
        return DeviceIdentity.VerifyWithPublicKey(publicKeyBase64, canonical, signatureBase64);
    }

    // ----- queued commands (v1.1): an explicit, operator-signed validity window -----
    //
    // Immediate commands use the ±120 s window above. A command the operator
    // marks queueable is signed over THIS canonical form instead, which binds
    // an explicit expiresAt (default: issuedAt + 24 h). The fleet server may
    // store and deliver it any time before expiry; the device verifies the
    // signature and the window itself. The relay gains no power: altering any
    // field (including the expiry) invalidates the signature.

    public static string CanonicalQueuedCommand(string commandId, long issuedAtUnix, long expiresAtUnix, string method, string path, ReadOnlySpan<byte> body)
        => string.Join('\n', "fleet-queued-command", commandId, issuedAtUnix.ToString(), expiresAtUnix.ToString(), method.ToUpperInvariant(), path, HmacAuth.Sha256Hex(body));

    public static string SignQueuedCommand(ECDsa key, string commandId, long issuedAtUnix, long expiresAtUnix, string method, string path, ReadOnlySpan<byte> body)
    {
        var canonical = System.Text.Encoding.UTF8.GetBytes(CanonicalQueuedCommand(commandId, issuedAtUnix, expiresAtUnix, method, path, body));
        return Convert.ToBase64String(key.SignData(canonical, HashAlgorithmName.SHA256));
    }

    public static bool VerifyQueuedCommand(string publicKeyBase64, string signatureBase64, string commandId, long issuedAtUnix, long expiresAtUnix, string method, string path, ReadOnlySpan<byte> body, long nowUnix)
    {
        if (expiresAtUnix <= issuedAtUnix) return false;
        if (nowUnix > expiresAtUnix) return false;                       // expired — never deliver late
        if (issuedAtUnix - nowUnix > HmacAuth.MaxSkewSeconds) return false; // issued implausibly far in the future
        var canonical = System.Text.Encoding.UTF8.GetBytes(CanonicalQueuedCommand(commandId, issuedAtUnix, expiresAtUnix, method, path, body));
        return DeviceIdentity.VerifyWithPublicKey(publicKeyBase64, canonical, signatureBase64);
    }

    // ----- relayed streams (v1.2): desktop + tunnels over the fleet channel -----
    //
    // A stream-open is signed exactly like a command: the client (operator key)
    // or the fleet panel (panel key) signs streamId + issuedAt + kind + payload
    // hash; the server relays the envelope untouched and the DEVICE verifies
    // the signature against its pinned keys before opening anything. The relay
    // therefore cannot open desktop sessions or tunnels on its own.

    public static string CanonicalStream(string streamId, long issuedAtUnix, string kind, string payloadJson)
        => string.Join('\n', "fleet-stream", streamId, issuedAtUnix.ToString(), kind,
            HmacAuth.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(payloadJson)));

    public static string SignStream(ECDsa key, string streamId, long issuedAtUnix, string kind, string payloadJson)
    {
        var canonical = System.Text.Encoding.UTF8.GetBytes(CanonicalStream(streamId, issuedAtUnix, kind, payloadJson));
        return Convert.ToBase64String(key.SignData(canonical, HashAlgorithmName.SHA256));
    }

    public static bool VerifyStream(string publicKeyBase64, string signatureBase64, string streamId, long issuedAtUnix, string kind, string payloadJson, long nowUnix)
    {
        if (Math.Abs(nowUnix - issuedAtUnix) > HmacAuth.MaxSkewSeconds) return false;
        var canonical = System.Text.Encoding.UTF8.GetBytes(CanonicalStream(streamId, issuedAtUnix, kind, payloadJson));
        return DeviceIdentity.VerifyWithPublicKey(publicKeyBase64, canonical, signatureBase64);
    }

    // ----- update rollout fetch (v1.2) -----
    //
    // A rollout tells a device: "download the package at this server-relative
    // path; its SHA-256 must be X". That instruction is signed (operator or
    // panel key) over path + hash, and the device verifies the signature AND
    // the downloaded bytes' hash before staging — a compromised relay can
    // neither substitute a package nor redirect the download elsewhere.

    public static string CanonicalUpdateFetch(string path, string sha256Hex)
        => string.Join('\n', "update-fetch", path, sha256Hex.Trim().ToLowerInvariant());

    public static string SignUpdateFetch(ECDsa key, string path, string sha256Hex)
    {
        var canonical = System.Text.Encoding.UTF8.GetBytes(CanonicalUpdateFetch(path, sha256Hex));
        return Convert.ToBase64String(key.SignData(canonical, HashAlgorithmName.SHA256));
    }

    public static bool VerifyUpdateFetch(string publicKeyBase64, string signatureBase64, string path, string sha256Hex)
    {
        var canonical = System.Text.Encoding.UTF8.GetBytes(CanonicalUpdateFetch(path, sha256Hex));
        return DeviceIdentity.VerifyWithPublicKey(publicKeyBase64, canonical, signatureBase64);
    }

    /// <summary>Request signing for operator → server API calls (same canonical shape as HMAC auth, ECDSA instead).</summary>
    public static string SignRequest(ECDsa key, string method, string pathAndQuery, long unixTimestamp, ReadOnlySpan<byte> body)
    {
        var canonical = System.Text.Encoding.UTF8.GetBytes(HmacAuth.CanonicalString(method, pathAndQuery, unixTimestamp, body));
        return Convert.ToBase64String(key.SignData(canonical, HashAlgorithmName.SHA256));
    }

    public static bool VerifyRequest(string publicKeyBase64, string signatureBase64, string method, string pathAndQuery, long unixTimestamp, ReadOnlySpan<byte> body, long nowUnix)
    {
        if (Math.Abs(nowUnix - unixTimestamp) > HmacAuth.MaxSkewSeconds) return false;
        var canonical = System.Text.Encoding.UTF8.GetBytes(HmacAuth.CanonicalString(method, pathAndQuery, unixTimestamp, body));
        return DeviceIdentity.VerifyWithPublicKey(publicKeyBase64, canonical, signatureBase64);
    }
}
