namespace EsoAgent.Core;

// Hardware video encoding for the desktop stream (backlog #3, BUILD-REPORT
// §20). The frame protocol was codec-negotiated from the start precisely so
// this could slot in: a session negotiates "h264" only when the host offers
// it (a working encoder exists — the factory probe below is the authority)
// AND the client offers it (it can decode). Everything else keeps the
// jpeg/rawdiff paths byte-for-byte.
//
// The encoder contract is deliberately small and platform-neutral. The
// Windows implementation (Media Foundation, EsoAgent.Tray) prefers a
// hardware MFT and falls back to the inbox software encoder; a Linux
// VAAPI factory can implement the same interface later without touching
// the runner, the negotiation, or the wire format.

/// <summary>One captured frame, BGRA32 — the layout both capture paths
/// already produce (DXGI Desktop Duplication surfaces and the GDI
/// fallback's Format32bppArgb bitmap are both BGRA in memory).</summary>
public sealed class RawVideoFrame
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Bytes per row in <see cref="Bgra"/>; 0 means tightly packed (Width * 4).</summary>
    public int Stride { get; init; }
    public long TimestampMs { get; init; }

    public int EffectiveStride => Stride > 0 ? Stride : Width * 4;
}

/// <summary>One encoded access unit in Annex B byte-stream form
/// (start-code-prefixed NAL units, sequence header riding on keyframes).</summary>
public sealed class EncodedPacket
{
    public required byte[] Data { get; init; }
    public required bool IsKeyFrame { get; init; }
}

/// <summary>
/// A stateful video encoder for one desktop session. Implementations are
/// configured for low latency (no B-frames), so in practice each fed frame
/// yields at most one packet — but the contract allows buffering: an empty
/// result means "accepted, nothing to send yet", never an error.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>The negotiated codec name this encoder produces ("h264").</summary>
    string Codec { get; }
    bool HardwareAccelerated { get; }
    string EncoderName { get; }
    IReadOnlyList<EncodedPacket> Encode(RawVideoFrame frame, bool forceKeyFrame);
}

/// <summary>Optional live retune: the runner's adaptive loop speaks in
/// quality/fps for stills codecs; for video the honest equivalent is the
/// target bitrate. Encoders that cannot retune simply don't implement it.</summary>
public interface IVideoEncoderBitrateControl
{
    void SetBitrate(int bitsPerSecond);
}

/// <summary>The result of asking a platform factory whether this machine
/// can encode at all, and how. Probed once and cached by the caller — the
/// probe activates real encoder objects, it is not a registry glance.</summary>
public sealed class VideoEncoderCapability
{
    public bool Available { get; init; }
    public bool HardwareAccelerated { get; init; }
    public string EncoderName { get; init; } = "";
    public string? FailureReason { get; init; }

    public static VideoEncoderCapability None(string reason)
        => new() { Available = false, FailureReason = reason };

    public static VideoEncoderCapability Found(string encoderName, bool hardwareAccelerated)
        => new() { Available = true, EncoderName = encoderName, HardwareAccelerated = hardwareAccelerated };
}

/// <summary>Creates session encoders for one platform. Probe() must be
/// honest: Available only when an encoder was actually activated and
/// configured during the probe, not merely enumerated.</summary>
public interface IVideoEncoderFactory
{
    VideoEncoderCapability Probe();
    IVideoEncoder? Create(int width, int height, int bitrateBps, int fps, int keyFrameIntervalFrames);
}

/// <summary>Codec names on the wire. The string IS the protocol value in
/// DesktopHello/DesktopFrame — never rename a shipped one.</summary>
public static class VideoCodecNames
{
    public const string H264 = "h264";
    public const string Jpeg = "jpeg";
    public const string RawDiff = "rawdiff";
}
