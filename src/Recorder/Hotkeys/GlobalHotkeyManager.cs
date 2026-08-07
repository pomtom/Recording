using System.Windows.Interop;
using Recorder.Utils;

namespace Recorder.Hotkeys;

public enum HotkeyAction
{
    Start,
    PauseResume,
    Stop,
}

/// <summary>
/// Registers the system-wide hotkeys and turns WM_HOTKEY messages into actions.
/// </summary>
/// <remarks>
/// <para>The hotkeys hang off a dedicated hidden window rather than the main window, so they keep
/// working while the app sits in the tray and do not die when that window is closed.</para>
///
/// <para><b>It must not be a message-only window.</b> An <c>HWND_MESSAGE</c> window is the obvious
/// choice for something invisible that only needs to receive messages, but the window manager never
/// delivers <c>WM_HOTKEY</c> to one. <c>RegisterHotKey</c> still returns success, so the failure is
/// completely silent — the hotkeys simply never fire. A plain top-level window that is never shown
/// (WS_POPUP, and WS_EX_TOOLWINDOW to keep it out of alt-tab) behaves correctly.</para>
///
/// <para>Registration can legitimately fail when another application already owns a combination.
/// That is reported through <see cref="RegistrationFailed"/> rather than thrown, because one
/// unavailable hotkey should not stop the other two from working.</para>
/// </remarks>
public sealed class GlobalHotkeyManager : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;

    private readonly Dictionary<int, HotkeyAction> _registered = [];
    private HwndSource? _source;
    private int _nextId = 1;
    private bool _disposed;

    /// <summary>Raised on the UI thread when a registered hotkey is pressed.</summary>
    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Raised for each hotkey that could not be registered.</summary>
    public event EventHandler<string>? RegistrationFailed;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is not null) return;

        // Top-level but never shown: WS_VISIBLE is deliberately absent, so the window exists only
        // to receive WM_HOTKEY. See the class remarks for why this cannot be HWND_MESSAGE.
        var parameters = new HwndSourceParameters("PomtomRecorder.Hotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExToolWindow,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Replaces the current registrations with the given set.
    /// </summary>
    /// <remarks>Called at startup and again whenever the user edits the hotkeys in Settings.</remarks>
    public void Apply(string startHotkey, string pauseHotkey, string stopHotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Initialize();

        UnregisterAll();

        Register(HotkeyAction.Start, startHotkey, "Start recording");
        Register(HotkeyAction.PauseResume, pauseHotkey, "Pause/resume");
        Register(HotkeyAction.Stop, stopHotkey, "Stop recording");

        // One line per launch, recorded on purpose: "my hotkey does nothing" is the most common
        // report there is, and knowing whether registration happened at all settles it instantly.
        Log.Warn($"Hotkeys active ({_registered.Count}/3) on hwnd 0x{_source?.Handle.ToInt64():X}: " +
                 $"start={startHotkey}, pause={pauseHotkey}, stop={stopHotkey}.");
    }

    private void Register(HotkeyAction action, string text, string label)
    {
        if (!HotkeyGesture.TryParse(text, out var gesture))
        {
            Log.Warn($"Hotkey '{text}' for {label} could not be parsed.");
            RaiseFailure($"{label} hotkey '{text}' is not valid.");
            return;
        }

        var handle = _source?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        var id = _nextId++;
        if (NativeMethods.RegisterHotKey(handle, id, gesture.Win32Modifiers, gesture.VirtualKey))
        {
            _registered[id] = action;
        }
        else
        {
            // Almost always means another application already owns the combination.
            Log.Warn($"RegisterHotKey failed for {label} ({gesture}).");
            RaiseFailure($"{label} hotkey {gesture} is already in use by another application.");
        }
    }

    private void UnregisterAll()
    {
        var handle = _source?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            foreach (var id in _registered.Keys)
            {
                try { NativeMethods.UnregisterHotKey(handle, id); }
                catch (Exception ex) { Log.Warn(ex, $"UnregisterHotKey failed for id {id}."); }
            }
        }
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;

        var id = wParam.ToInt32();
        if (_registered.TryGetValue(id, out var action))
        {
            handled = true;
            try { Pressed?.Invoke(this, action); }
            catch (Exception ex) { Log.Error(ex, $"The {action} hotkey handler threw."); }
        }
        else
        {
            Log.Warn($"WM_HOTKEY for unknown id {id}; ignoring.");
        }

        return IntPtr.Zero;
    }

    private void RaiseFailure(string message)
    {
        try { RegistrationFailed?.Invoke(this, message); }
        catch (Exception ex) { Log.Warn(ex, "A RegistrationFailed handler threw."); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();

        if (_source is not null)
        {
            try { _source.RemoveHook(WndProc); } catch { }
            try { _source.Dispose(); } catch { }
            _source = null;
        }
    }
}
