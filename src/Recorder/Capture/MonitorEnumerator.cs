using Recorder.Utils;
using static Recorder.Utils.NativeMethods;

namespace Recorder.Capture;

// MonitorInfo itself lives in Recorder.Core (Capture/MonitorInfo.cs). On Windows its Handle is an
// HMONITOR and its DeviceId a name such as \\.\DISPLAY1.

/// <summary>
/// Lists the monitors available to record.
/// </summary>
/// <remarks>
/// The bounds come back in physical pixels because the process is marked PerMonitorV2 in
/// app.manifest. That matters: the capture surface is sized in physical pixels, so a DPI-virtualised
/// rectangle would silently produce a scaled, blurry recording.
/// </remarks>
public static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var monitors = new List<MonitorInfo>();

        try
        {
            // The callback must not be collected while EnumDisplayMonitors is running; holding it
            // in a local for the duration of the synchronous call is enough.
            MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
            {
                try
                {
                    var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
                    if (GetMonitorInfo(hMonitor, ref info))
                    {
                        monitors.Add(new MonitorInfo(
                            Handle: hMonitor,
                            DeviceId: info.szDevice,
                            FriendlyName: DescribeDevice(info.szDevice),
                            Left: info.rcMonitor.Left,
                            Top: info.rcMonitor.Top,
                            Width: info.rcMonitor.Width,
                            Height: info.rcMonitor.Height,
                            IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Failed to describe a monitor; skipping it.");
                }
                return true;
            };

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Monitor enumeration failed.");
        }

        // Primary first, then left-to-right, so the dropdown reads the way the desktop looks.
        return monitors
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Left)
            .ToList();
    }

    /// <summary>Finds the configured monitor, falling back to primary when it is gone.</summary>
    public static MonitorInfo? Select(string? deviceId) => MonitorSelection.Select(Enumerate(), deviceId);

    private static string DescribeDevice(string deviceName)
    {
        try
        {
            var device = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(deviceName, 0, ref device, 0) && !string.IsNullOrWhiteSpace(device.DeviceString))
                return device.DeviceString.Trim();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "EnumDisplayDevices failed.");
        }

        // \\.\DISPLAY1 -> "Display 1"
        var trimmed = deviceName.TrimStart('\\', '.').Replace("DISPLAY", "Display ", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(trimmed) ? deviceName : trimmed;
    }
}
