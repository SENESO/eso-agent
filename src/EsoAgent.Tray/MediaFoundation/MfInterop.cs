using System.Runtime.InteropServices;
using System.Text;

namespace EsoAgent.Tray.MediaFoundation;

// Minimal Media Foundation interop for the H.264 encoder MFT — only the
// interfaces, methods and constants the encoder uses, declared from the
// Windows SDK headers (mftransform.h, mfobjects.h, mfapi.h). The
// configuration-deciding constants (MFT_CATEGORY_VIDEO_ENCODER and the
// MF_MT_* media-type attributes) were cross-checked against the SDK
// header values; the rest are the platform's long-stable interface IIDs.
// First run on a Windows machine must confirm the engaged encoder via
// the capabilities probe (BUILD-REPORT §20 lists the exact steps,
// including a diff of these GUIDs against the SDK headers on disk).

internal static class MfGuids
{
    // {F79EAC7D-E545-4387-BDEE-D647D7BDE42A}
    public static readonly Guid MftCategoryVideoEncoder = new(0xf79eac7d, 0xe545, 0x4387, 0xbd, 0xee, 0xd6, 0x47, 0xd7, 0xbd, 0xe4, 0x2a);
    // MFMediaType_Video {73646976-0000-0010-8000-00AA00389B71}
    public static readonly Guid MediaTypeVideo = new(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    // MFVideoFormat_NV12: FOURCC 'NV12' over the mediatype base GUID
    public static readonly Guid VideoFormatNv12 = new(0x3231564e, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    // MFVideoFormat_H264: FOURCC 'H264' over the mediatype base GUID.
    // Samples in this format carry start codes with SPS/PPS interleaved,
    // one complete picture per sample (Microsoft Learn, Video Subtype GUIDs).
    public static readonly Guid VideoFormatH264 = new(0x34363248, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    public static readonly Guid MtMajorType = new(0x48eba18e, 0xf8c9, 0x4687, 0xbf, 0x11, 0x0a, 0x74, 0xc9, 0xf9, 0x6a, 0x8f);
    public static readonly Guid MtSubtype = new(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);
    // {20332624-FB0D-4D9E-BD0D-CBF6786C102E}
    public static readonly Guid MtAvgBitrate = new(0x20332624, 0xfb0d, 0x4d9e, 0xbd, 0x0d, 0xcb, 0xf6, 0x78, 0x6c, 0x10, 0x2e);
    // {1652C33D-D6B2-4012-B834-72030849A37D} — UINT64: width << 32 | height
    public static readonly Guid MtFrameSize = new(0x1652c33d, 0xd6b2, 0x4012, 0xb8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xa3, 0x7d);
    // {C459A2E8-3D2C-4E44-B132-FEE5156C7BB0} — UINT64: numerator << 32 | denominator
    public static readonly Guid MtFrameRate = new(0xc459a2e8, 0x3d2c, 0x4e44, 0xb1, 0x32, 0xfe, 0xe5, 0x15, 0x6c, 0x7b, 0xb0);
    // {E2724BB8-E676-4806-B4B2-A8D6EFB44CCD}
    public static readonly Guid MtInterlaceMode = new(0xe2724bb8, 0xe676, 0x4806, 0xb4, 0xb2, 0xa8, 0xd6, 0xef, 0xb4, 0x4c, 0xcd);

    // mftransform.h — read via GetAttributes to detect an async MFT; the
    // unlock is set on the same store before any type is set. A wrong
    // value here fails safe: type negotiation rejects the candidate and
    // the factory moves on (see MfH264EncoderFactory).
    public static readonly Guid TransformAsync = new(0x6cb25525, 0x3622, 0x4abd, 0xa2, 0x99, 0x2f, 0x50, 0x34, 0xad, 0x33, 0xe1);
    public static readonly Guid TransformAsyncUnlock = new(0xe5666d6b, 0x3422, 0x4eb6, 0xa4, 0x21, 0xda, 0x7d, 0xb1, 0xf8, 0xe2, 0x07);
}

internal static class MfConst
{
    public const uint MfVersion = 0x00020070; // MF_VERSION (API 0x70, SDK 2)
    public const uint MfStartupFull = 0;

    // MFT_ENUM_FLAG_*
    public const uint EnumSyncMft = 0x00000001;
    public const uint EnumAsyncMft = 0x00000002;
    public const uint EnumHardware = 0x00000004;
    public const uint EnumSortAndFilter = 0x00000040;

    public const uint VideoInterlaceProgressive = 2;

    // MFT_MESSAGE_TYPE values used here
    public const uint MessageCommandFlush = 0x00000000;
    public const uint MessageCommandDrain = 0x00000001;
    public const uint MessageNotifyBeginStreaming = 0x10000000;
    public const uint MessageNotifyEndStreaming = 0x10000001;
    public const uint MessageNotifyStartOfStream = 0x10000003;

    // MediaEventType values for async MFTs
    public const ushort MeTransformNeedInput = 601;
    public const ushort MeTransformHaveOutput = 602;
    public const uint EventFlagNoWait = 0x00000001;

    public const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    public const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
}

[StructLayout(LayoutKind.Sequential)]
internal sealed class MftRegisterTypeInfo
{
    public Guid MajorType;
    public Guid Subtype;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint Flags;
    public uint Size;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
    public uint StreamID;
    [MarshalAs(UnmanagedType.Interface)]
    public IMfSample? Sample;
    public uint Status;
    public IntPtr Events; // IMFCollection — unused, always null in our calls
}

/// <summary>IMFAttributes (mfobjects.h) — full vtable, in order. Only the
/// methods the encoder calls carry exact signatures; the rest are declared
/// pointer-sized so their slots line up.</summary>
[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMfAttributes
{
    [PreserveSig] int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int Compare(IntPtr attributes, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int GetUINT32(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out uint length);
    [PreserveSig] int GetString(ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
    [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
    [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint blobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, [In] byte[] buffer, uint size);
    [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IntPtr destination);
}

/// <summary>IMFMediaType = IMFAttributes + 4 methods.</summary>
[ComImport, Guid("44ae0fa8-ea31-4109-8d2d-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMfMediaType : IMfAttributes
{
    [PreserveSig] int GetMajorType(out Guid majorType);
    [PreserveSig] int IsEqual([MarshalAs(UnmanagedType.Interface)] IMfMediaType other, out uint flags);
    [PreserveSig] int GetRepresentation(Guid format, out IntPtr representation);
    [PreserveSig] int FreeRepresentation(Guid format, IntPtr representation);
}

/// <summary>IMFActivate = IMFAttributes + activation.</summary>
[ComImport, Guid("7fee9ac9-83c0-4e05-995c-bb3412a907e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMfActivate : IMfAttributes
{
    [PreserveSig] int ActivateObject(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

/// <summary>IMFMediaBuffer (mfobjects.h).</summary>
[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMfMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint currentLength);
    [PreserveSig] int SetCurrentLength(uint currentLength);
    [PreserveSig] int GetMaxLength(out uint maxLength);
}

/// <summary>IMFSample = IMFAttributes + sample methods.</summary>
[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMfSample : IMfAttributes
{
    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, [MarshalAs(UnmanagedType.Interface)] out IMfMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer([MarshalAs(UnmanagedType.Interface)] out IMfMediaBuffer buffer);
    [PreserveSig] int AddBuffer([MarshalAs(UnmanagedType.Interface)] IMfMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(uint index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint totalLength);
    [PreserveSig] int CopyToBuffer([MarshalAs(UnmanagedType.Interface)] IMfMediaBuffer buffer);
}

/// <summary>IMFMediaEvent = IMFAttributes + 4 methods.</summary>
[ComImport, Guid("df35ebea-b6b5-4e7c-aab3-d0f1b30e3265"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMfMediaEvent : IMfAttributes
{
    [PreserveSig] int GetType(out ushort mediaEventType);
    [PreserveSig] int GetExtendedType(out Guid extendedType);
    [PreserveSig] int GetStatus(out int status);
    [PreserveSig] int GetValue(IntPtr value); // PROPVARIANT* — unused
}

/// <summary>IMFMediaEventGenerator (mfobjects.h) — its presence on an MFT
/// is also how an asynchronous MFT is detected (no attribute GUID needed).</summary>
[ComImport, Guid("765a34fd-f508-4e69-8c5f-e677b404ba46"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMfMediaEventGenerator
{
    [PreserveSig] int GetEvent(uint flags, [MarshalAs(UnmanagedType.Interface)] out IMfMediaEvent mediaEvent);
    [PreserveSig] int BeginGetEvent(IntPtr callback, [MarshalAs(UnmanagedType.IUnknown)] object state);
    [PreserveSig] int EndGetEvent(IntPtr result, [MarshalAs(UnmanagedType.Interface)] out IMfMediaEvent mediaEvent);
    [PreserveSig] int QueueEvent(uint mediaEventType, ref Guid extendedType, int status, IntPtr value);
}

/// <summary>IMFTransform (mftransform.h) — full vtable, in order.</summary>
[ComImport, Guid("bbeea701-0d63-4109-bda3-4e934180e73e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMfTransform
{
    [PreserveSig] int GetStreamLimits(out uint inputMinimum, out uint inputMaximum, out uint outputMinimum, out uint outputMaximum);
    [PreserveSig] int GetStreamCount(out uint inputStreams, out uint outputStreams);
    [PreserveSig] int GetStreamIDs(uint inputIdArraySize, [Out] uint[] inputIds, uint outputIdArraySize, [Out] uint[] outputIds);
    [PreserveSig] int GetInputStreamInfo(uint inputStreamId, out IntPtr streamInfo); // MFT_INPUT_STREAM_INFO* — unused
    [PreserveSig] int GetOutputStreamInfo(uint outputStreamId, out MftOutputStreamInfo streamInfo);
    [PreserveSig] int GetAttributes([MarshalAs(UnmanagedType.Interface)] out IMfAttributes attributes);
    [PreserveSig] int GetInputAvailableType(uint inputStreamId, uint typeIndex, [MarshalAs(UnmanagedType.Interface)] out IMfMediaType type);
    [PreserveSig] int GetOutputAvailableType(uint outputStreamId, uint typeIndex, [MarshalAs(UnmanagedType.Interface)] out IMfMediaType type);
    [PreserveSig] int SetInputType(uint inputStreamId, [MarshalAs(UnmanagedType.Interface)] IMfMediaType type, uint flags);
    [PreserveSig] int SetOutputType(uint outputStreamId, [MarshalAs(UnmanagedType.Interface)] IMfMediaType type, uint flags);
    [PreserveSig] int GetInputCurrentType(uint inputStreamId, [MarshalAs(UnmanagedType.Interface)] out IMfMediaType type);
    [PreserveSig] int GetOutputCurrentType(uint outputStreamId, [MarshalAs(UnmanagedType.Interface)] out IMfMediaType type);
    [PreserveSig] int GetInputStatus(uint inputStreamId, out uint flags);
    [PreserveSig] int GetOutputStatus(out uint flags);
    [PreserveSig] int SetOutputBounds(long lowerBound, long upperBound);
    [PreserveSig] int ProcessEvent(uint inputStreamId, [MarshalAs(UnmanagedType.Interface)] IMfMediaEvent mediaEvent);
    [PreserveSig] int ProcessInput(uint inputStreamId, [MarshalAs(UnmanagedType.Interface)] IMfSample sample, uint flags);
    [PreserveSig] int ProcessOutput(uint flags, uint outputBufferCount, [In, Out] MftOutputDataBuffer[] outputSamples, out uint status);
    [PreserveSig] int ProcessMessage(uint message, UIntPtr param);
}

internal static class MfPlat
{
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType([MarshalAs(UnmanagedType.Interface)] out IMfMediaType mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample([MarshalAs(UnmanagedType.Interface)] out IMfSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(uint maxLength, [MarshalAs(UnmanagedType.Interface)] out IMfMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFTEnumEx(
        ref Guid category,
        uint flags,
        [In] MftRegisterTypeInfo? inputType,
        [In] MftRegisterTypeInfo? outputType,
        out IntPtr activateArray, // IMFActivate*[] — caller frees with CoTaskMemFree
        out uint elementCount);

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new MfException(what, hr);
    }

    public static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject))
        {
            try { Marshal.ReleaseComObject(comObject); } catch { }
        }
    }
}

internal sealed class MfException : Exception
{
    public int HResultCode { get; }
    public MfException(string what, int hr)
        : base($"{what} failed (HRESULT 0x{hr:X8}).") => HResultCode = hr;
}
