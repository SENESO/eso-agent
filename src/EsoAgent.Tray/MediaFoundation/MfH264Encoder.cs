using System.Runtime.InteropServices;
using EsoAgent.Core;

namespace EsoAgent.Tray.MediaFoundation;

// Media Foundation H.264 encoding for the desktop pipeline (BUILD-REPORT
// §20). The factory enumerates the machine's H.264 encoder MFTs —
// hardware first (GPU vendor MFTs: NVENC / Quick Sync / AMF register in
// the same category), then everything else, which on Windows 10/11 always
// includes Microsoft's inbox software encoder. A candidate only counts
// when it ACTIVATES and accepts the full type negotiation at probe size;
// enumeration alone proves nothing, so the probe configures for real.
//
// Keyframes: ICodecAPI's force-keyframe property is deliberately not
// used (its property GUIDs were not re-verified against the SDK for this
// build). The policy scheduler in Core decides when a keyframe is due;
// when one is forced and the encoder has not produced an IDR recently,
// the transform is rebuilt — a fresh encoder's first frame is always an
// IDR. In practice hardware/software MFTs emit periodic IDRs on their
// own GOP cadence and the packetizer's IDR detection (the authority for
// the wire Full flag) keeps the scheduler honest, so rebuilds are rare.
//
// Every failure anywhere in here degrades, never breaks: the factory
// returns null, the tray refuses the h264 open with an error, and the
// service stops offering h264 for a hold period (the next session then
// negotiates jpeg as before). Hardware verification is pending the
// laptop being awake — see BUILD-REPORT §20 for the exact steps.

internal static class MfStartupGate
{
    private static readonly object Gate = new();
    private static bool _started;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started) return;
            MfPlat.Check(MfPlat.MFStartup(MfConst.MfVersion, MfConst.MfStartupFull), "MFStartup");
            _started = true;
        }
    }
}

public sealed class MfH264EncoderFactory : IVideoEncoderFactory
{
    public VideoEncoderCapability Probe()
    {
        try
        {
            MfStartupGate.EnsureStarted();
            using var core = TryCreateCore(640, 360, 1_000_000, 30, out var hardware, out var name);
            if (core == null) return VideoEncoderCapability.None("No H.264 encoder MFT could be activated and configured.");
            return VideoEncoderCapability.Found(name!, hardware);
        }
        catch (Exception ex)
        {
            return VideoEncoderCapability.None($"H.264 probe failed: {ex.Message}");
        }
    }

    public IVideoEncoder? Create(int width, int height, int bitrateBps, int fps, int keyFrameIntervalFrames)
    {
        try
        {
            MfStartupGate.EnsureStarted();
            var core = TryCreateCore(width, height, bitrateBps, fps, out var hardware, out var name);
            if (core == null) return null;
            return new MfH264Encoder(core, width, height, bitrateBps, fps, hardware, name!);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>First fully-configured encoder wins: hardware pass, then all.</summary>
    internal static MfEncoderCore? TryCreateCore(int width, int height, int bitrateBps, int fps, out bool hardware, out string? name)
    {
        foreach (var passHardware in new[] { true, false })
        {
            foreach (var activate in Enumerate(passHardware))
            {
                try
                {
                    var core = MfEncoderCore.TryOpen(activate, width, height, bitrateBps, fps);
                    if (core != null)
                    {
                        hardware = passHardware;
                        name = passHardware ? "hardware H.264 MFT (Media Foundation)" : "software H.264 MFT (Media Foundation)";
                        return core;
                    }
                }
                catch { /* candidate unusable — try the next */ }
                finally { MfPlat.Release(activate); }
            }
        }
        hardware = false;
        name = null;
        return null;
    }

    private static IEnumerable<IMfActivate> Enumerate(bool hardwareOnly)
    {
        var flags = MfConst.EnumSyncMft | MfConst.EnumAsyncMft | MfConst.EnumSortAndFilter
                    | (hardwareOnly ? MfConst.EnumHardware : 0u);
        var outputType = new MftRegisterTypeInfo { MajorType = MfGuids.MediaTypeVideo, Subtype = MfGuids.VideoFormatH264 };
        var category = MfGuids.MftCategoryVideoEncoder;
        var hr = MfPlat.MFTEnumEx(ref category, flags, null, outputType, out var array, out var count);
        var result = new List<IMfActivate>();
        if (hr < 0 || array == IntPtr.Zero) return result;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var ptr = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                if (ptr != IntPtr.Zero && Marshal.GetObjectForIUnknown(ptr) is IMfActivate activate)
                    result.Add(activate);
            }
        }
        finally { Marshal.FreeCoTaskMem(array); }
        return result;
    }
}

/// <summary>One configured encoder transform plus its streaming state.</summary>
internal sealed class MfEncoderCore : IDisposable
{
    private readonly IMfTransform _transform;
    private readonly IMfMediaEventGenerator? _events;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private byte[]? _sequenceHeader;
    private byte[]? _nv12;
    private bool _disposed;

    private MfEncoderCore(IMfTransform transform, IMfMediaEventGenerator? events, int width, int height, int fps)
    {
        _transform = transform;
        _events = events;
        _width = width;
        _height = height;
        _fps = fps;
    }

    public static MfEncoderCore? TryOpen(IMfActivate activate, int width, int height, int bitrateBps, int fps)
    {
        var iid = typeof(IMfTransform).GUID;
        var hr = activate.ActivateObject(ref iid, out var obj);
        if (hr < 0 || obj is not IMfTransform transform) { MfPlat.Release(obj); return null; }

        IMfMediaEventGenerator? events = null;
        try
        {
            // Async MFTs expose IMFMediaEventGenerator; its presence IS the
            // async test (Microsoft Learn: an async MFT must be unlocked
            // before use — set the attribute when the store exists).
            events = transform as IMfMediaEventGenerator;
            if (events != null && transform.GetAttributes(out var attributes) >= 0)
            {
                try
                {
                    var unlock = MfGuids.TransformAsyncUnlock;
                    attributes.SetUINT32(ref unlock, 1);
                }
                finally { MfPlat.Release(attributes); }
            }

            // Output type BEFORE input type (the H.264 encoder's contract).
            var outputType = CreateMediaType(MfGuids.VideoFormatH264, width, height, fps, bitrateBps);
            try { MfPlat.Check(transform.SetOutputType(0, outputType, 0), "SetOutputType(h264)"); }
            finally { MfPlat.Release(outputType); }

            var inputType = CreateMediaType(MfGuids.VideoFormatNv12, width, height, fps, bitrateBps: 0);
            try { MfPlat.Check(transform.SetInputType(0, inputType, 0), "SetInputType(nv12)"); }
            finally { MfPlat.Release(inputType); }

            MfPlat.Check(transform.ProcessMessage(MfConst.MessageNotifyBeginStreaming, UIntPtr.Zero), "ProcessMessage(BEGIN_STREAMING)");
            MfPlat.Check(transform.ProcessMessage(MfConst.MessageNotifyStartOfStream, UIntPtr.Zero), "ProcessMessage(START_OF_STREAM)");
            return new MfEncoderCore(transform, events, width, height, fps);
        }
        catch
        {
            MfPlat.Release(events);
            MfPlat.Release(transform);
            return null;
        }
    }

    private static IMfMediaType CreateMediaType(Guid subtype, int width, int height, int fps, int bitrateBps)
    {
        MfPlat.Check(MfPlat.MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            var major = MfGuids.MtMajorType;
            var video = MfGuids.MediaTypeVideo;
            MfPlat.Check(type.SetGUID(ref major, ref video), "media type major");
            var sub = MfGuids.MtSubtype;
            MfPlat.Check(type.SetGUID(ref sub, ref subtype), "media type subtype");
            var size = MfGuids.MtFrameSize;
            MfPlat.Check(type.SetUINT64(ref size, ((ulong)(uint)width << 32) | (uint)height), "media type frame size");
            var rate = MfGuids.MtFrameRate;
            MfPlat.Check(type.SetUINT64(ref rate, ((ulong)(uint)fps << 32) | 1u), "media type frame rate");
            var interlace = MfGuids.MtInterlaceMode;
            MfPlat.Check(type.SetUINT32(ref interlace, MfConst.VideoInterlaceProgressive), "media type interlace");
            if (bitrateBps > 0)
            {
                var avg = MfGuids.MtAvgBitrate;
                MfPlat.Check(type.SetUINT32(ref avg, (uint)bitrateBps), "media type avg bitrate");
            }
            return type;
        }
        catch
        {
            MfPlat.Release(type);
            throw;
        }
    }

    public IReadOnlyList<EncodedPacket> Encode(RawVideoFrame frame)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MfEncoderCore));
        _nv12 ??= new byte[PixelConversion.Nv12Size(_width, _height)];
        var nv12Length = PixelConversion.BgraToNv12(frame, _nv12);

        IMfSample? sample = null;
        IMfMediaBuffer? buffer = null;
        try
        {
            MfPlat.Check(MfPlat.MFCreateSample(out sample), "MFCreateSample");
            MfPlat.Check(MfPlat.MFCreateMemoryBuffer((uint)nv12Length, out buffer), "MFCreateMemoryBuffer");
            MfPlat.Check(buffer.Lock(out var ptr, out _, out _), "IMFMediaBuffer.Lock");
            try { Marshal.Copy(_nv12, 0, ptr, nv12Length); }
            finally { buffer.Unlock(); }
            MfPlat.Check(buffer.SetCurrentLength((uint)nv12Length), "IMFMediaBuffer.SetCurrentLength");
            MfPlat.Check(sample.AddBuffer(buffer), "IMFSample.AddBuffer");
            MfPlat.Check(sample.SetSampleTime(frame.TimestampMs * 10_000), "IMFSample.SetSampleTime");
            MfPlat.Check(sample.SetSampleDuration(10_000_000 / Math.Max(1, _fps)), "IMFSample.SetSampleDuration");

            if (_events != null) WaitForEvent(_events, MfConst.MeTransformNeedInput, 250);
            MfPlat.Check(_transform.ProcessInput(0, sample, 0), "IMFTransform.ProcessInput");
        }
        finally
        {
            MfPlat.Release(buffer);
            MfPlat.Release(sample);
        }

        return CollectOutput();
    }

    private List<EncodedPacket> CollectOutput()
    {
        var packets = new List<EncodedPacket>();
        for (var guard = 0; guard < 8; guard++)
        {
            if (_events != null && !WaitForEvent(_events, MfConst.MeTransformHaveOutput, 100)) break;

            IMfSample? outSample = null;
            IMfMediaBuffer? outBuffer = null;
            IMfSample? providedSample = null;
            try
            {
                MfPlat.Check(_transform.GetOutputStreamInfo(0, out var info), "GetOutputStreamInfo");
                // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES (0x100): the transform
                // allocates output samples itself and rejects caller ones.
                var providesSamples = (info.Flags & 0x100) != 0;
                if (!providesSamples)
                {
                    MfPlat.Check(MfPlat.MFCreateSample(out outSample), "MFCreateSample(output)");
                    MfPlat.Check(MfPlat.MFCreateMemoryBuffer(Math.Max(info.Size, 4096), out outBuffer), "MFCreateMemoryBuffer(output)");
                    MfPlat.Check(outSample.AddBuffer(outBuffer), "IMFSample.AddBuffer(output)");
                }

                var data = new MftOutputDataBuffer { StreamID = 0, Sample = outSample };
                var buffers = new[] { data };
                var hr = _transform.ProcessOutput(0, 1, buffers, out _);
                if (hr == MfConst.MfETransformNeedMoreInput) break;
                if (hr == MfConst.MfETransformStreamChange) continue; // types renegotiated inline; retry the pull
                MfPlat.Check(hr, "IMFTransform.ProcessOutput");

                var sampleToRead = buffers[0].Sample
                    ?? throw new MfException("IMFTransform.ProcessOutput returned no sample", -1);
                if (!ReferenceEquals(sampleToRead, outSample)) providedSample = sampleToRead;

                MfPlat.Check(sampleToRead.ConvertToContiguousBuffer(out var contiguous), "IMFSample.ConvertToContiguousBuffer");
                try
                {
                    MfPlat.Check(contiguous.Lock(out var ptr, out _, out var length), "IMFMediaBuffer.Lock(output)");
                    var bytes = new byte[length];
                    try { Marshal.Copy(ptr, bytes, 0, (int)length); }
                    finally { contiguous.Unlock(); }
                    if (bytes.Length > 0) packets.Add(MakePacket(bytes));
                }
                finally { MfPlat.Release(contiguous); }
            }
            finally
            {
                MfPlat.Release(providedSample);
                MfPlat.Release(outBuffer);
                MfPlat.Release(outSample);
            }
        }
        return packets;
    }

    private EncodedPacket MakePacket(byte[] annexB)
    {
        var isKey = H264Packetizer.IsKeyFrame(annexB);
        if (isKey)
        {
            // The sequence header rides the keyframe: extract SPS/PPS from
            // the first keyframe that carries them and prepend from then
            // on whenever a keyframe lacks them (decoder join points).
            _sequenceHeader ??= H264Packetizer.ExtractParameterSets(annexB);
            annexB = H264Packetizer.EnsureSequenceHeader(annexB, _sequenceHeader);
        }
        return new EncodedPacket { Data = annexB, IsKeyFrame = isKey };
    }

    private static bool WaitForEvent(IMfMediaEventGenerator events, ushort wanted, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var hr = events.GetEvent(MfConst.EventFlagNoWait, out var mediaEvent);
            if (hr < 0) // queue empty (MF_E_NO_EVENTS_AVAILABLE) — brief wait, then give up at the deadline
            {
                if (Environment.TickCount64 >= deadline) return false;
                Thread.Sleep(2);
                continue;
            }
            try
            {
                mediaEvent.GetType(out var type);
                if (type == wanted) return true;
                if (Environment.TickCount64 >= deadline) return false;
            }
            finally { MfPlat.Release(mediaEvent); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transform.ProcessMessage(MfConst.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        MfPlat.Release(_events);
        MfPlat.Release(_transform);
    }
}

public sealed class MfH264Encoder : IVideoEncoder, IVideoEncoderBitrateControl
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private MfEncoderCore? _core;
    private int _bitrateBps;
    private int _pendingBitrateBps;
    private int _framesSinceKeyFrame;
    private bool _fedAnyFrame; // a fresh transform's first frame is an IDR — no rebuild needed for it
    private bool _disposed;

    internal MfH264Encoder(MfEncoderCore core, int width, int height, int bitrateBps, int fps, bool hardware, string name)
    {
        _core = core;
        _width = width;
        _height = height;
        _fps = fps;
        _bitrateBps = bitrateBps;
        _pendingBitrateBps = bitrateBps;
        HardwareAccelerated = hardware;
        EncoderName = name;
    }

    public string Codec => VideoCodecNames.H264;
    public bool HardwareAccelerated { get; }
    public string EncoderName { get; }

    public void SetBitrate(int bitsPerSecond) => _pendingBitrateBps = Math.Clamp(bitsPerSecond, H264Policy.MinBitrateBps, H264Policy.MaxBitrateBps);

    public IReadOnlyList<EncodedPacket> Encode(RawVideoFrame frame, bool forceKeyFrame)
    {
        if (_disposed || _core == null) return Array.Empty<EncodedPacket>();
        if (frame.Width != _width || frame.Height != _height) return Array.Empty<EncodedPacket>();

        // A forced keyframe the encoder will not produce on its own (it
        // has not emitted an IDR since the last one we counted) is
        // honoured by rebuilding the transform — the new one's first
        // frame is an IDR. A live bitrate retune rides the same rebuild.
        var retune = Math.Abs(_pendingBitrateBps - _bitrateBps) > _bitrateBps / 5;
        if ((forceKeyFrame && _fedAnyFrame && _framesSinceKeyFrame > 0) || retune)
        {
            if (!Rebuild()) return Array.Empty<EncodedPacket>();
        }

        var packets = _core!.Encode(frame);
        _fedAnyFrame = true;
        foreach (var packet in packets)
            _framesSinceKeyFrame = packet.IsKeyFrame ? 0 : _framesSinceKeyFrame + 1;
        return packets;
    }

    private bool Rebuild()
    {
        _core?.Dispose();
        _core = null;
        var core = MfH264EncoderFactory.TryCreateCore(_width, _height, _pendingBitrateBps, _fps, out _, out _);
        if (core == null) return false;
        _core = core;
        _bitrateBps = _pendingBitrateBps;
        _framesSinceKeyFrame = 0;
        _fedAnyFrame = false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _core?.Dispose();
        _core = null;
    }
}

/// <summary>
/// Process-lifetime capability cache for the tray: the probe activates a
/// real encoder, so it runs once and the answer is reused by every
/// "capabilities" request and desktop open. Encoders do not appear or
/// vanish mid-run in practice; a driver change needs a tray restart,
/// which the service-side cache TTL covers from its side.
/// </summary>
public static class DesktopCapabilities
{
    private static readonly object Gate = new();
    private static IVideoEncoderFactory? _factory;
    private static VideoEncoderCapability? _cached;

    public static IVideoEncoderFactory Factory
    {
        get { lock (Gate) { return _factory ??= new MfH264EncoderFactory(); } }
    }

    public static VideoEncoderCapability H264
    {
        get { lock (Gate) { return _cached ??= Factory.Probe(); } }
    }

    /// <summary>The codec list the tray can actually serve right now.</summary>
    public static IReadOnlyList<string> Codecs()
    {
        var codecs = new List<string> { VideoCodecNames.Jpeg };
        if (H264.Available) codecs.Add(VideoCodecNames.H264);
        return codecs;
    }
}
