namespace EsoAgent.Core;

/// <summary>
/// H.264 session policy: how the stills-codec knobs (quality 10..95,
/// fps ceiling) translate to a video encoder, and when keyframes happen.
/// Pure logic — the platform encoder consumes its decisions, the tests
/// pin them.
/// </summary>
public static class H264Policy
{
    public const int MinBitrateBps = 400_000;
    public const int MaxBitrateBps = 25_000_000;

    /// <summary>
    /// Target bitrate for a stream. Model: bits-per-pixel-per-frame scaled
    /// by the quality hint (the same 10..95 the jpeg path uses), so the
    /// runner's presets and adaptive loop keep their meaning across codecs.
    /// At 1080p30 / quality 60 this lands at ~7.5 Mbps — the range hardware
    /// encoders are built for, and far below the jpeg path's wire cost for
    /// the same motion (BENCHMARKS.md: one full pixel frame ≈ 50 KB before
    /// encoding, every frame, changed or not).
    /// </summary>
    public static int BitrateFor(int width, int height, int fps, int quality)
    {
        var bpp = Math.Clamp(quality, 10, 95) switch
        {
            >= 80 => 0.20,
            >= 55 => 0.12,
            >= 35 => 0.07,
            _ => 0.04,
        };
        var bps = (double)width * height * Math.Clamp(fps, 1, 60) * bpp;
        return (int)Math.Clamp(bps, MinBitrateBps, MaxBitrateBps);
    }

    /// <summary>Keyframe cadence: a keyframe at least every 2 seconds of
    /// stream (late joiners and loss recovery), clamped to sane bounds.</summary>
    public static int KeyFrameIntervalFrames(int fps) => Math.Clamp(fps * 2, 30, 120);

    /// <summary>The frame → wire mapping: an encoded packet IS a
    /// DesktopFrame whose Full flag carries the keyframe bit (the field
    /// already means "self-contained frame" for rawdiff keyframes).</summary>
    public static DesktopFrame ToDesktopFrame(EncodedPacket packet, long seq, int width, int height) => new()
    {
        Seq = seq,
        Codec = VideoCodecNames.H264,
        Full = packet.IsKeyFrame,
        Width = width,
        Height = height,
        Data = Convert.ToBase64String(packet.Data),
    };
}

/// <summary>
/// Decides, per captured frame, whether the encoder must be told to emit
/// a keyframe: the first frame always, then on the policy interval, and
/// whenever the consumer asks (a new viewer joining mid-stream has
/// nothing to decode P-frames against).
/// </summary>
public sealed class KeyFrameScheduler
{
    private readonly int _intervalFrames;
    private int _sinceKey;
    private bool _requestPending = true; // first frame is a keyframe

    public KeyFrameScheduler(int intervalFrames) => _intervalFrames = Math.Max(1, intervalFrames);

    public void RequestKeyFrame() => _requestPending = true;

    /// <summary>Call once per frame fed to the encoder; the answer rides
    /// that frame. Feed the encoder's actual keyframe report back through
    /// <see cref="ObservePacket"/> so the cadence tracks reality (an
    /// encoder may keyframe earlier than asked, e.g. on a scene cut).</summary>
    public bool NextForceKeyFrame()
    {
        var force = _requestPending || _sinceKey >= _intervalFrames;
        _requestPending = false;
        return force;
    }

    public void ObservePacket(bool wasKeyFrame)
    {
        _sinceKey = wasKeyFrame ? 0 : _sinceKey + 1;
    }
}

/// <summary>
/// Annex B byte-stream plumbing between the platform encoder and the
/// wire. Media Foundation's H.264 encoder emits access units whose
/// keyframes may or may not carry the SPS/PPS sequence header inline
/// (it is exposed separately as MF_MT_MPEG_SEQUENCE_HEADER); a decoder
/// joining at a keyframe needs the header ON that frame. These helpers
/// normalize that, and read NAL types back out (keyframe detection by
/// IDR presence — the authority, not a sample flag).
/// </summary>
public static class H264Packetizer
{
    /// <summary>NAL unit type of the first NAL in the buffer, or -1.</summary>
    public static int FirstNalType(ReadOnlySpan<byte> data)
    {
        var start = StartCodeLength(data);
        if (start < 0 || data.Length <= start) return -1;
        return data[start] & 0x1F;
    }

    public static bool ContainsNalType(ReadOnlySpan<byte> data, int nalType)
    {
        var i = 0;
        while (i < data.Length)
        {
            var sc = StartCodeLength(data[i..]);
            if (sc < 0)
            {
                // Not start-code framed (e.g. a bare payload): treat the
                // remainder as one NAL.
                return data.Length > i && (data[i] & 0x1F) == nalType;
            }
            if (i + sc < data.Length && (data[i + sc] & 0x1F) == nalType) return true;
            // Skip to the next start code.
            var next = IndexOfStartCode(data, i + sc);
            if (next < 0) return false;
            i = next;
        }
        return false;
    }

    /// <summary>True when the access unit contains an IDR slice (NAL type 5).</summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> accessUnit) => ContainsNalType(accessUnit, 5);

    public static bool HasSequenceHeader(ReadOnlySpan<byte> accessUnit) => ContainsNalType(accessUnit, 7);

    /// <summary>
    /// Returns the access unit with the sequence header in front when it
    /// is a keyframe that lacks one; otherwise the unit unchanged. The
    /// header is expected in Annex B form (as MF_MT_MPEG_SEQUENCE_HEADER
    /// provides it); a header without a leading start code gets one.
    /// </summary>
    public static byte[] EnsureSequenceHeader(byte[] accessUnit, byte[]? sequenceHeader)
    {
        if (sequenceHeader == null || sequenceHeader.Length == 0) return accessUnit;
        if (!IsKeyFrame(accessUnit) || HasSequenceHeader(accessUnit)) return accessUnit;
        var header = sequenceHeader;
        if (StartCodeLength(header) < 0)
        {
            var framed = new byte[header.Length + 4];
            framed[3] = 1;
            header.CopyTo(framed, 4);
            header = framed;
        }
        var combined = new byte[header.Length + accessUnit.Length];
        header.CopyTo(combined, 0);
        accessUnit.CopyTo(combined, header.Length);
        return combined;
    }

    /// <summary>
    /// Builds a sequence header (SPS then PPS, Annex B form) out of an
    /// access unit that carries them — the self-contained alternative to
    /// reading MF_MT_MPEG_SEQUENCE_HEADER off the encoder's output type,
    /// and the form <see cref="EnsureSequenceHeader"/> consumes. Returns
    /// null when the unit holds no SPS.
    /// </summary>
    public static byte[]? ExtractParameterSets(ReadOnlySpan<byte> accessUnit)
    {
        byte[]? sps = null, pps = null;
        var i = 0;
        while (i < accessUnit.Length)
        {
            var sc = StartCodeLength(accessUnit[i..]);
            if (sc < 0) break;
            var next = IndexOfStartCode(accessUnit, i + sc);
            var end = next < 0 ? accessUnit.Length : next;
            if (i + sc < end)
            {
                var type = accessUnit[i + sc] & 0x1F;
                if (type == 7 && sps == null) sps = accessUnit[i..end].ToArray();
                else if (type == 8 && pps == null) pps = accessUnit[i..end].ToArray();
            }
            if (next < 0) break;
            i = next;
        }
        if (sps == null) return null;
        if (pps == null) return sps;
        var both = new byte[sps.Length + pps.Length];
        sps.CopyTo(both, 0);
        pps.CopyTo(both, sps.Length);
        return both;
    }

    /// <summary>Length of the Annex B start code at the start of the
    /// span (3 or 4), or -1 when the span does not begin with one.</summary>
    public static int StartCodeLength(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1) return 4;
        if (data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1) return 3;
        return -1;
    }

    private static int IndexOfStartCode(ReadOnlySpan<byte> data, int from)
    {
        for (var i = from; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && (data[i + 2] == 1 || (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)))
                return i;
        }
        return -1;
    }
}
