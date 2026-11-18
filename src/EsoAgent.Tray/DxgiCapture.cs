using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace EsoAgent.Tray;

/// <summary>
/// DXGI Desktop Duplication capture (the primary Windows capture path).
/// Hand-written COM interop — no third-party packages. Uses
/// IDXGIOutputDuplication.MapDesktopSurface, which gives direct CPU access to
/// the desktop image, so no device-context texture copies are needed.
/// EVERY failure is an HRESULT check that returns false → the caller falls
/// back to GDI for the rest of the session. Compiles clean here; live
/// verification is PENDING FIRST REAL INSTALL.
/// </summary>
public sealed class DxgiCapture : IDisposable
{
    private IDXGIOutputDuplication? _duplication;
    private readonly int _outputIndex;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? LastError { get; private set; }

    public DxgiCapture(int outputIndex) => _outputIndex = outputIndex;

    public bool Initialize()
    {
        try
        {
            var hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                out var device, out _, out var context);
            if (hr != 0 || device == null) { LastError = $"D3D11CreateDevice failed (0x{hr:X8})"; return false; }
            try
            {
                var dxgiDevice = (IDXGIDevice)device;
                hr = dxgiDevice.GetAdapter(out var adapter);
                if (hr != 0 || adapter == null) { LastError = $"GetAdapter failed (0x{hr:X8})"; return false; }
                try
                {
                    hr = adapter.EnumOutputs((uint)_outputIndex, out var output);
                    if (hr != 0 || output == null) { LastError = $"EnumOutputs({_outputIndex}) failed (0x{hr:X8})"; return false; }
                    try
                    {
                        var output1 = (IDXGIOutput1)output;
                        hr = output1.DuplicateOutput(device, out _duplication);
                        if (hr != 0 || _duplication == null) { LastError = $"DuplicateOutput failed (0x{hr:X8})"; return false; }
                        return true;
                    }
                    finally { Marshal.ReleaseComObject(output); }
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
            finally
            {
                if (context != null) Marshal.ReleaseComObject(context);
                Marshal.ReleaseComObject(device);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>Captures one frame into a new Bitmap (BGRA). Returns false on any failure (caller falls back to GDI).</summary>
    public bool TryCapture(out Bitmap? bitmap)
    {
        bitmap = null;
        if (_duplication == null) return false;
        var hr = _duplication.AcquireNextFrame(500, out _, out var resource);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return false; // no new frame yet — treated as "no capture" by caller loop retry
        if (hr != 0) { LastError = $"AcquireNextFrame failed (0x{hr:X8})"; return false; }
        try
        {
            hr = _duplication.MapDesktopSurface(out var mapped);
            if (hr != 0) { LastError = $"MapDesktopSurface failed (0x{hr:X8})"; return false; }
            try
            {
                // Desktop size is learned from the first mapped surface via the output desc is not
                // available here; the caller passes expected size through Width/Height once known:
                // MapDesktopSurface maps the full desktop image of this output.
                var width = Width;
                var height = Height;
                if (width <= 0 || height <= 0) return false; // size not set yet — caller sets from Screen bounds
                bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[width * 4];
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(mapped.PBits, y * mapped.Pitch), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                    }
                }
                finally { bitmap.UnlockBits(data); }
                return true;
            }
            finally
            {
                _duplication.UnMapDesktopSurface(IntPtr.Zero);
            }
        }
        finally
        {
            if (resource != null) Marshal.ReleaseComObject(resource);
            _duplication.ReleaseFrame();
        }
    }

    public void SetSize(int width, int height) { Width = width; Height = height; }

    public void Dispose()
    {
        if (_duplication != null) { Marshal.ReleaseComObject(_duplication); _duplication = null; }
    }

    // ----- interop -----
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
        out ID3D11Device ppDevice, out int pFeatureLevel, out ID3D11DeviceContext ppImmediateContext);

    [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Device { }

    [ComImport, Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11DeviceContext { }

    [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIDevice
    {
        // IDXGIObject prefix (SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent) — never called.
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int GetAdapter(out IDXGIAdapter adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput { }

    [ComImport, Guid("00cddea8-939b-4b4c-a628-6b30c3b77ea7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput1
    {
        // IDXGIObject (4) + IDXGIOutput (12) prefix — never called.
        [PreserveSig] int A01(); [PreserveSig] int A02(); [PreserveSig] int A03(); [PreserveSig] int A04();
        [PreserveSig] int A05(); [PreserveSig] int A06(); [PreserveSig] int A07(); [PreserveSig] int A08();
        [PreserveSig] int A09(); [PreserveSig] int A10(); [PreserveSig] int A11(); [PreserveSig] int A12();
        [PreserveSig] int A13(); [PreserveSig] int A14(); [PreserveSig] int A15(); [PreserveSig] int A16();
        // IDXGIOutput1 additions: GetDisplayModeList1, FindClosestMatchingDisplayMode1, GetDisplaySurfaceData1 — never called.
        [PreserveSig] int B01(); [PreserveSig] int B02(); [PreserveSig] int B03();
        [PreserveSig] int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        [MarshalAs(UnmanagedType.Bool)] public bool RectsCoalesced;
        [MarshalAs(UnmanagedType.Bool)] public bool ProtectedContentMaskedOut;
        public DXGI_OUTDUPL_POINTER_POSITION PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_POINTER_POSITION
    {
        public Point Position;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_MAPPED_RECT
    {
        public int Pitch;
        public IntPtr PBits;
    }

    [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutputDuplication
    {
        // IDXGIObject prefix — never called.
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int AcquireNextFrame(uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out IDXGIResource resource);
        [PreserveSig] int GetFrameDirtyRects();
        [PreserveSig] int GetFrameMoveRects();
        [PreserveSig] int GetFramePointerShape();
        [PreserveSig] int MapDesktopSurface(out DXGI_MAPPED_RECT lockedRect);
        [PreserveSig] int UnMapDesktopSurface(IntPtr rect);
        [PreserveSig] int ReleaseFrame();
    }

    [ComImport, Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIResource { }
}
