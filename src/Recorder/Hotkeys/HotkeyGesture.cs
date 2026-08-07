using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Windows.Input;
using Recorder.Utils;

namespace Recorder.Hotkeys;

/// <summary>
/// A parsed global hotkey such as <c>Ctrl+Shift+R</c>.
/// </summary>
/// <remarks>
/// Round-trips between the settings-file string form and the modifier/virtual-key pair that
/// <c>RegisterHotKey</c> needs. Parsing is deliberately lenient about separators and casing so a
/// hand-edited settings file behaves the way the user expects.
/// </remarks>
public sealed class HotkeyGesture : IEquatable<HotkeyGesture>
{
    public ModifierKeys Modifiers { get; }

    /// <summary>The non-modifier key.</summary>
    public Key Key { get; }

    public HotkeyGesture(ModifierKeys modifiers, Key key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    /// <summary>Win32 modifier flags for <c>RegisterHotKey</c>, including MOD_NOREPEAT.</summary>
    public uint Win32Modifiers
    {
        get
        {
            uint m = NativeMethods.MOD_NOREPEAT;
            if (Modifiers.HasFlag(ModifierKeys.Alt)) m |= NativeMethods.MOD_ALT;
            if (Modifiers.HasFlag(ModifierKeys.Control)) m |= NativeMethods.MOD_CONTROL;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) m |= NativeMethods.MOD_SHIFT;
            if (Modifiers.HasFlag(ModifierKeys.Windows)) m |= NativeMethods.MOD_WIN;
            return m;
        }
    }

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>A hotkey with no modifier would swallow a plain keystroke system-wide; reject it.</summary>
    public bool IsValid => Modifiers != ModifierKeys.None && Key != Key.None && !IsModifierKey(Key);

    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = ModifierKeys.None;
        var key = Key.None;

        foreach (var rawPart in text.Split(['+', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            if (part.Length == 0) continue;

            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; continue;
                case "shift": modifiers |= ModifierKeys.Shift; continue;
                case "alt": modifiers |= ModifierKeys.Alt; continue;
                case "win" or "windows" or "meta": modifiers |= ModifierKeys.Windows; continue;
            }

            // A bare digit is D0-D9 in WPF's Key enum.
            var candidate = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
            if (!Enum.TryParse<Key>(candidate, ignoreCase: true, out var parsed)) return false;
            if (key != Key.None) return false;   // more than one non-modifier key
            key = parsed;
        }

        var result = new HotkeyGesture(modifiers, key);
        if (!result.IsValid) return false;

        gesture = result;
        return true;
    }

    public static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin
        or Key.System;

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(FormatKey(Key));
        return sb.ToString();
    }

    private static string FormatKey(Key key)
    {
        var name = key.ToString();
        // D0-D9 read better as bare digits.
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return name[1].ToString();
        return name;
    }

    public bool Equals(HotkeyGesture? other) =>
        other is not null && other.Modifiers == Modifiers && other.Key == Key;

    public override bool Equals(object? obj) => Equals(obj as HotkeyGesture);

    public override int GetHashCode() => HashCode.Combine((int)Modifiers, (int)Key);
}
