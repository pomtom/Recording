using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Recorder.Capture;

/// <summary>
/// The bridge between Windows Graphics Capture (a WinRT API) and Direct3D 11 (a COM API).
/// </summary>
/// <remarks>
/// WGC hands out <c>IDirect3DSurface</c> objects and expects an <c>IDirect3DDevice</c>, neither of
/// which the D3D11 projection knows anything about. Three undocumented-looking but entirely
/// supported interop shims close the gap:
/// <list type="bullet">
/// <item><c>CreateDirect3D11DeviceFromDXGIDevice</c> wraps a DXGI device as a WinRT device.</item>
/// <item><c>IGraphicsCaptureItemInterop</c> turns an HMONITOR or HWND into a capture item; it is
/// reachable only through the class's activation factory, not the projection.</item>
/// <item><c>IDirect3DDxgiInterfaceAccess</c> unwraps a captured frame back to an
/// <c>ID3D11Texture2D</c>.</item>
/// </list>
/// </remarks>
internal static class Direct3DInterop
{
    private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    /// <summary>Wraps a Vortice D3D11 device as the WinRT device WGC requires.</summary>
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();

        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var abi);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            // FromAbi takes its own reference; release the one the factory handed us.
            if (abi != IntPtr.Zero) Marshal.Release(abi);
        }
    }

    /// <summary>Creates a capture item covering an entire monitor.</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hMonitor)
    {
        var interop = GetCaptureItemInterop();
        var iid = IID_GraphicsCaptureItem;
        var abi = interop.CreateForMonitor(hMonitor, ref iid);
        if (abi == IntPtr.Zero)
            throw new InvalidOperationException("CreateForMonitor returned null; the monitor may have been disconnected.");

        try
        {
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// <summary>Creates a capture item for a single window. Unused in v1; kept for window-capture support.</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = GetCaptureItemInterop();
        var iid = IID_GraphicsCaptureItem;
        var abi = interop.CreateForWindow(hwnd, ref iid);
        if (abi == IntPtr.Zero)
            throw new InvalidOperationException("CreateForWindow returned null.");

        try
        {
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// <summary>
    /// Unwraps a captured frame's surface into the underlying D3D11 texture.
    /// </summary>
    /// <remarks>
    /// The returned texture is owned by the caller and must be disposed; it holds a reference on the
    /// frame's surface, so failing to dispose it starves the capture frame pool.
    /// </remarks>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = IID_ID3D11Texture2D;
        var ptr = access.GetInterface(ref iid);
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException("The capture surface did not expose an ID3D11Texture2D.");

        // The Vortice wrapper adopts the reference GetInterface already added.
        return new ID3D11Texture2D(ptr);
    }

    private static IGraphicsCaptureItemInterop GetCaptureItemInterop()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";

        var hr = WindowsCreateString(className, className.Length, out var hstring);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        try
        {
            var iid = IID_IGraphicsCaptureItemInterop;
            hr = RoGetActivationFactory(hstring, ref iid, out var factory);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            try
            {
                return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }
}
