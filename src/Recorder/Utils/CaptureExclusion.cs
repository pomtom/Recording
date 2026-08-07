using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Recorder.Utils;

/// <summary>
/// Marks our own windows so they never show up inside a recording.
/// </summary>
/// <remarks>
/// Windows Graphics Capture records a whole monitor, so there is no way to ask it to leave a
/// particular window out. The exclusion has to come from the other side:
/// <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c> tells the compositor to omit the window
/// from every capture path (WGC and Desktop Duplication alike) while leaving it fully visible on
/// screen. Requires Windows 10 2004+; on older builds the call fails and the window simply appears
/// in the recording, which is a cosmetic degradation rather than a functional one.
/// </remarks>
public static class CaptureExclusion
{
    /// <summary>Excludes a WPF window from screen capture. Safe to call before or after the handle exists.</summary>
    public static void Exclude(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var helper = new WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero)
        {
            Exclude(helper.Handle);
            return;
        }

        // No HWND yet — apply as soon as one exists.
        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            Exclude(new WindowInteropHelper(window).Handle);
        }
        window.SourceInitialized += OnSourceInitialized;
    }

    public static void Exclude(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (!NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
            {
                Log.Warn($"SetWindowDisplayAffinity failed (error {Marshal.GetLastWin32Error()}); " +
                         "this window may appear in recordings.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "SetWindowDisplayAffinity threw.");
        }
    }

    /// <summary>Makes a window click-through and keeps it out of alt-tab.</summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        }
        catch (Exception e)
        {
            Log.Warn(e, "Failed to set click-through styles.");
        }
    }

    /// <summary>Keeps a window out of alt-tab without making it click-through.</summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        }
        catch (Exception e)
        {
            Log.Warn(e, "Failed to set tool-window style.");
        }
    }
}
