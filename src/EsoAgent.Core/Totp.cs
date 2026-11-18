using System.Security.Cryptography;
using System.Text;

namespace EsoAgent.Core;

/// <summary>
/// RFC 4648 base32 (no padding) — the encoding authenticator apps expect for
/// TOTP secrets. Decode is tolerant of spaces, dashes and lowercase.
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException($"Invalid base32 character '{c}'.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
            }
        }
        return bytes.ToArray();
    }
}

/// <summary>
/// TOTP second factor for owner panel logins (v1.2, RFC 6238): HMAC-SHA1,
/// 30-second step, 6 digits, verified with a ±1 step window for clock drift.
/// Pure logic — verified against the RFC 6238 test vectors in the test suite.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    /// <summary>Generates a fresh random secret, base32-encoded (20 bytes = 160 bits).</summary>
    public static string NewSecretBase32() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    public static string ComputeCode(byte[] secret, long unixSeconds)
    {
        var counter = unixSeconds / StepSeconds;
        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8) | (hash[offset + 3] & 0xFF);
        var code = binary % 1_000_000;
        return code.ToString($"D{Digits}");
    }

    /// <summary>Verifies a user-entered code against the secret at the given time,
    /// accepting the previous and next step (±30 s of drift).</summary>
    public static bool Verify(string secretBase32, string? code, long unixSeconds)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var normalized = new string(code.Where(char.IsDigit).ToArray());
        if (normalized.Length != Digits) return false;
        byte[] secret;
        try { secret = Base32.Decode(secretBase32); }
        catch { return false; }
        for (var step = -1; step <= 1; step++)
        {
            var candidate = ComputeCode(secret, unixSeconds + step * StepSeconds);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(normalized)))
                return true;
        }
        return false;
    }

    /// <summary>The otpauth:// URI authenticator apps scan (rendered as text in the
    /// panels — the owner can also type the secret manually).</summary>
    public static string OtpauthUri(string secretBase32, string account, string issuer = "EsoAgent")
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
           $"?secret={secretBase32}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}
