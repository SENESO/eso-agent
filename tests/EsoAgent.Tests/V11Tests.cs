using System.Net;
using System.Security.Cryptography;
using System.Text;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

// v1.1 operator upgrades: job lifecycle, offline-queue policy + queued
// signatures, enrollment IP filter, env redaction, restart policy, doctor
// audit counting, toolchain PATH lookup, log-follow filter, shell env.

public class JobLifecycleTests
{
    private static JobRecord Running() => new()
    {
        Id = "abc",
        State = "running",
        Command = "sleep",
        Args = new[] { "60" },
        Pid = 4242,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        EnvKeys = new List<string> { "FOO" },
    };

    [Fact]
    public void CloseOut_RunningBecomesInterrupted_PreservingRecord()
    {
        var now = DateTimeOffset.UtcNow;
        var closed = JobLifecycle.CloseOutOnStartup(Running(), now);
        Assert.Equal("interrupted", closed.State);
        Assert.Equal(now, closed.EndedAt);
        Assert.Null(closed.ExitCode); // unknowable — never invented
        Assert.Equal(4242, closed.Pid);
        Assert.Equal("sleep", closed.Command);
        Assert.Equal(new[] { "60" }, closed.Args);
        Assert.Equal(new List<string> { "FOO" }, closed.EnvKeys);
    }

    [Theory]
    [InlineData("exited")]
    [InlineData("cancelled")]
    [InlineData("interrupted")]
    public void CloseOut_TerminalStatesAreUnchanged(string state)
    {
        var record = Running();
        record.State = state;
        record.ExitCode = 3;
        var closed = JobLifecycle.CloseOutOnStartup(record, DateTimeOffset.UtcNow);
        Assert.Same(record, closed);
        Assert.Equal(3, closed.ExitCode);
    }

    [Theory]
    [InlineData("running", "exited", true)]
    [InlineData("running", "cancelled", true)]
    [InlineData("running", "interrupted", true)]
    [InlineData("running", "running", false)]
    [InlineData("exited", "running", false)]
    [InlineData("exited", "cancelled", false)]
    [InlineData("cancelled", "exited", false)]
    [InlineData("interrupted", "exited", false)]
    public void CanTransition_Matrix(string from, string to, bool expected)
        => Assert.Equal(expected, JobLifecycle.CanTransition(from, to));
}

public class QueuePolicyTests
{
    private static QueuedCommandRecord Record(long expiresAt, string state = "queued") => new()
    {
        Id = "q1",
        DeviceCode = "AAAA-1111",
        ExpiresAt = expiresAt,
        State = state,
    };

    [Fact]
    public void Expiry_Boundary()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var record = Record(expiry.ToUnixTimeSeconds());
        Assert.False(QueuePolicy.IsExpired(record, expiry.AddSeconds(-1)));
        Assert.False(QueuePolicy.IsExpired(record, expiry)); // expires strictly AFTER the deadline
        Assert.True(QueuePolicy.IsExpired(record, expiry.AddSeconds(1)));
    }

    [Fact]
    public void Deliverable_OnlyWhileQueuedAndUnexpired()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var now = DateTimeOffset.UtcNow;
        Assert.True(QueuePolicy.IsDeliverable(Record(expiry.ToUnixTimeSeconds()), now));
        Assert.False(QueuePolicy.IsDeliverable(Record(expiry.ToUnixTimeSeconds(), "delivered"), now));
        Assert.False(QueuePolicy.IsDeliverable(Record(expiry.ToUnixTimeSeconds(), "expired"), now));
        Assert.False(QueuePolicy.IsDeliverable(Record(now.AddHours(-1).ToUnixTimeSeconds()), now));
    }

    [Fact]
    public void DefaultTtl_Is24Hours()
        => Assert.Equal(24 * 60 * 60, QueuePolicy.DefaultTtlSeconds);
}

public class QueuedSigningTests
{
    private static (ECDsa Key, string Pub) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void QueuedCommand_RoundTrip_AndWindowEnforced()
    {
        var (key, pub) = NewKey();
        using (key)
        {
            var body = Encoding.UTF8.GetBytes("{\"file\":\"echo\"}");
            var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expires = issued + 3600;
            var sig = OperatorSigning.SignQueuedCommand(key, "cmd1", issued, expires, "POST", "/api/exec", body);

            Assert.True(OperatorSigning.VerifyQueuedCommand(pub, sig, "cmd1", issued, expires, "POST", "/api/exec", body, issued + 60));
            Assert.True(OperatorSigning.VerifyQueuedCommand(pub, sig, "cmd1", issued, expires, "POST", "/api/exec", body, expires)); // at the deadline: still valid
            Assert.False(OperatorSigning.VerifyQueuedCommand(pub, sig, "cmd1", issued, expires, "POST", "/api/exec", body, expires + 1)); // late: refused
            // Tampering with any bound field breaks it.
            Assert.False(OperatorSigning.VerifyQueuedCommand(pub, sig, "cmd1", issued, expires, "POST", "/api/shell", body, issued + 60));
            Assert.False(OperatorSigning.VerifyQueuedCommand(pub, sig, "cmd1", issued, expires + 9999, "POST", "/api/exec", body, issued + 60));
            Assert.False(OperatorSigning.VerifyQueuedCommand(pub, sig, "cmd1", issued, expires, "POST", "/api/exec",
                Encoding.UTF8.GetBytes("{\"file\":\"rm\"}"), issued + 60));
            // Issued implausibly far in the future is refused.
            Assert.False(OperatorSigning.VerifyQueuedCommand(pub, sig, "cmd1", issued, expires, "POST", "/api/exec", body, issued - 3600));
            // The immediate canonical form does NOT verify as a queued command (form separation).
            var immediateSig = OperatorSigning.SignCommand(key, "cmd1", issued, "POST", "/api/exec", body);
            Assert.False(OperatorSigning.VerifyQueuedCommand(pub, immediateSig, "cmd1", issued, expires, "POST", "/api/exec", body, issued + 60));
            // And a queued signature does not verify under the immediate verifier.
            Assert.False(OperatorSigning.VerifyCommand(pub, sig, "cmd1", issued, "POST", "/api/exec", body, issued + 60));
        }
    }
}

public class IpFilterTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]     // loopback
    [InlineData("::1", false)]           // loopback v6
    [InlineData("169.254.10.20", false)] // link-local
    [InlineData("fe80::1", false)]       // link-local v6
    [InlineData("0.0.0.0", false)]       // unspecified
    [InlineData("192.168.1.5", true)]    // LAN
    [InlineData("10.0.0.8", true)]       // private
    [InlineData("100.68.105.55", true)]  // tailnet CGNAT
    [InlineData("10.77.0.2", true)]      // mesh virtual IP range
    [InlineData("203.0.113.9", true)]    // public
    public void IsReportableIp_Cases(string ip, bool expected)
        => Assert.Equal(expected, NetHelpers.IsReportableIp(IPAddress.Parse(ip)));

    [Fact]
    public void DiscoverInterfaceIps_NeverContainsLoopbackOrLinkLocal()
    {
        foreach (var ip in NetHelpers.DiscoverInterfaceIps())
        {
            var parsed = IPAddress.Parse(ip);
            Assert.False(IPAddress.IsLoopback(parsed), ip);
            Assert.True(NetHelpers.IsReportableIp(parsed), ip);
        }
    }
}

public class EnvAuditTests
{
    [Fact]
    public void Describe_ListsKeyNamesOnly_NeverValues()
    {
        var env = new Dictionary<string, string>
        {
            ["MY_TOKEN"] = "super-secret-value",
            ["PLAIN"] = "hello",
        };
        var description = EnvAudit.Describe(env);
        Assert.Contains("MY_TOKEN", description);
        Assert.Contains("PLAIN", description);
        Assert.DoesNotContain("super-secret-value", description);
        Assert.DoesNotContain("hello", description);
    }

    [Fact]
    public void Describe_NullOrEmpty_IsEmpty()
    {
        Assert.Equal("", EnvAudit.Describe(null));
        Assert.Equal("", EnvAudit.Describe(new Dictionary<string, string>()));
    }

    [Fact]
    public void KeyNames_AreSorted()
    {
        var env = new Dictionary<string, string> { ["ZED"] = "1", ["ALPHA"] = "2" };
        Assert.Equal(new List<string> { "ALPHA", "ZED" }, EnvAudit.KeyNames(env));
    }
}

public class RestartPolicyTests
{
    [Theory]
    [InlineData("never", 0, 0, null, false)]
    [InlineData("never", 1, 0, null, false)]
    [InlineData("on-failure", 0, 0, null, false)]  // clean exit: no restart
    [InlineData("on-failure", 1, 0, null, true)]
    [InlineData("on-failure", null, 0, null, true)] // killed/unknown counts as failure
    [InlineData("always", 0, 0, null, true)]
    [InlineData("always", 1, 3, null, true)]
    [InlineData("always", 1, 3, 3, false)]          // cap reached
    [InlineData("on-failure", 1, 2, 2, false)]      // cap reached
    [InlineData("on-failure", 1, 1, 2, true)]       // one restart left
    [InlineData("bogus-policy", 1, 0, null, false)] // unrecognized = never
    public void ShouldRestart_Matrix(string policy, int? exitCode, int restarts, int? maxRestarts, bool expected)
        => Assert.Equal(expected, RestartPolicy.ShouldRestart(policy, exitCode, restarts, maxRestarts));
}

public class DoctorAuditTests
{
    [Fact]
    public void CountAuditErrors_CountsOnlyErrorResults()
    {
        var lines = new[]
        {
            "{\"ts\":\"2026-11-18T00:00:00Z\",\"endpoint\":\"/api/exec\",\"summary\":\"POST /api/exec\",\"result\":\"200 in 12ms\"}",
            "{\"ts\":\"2026-11-18T00:00:01Z\",\"endpoint\":\"/api/exec\",\"summary\":\"POST /api/exec\",\"result\":\"404 in 3ms\"}",
            "{\"ts\":\"2026-11-18T00:00:02Z\",\"endpoint\":\"/api/exec\",\"summary\":\"POST /api/exec\",\"result\":\"500 in 9ms\"}",
            "{\"ts\":\"2026-11-18T00:00:03Z\",\"endpoint\":\"fleet\",\"summary\":\"connection failed\",\"result\":\"retrying\"}",
            "{\"ts\":\"2026-11-18T00:00:04Z\",\"endpoint\":\"desktop\",\"summary\":\"session error\",\"result\":\"error\"}",
            "{\"ts\":\"2026-11-18T00:00:05Z\",\"endpoint\":\"/api/exec\",\"summary\":\"auth-failed\",\"result\":\"401\"}",
            "{\"ts\":\"2026-11-18T00:00:06Z\",\"endpoint\":\"fleet\",\"summary\":\"REJECTED\",\"result\":\"refused\"}",
            "{\"ts\":\"2026-11-18T00:00:07Z\",\"endpoint\":\"supervise\",\"summary\":\"restarted\",\"result\":\"ok\"}",
            "this is not json",
            "{\"endpoint\":\"/api/status\"}", // no result field
        };
        // 404, 500, error, 401, refused = 5
        Assert.Equal(5, Doctor.CountAuditErrors(lines));
    }
}

public class ToolchainProbeTests
{
    [Fact]
    public void FindOnPath_FindsAndMisses()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"eso-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var fileName = isWindows ? "mytool.exe" : "mytool";
            File.WriteAllText(Path.Combine(dir, fileName), "x");
            var sep = isWindows ? ";" : ":";
            var found = ToolchainProbes.FindOnPath("mytool", $"/nonexistent{sep}{dir}", isWindows);
            Assert.Equal(Path.Combine(dir, fileName), found);
            Assert.Null(ToolchainProbes.FindOnPath("definitely-not-here", dir, isWindows));
            Assert.Null(ToolchainProbes.FindOnPath("mytool", null, isWindows));
            Assert.Null(ToolchainProbes.FindOnPath("mytool", "", isWindows));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FirstLine_PrefersStdoutSkipsBlanks()
    {
        Assert.Equal("git version 2.43.0", ToolchainProbes.FirstLine("git version 2.43.0\nmore", null));
        Assert.Equal("openjdk 21", ToolchainProbes.FirstLine("", "openjdk 21\n"));
        Assert.Equal("x", ToolchainProbes.FirstLine("\n\n  x  \ny"));
        Assert.Null(ToolchainProbes.FirstLine("", null));
        Assert.Null(ToolchainProbes.FirstLine());
    }
}

public class LogFollowFilterTests
{
    [Fact]
    public void Matches_NullPatternMatchesAll()
    {
        Assert.True(LogFollowFilter.Matches("anything", null));
        Assert.True(LogFollowFilter.Matches("anything", ""));
    }

    [Fact]
    public void Matches_SubstringCaseInsensitive()
    {
        Assert.True(LogFollowFilter.Matches("ERROR: disk full", "error"));
        Assert.False(LogFollowFilter.Matches("all good", "error"));
    }

    [Fact]
    public void Matches_RegexWhenItCompiles()
    {
        Assert.True(LogFollowFilter.Matches("code 500 returned", @"\b5\d\d\b"));
        Assert.False(LogFollowFilter.Matches("code 200 returned", @"\b5\d\d\b"));
    }

    [Fact]
    public void Matches_InvalidRegexFallsBackToSubstring()
    {
        Assert.True(LogFollowFilter.Matches("a [unclosed thing", "[unclosed"));
        Assert.False(LogFollowFilter.Matches("nothing here", "[unclosed"));
    }
}

public class ShellEnvTests
{
    [Fact]
    public void ResolveShell_CarriesEnv_ForStdinShellsAndCmd()
    {
        var env = new Dictionary<string, string> { ["K"] = "V" };
        var bash = ProcessRunner.ResolveShell(new ShellRequest { Shell = "bash", Script = "echo $K", Env = env });
        Assert.Equal(env, bash.Env);
        var cmd = ProcessRunner.ResolveShell(new ShellRequest { Shell = "cmd", Script = "echo %K%", Env = env });
        Assert.Equal(env, cmd.Env);
    }
}
