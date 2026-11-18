using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>v1.2 pure-logic tests: TOTP, stream/update signing, tunnel
/// targets, magic packets, rollout states, activity merge/CSV, the.esorec
/// recording format, schedule policy, health summary, patch/telemetry
/// parsers, app-command building, perceive extract/diff/ID-pass/projection,
/// and the vision/window parsers. Networked behavior is covered by the
/// .smoke drivers and the live deployment script in V12-REPORT.md.</summary>
public class V12Tests
{
    // ---------- TOTP (RFC 6238) ----------

    [Fact]
    public void Totp_Rfc6238Vector()
    {
        // RFC 6238 test secret: ASCII "12345678901234567890".
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Base32.Encode(secret));
        // T=59 → 94287082 (8-digit) → 6-digit truncation 287082.
        Assert.Equal("287082", Totp.ComputeCode(secret, 59));
        // T=1111111109 → 07081804 → 081804; T=1234567890 → 89005924 → 005924.
        // (Cross-verified against Python's hmac/hashlib — an independent
        // RFC 6238 implementation — which agrees with these values exactly.)
        Assert.Equal("081804", Totp.ComputeCode(secret, 1111111109));
        Assert.Equal("005924", Totp.ComputeCode(secret, 1234567890));
    }

    [Fact]
    public void Totp_VerifyWindow()
    {
        var secret = Totp.NewSecretBase32();
        var secretBytes = Base32.Decode(secret);
        var code = Totp.ComputeCode(secretBytes, 1_700_000_000);
        Assert.True(Totp.Verify(secret, code, 1_700_000_000));
        Assert.True(Totp.Verify(secret, code, 1_700_000_000 + 30));   // ±1 step
        Assert.False(Totp.Verify(secret, code, 1_700_000_000 + 300)); // far outside
        Assert.False(Totp.Verify(secret, "000000", 1_700_000_000));
        Assert.False(Totp.Verify(secret, null, 1_700_000_000));
        Assert.False(Totp.Verify(secret, "12345", 1_700_000_000));
    }

    [Fact]
    public void Base32_RoundTrip()
    {
        var data = RandomNumberGenerator.GetBytes(20);
        Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
    }

    // ---------- signed streams + update fetch ----------

    [Fact]
    public void StreamSigning_RoundTripAndTamper()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = OperatorSigning.SignStream(key, "stream1", now, "desktop", "{\"type\":\"hello\"}");
        Assert.True(OperatorSigning.VerifyStream(pub, sig, "stream1", now, "desktop", "{\"type\":\"hello\"}", now));
        Assert.False(OperatorSigning.VerifyStream(pub, sig, "stream1", now, "desktop", "{\"type\":\"hello\",\"record\":true}", now));
        Assert.False(OperatorSigning.VerifyStream(pub, sig, "stream1", now, "tunnel", "{\"type\":\"hello\"}", now));
        Assert.False(OperatorSigning.VerifyStream(pub, sig, "stream1", now - 10_000, "desktop", "{\"type\":\"hello\"}", now)); // stale
    }

    [Fact]
    public void UpdateFetchSigning_RoundTripAndTamper()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var sig = OperatorSigning.SignUpdateFetch(key, "updates/package.zip", "abc123");
        Assert.True(OperatorSigning.VerifyUpdateFetch(pub, sig, "updates/package.zip", "abc123"));
        Assert.False(OperatorSigning.VerifyUpdateFetch(pub, sig, "updates/package.zip", "def456"));
        Assert.False(OperatorSigning.VerifyUpdateFetch(pub, sig, "updates/other.zip", "abc123"));
    }

    // ---------- tunnels ----------

    [Theory]
    [InlineData("127.0.0.1:8737", "127.0.0.1", 8737)]
    [InlineData("example.com:443", "example.com", 443)]
    [InlineData("[::1]:8080", "::1", 8080)]
    public void TunnelTarget_Parses(string text, string host, int port)
    {
        Assert.True(TunnelTarget.TryParse(text, out var t));
        Assert.Equal(host, t.Host);
        Assert.Equal(port, t.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noport")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:abc")]
    [InlineData("[::1]8080")]
    public void TunnelTarget_Rejects(string text) => Assert.False(TunnelTarget.TryParse(text, out _));

    // ---------- Wake-on-LAN ----------

    [Fact]
    public void MagicPacket_Shape()
    {
        var packet = MagicPacket.Build("00:11:22:33:44:55");
        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        var mac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        for (var repeat = 0; repeat < 16; repeat++)
            Assert.Equal(mac, packet.Skip(6 + repeat * 6).Take(6).ToArray());
        Assert.True(MagicPacket.TryParseMac("00-11-22-33-44-55", out _));
        Assert.True(MagicPacket.TryParseMac("001122334455", out _));
        Assert.False(MagicPacket.TryParseMac("00:11:22:33:44", out _));
        Assert.False(MagicPacket.TryParseMac("zz:11:22:33:44:55", out _));
        Assert.Throws<ArgumentException>(() => MagicPacket.Build("nope"));
    }

    // ---------- rollout states ----------

    [Fact]
    public void RolloutStates_Transitions()
    {
        Assert.True(RolloutStates.CanTransition(RolloutStates.Queued, RolloutStates.Sent));
        Assert.True(RolloutStates.CanTransition(RolloutStates.Queued, RolloutStates.Offline));
        Assert.True(RolloutStates.CanTransition(RolloutStates.Sent, RolloutStates.Applying));
        Assert.True(RolloutStates.CanTransition(RolloutStates.Applying, RolloutStates.Applied));
        Assert.True(RolloutStates.CanTransition(RolloutStates.Applying, RolloutStates.Failed));
        Assert.False(RolloutStates.CanTransition(RolloutStates.Queued, RolloutStates.Applied));
        Assert.False(RolloutStates.CanTransition(RolloutStates.Applied, RolloutStates.Failed));
        Assert.False(RolloutStates.CanTransition(RolloutStates.Offline, RolloutStates.Sent)); // terminal: no surprise updates
        Assert.False(RolloutStates.CanTransition(RolloutStates.Failed, RolloutStates.Queued));
    }

    // ---------- activity ----------

    private static ActivityEvent Ev(string id, DateTimeOffset at, string source = "device", string? code = "AAAA-1111", string summary = "did a thing")
        => new() { Id = id, At = at, Source = source, DeviceCode = code, Kind = "/api/exec", Summary = summary, Result = "ok" };

    [Fact]
    public void ActivityMerge_OrderLimitFilter()
    {
        var t0 = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            Ev("a", t0), Ev("b", t0.AddMinutes(5)), Ev("c", t0.AddMinutes(2), code: "BBBB-2222"),
        };
        var merged = ActivityQuery.Apply(events, new ActivityQuery { Limit = 10 });
        Assert.Equal(new[] { "b", "c", "a" }, merged.Select(e => e.Id).ToArray()); // newest first
        var filtered = ActivityQuery.Apply(events, new ActivityQuery { Device = "BBBB-2222" });
        Assert.Equal(new[] { "c" }, filtered.Select(e => e.Id).ToArray());
        var limited = ActivityQuery.Apply(events, new ActivityQuery { Limit = 2 });
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public void ActivityCsv_Escapes()
    {
        var csv = ActivityCsv.ToCsv(new[] { Ev("x", DateTimeOffset.UnixEpoch, summary: "a,b \"quoted\"\nline") });
        Assert.Contains("\"a,b \"\"quoted\"\"\nline\"", csv);
        Assert.StartsWith("at,source,deviceCode,deviceName,kind,result,summary", csv);
    }

    // ---------- recording format ----------

    [Fact]
    public async Task SessionRecorder_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "esorec-test-" + Guid.NewGuid().ToString("N"));
        string fileName;
        {
            await using var rec = new SessionRecorder(dir, "operator", "jpeg", 800, 600);
            fileName = rec.FileName;
            rec.Record(new DesktopFrame { Seq = 1, Codec = "jpeg", Full = true, Width = 800, Height = 600, Data = "AAAA" });
            rec.Record(new DesktopFrame { Seq = 2, Codec = "jpeg", Full = false, Width = 800, Height = 600, Data = "BBBB" });
        }
        var full = Path.Combine(dir, fileName);
        Assert.True(File.Exists(full));
        var (header, frames) = SessionRecorder.Parse(full);
        Assert.Equal("operator", header.Origin);
        Assert.Equal("jpeg", header.Codec);
        Assert.Equal(800, header.Width);
        Assert.Equal(2, frames.Count);
        Assert.Equal(1, frames[0].Seq);
        Assert.Equal("BBBB", frames[1].Data);
        var listed = SessionRecorder.List(dir);
        Assert.Single(listed);
        Assert.Equal(fileName, listed[0].Name);
        Directory.Delete(dir, recursive: true);
    }

    // ---------- schedules ----------

    [Fact]
    public void SchedulePolicy_Due()
    {
        var now = new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.FromHours(2));
        var interval = new ScheduledTaskConfig { Name = "i", Command = "echo hi", IntervalMinutes = 60 };
        Assert.True(SchedulePolicy.IsDue(interval, null, now));                       // never ran
        Assert.False(SchedulePolicy.IsDue(interval, now.AddMinutes(-30), now));        // too soon
        Assert.True(SchedulePolicy.IsDue(interval, now.AddMinutes(-61), now));         // elapsed

        var daily = new ScheduledTaskConfig { Name = "d", Command = "echo hi", DailyAt = "09:00" };
        Assert.True(SchedulePolicy.IsDue(daily, null, now));                           // past 09:00, not ran
        Assert.False(SchedulePolicy.IsDue(daily, now.AddHours(-1), now));              // already ran today
        Assert.False(SchedulePolicy.IsDue(daily, null, now.AddHours(-1)));             // 08:30 < 09:00

        var disabled = new ScheduledTaskConfig { Name = "x", Command = "echo hi", IntervalMinutes = 5, Enabled = false };
        Assert.False(SchedulePolicy.IsDue(disabled, null, now));
        Assert.True(SchedulePolicy.TryParseDailyAt("23:59", out var at));
        Assert.Equal(new TimeSpan(23, 59, 0), at);
        Assert.False(SchedulePolicy.TryParseDailyAt("24:00", out _));
        Assert.False(SchedulePolicy.TryParseDailyAt("9", out _));
    }

    // ---------- health ----------

    [Fact]
    public void HealthSummary_Builds()
    {
        var doctor = new DoctorResponse
        {
            Overall = "pass",
            Checks = new List<DoctorCheckDto>
            {
                new() { Name = "certificate", Status = "pass", Detail = "Panel certificate valid (1826 days left)" },
            },
        };
        var status = new StatusResponse
        {
            Disks = new List<DiskDto>
            {
                new DiskDto { Name = "/", TotalBytes = 100, FreeBytes = 40 },
                new DiskDto { Name = "/data", TotalBytes = 100, FreeBytes = 8 },
            },
            Supervised = new List<SupervisedStatusDto> { new() { Name = "svc", State = "gave-up" } },
        };
        var dto = HealthSummary.Build("AAAA-1111", "box", true, DateTimeOffset.UtcNow, doctor, status, DateTimeOffset.UtcNow);
        Assert.Equal(8.0, dto.MinDiskFreePercent!.Value, precision: 1);
        Assert.Equal(1826.0, dto.CertDaysLeft!.Value, precision: 1);
        Assert.Equal("warn", dto.Overall); // gave-up supervision upgrades pass → warn
        Assert.Equal(-3.0, HealthSummary.ParseCertDays("cert expiring (-3 days left)")!.Value, precision: 1);
        Assert.Null(HealthSummary.ParseCertDays("no shape here"));
    }

    // ---------- desktop frames ----------

    [Fact]
    public void DesktopPrivacyChat_RoundTrip()
    {
        var privacy = JsonSerializer.Deserialize<DesktopPrivacy>(
            JsonSerializer.Serialize(new DesktopPrivacy { Enabled = true }, EsoJson.Options), EsoJson.Options);
        Assert.True(privacy!.Enabled);
        Assert.Equal("privacy", privacy.Type);
        var chat = JsonSerializer.Deserialize<DesktopChat>(
            JsonSerializer.Serialize(new DesktopChat { Text = "hello", From = "device" }, EsoJson.Options), EsoJson.Options);
        Assert.Equal("hello", chat!.Text);
        Assert.Equal("chat", chat.Type);
    }

    // ---------- patch + telemetry parsers ----------

    [Fact]
    public void AptPatchParser_Parses()
    {
        const string output = """
            Listing...
            curl/noble-updates,noble-security 8.5.0-2ubuntu10.6 amd64 [upgradable from: 8.5.0-2ubuntu10.5]
            vim/noble-updates 2:9.1.0016-1ubuntu7.8 amd64 [upgradable from: 2:9.1.0016-1ubuntu7.7]
            """;
        var items = AptPatchParser.ParseUpgradable(output);
        Assert.Equal(2, items.Count);
        Assert.Equal("curl", items[0].Id);
        Assert.Equal("security", items[0].Kind);
        Assert.Equal("update", items[1].Kind);
        Assert.Equal("2:9.1.0016-1ubuntu7.8", items[1].Version);
    }

    [Fact]
    public void TelemetryParsers_Parse()
    {
        var gpus = TelemetryParsers.ParseNvidiaSmi("NVIDIA GeForce RTX 4070, 42, 2048, 12288, 61");
        Assert.Single(gpus);
        Assert.Equal(42, gpus[0].UtilPercent);
        Assert.Equal(2048L * 1024 * 1024, gpus[0].VramUsedBytes);
        Assert.Equal(61, gpus[0].TempC);

        var netstat = TelemetryParsers.ParseNetstat(
            "  TCP    192.168.1.5:51000    93.184.216.34:443    ESTABLISHED     4321\n  UDP    0.0.0.0:5353    *:*    1234\nActive Connections");
        Assert.Equal(2, netstat.Count);
        Assert.Equal("ESTABLISHED", netstat[0].State);
        Assert.Equal(4321, netstat[0].Pid);
        Assert.Equal(1234, netstat[1].Pid);

        var ss = TelemetryParsers.ParseSs(
            "State Recv-Q Send-Q Local Address:Port Peer Address:Port Process\nESTAB 0 0 192.168.1.5:51000 93.184.216.34:443 users:((\"curl\",pid=777,fd=5))",
            "tcp");
        Assert.Single(ss);
        Assert.Equal(777, ss[0].Pid);
        Assert.Equal("curl", ss[0].Process);
    }

    // ---------- app bridges + perceive ----------

    [Fact]
    public void AppCommandLine_Builds()
    {
        var tokens = AppCommandLine.Tokenize("--background \"my file.blend\" --python script.py");
        Assert.Equal(new[] { "--background", "my file.blend", "--python", "script.py" }, tokens);

        var (file, args, error) = AppCommandLine.Build("blender",
            "--background \"{blend}\" --python \"{script}\"",
            new Dictionary<string, string> { ["blend"] = "a b.blend", ["script"] = "s.py" });
        Assert.Null(error);
        Assert.Equal("blender", file);
        Assert.Equal(new[] { "--background", "a b.blend", "--python", "s.py" }, args);

        var missing = AppCommandLine.Build("blender", "--python \"{script}\"", new Dictionary<string, string>());
        Assert.NotNull(missing.Error);
        Assert.Contains("script", missing.Error);
    }

    [Fact]
    public void PerceiveJson_Extracts()
    {
        const string stdout = "noise before\nESO_PERCEIVE_JSON_BEGIN\n{\"app\":\"blender\",\"items\":[]}\nESO_PERCEIVE_JSON_END\nnoise after";
        var json = PerceiveJson.Extract(stdout);
        Assert.NotNull(json);
        Assert.Equal("blender", json!.Value.GetProperty("app").GetString());
        Assert.Null(PerceiveJson.Extract("no sentinels here"));
    }

    [Fact]
    public void SceneDiff_Compares()
    {
        using var before = JsonDocument.Parse("""{"items":[{"name":"Cube","location":[0,0,0]},{"name":"Lamp","energy":10},{"name":"Old","x":1}]}""");
        using var after = JsonDocument.Parse("""{"items":[{"name":"Cube","location":[1,0,0]},{"name":"Lamp","energy":10},{"name":"New","x":2}]}""");
        var diff = SceneDiff.Compare(before.RootElement, after.RootElement);
        Assert.Equal(new[] { "New" }, diff.Added);
        Assert.Equal(new[] { "Old" }, diff.Removed);
        Assert.Equal(1, diff.Unchanged);
        var changed = Assert.Single(diff.Changed);
        Assert.Equal("Cube", changed.Name);
        Assert.Equal("location", changed.Changes[0].Field);
    }

    [Fact]
    public void BmpIdPass_Measures()
    {
        // 4x2 24bpp BMP, bottom-up rows. Top row (y=0): A A black black.
        // Bottom row (y=1): black black black B. BMP stores BGR, rows padded to 4 bytes.
        static byte[] Pixel(int r, int g, int b) => new[] { (byte)b, (byte)g, (byte)r };
        var bmp = new List<byte>();
        bmp.AddRange(new byte[54]); // header filled below
        var bottomRow = new List<byte>();
        bottomRow.AddRange(Pixel(0, 0, 0)); bottomRow.AddRange(Pixel(0, 0, 0)); bottomRow.AddRange(Pixel(0, 0, 0)); bottomRow.AddRange(Pixel(200, 0, 0));
        var topRow = new List<byte>();
        topRow.AddRange(Pixel(10, 20, 30)); topRow.AddRange(Pixel(10, 20, 30)); topRow.AddRange(Pixel(0, 0, 0)); topRow.AddRange(Pixel(0, 0, 0));
        bmp.AddRange(bottomRow); bmp.AddRange(topRow); // bottom-up: bottom row stored first
        var bytes = bmp.ToArray();
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(4).CopyTo(bytes, 18);
        BitConverter.GetBytes(2).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);

        var legend = BmpIdPass.ParseLegend("""{"items":[{"name":"A","index":1,"color":[10,20,30]},{"name":"B","index":2,"color":[200,0,0]}]}""");
        var (measurements, error) = BmpIdPass.Analyze(bytes, legend);
        Assert.Null(error);
        Assert.Equal(2, measurements.Count);
        var a = measurements.Single(m => m.Name == "A");
        Assert.Equal(2, a.Pixels);
        Assert.Equal(25.0, a.CoveragePercent, precision: 1);
        Assert.Equal(0, a.BboxX); Assert.Equal(0, a.BboxY); Assert.Equal(2, a.BboxW); Assert.Equal(1, a.BboxH);
        var b = measurements.Single(m => m.Name == "B");
        Assert.Equal(1, b.Pixels);
        Assert.Equal(3, b.BboxX); Assert.Equal(1, b.BboxY);
    }

    [Fact]
    public void ScreenProjection_Projects()
    {
        // Camera at origin looking +X (yaw 0), fov 90, square frame.
        // Box centered dead ahead → screen bbox centered, positive coverage.
        var m = ScreenProjection.Project("box",
            new[] { 0.0, 0, 0 }, pitchDeg: 0, yawDeg: 0, rollDeg: 0, fovDeg: 90, aspect: 1.0,
            boundsOrigin: new[] { 10.0, 0, 0 }, boundsExtent: new[] { 1.0, 1, 1 });
        Assert.NotNull(m);
        Assert.True(m!.X < 0.5 && m.X + m.W > 0.5, $"bbox should straddle center: X={m.X} W={m.W}");
        Assert.True(m.Y < 0.5 && m.Y + m.H > 0.5);
        Assert.True(m.CoveragePercent > 0);

        // Behind the camera → no measurement.
        var behind = ScreenProjection.Project("box",
            new[] { 0.0, 0, 0 }, 0, 0, 0, 90, 1.0,
            boundsOrigin: new[] { -10.0, 0, 0 }, boundsExtent: new[] { 1.0, 1, 1 });
        Assert.Null(behind);

        // Yaw 90 → looking +Y; box on +Y is centered.
        var yawed = ScreenProjection.Project("box",
            new[] { 0.0, 0, 0 }, 0, 90, 0, 90, 1.0,
            boundsOrigin: new[] { 0.0, 10, 0 }, boundsExtent: new[] { 1.0, 1, 1 });
        Assert.NotNull(yawed);
        Assert.True(yawed!.X < 0.5 && yawed.X + yawed.W > 0.5);
    }

    // ---------- vision parsers ----------

    [Fact]
    public void VisionParsers_Parse()
    {
        var windows = VisionParsers.ParseWindowDump(
            "ESO_WIN|1234|555|10|20|800|600|1|My App | With Pipes\nESO_WIN|99|777|0|0|100|100|0|Other\nnoise line");
        Assert.Equal(2, windows.Count);
        Assert.Equal("My App | With Pipes", windows[0].Title);
        Assert.Equal(555, windows[0].Pid);
        Assert.True(windows[0].Focused);
        Assert.False(windows[1].Focused);

        var monitors = VisionParsers.ParseXrandrMonitors(
            "Monitors: 1\n 0: +*HDMI-1 1920/510x1080/290+0+0  HDMI-1");
        Assert.Single(monitors);
        Assert.Equal(1920, monitors[0].Width);
        Assert.Equal(1080, monitors[0].Height);
        Assert.True(monitors[0].Primary);
        Assert.Equal("HDMI-1", monitors[0].Name);

        var dump = VisionParsers.ParseMonitorDump("ESO_MON|0|DISPLAY1|0|0|1920|1080|1");
        Assert.Single(dump);
        Assert.True(dump[0].Primary);
    }
}

/// <summary>Item 37 (efficiency layer) pure-logic tests.</summary>
public class EfficiencyTests
{
    [Fact]
    public void JsonStructuralDiff_FindsChanges()
    {
        using var a = JsonDocument.Parse("""{"name":"box","cpu":40,"disks":[{"name":"/","free":10}],"gone":1}""");
        using var b = JsonDocument.Parse("""{"name":"box","cpu":55,"disks":[{"name":"/","free":9}],"new":true}""");
        var diff = JsonStructuralDiff.Compare(a.RootElement, b.RootElement);
        Assert.Contains(diff.Changes, c => c.Path == "$.cpu" && c.Kind == "changed" && c.From == "40" && c.To == "55");
        Assert.Contains(diff.Changes, c => c.Path == "$.disks[0].free" && c.Kind == "changed");
        Assert.Contains(diff.Changes, c => c.Path == "$.gone" && c.Kind == "removed");
        Assert.Contains(diff.Changes, c => c.Path == "$.new" && c.Kind == "added");
        Assert.True(diff.UnchangedFields >= 2); // name + disks[0].name
        Assert.False(diff.Truncated);
    }

    [Fact]
    public void DeltaEnvelope_FullThenDelta()
    {
        var store = new DeltaStore();
        var first = DeltaEnvelope.Build(store, "inventory", """{"a":1,"b":2}""", deltaRequested: true);
        Assert.False(first.IsDelta);
        Assert.NotNull(first.Full);
        var second = DeltaEnvelope.Build(store, "inventory", """{"a":1,"b":3}""", deltaRequested: true);
        Assert.True(second.IsDelta);
        Assert.NotNull(second.BaselineAt);
        Assert.Single(second.Diff!.Changes);
        Assert.Equal("$.b", second.Diff.Changes[0].Path);
        Assert.True(second.SentBytes < second.FullBytes + 400); // diff envelope is compact for a one-field change
        var third = DeltaEnvelope.Build(store, "inventory", """{"a":1,"b":3}""", deltaRequested: true);
        Assert.True(third.IsDelta);
        Assert.Empty(third.Diff!.Changes);
        Assert.Contains("No changes", third.Note);
    }

    [Fact]
    public void AgentEvents_FiltersToEventKinds()
    {
        var lines = new[]
        {
            """{"ts":"2026-11-18T10:00:00+00:00","endpoint":"/api/exec","summary":"ran ls","result":"ok"}""",
            """{"ts":"2026-11-18T10:01:00+00:00","endpoint":"crash","summary":"app crashed: foo (module bar.dll)","result":"alert"}""",
            """{"ts":"2026-11-18T10:02:00+00:00","endpoint":"telemetry","summary":"cpu above 90% on pid 5","result":"warn"}""",
            "not json at all",
            """{"ts":"2026-11-18T10:03:00+00:00","endpoint":"watch","summary":"file changed: /tmp/x","result":"ok"}""",
        };
        var page = AgentEvents.FromAuditLines(lines, total: 42, since: 0);
        Assert.Equal(42, page.Next);
        Assert.Equal(3, page.Events.Count);
        Assert.Equal("crash", page.Events[0].Kind);
        Assert.Equal("watch", page.Events[2].Kind);
    }

    [Fact]
    public void AdaptiveQuality_StepsDownToFloorAndFlags()
    {
        // Plenty of bandwidth: hold at requested.
        var hold = AdaptiveQuality.Evaluate(60, 10, 60, 10, bytesPerSecond: 10_000_000, avgFrameBytes: 50_000);
        Assert.False(hold.Changed);
        Assert.False(hold.BelowFloor);

        // Tight bandwidth: quality steps down first.
        var down = AdaptiveQuality.Evaluate(60, 10, 60, 10, bytesPerSecond: 100_000, avgFrameBytes: 50_000);
        Assert.True(down.Changed);
        Assert.Equal(50, down.Quality);
        Assert.Equal(10, down.Fps);

        // At floor quality: fps steps down.
        var fpsDown = AdaptiveQuality.Evaluate(AdaptiveQuality.FloorQuality, 10, 60, 10, bytesPerSecond: 100_000, avgFrameBytes: 50_000);
        Assert.True(fpsDown.Changed);
        Assert.True(fpsDown.Fps < 10 && fpsDown.Fps >= AdaptiveQuality.FloorFps);

        // At the floor and still unsustainable: flag, never go below.
        var floor = AdaptiveQuality.Evaluate(AdaptiveQuality.FloorQuality, AdaptiveQuality.FloorFps, 60, 10,
            bytesPerSecond: 10_000, avgFrameBytes: 50_000);
        Assert.False(floor.Changed);
        Assert.True(floor.BelowFloor);
        Assert.Equal(AdaptiveQuality.FloorQuality, floor.Quality);
        Assert.Equal(AdaptiveQuality.FloorFps, floor.Fps);

        // Recovery: headroom climbs quality back up.
        var up = AdaptiveQuality.Evaluate(40, 4, 60, 10, bytesPerSecond: 10_000_000, avgFrameBytes: 50_000);
        Assert.True(up.Changed);
        Assert.Equal(45, up.Quality);
    }
    // ---------- universal workbench presets (item 38) + polyglot (item 40) ----------

    [Fact]
    public void Presets_AreCompleteUniqueAndHonest()
    {
        var presets = AppCommandPresets.All;
        Assert.NotEmpty(presets);
        Assert.Equal(presets.Count, presets.Select(p => p.Name).Distinct().Count());
        Assert.All(presets, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Executable));
            Assert.False(string.IsNullOrWhiteSpace(p.ArgsTemplate));
        });
        // Every promised family is present.
        foreach (var name in new[] { "blender", "unreal-python", "gimp", "photoshop", "unity", "godot",
                                   "dotnet-build", "dotnet-test", "git", "node",
                                   "net-interfaces", "net-routes", "net-ping", "net-port",
                                   "python", "go-build", "cargo-build", "maven-package", "cmake-build" })
            Assert.Contains(presets, p => p.Name == name);
        // Degraded bridges are LABELLED degraded, never dressed up.
        var photoshop = presets.Single(p => p.Name == "photoshop");
        Assert.Contains("DEGRADED", photoshop.Notes ?? "");
        Assert.Null(photoshop.Perceive);
        // Perceive presets point at the builtin scripts.
        Assert.Equal("builtin:blender", presets.Single(p => p.Name == "blender").Perceive?.Script);
        Assert.Equal("builtin:godot", presets.Single(p => p.Name == "godot").Perceive?.Script);
        // Verify verbs exist where claimed.
        foreach (var name in new[] { "dotnet-build", "git", "net-ping", "net-port", "go-build", "cargo-build" })
            Assert.False(string.IsNullOrWhiteSpace(presets.Single(p => p.Name == name).VerifyTemplate), name);
    }

    [Fact]
    public void VerifyTemplate_SubstitutesLikeRunTemplate()
    {
        var git = AppCommandPresets.All.Single(p => p.Name == "git");
        var (file, args, error) = AppCommandLine.Build(git.Executable, git.VerifyTemplate!,
            new Dictionary<string, string> { ["repo"] = "/work/repo" });
        Assert.Null(error);
        Assert.Equal("git", file);
        Assert.Contains("status --porcelain", string.Join(" ", args));
        Assert.Contains("/work/repo", string.Join(" ", args));
    }

    [Fact]
    public void PerceiveScripts_AreEmbedded()
    {
        var names = typeof(AppBridgeService).Assembly.GetManifestResourceNames();
        Assert.Contains(names, n => n.EndsWith("blender_perceive.py"));
        Assert.Contains(names, n => n.EndsWith("unreal_perceive.py"));
        Assert.Contains(names, n => n.EndsWith("godot_perceive.gd"));
    }

    [Theory]
    [InlineData("dotnet", new[] { "App.sln", "Program.cs" })]
    [InlineData("dotnet", new[] { "Lib.csproj" })]
    [InlineData("node", new[] { "package.json", "index.js" })]
    [InlineData("python", new[] { "pyproject.toml", "main.py" })]
    [InlineData("python", new[] { "requirements.txt" })]
    [InlineData("go", new[] { "go.mod", "main.go" })]
    [InlineData("rust", new[] { "Cargo.toml" })]
    [InlineData("java-maven", new[] { "pom.xml" })]
    [InlineData("java-gradle", new[] { "build.gradle.kts" })]
    [InlineData("cmake", new[] { "CMakeLists.txt", "main.cpp" })]
    [InlineData("make", new[] { "Makefile" })]
    public void ProjectDetector_FindsKinds(string kind, string[] files)
    {
        var detections = ProjectDetector.Detect(files);
        var hit = Assert.Single(detections, d => d.Kind == kind);
        Assert.NotEmpty(hit.Suggested);
        Assert.All(hit.Suggested, e => Assert.False(string.IsNullOrWhiteSpace(e.Executable)));
    }

    [Fact]
    public void ProjectDetector_UnknownDirIsEmpty_AndMultiStackReportsBoth()
    {
        Assert.Empty(ProjectDetector.Detect(new[] { "notes.txt", "photo.png" }));
        var both = ProjectDetector.Detect(new[] { "App.sln", "package.json" });
        Assert.Equal(2, both.Count);
        Assert.Equal("dotnet", both[0].Kind); // .NET outranks node when both markers exist
    }
    // ---------- glance checklist (item 39) ----------

    private static WindowSeeDto SeeWindow(string title, bool focused, params (string Role, string Name)[] nodes) => new()
    {
        Window = new WindowInfoDto { Title = title, Focused = focused },
        HasTree = nodes.Length > 0,
        Nodes = nodes.Select(n => new UiNodeDto { Role = n.Role, Name = n.Name }).ToList(),
    };

    [Fact]
    public void Glance_AnswersTheChecklist()
    {
        var windows = new List<WindowSeeDto>
        {
            SeeWindow("Editor — project", true, ("Text", "Hello world"), ("Button", "Save")),
            SeeWindow("Error", false, ("Text", "Build failed: CS1002")),
        };
        var glance = GlanceBuilder.Build(windows, null);
        Assert.Equal(2, glance.OpenWindows.Count);
        Assert.Equal("Editor — project", glance.FocusedWindow);
        Assert.Contains("Hello world", glance.VisibleTexts);
        Assert.Equal("Build failed: CS1002", glance.LikelyDialogOrError); // node text beats the window title — more specific
        Assert.Contains(glance.ChangedSinceLastLook, c => c.Contains("baseline"));
    }

    [Fact]
    public void Glance_ReportsChangesAndDialogRoles()
    {
        var windows = new List<WindowSeeDto>
        {
            SeeWindow("App", false, ("dialog", ""), ("Text", "Are you sure?")),
            SeeWindow("New Window", false),
        };
        var glance = GlanceBuilder.Build(windows, new List<string> { "App", "Old Window" });
        Assert.Equal("App", glance.LikelyDialogOrError); // dialog role, empty name → window title
        Assert.Contains(glance.ChangedSinceLastLook, c => c == "opened: New Window");
        Assert.Contains(glance.ChangedSinceLastLook, c => c == "closed: Old Window");
    }

    // ---------- access codes / grants (item 41) ----------

    [Fact]
    public void AccessCode_GenerateHashDisplay()
    {
        var code = AccessCodePolicy.Generate();
        Assert.Equal(8, AccessCodePolicy.Normalize(code).Length);
        Assert.Equal(AccessCodePolicy.Hash(code), AccessCodePolicy.Hash(AccessCodePolicy.Display(code)));
        Assert.Contains("-", AccessCodePolicy.Display(code));
        Assert.NotEqual(AccessCodePolicy.Hash(code), AccessCodePolicy.Hash(AccessCodePolicy.Generate()));
    }

    [Fact]
    public void AccessCode_CanRedeemRules()
    {
        var now = DateTimeOffset.UtcNow;
        var code = new AccessCodeRecord { ExpiresAt = now.AddMinutes(30) };
        Assert.True(AccessCodePolicy.CanRedeem(code, now).Ok);
        code.Revoked = true;
        Assert.False(AccessCodePolicy.CanRedeem(code, now).Ok);
        code.Revoked = false; code.ExpiresAt = now.AddMinutes(-1);
        Assert.False(AccessCodePolicy.CanRedeem(code, now).Ok);
        code.ExpiresAt = now.AddMinutes(30); code.MaxUses = 2; code.Uses = 2;
        Assert.False(AccessCodePolicy.CanRedeem(code, now).Ok);
        code.Uses = 1;
        Assert.True(AccessCodePolicy.CanRedeem(code, now).Ok);
    }

    [Fact]
    public void GrantScope_PathRules()
    {
        var full = new GrantScope();
        Assert.True(full.Allows("/api/status"));
        Assert.True(full.Allows("/api/exec"));
        Assert.True(full.Allows("/api/files/list?path=/"));
        Assert.False(full.Allows("/api/update/apply"));       // never via grants
        Assert.False(full.Allows("/api/access"));             // never via grants
        var viewOnly = new GrantScope { Exec = false, Files = false };
        Assert.False(viewOnly.Allows("/api/exec"));
        Assert.False(viewOnly.Allows("/api/files/list"));
        Assert.True(viewOnly.Allows("/api/doctor"));          // awareness stays
        Assert.False(viewOnly.Allows("/api/diagnose"));       // diagnose rides exec scope
    }

    [Fact]
    public void GrantToken_SignVerifyTamper()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var scope = new GrantScope { DesktopViewOnly = true };
        var token = new GrantTokenDto
        {
            GrantId = "abc123", DeviceCode = "TEST-0001", Scope = scope, ExpiresAtUnix = 1893456000,
            Signature = GrantSigning.Sign(key, "abc123", "TEST-0001", 1893456000, scope),
        };
        Assert.True(GrantSigning.Verify(pub, token));
        token.Scope = new GrantScope(); // widening the scope breaks the signature
        Assert.False(GrantSigning.Verify(pub, token));
    }

    // ---------- release channel (item 45) ----------

    [Theory]
    [InlineData("1.2.0", "1.2.1", GateDecision.Newer)]
    [InlineData("1.2.0", "1.2.0", GateDecision.Same)]
    [InlineData("1.2.0", "v1.2.0", GateDecision.Same)]
    [InlineData("1.2.0", "1.1.9", GateDecision.Downgrade)]
    [InlineData("1.2.0", "2.0.0", GateDecision.Newer)]
    [InlineData("1.2.0", "banana", GateDecision.Unknown)]
    [InlineData("1.2.0", null, GateDecision.Unknown)]
    public void UpdateGate_Decisions(string current, string? candidate, GateDecision expected) =>
        Assert.Equal(expected, UpdateGate.Decide(current, candidate));

    [Fact]
    public void ReleaseManifest_SignVerifyTamper()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var manifest = new ReleaseManifest
        {
            Version = "1.3.0",
            Artifacts = new()
            {
                ["linux-agent"] = new ReleaseArtifact { Url = "https://example.invalid/a.zip", Sha256 = "ab12" },
                ["windows-agent"] = new ReleaseArtifact { Url = "https://example.invalid/b.zip", Sha256 = "cd34" },
            },
        };
        manifest.Signature = ReleaseManifestSigning.Sign(key, manifest);
        Assert.True(ReleaseManifestSigning.Verify(pub, manifest));
        manifest.Artifacts["linux-agent"].Sha256 = "ffff";   // tampered hash
        Assert.False(ReleaseManifestSigning.Verify(pub, manifest));
        manifest.Artifacts["linux-agent"].Sha256 = "ab12";
        manifest.Version = "1.4.0";                          // tampered version
        Assert.False(ReleaseManifestSigning.Verify(pub, manifest));
    }

    [Fact]
    public void PackageVersion_StampRoundTrip()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eso-gate-" + Guid.NewGuid().ToString("N")));
        try
        {
            var zipPath = Path.Combine(dir.FullName, "pkg.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("esoagent-version.txt");
                using var w = new StreamWriter(entry.Open());
                w.Write("1.2.1");
            }
            Assert.Equal("1.2.1", UpdateGate.ReadPackageVersion(zipPath));
            Assert.Equal(GateDecision.Newer, UpdateGate.Decide(ProductInfo.Version, UpdateGate.ReadPackageVersion(zipPath)));
        }
        finally { dir.Delete(recursive: true); }
    }

}
