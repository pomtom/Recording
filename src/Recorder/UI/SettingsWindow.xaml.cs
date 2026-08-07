using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Recorder.Capture;
using Recorder.Hotkeys;
using Recorder.Settings;
using Recorder.Utils;

namespace Recorder.UI;

/// <summary>
/// Editor for everything in settings.json.
/// </summary>
/// <remarks>
/// Nothing is written until Save, and Save validates first — an unusable output folder or a
/// duplicated hotkey is reported inline rather than persisted and discovered later at record time.
/// </remarks>
public partial class SettingsWindow : Window
{
    private sealed record MonitorChoice(string? DeviceId, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly SettingsManager _settings;
    private HotkeyGesture? _startHotkey;
    private HotkeyGesture? _pauseHotkey;
    private HotkeyGesture? _stopHotkey;

    public SettingsWindow(SettingsManager settings)
    {
        _settings = settings;
        InitializeComponent();

        CaptureExclusion.Exclude(this);

        PopulateChoices();
        LoadFrom(_settings.Current);

        BrowseButton.Click += (_, _) => BrowseForFolder();
        CancelButton.Click += (_, _) => { DialogResult = false; Close(); };
        SaveButton.Click += (_, _) => Save();

        WireHotkeyBox(StartHotkeyBox, g => _startHotkey = g);
        WireHotkeyBox(PauseHotkeyBox, g => _pauseHotkey = g);
        WireHotkeyBox(StopHotkeyBox, g => _stopHotkey = g);
    }

    private void PopulateChoices()
    {
        MonitorCombo.Items.Add(new MonitorChoice(null, "Primary display (follow Windows)"));
        foreach (var monitor in MonitorEnumerator.Enumerate())
            MonitorCombo.Items.Add(new MonitorChoice(monitor.DeviceId, monitor.DisplayLabel));

        ResolutionCombo.Items.Add(new Choice<ResolutionPreset>(ResolutionPreset.P720, "720p"));
        ResolutionCombo.Items.Add(new Choice<ResolutionPreset>(ResolutionPreset.P1080, "1080p"));
        ResolutionCombo.Items.Add(new Choice<ResolutionPreset>(ResolutionPreset.P1440, "1440p"));
        ResolutionCombo.Items.Add(new Choice<ResolutionPreset>(ResolutionPreset.Native, "Native"));

        FpsCombo.Items.Add(new Choice<int>(30, "30 FPS"));
        FpsCombo.Items.Add(new Choice<int>(60, "60 FPS"));

        CountdownCombo.Items.Add(new Choice<int>(0, "Off"));
        foreach (var seconds in new[] { 3, 5, 10 })
            CountdownCombo.Items.Add(new Choice<int>(seconds, $"{seconds} s"));
    }

    private void LoadFrom(AppSettings settings)
    {
        SelectMonitor(settings.MonitorDeviceId);
        SelectChoice(ResolutionCombo, settings.ResolutionPreset);
        SelectChoice(FpsCombo, settings.FPS);
        SelectChoice(CountdownCombo, settings.Countdown);

        CursorCheck.IsChecked = settings.CaptureCursor;
        SystemAudioCheck.IsChecked = settings.RecordSystemAudio;
        MicrophoneCheck.IsChecked = settings.RecordMicrophone;

        FolderBox.Text = settings.OutputFolder;

        HotkeyGesture.TryParse(settings.StartHotkey, out _startHotkey);
        HotkeyGesture.TryParse(settings.PauseHotkey, out _pauseHotkey);
        HotkeyGesture.TryParse(settings.StopHotkey, out _stopHotkey);

        StartHotkeyBox.Text = _startHotkey?.ToString() ?? settings.StartHotkey;
        PauseHotkeyBox.Text = _pauseHotkey?.ToString() ?? settings.PauseHotkey;
        StopHotkeyBox.Text = _stopHotkey?.ToString() ?? settings.StopHotkey;

        FolderHint.Text = $"Files are named {DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
    }

    private void SelectMonitor(string? deviceId)
    {
        foreach (var item in MonitorCombo.Items)
        {
            if (item is MonitorChoice choice &&
                string.Equals(choice.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                MonitorCombo.SelectedItem = item;
                return;
            }
        }
        MonitorCombo.SelectedIndex = 0;
    }

    private static void SelectChoice<T>(ComboBox combo, T value)
    {
        foreach (var item in combo.Items)
        {
            if (item is Choice<T> choice && EqualityComparer<T>.Default.Equals(choice.Value, value))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static T GetChoice<T>(ComboBox combo, T fallback) =>
        combo.SelectedItem is Choice<T> choice ? choice.Value : fallback;

    /// <summary>
    /// Turns a read-only text box into a hotkey capture field.
    /// </summary>
    /// <remarks>
    /// PreviewKeyDown is used rather than KeyDown so the combination is intercepted before WPF
    /// routes it anywhere (Tab and the arrow keys would otherwise move focus instead of being
    /// captured). Modifier-only presses are ignored until a real key joins them.
    /// </remarks>
    private void WireHotkeyBox(TextBox box, Action<HotkeyGesture?> assign)
    {
        box.GotFocus += (_, _) =>
        {
            box.Tag = box.Text;
            box.Text = "Press a combination…";
            box.SelectAll();
        };

        box.LostFocus += (_, _) =>
        {
            if (box.Text == "Press a combination…") box.Text = box.Tag as string ?? string.Empty;
        };

        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                box.Text = box.Tag as string ?? string.Empty;
                Keyboard.ClearFocus();
                return;
            }

            if (HotkeyGesture.IsModifierKey(key)) return;   // still waiting for the real key

            var gesture = new HotkeyGesture(Keyboard.Modifiers, key);
            if (!gesture.IsValid)
            {
                ShowError("A hotkey needs at least one modifier (Ctrl, Alt, Shift or Win).");
                return;
            }

            ClearError();
            assign(gesture);
            box.Text = gesture.ToString();
            box.Tag = box.Text;
            Keyboard.ClearFocus();
        };
    }

    private void BrowseForFolder()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose where recordings are saved",
                Multiselect = false,
            };

            var current = FolderBox.Text;
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                dialog.InitialDirectory = current;

            if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The folder picker failed.");
            ShowError("The folder picker could not be opened. Type the path instead.");
        }
    }

    private void Save()
    {
        var folder = FolderBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowError("Choose a folder for recordings.");
            return;
        }

        // Probe now so an unwritable folder is caught here rather than three seconds into a
        // countdown. A drive that is merely disconnected today still saves fine.
        if (!OutputFolder.TryPrepare(folder, out var reason))
        {
            ShowError($"That folder cannot be written to ({reason}). Recordings would be saved to " +
                      $"{AppPaths.FallbackOutputFolder} instead.");
            return;
        }

        var gestures = new[] { _startHotkey, _pauseHotkey, _stopHotkey };
        if (gestures.Any(g => g is null))
        {
            ShowError("Every hotkey needs a valid combination.");
            return;
        }

        if (gestures.Distinct().Count() != gestures.Length)
        {
            ShowError("Each action needs its own hotkey.");
            return;
        }

        var updated = _settings.Current.Clone();
        updated.MonitorDeviceId = (MonitorCombo.SelectedItem as MonitorChoice)?.DeviceId;
        updated.Resolution = AppSettings.FormatResolution(GetChoice(ResolutionCombo, ResolutionPreset.P1080));
        updated.FPS = GetChoice(FpsCombo, 60);
        updated.Countdown = GetChoice(CountdownCombo, 3);
        updated.CaptureCursor = CursorCheck.IsChecked == true;
        updated.RecordSystemAudio = SystemAudioCheck.IsChecked == true;
        updated.RecordMicrophone = MicrophoneCheck.IsChecked == true;
        updated.OutputFolder = folder;
        updated.StartHotkey = _startHotkey!.ToString();
        updated.PauseHotkey = _pauseHotkey!.ToString();
        updated.StopHotkey = _stopHotkey!.ToString();

        _settings.Save(updated);

        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
