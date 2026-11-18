using System.Security.Cryptography;
using System.Text;

namespace EsoAgent.Core;

// =====================================================================
// Item 41 — access codes (control grants): the TeamViewer-style handover,
// done our way. The owner (panel / app / `eso access grant`) mints a short
// code for ONE device, scoped by capabilities, with an expiry and an
// optional use count. Whoever holds the code redeems it at the fleet
// server and receives a signed, scoped, expiring GRANT TOKEN — no account,
// no password sharing. Grants are executed by the server as panel-signed
// commands (devices already trust the panel key), are checked against the
// server-side grant record on EVERY use, can be revoked instantly, and
// never include owner-only powers (revoke operator, rotate keys, change
// owner password, mint further grants). Every redemption and every action
// under a grant lands in the unified activity timeline, attributed.
// =====================================================================

/// <summary>What a grant may do. Least-privilege: anything not listed here
/// (and not in <see cref="Allows"/>) is denied.</summary>
public sealed class GrantScope
{
    public bool Exec { get; set; } = true;
    public bool Files { get; set; } = true;
    public bool Clipboard { get; set; } = true;
    public bool Tunnels { get; set; }
    public bool DesktopControl { get; set; } = true;
    /// <summary>Desktop is allowed but view-only (input + privacy dropped
    /// at the relay; the device is also told View mode).</summary>
    public bool DesktopViewOnly { get; set; }

    /// <summary>Canonical flag string covered by the token signature.</summary>
    public string Canonical() => string.Concat(
        Exec ? "1" : "0", Files ? "1" : "0", Clipboard ? "1" : "0",
        Tunnels ? "1" : "0", DesktopControl ? "1" : "0", DesktopViewOnly ? "1" : "0");

    /// <summary>Which device API paths this scope may invoke through the
    /// grant command channel. Read-only awareness (status/doctor) is always
    /// allowed; everything unlisted is denied.</summary>
    public bool Allows(string path)
    {
        var p = path.Split('?')[0].TrimEnd('/').ToLowerInvariant();
        if (p is "/api/status" or "/api/doctor") return true;
        if (Exec && (p is "/api/exec" or "/api/shell" || p.StartsWith("/api/sessions") ||
                     p.StartsWith("/api/launch") || p.StartsWith("/api/processes") || p.StartsWith("/api/power"))) return true;
        if (Files && p.StartsWith("/api/files")) return true;
        if (Exec && (p is "/api/diagnose" or "/api/inventory" or "/api/telemetry")) return true;
        return false;
    }

    public string Describe() =>
        $"exec={(Exec ? "on" : "off")} files={(Files ? "on" : "off")} clipboard={(Clipboard ? "on" : "off")} " +
        $"tunnels={(Tunnels ? "on" : "off")} desktop={(DesktopControl ? (DesktopViewOnly ? "view-only" : "control") : "off")}";
}

/// <summary>A minted access code as stored server-side. The code itself is
/// NEVER stored — only its SHA-256 (codes are short; a leaked store must
/// not hand out working codes). The plaintext is shown once at creation.</summary>
public sealed class AccessCodeRecord
{
    public string Id { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public string DeviceCode { get; set; } = "";
    public GrantScope Scope { get; set; } = new();
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public int Uses { get; set; }
    public bool Revoked { get; set; }
}

/// <summary>One redemption of a code = one live grant. The record is the
/// source of truth at every use; the token is just its signed carrier.</summary>
public sealed class GrantRecord
{
    public string Id { get; set; } = "";
    public string CodeId { get; set; } = "";
    public string DeviceCode { get; set; } = "";
    public GrantScope Scope { get; set; } = new();
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string RedeemedFrom { get; set; } = "";
    public bool Revoked { get; set; }
    public int Actions { get; set; }
}

/// <summary>The signed carrier a grantee presents on every call.</summary>
public sealed class GrantTokenDto
{
    public string GrantId { get; set; } = "";
    public string DeviceCode { get; set; } = "";
    public GrantScope Scope { get; set; } = new();
    public long ExpiresAtUnix { get; set; }
    public string Signature { get; set; } = "";
}

public static class GrantSigning
{
    public static string Canonical(string grantId, string deviceCode, long expiresAtUnix, GrantScope scope)
        => string.Join('\n', "fleet-grant", grantId, deviceCode, expiresAtUnix.ToString(), scope.Canonical());

    public static string Sign(ECDsa key, string grantId, string deviceCode, long expiresAtUnix, GrantScope scope)
        => Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(Canonical(grantId, deviceCode, expiresAtUnix, scope)), HashAlgorithmName.SHA256));

    public static bool Verify(string publicKeySpkiBase64, GrantTokenDto token)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            return key.VerifyData(
                Encoding.UTF8.GetBytes(Canonical(token.GrantId, token.DeviceCode, token.ExpiresAtUnix, token.Scope)),
                Convert.FromBase64String(token.Signature), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }
}

public static class AccessCodePolicy
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O/1/I/L

    /// <summary>A fresh 8-character code, displayed XXXX-XXXX.</summary>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[8];
        for (int i = 0; i < 8; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return new string(chars);
    }

    public static string Normalize(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)))).ToLowerInvariant();

    public static string Display(string code)
    {
        var n = Normalize(code);
        return n.Length == 8 ? n[..4] + "-" + n[4..] : n;
    }

    /// <summary>Can this code be redeemed right now? Returns a plain reason
    /// when it cannot — the same reasons are shown to owners and grantees.</summary>
    public static (bool Ok, string Reason) CanRedeem(AccessCodeRecord code, DateTimeOffset now)
    {
        if (code.Revoked) return (false, "This access code has been revoked.");
        if (now >= code.ExpiresAt) return (false, "This access code has expired.");
        if (code.MaxUses is { } max && code.Uses >= max) return (false, "This access code has no uses left.");
        return (true, "");
    }

    /// <summary>Is this grant usable for one more action right now?</summary>
    public static (bool Ok, string Reason) GrantUsable(GrantRecord grant, DateTimeOffset now)
    {
        if (grant.Revoked) return (false, "This grant has been revoked.");
        if (now >= grant.ExpiresAt) return (false, "This grant has expired.");
        return (true, "");
    }
}

// ----- API shapes -----

public sealed class AccessCodeCreateRequest
{
    public string DeviceCode { get; set; } = "";
    public GrantScope Scope { get; set; } = new();
    public int ExpiresMinutes { get; set; } = 60;
    public int? MaxUses { get; set; }
}

public sealed class AccessCodeCreatedResponse
{
    public string Id { get; set; } = "";
    /// <summary>The plaintext code — shown ONCE at creation, never stored.</summary>
    public string Code { get; set; } = "";
    public string DeviceCode { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public GrantScope Scope { get; set; } = new();
}

public sealed class AccessRedeemRequest
{
    public string Code { get; set; } = "";
}

public sealed class AccessRedeemResponse
{
    public GrantTokenDto Token { get; set; } = new();
    public string DeviceCode { get; set; } = "";
    public string DeviceName { get; set; } = "";
}

public sealed class AccessRevokeRequest
{
    /// <summary>A grant id OR a code id — revoking a code also blocks all
    /// future redemptions; revoking a grant kills that one token.</summary>
    public string Id { get; set; } = "";
}

public sealed class AccessCodeDto
{
    public string Id { get; set; } = "";
    public string DeviceCode { get; set; } = "";
    public GrantScope Scope { get; set; } = new();
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public int Uses { get; set; }
    public bool Revoked { get; set; }
    public bool Expired { get; set; }
}

public sealed class GrantDto
{
    public string Id { get; set; } = "";
    public string CodeId { get; set; } = "";
    public string DeviceCode { get; set; } = "";
    public GrantScope Scope { get; set; } = new();
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string RedeemedFrom { get; set; } = "";
    public bool Revoked { get; set; }
    public bool Active { get; set; }
    public int Actions { get; set; }
}
