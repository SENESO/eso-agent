using EsoAgent.Core;
using Xunit;

namespace EsoAgent.Tests;

/// <summary>
/// The H.264 groundwork's testable surface (BUILD-REPORT §20): pixel
/// conversion, Annex B packet plumbing, session policy and negotiation.
/// The Media Foundation encoder itself is Windows-only and probed on the
/// machine — what is pinned here is every decision the platform code
/// consumes, with values computed from the documented formulas, not from
/// the code under test.
/// </summary>
public class VideoCodecTests
{
    private static RawVideoFrame Frame(byte[] bgra, int width, int height, int stride = 0)
        => new() { Bgra = bgra, Width = width, Height = height, Stride = stride };

    // One pixel, BGRA order.
    private static byte[] Pixel(byte b, byte g, byte r) => new[] { b, g, r, (byte)255 };
    private static byte[] Red => Pixel(0, 0, 255);
    private static byte[] Green => Pixel(0, 255, 0);
    private static byte[] Blue => Pixel(255, 0, 0);
    private static byte[] White => Pixel(255, 255, 255);
    private static byte[] Black => Pixel(0, 0, 0);

    [Fact]
    public void Nv12Size_MatchesTheFormatDefinition()
    {
        Assert.Equal(6, PixelConversion.Nv12Size(2, 2));
        Assert.Equal(17, PixelConversion.Nv12Size(3, 3)); // odd dims round the chroma plane up
        Assert.Equal(3_110_400, PixelConversion.Nv12Size(1920, 1080));
    }

    [Fact]
    public void BgraToNv12_KnownColors_ProduceTheDocumentedValues()
    {
        // BT.601 limited range: black Y=16, white Y=235, neutral chroma 128.
        // Red (66/129/25 coefficients): Y=82, U=90, V=240.
        var frame = Frame(new[] { Red, Green, Blue, White }.SelectMany(p => p).ToArray(), 2, 2);
        var nv12 = new byte[PixelConversion.Nv12Size(2, 2)];
        var written = PixelConversion.BgraToNv12(frame, nv12);

        Assert.Equal(nv12.Length, written);
        Assert.Equal(new byte[] { 82, 144, 41, 235 }, nv12[..4]); // Y plane, row-major
    }

    [Fact]
    public void BgraToNv12_NeutralColors_HaveCenteredChroma()
    {
        var frame = Frame(new[] { White, White, White, White }.SelectMany(p => p).ToArray(), 2, 2);
        var nv12 = new byte[PixelConversion.Nv12Size(2, 2)];
        PixelConversion.BgraToNv12(frame, nv12);
        Assert.Equal(128, nv12[4]); // U
        Assert.Equal(128, nv12[5]); // V
    }

    [Fact]
    public void BgraToNv12_ChromaIsThe2x2Average()
    {
        // Two red + two black average to R=127: U=109, V=184 by the formula.
        var frame = Frame(new[] { Red, Red, Black, Black }.SelectMany(p => p).ToArray(), 2, 2);
        var nv12 = new byte[PixelConversion.Nv12Size(2, 2)];
        PixelConversion.BgraToNv12(frame, nv12);
        Assert.Equal(109, nv12[4]);
        Assert.Equal(184, nv12[5]);
    }

    [Fact]
    public void BgraToNv12_PaddedStride_ReadsTheSamePixels()
    {
        var tight = Frame(new[] { Red, Green, Blue, White }.SelectMany(p => p).ToArray(), 2, 2);
        var paddedBytes = new byte[16 * 2];
        Array.Copy(Red, 0, paddedBytes, 0, 4);
        Array.Copy(Green, 0, paddedBytes, 4, 4);
        Array.Copy(Blue, 0, paddedBytes, 16, 4);
        Array.Copy(White, 0, paddedBytes, 20, 4);
        var padded = Frame(paddedBytes, 2, 2, stride: 16);

        var a = new byte[PixelConversion.Nv12Size(2, 2)];
        var b = new byte[PixelConversion.Nv12Size(2, 2)];
        PixelConversion.BgraToNv12(tight, a);
        PixelConversion.BgraToNv12(padded, b);
        Assert.Equal(a, b);
    }

    // ---- Annex B plumbing ----

    private static readonly byte[] Sps = { 0, 0, 0, 1, 0x67, 0x64, 0x00, 0x1f };
    private static readonly byte[] Pps = { 0, 0, 0, 1, 0x68, 0xee, 0x3c, 0x80 };
    private static readonly byte[] Idr = { 0, 0, 0, 1, 0x65, 0x88, 0x84 };
    private static readonly byte[] NonIdr = { 0, 0, 0, 1, 0x41, 0x9a };

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void Packetizer_DetectsKeyFramesByIdrPresence()
    {
        Assert.True(H264Packetizer.IsKeyFrame(Join(Sps, Pps, Idr)));
        Assert.True(H264Packetizer.IsKeyFrame(Idr));
        Assert.False(H264Packetizer.IsKeyFrame(NonIdr));
        Assert.False(H264Packetizer.IsKeyFrame(Join(Sps, Pps)));
    }

    [Fact]
    public void Packetizer_HandlesThreeByteStartCodes()
    {
        var idr3 = new byte[] { 0, 0, 1, 0x65, 0x88 };
        Assert.True(H264Packetizer.IsKeyFrame(idr3));
        Assert.Equal(5, H264Packetizer.FirstNalType(idr3));
        Assert.Equal(7, H264Packetizer.FirstNalType(Sps));
        Assert.Equal(-1, H264Packetizer.FirstNalType(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Packetizer_ExtractsTheParameterSets()
    {
        Assert.Equal(Join(Sps, Pps), H264Packetizer.ExtractParameterSets(Join(Sps, Pps, Idr)));
        Assert.Equal(Sps, H264Packetizer.ExtractParameterSets(Join(Sps, Idr)));
        Assert.Null(H264Packetizer.ExtractParameterSets(NonIdr));
    }

    [Fact]
    public void Packetizer_EnsureSequenceHeader_OnlyTouchesBareKeyFrames()
    {
        var header = Join(Sps, Pps);
        Assert.Equal(Join(Sps, Pps, Idr), H264Packetizer.EnsureSequenceHeader(Idr, header));
        Assert.Equal(Join(Sps, Pps, Idr), H264Packetizer.EnsureSequenceHeader(Join(Sps, Pps, Idr), header));
        Assert.Equal(NonIdr, H264Packetizer.EnsureSequenceHeader(NonIdr, header));
        Assert.Equal(Idr, H264Packetizer.EnsureSequenceHeader(Idr, null));
        Assert.Equal(Idr, H264Packetizer.EnsureSequenceHeader(Idr, Array.Empty<byte>()));
    }

    // ---- Policy ----

    [Fact]
    public void Policy_Bitrate_1080p30Quality60_LandsInTheHardwareRange()
    {
        var bps = H264Policy.BitrateFor(1920, 1080, 30, 60);
        Assert.InRange(bps, 7_000_000, 8_000_000);
    }

    [Fact]
    public void Policy_Bitrate_IsMonotonicAndClamped()
    {
        Assert.True(H264Policy.BitrateFor(1920, 1080, 30, 90) > H264Policy.BitrateFor(1920, 1080, 30, 60));
        Assert.True(H264Policy.BitrateFor(1920, 1080, 30, 60) > H264Policy.BitrateFor(1920, 1080, 30, 20));
        Assert.Equal(H264Policy.MinBitrateBps, H264Policy.BitrateFor(16, 16, 1, 10));
        Assert.Equal(H264Policy.MaxBitrateBps, H264Policy.BitrateFor(7680, 4320, 60, 95));
    }

    [Fact]
    public void Policy_KeyFrameInterval_IsTwoSecondsClamped()
    {
        Assert.Equal(30, H264Policy.KeyFrameIntervalFrames(10));
        Assert.Equal(60, H264Policy.KeyFrameIntervalFrames(30));
        Assert.Equal(120, H264Policy.KeyFrameIntervalFrames(120));
    }

    [Fact]
    public void Scheduler_FirstFrameThenIntervalThenOnRequest()
    {
        var scheduler = new KeyFrameScheduler(intervalFrames: 3);
        Assert.True(scheduler.NextForceKeyFrame()); // first frame
        scheduler.ObservePacket(false);
        Assert.False(scheduler.NextForceKeyFrame());
        scheduler.ObservePacket(false);
        Assert.False(scheduler.NextForceKeyFrame());
        scheduler.ObservePacket(false);
        Assert.True(scheduler.NextForceKeyFrame()); // interval reached
        scheduler.ObservePacket(true);
        Assert.False(scheduler.NextForceKeyFrame());
        scheduler.RequestKeyFrame(); // a viewer joined
        Assert.True(scheduler.NextForceKeyFrame());
    }

    [Fact]
    public void Scheduler_AnEarlyEncoderKeyFrame_ResetsTheCadence()
    {
        var scheduler = new KeyFrameScheduler(intervalFrames: 3);
        Assert.True(scheduler.NextForceKeyFrame());
        scheduler.ObservePacket(true); // encoder keyframed on its own
        Assert.False(scheduler.NextForceKeyFrame());
        scheduler.ObservePacket(false);
        Assert.False(scheduler.NextForceKeyFrame());
    }

    // ---- Negotiation + wire mapping ----

    [Fact]
    public void Negotiation_H264WinsOnlyWhenBothSidesOfferIt()
    {
        Assert.Equal("h264", DesktopNegotiation.PickCodec(new[] { "jpeg", "h264" }, new[] { "h264" }));
        Assert.Equal("h264", DesktopNegotiation.PickCodec(new[] { "h264" }, new[] { "h264", "jpeg" }));
        Assert.Equal("jpeg", DesktopNegotiation.PickCodec(new[] { "jpeg", "h264" }, new[] { "jpeg" }));
        Assert.Equal("jpeg", DesktopNegotiation.PickCodec(new[] { "jpeg" }, new[] { "h264", "jpeg" }));
        Assert.Null(DesktopNegotiation.PickCodec(new[] { "h264" }, new[] { "jpeg" }));
    }

    [Fact]
    public void ToDesktopFrame_CarriesThePacketAndTheKeyFrameBit()
    {
        var packet = new EncodedPacket { Data = new byte[] { 1, 2, 3 }, IsKeyFrame = true };
        var frame = H264Policy.ToDesktopFrame(packet, seq: 7, width: 1920, height: 1080);
        Assert.Equal("h264", frame.Codec);
        Assert.True(frame.Full);
        Assert.Equal(7, frame.Seq);
        Assert.Equal("AQID", frame.Data);

        var delta = H264Policy.ToDesktopFrame(new EncodedPacket { Data = new byte[] { 9 }, IsKeyFrame = false }, 8, 1920, 1080);
        Assert.False(delta.Full);
    }
}
