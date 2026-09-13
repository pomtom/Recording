using System.Runtime.InteropServices;

namespace Recorder.Utils;

/// <summary>
/// P/Invoke surface used across the app. Kept in one place so signatures stay consistent.
/// </summary>
internal static class NativeMethods
{
    // ---- Window display affinity (keeps our own windows out of the recording) ----

    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;   // Windows 10 2004+

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    // ---- Extended window styles ----

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;   // click-through
    public const int WS_EX_TOOLWINDOW = 0x00000080;    // no alt-tab entry
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ---- Global hotkeys ----

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // ---- Monitor enumeration ----

    public const int MONITORINFOF_PRIMARY = 0x00000001;
    public const int CCHDEVICENAME = 32;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    public const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>
    /// A window's bounds in physical pixels, in the same virtual-screen space as
    /// <see cref="MONITORINFOEX.rcMonitor"/>.
    /// </summary>
    /// <remarks>
    /// The process is PerMonitorV2, so this is the honest answer and needs no DPI arithmetic — which
    /// is the point. WPF's <c>Window.Left</c>/<c>Top</c> are device-independent units in a global
    /// space whose relationship to physical pixels is per-monitor affine, so converting them with a
    /// single scale factor is only correct when every monitor shares one DPI. Asking Win32 for the
    /// rectangle skips the whole problem.
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Moves and resizes a window in physical pixels.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="GetWindowRect"/>, and used for the same reason: a window whose
    /// position is what gets baked into a video has to be placed in the same coordinate space it is
    /// later measured in. Setting WPF's <c>Left</c>/<c>Width</c> instead would put a DPI conversion
    /// on one side of that round trip and not the other.
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>The work area (screen minus taskbar) of the monitor a window is on, in physical pixels.</summary>
    public static bool TryGetWorkArea(IntPtr hwnd, out RECT workArea)
    {
        workArea = default;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        workArea = info.rcWork;
        return true;
    }

    // ---- Per-monitor DPI ----

    public enum MonitorDpiType
    {
        Effective = 0,
        Angular = 1,
        Raw = 2,
    }

    [DllImport("shcore.dll", SetLastError = true)]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// DPI scale factor of a monitor (1.0 at 96 DPI).
    /// </summary>
    /// <remarks>
    /// The process is PerMonitorV2, so monitor geometry is in physical pixels while WPF works in
    /// device-independent units. Converting between them needs the scale of the monitor in
    /// question, not of whichever monitor happens to hold the main window.
    /// </remarks>
    public static double GetMonitorScale(IntPtr hMonitor)
    {
        try
        {
            if (hMonitor != IntPtr.Zero &&
                GetDpiForMonitor(hMonitor, MonitorDpiType.Effective, out var dpiX, out _) == 0 &&
                dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "GetDpiForMonitor failed; assuming 100% scaling.");
        }
        return 1.0;
    }

    // ---- Icons ----

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---- Timer resolution ----
    // The frame pacer needs ~1 ms sleep granularity; the default is 15.6 ms.

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint ms);
}
