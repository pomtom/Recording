using NAudio.CoreAudioApi;
using Recorder.Utils;

namespace Recorder.Capture;

/// <summary>An audio endpoint that can be recorded.</summary>
/// <param name="DeviceId">WASAPI endpoint id, persisted in settings.</param>
/// <param name="FriendlyName">Name as shown in the Windows sound control panel.</param>
/// <param name="IsDefault">True if this is the endpoint Windows would pick on its own.</param>
public sealed record AudioDeviceInfo(string DeviceId, string FriendlyName, bool IsDefault)
{
    public string DisplayLabel => IsDefault ? $"{FriendlyName} (default)" : FriendlyName;
}

/// <summary>
/// Lists the audio endpoints available to record, and resolves the configured one.
/// </summary>
/// <remarks>
/// Deliberately the same shape as <see cref="MonitorEnumerator"/>, including the most important part
/// of its behaviour: a configured device that has since been unplugged falls back to the system
/// default with a warning rather than failing. Losing the exact microphone someone picked is a much
/// smaller problem than losing the recording.
/// </remarks>
public static class AudioDeviceEnumerator
{
    public static IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                using var fallback = enumerator.GetDefaultAudioEndpoint(flow, RoleFor(flow));
                defaultId = fallback?.ID;
            }
            catch (Exception ex)
            {
                // No default endpoint at all (no sound card, everything disabled) is survivable:
                // the list is still worth showing.
                Log.Warn(ex, $"No default {flow} endpoint to mark.");
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    devices.Add(new AudioDeviceInfo(
                        device.ID,
                        device.FriendlyName,
                        string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception ex)
                {
                    // A device can disappear between enumeration and being read.
                    Log.Warn(ex, "Failed to describe an audio endpoint; skipping it.");
                }
                finally
                {
                    try { device.Dispose(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Audio endpoint enumeration failed for {flow}.");
        }

        // Default first, then alphabetical, so the dropdown opens on the sensible choice.
        return devices
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Opens the configured endpoint, falling back to the system default when it is gone.
    /// </summary>
    /// <remarks>The caller owns the returned device and must dispose it.</remarks>
    public static MMDevice GetDevice(string? deviceId, DataFlow flow)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    var device = enumerator.GetDevice(deviceId);
                    if (device is not null && device.State == DeviceState.Active) return device;

                    try { device?.Dispose(); } catch { }
                    Log.Warn($"Configured {flow} device '{deviceId}' is not active; using the default.");
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, $"Configured {flow} device '{deviceId}' could not be opened; using the default.");
                }
            }

            return enumerator.GetDefaultAudioEndpoint(flow, RoleFor(flow))
                ?? throw new InvalidOperationException($"No default {flow} device.");
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    /// <summary>
    /// The role Windows should use when picking a default.
    /// </summary>
    /// <remarks>
    /// Loopback follows <see cref="Role.Multimedia"/> because that is the endpoint music and video
    /// actually play out of, while capture follows <see cref="Role.Communications"/> because that is
    /// the one headset microphones register themselves as. This mirrors what the recorder did before
    /// devices were selectable.
    /// </remarks>
    private static Role RoleFor(DataFlow flow) =>
        flow == DataFlow.Render ? Role.Multimedia : Role.Communications;
}
