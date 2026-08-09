using Microsoft.Win32;

namespace Recorder.Utils;

/// <summary>
/// Registers or removes the recorder in the per-user Run key.
/// </summary>
/// <remarks>
/// <para>HKCU rather than HKLM deliberately: the app is portable and must never need administrator
/// rights, and a per-user entry needs neither.</para>
///
/// <para>Portability does create one wrinkle worth knowing about. The Run value is an absolute path,
/// so moving or renaming the executable leaves a stale entry pointing at nothing. <see cref="Apply"/>
/// is therefore called on every launch, not only when the setting is toggled: if the recorder is
/// running from somewhere new, the registration is quietly corrected to match.</para>
/// </remarks>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PomtomRecorder";

    /// <summary>Brings the Run key into line with <paramref name="enabled"/>. Never throws.</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                Log.Warn("The Run registry key is unavailable; start-with-Windows was not applied.");
                return;
            }

            var existing = key.GetValue(ValueName) as string;

            if (!enabled)
            {
                if (existing is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                Log.Warn("The executable path is unknown; start-with-Windows was not applied.");
                return;
            }

            // Quoted so a path containing spaces is not parsed as a command plus arguments.
            var desired = $"\"{path}\"";
            if (!string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, desired, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            // Locked-down or policy-managed machines can refuse this. It is a convenience, not a
            // requirement, so a failure must never interfere with recording.
            Log.Warn(ex, "Could not update the start-with-Windows registration.");
        }
    }

    /// <summary>Whether the recorder is currently registered to start with Windows.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not read the start-with-Windows registration.");
            return false;
        }
    }
}
