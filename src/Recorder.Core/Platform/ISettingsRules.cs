namespace Recorder.Platform;

/// <summary>
/// The settings validation rules that depend on platform UI conventions.
/// </summary>
/// <remarks>
/// Colours and hotkeys are stored as text, but what text is valid is defined by the UI toolkit that
/// will eventually parse it, so each app supplies its own rule rather than Core guessing.
/// </remarks>
public interface ISettingsRules
{
    /// <summary>Whether the UI can parse <paramref name="value"/> as a colour.</summary>
    bool IsValidColor(string? value);

    /// <summary>Parses a hotkey and returns its canonical spelling.</summary>
    /// <returns>False when the text is not a hotkey this platform can register.</returns>
    bool TryNormalizeHotkey(string? value, out string normalized);
}
