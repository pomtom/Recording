using Recorder.Utils;
using static Recorder.Utils.NativeMethods;

namespace Recorder.Capture;

/// <summary>A monitor that can be recorded.</summary>
/// <param name="Handle">HMONITOR — valid only for the current display arrangement.</param>
/// <param name="DeviceId">Stable-ish device name such as <c>\\.\DISPLAY1</c>, persisted in settings.</param>
/// <param name="FriendlyName">Adapter/monitor description for the settings dropdown.</param>
/// <param name="Bounds">Position and size in <em>physical</em> pixels.</param>
public sealed record MonitorInfo(
    IntPtr Handle,
    string DeviceId,
    string FriendlyName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary)
{
    public string DisplayLabel =>
        $"{FriendlyName} — {Width}×{Height}{(IsPrimary ? " (primary)" : string.Empty)}";
}

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
    public static MonitorInfo? Select(string? deviceId)
    {
        var all = Enumerate();
        if (all.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var match = all.FirstOrDefault(m => string.Equals(m.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;

            // The saved monitor was unplugged or renamed — recording on primary beats not recording.
            Log.Warn($"Configured monitor '{deviceId}' not found; using the primary display.");
        }

        return all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
    }

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
