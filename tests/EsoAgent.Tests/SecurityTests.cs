using System.Text;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

public class HmacAuthTests
{
    private static readonly byte[] Token = Convert.FromBase64String(Convert.ToBase64String(new byte[32]));
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
    private const long Now = 1_800_000_000;

    [Fact]
    public void SignVerify_RoundTrips()
    {
        var sig = HmacAuth.Sign(Token, "POST", "/api/exec", Now, Body);
        Assert.True(HmacAuth.Verify(Token, "POST", "/api/exec", Now, Body, sig, Now));
    }

    [Fact]
    public void Verify_RejectsTamperedBody()
    {
        var sig = HmacAuth.Sign(Token, "POST", "/api/exec", Now, Body);
        var tampered = Encoding.UTF8.GetBytes("{\"hello\":\"evil\"}");
        Assert.False(HmacAuth.Verify(Token, "POST", "/api/exec", Now, tampered, sig, Now));
    }

    [Fact]
    public void Verify_RejectsTamperedPathAndMethod()
    {
        var sig = HmacAuth.Sign(Token, "POST", "/api/exec", Now, Body);
        Assert.False(HmacAuth.Verify(Token, "POST", "/api/power/reboot", Now, Body, sig, Now));
        Assert.False(HmacAuth.Verify(Token, "GET", "/api/exec", Now, Body, sig, Now));
    }

    [Fact]
    public void Verify_RejectsOutsideReplayWindow()
    {
        var sig = HmacAuth.Sign(Token, "GET", "/api/status", Now, Array.Empty<byte>());
        Assert.True(HmacAuth.Verify(Token, "GET", "/api/status", Now, Array.Empty<byte>(), sig, Now + 120));
        Assert.False(HmacAuth.Verify(Token, "GET", "/api/status", Now, Array.Empty<byte>(), sig, Now + 121));
        Assert.False(HmacAuth.Verify(Token, "GET", "/api/status", Now, Array.Empty<byte>(), sig, Now - 121));
    }

    [Fact]
    public void Verify_RejectsWrongTokenAndGarbageSignature()
    {
        var sig = HmacAuth.Sign(Token, "GET", "/api/status", Now, Array.Empty<byte>());
        var other = HmacAuth.GenerateToken();
        Assert.False(HmacAuth.Verify(other, "GET", "/api/status", Now, Array.Empty<byte>(), sig, Now));
        Assert.False(HmacAuth.Verify(Token, "GET", "/api/status", Now, Array.Empty<byte>(), "not-hex", Now));
    }

    [Fact]
    public void TokenFile_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory("eso-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "token");
            var token = HmacAuth.GenerateToken();
            File.WriteAllText(path, Convert.ToBase64String(token) + "\n");
            Assert.Equal(token, HmacAuth.LoadTokenFile(path));
        }
        finally { dir.Delete(recursive: true); }
    }
}

public class OwnerAuthTests
{
    [Fact]
    public void Password_CreateAndVerify()
    {
        var record = OwnerPassword.Create("correct horse battery", iterations: 10_000);
        Assert.True(OwnerPassword.Verify("correct horse battery", record));
        Assert.False(OwnerPassword.Verify("wrong", record));
        // Salted: two records for the same password differ.
        var other = OwnerPassword.Create("correct horse battery", iterations: 10_000);
        Assert.NotEqual(record.Hash, other.Hash);
        Assert.NotEqual(record.Salt, other.Salt);
    }

    [Fact]
    public void Sessions_CreateValidateRevokeAndCsrf()
    {
        var store = new OwnerSessionStore();
        var session = store.Create();
        Assert.NotNull(store.Validate(session.Id));
        Assert.True(OwnerSessionStore.CheckCsrf(session, session.CsrfToken));
        Assert.False(OwnerSessionStore.CheckCsrf(session, "bogus"));
        Assert.False(OwnerSessionStore.CheckCsrf(session, null));
        store.Revoke(session.Id);
        Assert.Null(store.Validate(session.Id));
        Assert.Null(store.Validate(null));
    }

    [Fact]
    public void RateLimiter_LocksAfterFiveFailures_AndSuccessClears()
    {
        var limiter = new LoginRateLimiter();
        for (int i = 0; i < 4; i++) limiter.RecordFailure("1.2.3.4");
        Assert.False(limiter.IsLockedOut("1.2.3.4", out _));
        limiter.RecordFailure("1.2.3.4");
        Assert.True(limiter.IsLockedOut("1.2.3.4", out var remaining));
        Assert.True(remaining > TimeSpan.Zero && remaining <= TimeSpan.FromSeconds(30));
        limiter.RecordSuccess("1.2.3.4");
        Assert.False(limiter.IsLockedOut("1.2.3.4", out _));
    }
}

public class UpdateSignatureTests
{
    [Fact]
    public void PackageSignature_RoundTrips_AndRejectsTampering()
    {
        var token = HmacAuth.GenerateToken();
        var sha = HmacAuth.Sha256Hex(Encoding.UTF8.GetBytes("fake package bytes"));
        var sig = UpdateService.SignPackage(token, sha);
        Assert.True(UpdateService.VerifyPackageSignature(token, sha, sig));
        Assert.False(UpdateService.VerifyPackageSignature(token, HmacAuth.Sha256Hex(new byte[] { 1 }), sig));
        Assert.False(UpdateService.VerifyPackageSignature(HmacAuth.GenerateToken(), sha, sig));
    }
}

public class ConfigAndKillSwitchTests
{
    [Fact]
    public void KillSwitch_FileBacked_LockUnlock()
    {
        var dir = Directory.CreateTempSubdirectory("eso-test-");
        try
        {
            var lockFile = Path.Combine(dir.FullName, "disabled.lock");
            var ks = new KillSwitch(lockFile);
            Assert.False(ks.IsLocked);
            ks.Lock();
            Assert.True(ks.IsLocked);
            Assert.True(File.Exists(lockFile));
            // A fresh instance (e.g. after a restart) sees the file lock too.
            Assert.True(new KillSwitch(lockFile).IsLocked);
            ks.Unlock();
            Assert.False(ks.IsLocked);
            Assert.False(File.Exists(lockFile));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void AgentConfig_LoadsJsonAndDefaults()
    {
        var dir = Directory.CreateTempSubdirectory("eso-test-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "esoagent.json"), "{\"port\": 9999, \"deviceName\": \"lab-box\"}");
            var cfg = AgentConfig.Load(dir.FullName);
            Assert.Equal(9999, cfg.Port);
            Assert.Equal("lab-box", cfg.ResolvedDeviceName);
            Assert.Equal(dir.FullName, cfg.ResolvedDataDir);
            Assert.Equal(51820, cfg.Mesh.ListenPort); // mesh defaults present
        }
        finally { dir.Delete(recursive: true); }
    }
}
