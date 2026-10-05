using Recorder.Utils;

namespace Recorder.Capture;

/// <summary>A monitor that can be recorded.</summary>
/// <param name="Handle">
/// Opaque native handle, meaningful only to the platform that produced it (an HMONITOR on Windows,
/// a CGDirectDisplayID on macOS). Valid only for the current display arrangement.
/// </param>
/// <param name="DeviceId">Stable-ish device identifier, persisted in settings.</param>
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

/// <summary>Picks the monitor to record from whatever is attached.</summary>
public static class MonitorSelection
{
    /// <summary>Finds the configured monitor, falling back to primary when it is gone.</summary>
    public static MonitorInfo? Select(IReadOnlyList<MonitorInfo> all, string? deviceId)
    {
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
}
