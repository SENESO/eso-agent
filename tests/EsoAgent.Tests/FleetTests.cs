using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

public class DeviceIdentityTests
{
    [Fact]
    public void Code_IsStableAndFormatted()
    {
        var spki = Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==");
        var a = DeviceIdentity.CodeFromPublicKey(spki);
        var b = DeviceIdentity.CodeFromPublicKey(spki);
        Assert.Equal(a, b);
        Assert.Equal(9, a.Length);
        Assert.Equal('-', a[4]);
        Assert.True(DeviceIdentity.LooksLikeCode(a));
        Assert.True(DeviceIdentity.LooksLikeCode(a.ToLowerInvariant()));
    }

    [Fact]
    public void LooksLikeCode_RejectsNonCodes()
    {
        Assert.False(DeviceIdentity.LooksLikeCode("127.0.0.1"));
        Assert.False(DeviceIdentity.LooksLikeCode("ABCD1234"));   // missing dash
        Assert.False(DeviceIdentity.LooksLikeCode("ABCD-12345")); // too long
        Assert.False(DeviceIdentity.LooksLikeCode("ABCD-123I"));  // I is not Crockford
        Assert.False(DeviceIdentity.LooksLikeCode(""));
    }

    [Fact]
    public void DifferentKeys_GiveDifferentCodes()
    {
        using var k1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var k2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.NotEqual(
            DeviceIdentity.CodeFromPublicKey(k1.ExportSubjectPublicKeyInfo()),
            DeviceIdentity.CodeFromPublicKey(k2.ExportSubjectPublicKeyInfo()));
    }
}

public class OperatorSigningTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"file\":\"echo\"}");
    private const long Now = 1_800_000_000;

    private static (ECDsa Key, string Pub) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Command_SignVerify_RoundTrips()
    {
        var (key, pub) = NewKey();
        var sig = OperatorSigning.SignCommand(key, "cmd1", Now, "POST", "/api/exec", Body);
        Assert.True(OperatorSigning.VerifyCommand(pub, sig, "cmd1", Now, "POST", "/api/exec", Body, Now));
    }

    [Fact]
    public void Command_RejectsTamperingAndStaleTimestamps()
    {
        var (key, pub) = NewKey();
        var sig = OperatorSigning.SignCommand(key, "cmd1", Now, "POST", "/api/exec", Body);
        Assert.False(OperatorSigning.VerifyCommand(pub, sig, "cmd1", Now, "POST", "/api/power/reboot", Body, Now));
        Assert.False(OperatorSigning.VerifyCommand(pub, sig, "cmd2", Now, "POST", "/api/exec", Body, Now));
        Assert.False(OperatorSigning.VerifyCommand(pub, sig, "cmd1", Now, "POST", "/api/exec", Body, Now + 1000));
    }

    [Fact]
    public void Admin_SignVerify_AndKeySeparation()
    {
        var (operatorKey, operatorPub) = NewKey();
        var (panelKey, panelPub) = NewKey();
        var sig = OperatorSigning.SignAdmin(operatorKey, "rotate-token", "a1", Now, "{}");
        Assert.True(OperatorSigning.VerifyAdmin(operatorPub, sig, "rotate-token", "a1", Now, "{}", Now));
        // The panel key must not validate an operator signature and vice versa —
        // this separation is what keeps owner-reserved actions owner-only.
        Assert.False(OperatorSigning.VerifyAdmin(panelPub, sig, "rotate-token", "a1", Now, "{}", Now));
        var panelSig = OperatorSigning.SignAdmin(panelKey, "revoke-operator", "a2", Now, "{}");
        Assert.True(OperatorSigning.VerifyAdmin(panelPub, panelSig, "revoke-operator", "a2", Now, "{}", Now));
        Assert.False(OperatorSigning.VerifyAdmin(operatorPub, panelSig, "revoke-operator", "a2", Now, "{}", Now));
    }

    [Fact]
    public void Request_SignVerify_RoundTrips()
    {
        var (key, pub) = NewKey();
        var sig = OperatorSigning.SignRequest(key, "POST", "/fleet/admin/approve", Now, Body);
        Assert.True(OperatorSigning.VerifyRequest(pub, sig, "POST", "/fleet/admin/approve", Now, Body, Now));
        Assert.False(OperatorSigning.VerifyRequest(pub, sig, "POST", "/fleet/admin/reject", Now, Body, Now));
    }

    [Fact]
    public void EnrollmentKey_HashIsStableAndKeyIsFormatted()
    {
        var key = EnrollmentKeys.Generate();
        Assert.StartsWith("eso-enroll-", key);
        Assert.Equal(EnrollmentKeys.Hash(key), EnrollmentKeys.Hash(key));
        Assert.NotEqual(EnrollmentKeys.Hash(key), EnrollmentKeys.Hash(EnrollmentKeys.Generate()));
        Assert.Equal(64, EnrollmentKeys.Hash(key).Length); // sha-256 hex
    }
}

public class DesktopProtocolTests
{
    [Fact]
    public void Negotiation_PrefersJpeg_AndDefaultsToView()
    {
        Assert.Equal("jpeg", DesktopNegotiation.PickCodec(new[] { "jpeg", "rawdiff" }, new[] { "rawdiff", "jpeg" }));
        Assert.Equal("rawdiff", DesktopNegotiation.PickCodec(new[] { "rawdiff" }, new[] { "rawdiff", "jpeg" }));
        Assert.Null(DesktopNegotiation.PickCodec(new[] { "jpeg" }, new[] { "h264" }));
        Assert.Equal("Control", DesktopNegotiation.NormalizeMode("control"));
        Assert.Equal("View", DesktopNegotiation.NormalizeMode("anything-else"));
        Assert.Equal("View", DesktopNegotiation.NormalizeMode(null));
    }

    [Fact]
    public void Input_RoundTripsThroughJson()
    {
        var input = new DesktopInput
        {
            Kind = "key", Key = "ctrl+alt+delete", Down = true, X = 0.5, Y = 0.25, Ctrl = true, Alt = true,
        };
        var json = JsonSerializer.Serialize(input, EsoJson.Options);
        var back = JsonSerializer.Deserialize<DesktopInput>(json, EsoJson.Options)!;
        Assert.Equal("key", back.Kind);
        Assert.Equal("ctrl+alt+delete", back.Key);
        Assert.True(back.Down);
        Assert.Equal(0.5, back.X);
        Assert.True(back.Ctrl && back.Alt);
    }

    [Fact]
    public void RawDiff_UnchangedFrameIsSkipped_AndDiffRoundTrips()
    {
        var source = new SyntheticCaptureSource();
        var pump = new RawDiffPump();
        var first = pump.Next(source, 0);
        Assert.NotNull(first);
        Assert.True(first!.Full); // first frame is a keyframe

        // Decode the keyframe; the decoder state is the base for diffs.
        var decoded = RawDiffCodec.Decode(Convert.FromBase64String(first.Data), null);

        var second = pump.Next(source, 0);
        Assert.NotNull(second); // the moving box changed some tiles
        Assert.False(second!.Full);
        var decoded2 = RawDiffCodec.Decode(Convert.FromBase64String(second.Data), decoded);
        Assert.Equal(decoded.Width, decoded2.Width);

        // A static source produces no second frame at all.
        var still = new StillSource();
        var stillPump = new RawDiffPump();
        Assert.NotNull(stillPump.Next(still, 0));
        Assert.Null(stillPump.Next(still, 0));
        Assert.Null(stillPump.Next(still, 0));
    }

    private sealed class StillSource : IDesktopCaptureSource
    {
        public IReadOnlyList<MonitorInfo> Monitors { get; } = new[] { new MonitorInfo { Index = 0, Name = "still", Width = 64, Height = 64 } };
        public RawFrame Capture(int monitorIndex)
        {
            var data = new byte[64 * 64 * 4];
            for (int i = 0; i < data.Length; i++) data[i] = 7;
            return new RawFrame { Width = 64, Height = 64, Bgra = data };
        }
    }

    [Fact]
    public void RawDiff_DecodeOfKeyframe_ReproducesPixels()
    {
        var source = new SyntheticCaptureSource();
        var frame = source.Capture(0);
        var hashes = RawDiffCodec.TileHashes(frame);
        var all = RawDiffCodec.ChangedTiles(null, hashes);
        var packet = RawDiffCodec.Encode(frame, all);
        var decoded = RawDiffCodec.Decode(packet, null);
        Assert.Equal(frame.Bgra, decoded.Bgra);
    }
}
