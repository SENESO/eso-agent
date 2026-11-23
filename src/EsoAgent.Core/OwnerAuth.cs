using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EsoAgent.Core;

/// <summary>
/// OWNER authentication for the control panel — deliberately separate from the
/// API token. Hierarchy: the owner (password) outranks the API token. Only the
/// owner can pause/resume API access and rotate the API token; those operations
/// are not exposed to the token at all.
///
/// Password stored as PBKDF2-SHA256 (Rfc2898DeriveBytes), random 16-byte salt,
/// 210k iterations. Sessions are random server-side ids in an HttpOnly,
/// SameSite=Strict cookie; every session carries a CSRF token required on
/// mutating panel calls. Login attempts are rate-limited with lockout backoff.
/// </summary>
public sealed class OwnerPasswordRecord
{
    public string Salt { get; set; } = "";        // base64
    public string Hash { get; set; } = "";        // base64
    public int Iterations { get; set; } = 210_000;
    /// <summary>TOTP second factor (v1.2): base32 secret, present only after the
    /// owner enrolls from the panel. The secret is a credential — it lives only
    /// in owner.json (same protection as the password hash).</summary>
    public string? TotpSecretBase32 { get; set; }
    public bool TotpEnabled { get; set; }
}

public static class OwnerPassword
{
    public const int DefaultIterations = 210_000;

    public static OwnerPasswordRecord Create(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return new OwnerPasswordRecord
        {
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            Iterations = iterations,
        };
    }

    public static bool Verify(string password, OwnerPasswordRecord record)
    {
        try
        {
            var salt = Convert.FromBase64String(record.Salt);
            var expected = Convert.FromBase64String(record.Hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, record.Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Writes owner.json (used by the heads' `set-owner-password` command and installers).</summary>
    public static void SaveRecord(string path, OwnerPasswordRecord record)
    {
        StateFile.WriteJson(path, record);
    }
}

/// <summary>Loads and holds the owner password record (owner.json in the data dir).</summary>
public sealed class OwnerAuthStore
{
    private OwnerPasswordRecord? _record;
    private string? _path;
    private string? _pendingTotpSecret;
    public bool IsConfigured => _record != null;

    public void Load(string path)
    {
        _path = path;
        // Through StateFile (defect-8): a torn owner.json recovers the
        // record from its last-good backup instead of silently
        // un-configuring owner auth; a doubly-corrupt file is
        // quarantined (bytes preserved), never silently replaced.
        var status = StateFile.TryReadJson<OwnerPasswordRecord>(path, out var record);
        _record = record;
        if (status == StateFileRead.Corrupt) StateFile.QuarantineCorrupt(path);
    }

    public bool Verify(string password) => _record != null && OwnerPassword.Verify(password, _record);

    /// <summary>Replaces the in-memory record (owner password change from the panel).</summary>
    public void SetRecord(OwnerPasswordRecord record) => _record = record;

    // ----- TOTP second factor (v1.2) -----

    public bool TotpEnabled => _record is { TotpEnabled: true } && !string.IsNullOrEmpty(_record.TotpSecretBase32);

    public bool VerifyTotp(string? code)
        => TotpEnabled && Totp.Verify(_record!.TotpSecretBase32!, code, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>Starts enrollment: generates a secret held in memory only until
    /// the owner confirms with a valid code from their authenticator app.</summary>
    public string BeginTotpEnrollment()
    {
        _pendingTotpSecret = Totp.NewSecretBase32();
        return _pendingTotpSecret;
    }

    /// <summary>Confirms enrollment with a code from the pending secret; on
    /// success the secret is persisted into owner.json and 2FA is live.</summary>
    public bool ConfirmTotpEnrollment(string? code)
    {
        if (_record == null || _pendingTotpSecret == null) return false;
        if (!Totp.Verify(_pendingTotpSecret, code, DateTimeOffset.UtcNow.ToUnixTimeSeconds())) return false;
        _record.TotpSecretBase32 = _pendingTotpSecret;
        _record.TotpEnabled = true;
        _pendingTotpSecret = null;
        Save();
        return true;
    }

    /// <summary>Turns 2FA off and erases the secret (panel action after password
    /// re-entry, or the local `disable-2fa` recovery command).</summary>
    public void DisableTotp()
    {
        if (_record == null) return;
        _record.TotpSecretBase32 = null;
        _record.TotpEnabled = false;
        _pendingTotpSecret = null;
        Save();
    }

    /// <summary>Copies the TOTP enrollment onto a replacement record, so an
    /// owner password change never silently drops the second factor.</summary>
    public void CarryTotpTo(OwnerPasswordRecord next)
    {
        if (_record == null) return;
        next.TotpSecretBase32 = _record.TotpSecretBase32;
        next.TotpEnabled = _record.TotpEnabled;
    }

    /// <summary>Persists the current record back to the loaded owner.json path.</summary>
    public void Save()
    {
        if (_record != null && _path != null) OwnerPassword.SaveRecord(_path, _record);
    }
}

/// <summary>
/// Pre-auth tokens for the two-step owner login (v1.2): a correct password
/// with 2FA enabled mints one of these instead of a session; presenting a
/// valid TOTP code within 5 minutes exchanges it for the real session.
/// Single-use, in-memory, bound to nothing else — the token IS the proof
/// that step one passed.
/// </summary>
public sealed class PendingTotpStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new();
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public string Create()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = DateTimeOffset.UtcNow.Add(Lifetime);
        return token;
    }

    /// <summary>Validates and consumes a token (one attempt burns it only on success;
    /// a wrong code keeps it — the login rate limiter guards brute force).</summary>
    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        return _tokens.TryGetValue(token, out var expires) && expires > DateTimeOffset.UtcNow;
    }

    public void Consume(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _tokens.TryRemove(token, out _);
    }
}

/// <summary>One owner-panel session.</summary>
public sealed class OwnerSession
{
    public required string Id { get; init; }
    public required string CsrfToken { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>In-memory session store. Sessions live 12 hours; not persisted (a service restart logs the owner out — acceptable and safer).</summary>
public sealed class OwnerSessionStore
{
    private readonly ConcurrentDictionary<string, OwnerSession> _sessions = new();
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public OwnerSession Create()
    {
        var session = new OwnerSession
        {
            Id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CsrfToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ExpiresAt = DateTimeOffset.UtcNow.Add(Lifetime),
        };
        _sessions[session.Id] = session;
        return session;
    }

    /// <summary>Live (unexpired) session count — the tray's owner sign-in indicator.</summary>
    public int ActiveCount
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var n = 0;
            foreach (var s in _sessions.Values) if (s.ExpiresAt > now) n++;
            return n;
        }
    }

    public OwnerSession? Validate(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (!_sessions.TryGetValue(id, out var s)) return null;
        if (s.ExpiresAt <= DateTimeOffset.UtcNow) { _sessions.TryRemove(id, out _); return null; }
        return s;
    }

    public void Revoke(string? id)
    {
        if (!string.IsNullOrEmpty(id)) _sessions.TryRemove(id, out _);
    }

    public static bool CheckCsrf(OwnerSession session, string? provided)
        => !string.IsNullOrEmpty(provided) &&
           CryptographicOperations.FixedTimeEquals(
               System.Text.Encoding.UTF8.GetBytes(session.CsrfToken),
               System.Text.Encoding.UTF8.GetBytes(provided));
}

/// <summary>
/// Login rate limiting: after 5 consecutive failures the source is locked out,
/// with the lock doubling each time (30s → 60s → … → max 15 min). A successful
/// login resets the counter.
/// </summary>
public sealed class LoginRateLimiter
{
    private sealed class Entry { public int Failures; public DateTimeOffset LockedUntil; public int LockCount; }
    private readonly ConcurrentDictionary<string, Entry> _bySource = new();

    public bool IsLockedOut(string source, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (_bySource.TryGetValue(source, out var e) && e.LockedUntil > DateTimeOffset.UtcNow)
        {
            remaining = e.LockedUntil - DateTimeOffset.UtcNow;
            return true;
        }
        return false;
    }

    public void RecordFailure(string source)
    {
        var e = _bySource.GetOrAdd(source, _ => new Entry());
        lock (e)
        {
            e.Failures++;
            if (e.Failures >= 5)
            {
                e.LockCount++;
                var seconds = Math.Min(30 * (1 << Math.Min(e.LockCount - 1, 5)), 900);
                e.LockedUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
                e.Failures = 0;
            }
        }
    }

    public void RecordSuccess(string source) => _bySource.TryRemove(source, out _);
}
