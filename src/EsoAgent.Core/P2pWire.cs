using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EsoAgent.Core;

/// <summary>
/// Framing shared by the whole P2P layer: a 4-byte little-endian length
/// prefix followed by that many bytes. RPC messages are UTF-8 JSON
/// frames; a session connection starts with one JSON frame (the signed
/// open) and then switches to binary AES-GCM frames under the same
/// prefix. Ported from the P2P lab (experiments/p2p-lab) — the design
/// that was measured there ships unchanged unless a comment says why.
/// </summary>
public static class P2pWire
{
    public const int MaxFrame = 4 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream s, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var hdr = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(hdr, payload.Length);
        await s.WriteAsync(hdr, ct);
        await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<byte[]?> ReadFrameAsync(Stream s, CancellationToken ct = default)
    {
        var hdr = new byte[4];
        if (!await ReadExactAsync(s, hdr, ct)) return null;
        var len = BinaryPrimitives.ReadInt32LittleEndian(hdr);
        if (len < 0 || len > MaxFrame) throw new InvalidDataException($"frame length {len} out of range");
        var buf = new byte[len];
        if (!await ReadExactAsync(s, buf, ct)) return null;
        return buf;
    }

    private static async Task<bool> ReadExactAsync(Stream s, Memory<byte> buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf[read..], ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public static Task WriteJsonAsync(Stream s, JsonObject obj, CancellationToken ct = default)
        => WriteFrameAsync(s, Encoding.UTF8.GetBytes(obj.ToJsonString()), ct);

    public static async Task<JsonObject?> ReadJsonAsync(Stream s, CancellationToken ct = default)
    {
        var frame = await ReadFrameAsync(s, ct);
        if (frame == null) return null;
        return JsonNode.Parse(Encoding.UTF8.GetString(frame)) as JsonObject
               ?? throw new InvalidDataException("frame is not a JSON object");
    }
}

/// <summary>Typed accessors for the JSON wire shapes (missing/wrong-typed = default, never throw).</summary>
public static class P2pJson
{
    public static string S(JsonObject o, string key) => o[key]?.GetValue<string>() ?? "";
    public static long L(JsonObject o, string key) => o[key]?.GetValue<long>() ?? 0;
    public static int I(JsonObject o, string key) => o[key]?.GetValue<int>() ?? 0;
    public static bool B(JsonObject o, string key) => o[key]?.GetValue<bool>() ?? false;
    public static byte[] B64(JsonObject o, string key)
    {
        var s = S(o, key);
        return s.Length == 0 ? Array.Empty<byte>() : Convert.FromBase64String(s);
    }
    public static string B64(byte[] b) => Convert.ToBase64String(b);
}

public static class P2pHex
{
    public static string Of(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();
    public static byte[] Parse(string hex) => Convert.FromHexString(hex);
    public static string Sha256Hex(ReadOnlySpan<byte> b) => Of(SHA256.HashData(b));
    public static byte[] Sha256(ReadOnlySpan<byte> b) => SHA256.HashData(b);
}
