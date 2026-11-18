using System.Security.Cryptography;
using System.Text;

namespace EsoAgent.Core;

/// <summary>
/// Request signing scheme shared by server and client.
/// Signature = HMAC-SHA256(token, method + "\n" + pathAndQuery + "\n" + unixTimestamp + "\n" + hex(sha256(body)))
/// Carried in headers X-Eso-Timestamp / X-Eso-Signature. Timestamps outside ±120 s are
/// rejected (anti-replay). Comparison is constant-time.
/// </summary>
public static class HmacAuth
{
    public const string TimestampHeader = "X-Eso-Timestamp";
    public const string SignatureHeader = "X-Eso-Signature";
    public const int MaxSkewSeconds = 120;

    public static string Sha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string CanonicalString(string method, string pathAndQuery, long unixTimestamp, ReadOnlySpan<byte> body)
        => string.Join('\n', method.ToUpperInvariant(), pathAndQuery, unixTimestamp.ToString(), Sha256Hex(body));

    public static string Sign(byte[] token, string method, string pathAndQuery, long unixTimestamp, ReadOnlySpan<byte> body)
    {
        var canonical = Encoding.UTF8.GetBytes(CanonicalString(method, pathAndQuery, unixTimestamp, body));
        using var hmac = new HMACSHA256(token);
        return Convert.ToHexString(hmac.ComputeHash(canonical)).ToLowerInvariant();
    }

    public static bool Verify(byte[] token, string method, string pathAndQuery, long unixTimestamp,
        ReadOnlySpan<byte> body, string providedSignatureHex, long nowUnix)
    {
        if (Math.Abs(nowUnix - unixTimestamp) > MaxSkewSeconds) return false;
        byte[] expected, provided;
        try
        {
            expected = Convert.FromHexString(Sign(token, method, pathAndQuery, unixTimestamp, body));
            provided = Convert.FromHexString(providedSignatureHex.Trim());
        }
        catch (FormatException) { return false; }
        return expected.Length == provided.Length && CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>Loads a token file: raw base64 of the 32-byte token (whitespace tolerated).</summary>
    public static byte[] LoadTokenFile(string path)
    {
        var text = File.ReadAllText(path).Trim();
        var token = Convert.FromBase64String(text);
        if (token.Length < 32) throw new InvalidDataException("Token must be at least 32 bytes.");
        return token;
    }

    public static byte[] GenerateToken() => RandomNumberGenerator.GetBytes(32);
}
