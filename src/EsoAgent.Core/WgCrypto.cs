using System.Numerics;
using System.Security.Cryptography;

namespace EsoAgent.Core;

/// <summary>
/// X25519 (RFC 7748) — WireGuard's key exchange. The BCL has no Curve25519, so
/// this is a straightforward BigInteger Montgomery ladder. It runs at install /
/// enrollment time only (key generation), so the non-constant-time BigInteger
/// arithmetic is acceptable here; WireGuard itself does all session crypto.
/// Verified against the RFC 7748 test vectors in the unit tests.
/// </summary>
public static class WgCrypto
{
    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger A24 = 121665;

    /// <summary>Raw X25519: scalar × u-coordinate → u-coordinate (32 bytes each, little-endian).</summary>
    public static byte[] X25519(byte[] scalar, byte[] uCoordinate)
    {
        if (scalar.Length != 32 || uCoordinate.Length != 32)
            throw new ArgumentException("X25519 inputs must be 32 bytes.");

        var k = (byte[])scalar.Clone();
        k[0] &= 248; k[31] &= 127; k[31] |= 64; // clamp per RFC 7748
        var n = DecodeLittleEndian(k);

        var uBytes = (byte[])uCoordinate.Clone();
        uBytes[31] &= 127; // mask the top bit of u per RFC 7748
        var x1 = DecodeLittleEndian(uBytes);

        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        var swap = 0;
        for (int t = 254; t >= 0; t--)
        {
            var kt = (int)((n >> t) & 1);
            swap ^= kt;
            if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            swap = kt;

            var a = Mod(x2 + z2); var aa = Mod(a * a);
            var b = Mod(x2 - z2); var bb = Mod(b * b);
            var e = Mod(aa - bb);
            var c = Mod(x3 + z3); var d = Mod(x3 - z3);
            var da = Mod(d * a); var cb = Mod(c * b);
            x3 = Mod((da + cb) * (da + cb));
            z3 = Mod(x1 * Mod((da - cb) * (da - cb)));
            x2 = Mod(aa * bb);
            z2 = Mod(e * Mod(aa + A24 * e));
        }
        if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }

        var result = Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
        return EncodeLittleEndian(result);
    }

    /// <summary>Generates a WireGuard keypair: (privateKeyB64, publicKeyB64), standard base64 as wg uses.</summary>
    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        var priv = RandomNumberGenerator.GetBytes(32);
        var pub = PublicKeyFromPrivate(priv);
        return (Convert.ToBase64String(priv), Convert.ToBase64String(pub));
    }

    public static byte[] PublicKeyFromPrivate(byte[] privateKey)
    {
        var basepoint = new byte[32];
        basepoint[0] = 9;
        return X25519(privateKey, basepoint);
    }

    private static BigInteger Mod(BigInteger x) => ((x % P) + P) % P;

    private static BigInteger DecodeLittleEndian(byte[] bytes)
    {
        var extended = new byte[bytes.Length + 1]; // sign byte 0 → always positive
        Array.Copy(bytes, extended, bytes.Length);
        return new BigInteger(extended);
    }

    private static byte[] EncodeLittleEndian(BigInteger value)
    {
        var raw = value.ToByteArray(); // little-endian, may include sign byte
        var output = new byte[32];
        Array.Copy(raw, output, Math.Min(32, raw.Length));
        return output;
    }
}
