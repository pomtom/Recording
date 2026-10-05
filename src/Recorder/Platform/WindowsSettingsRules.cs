using Recorder.Hotkeys;

namespace Recorder.Platform;

/// <summary>Validates colours the way WPF parses them and hotkeys the way Win32 registers them.</summary>
public sealed class WindowsSettingsRules : ISettingsRules
{
    /// <summary>Whether a colour string is one WPF can parse, without throwing to find out.</summary>
    public bool IsValidColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            System.Windows.Media.ColorConverter.ConvertFromString(value);
            return true;
        }
        catch (FormatException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    public bool TryNormalizeHotkey(string? value, out string normalized)
    {
        if (HotkeyGesture.TryParse(value, out var gesture) && gesture is not null)
        {
            normalized = gesture.ToString();
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}
