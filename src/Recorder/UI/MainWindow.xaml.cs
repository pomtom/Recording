using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Recorder.Capture;
using Recorder.Core;
using Recorder.Settings;
using Recorder.Utils;

namespace Recorder.UI;

/// <summary>
/// The main control window: Start, Pause, Stop, Settings, Exit.
/// </summary>
/// <remarks>
/// Closing this window hides it to the tray instead of exiting, per the spec. Only an explicit
/// Exit — from here or the tray menu — actually shuts the app down, and that path finalizes any
/// recording still in progress first.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly RecordingManager _manager;
    private readonly SettingsManager _settings;
    private readonly Func<Task> _exitRequested;
    private readonly Action _settingsRequested;
    private readonly DispatcherTimer _elapsedTimer;

    /// <summary>Set by the app when it is really shutting down, so Close is allowed through.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(
        RecordingManager manager,
        SettingsManager settings,
        Action settingsRequested,
        Func<Task> exitRequested)
    {
        _manager = manager;
        _settings = settings;
        _settingsRequested = settingsRequested;
        _exitRequested = exitRequested;

        InitializeComponent();

        StartButton.Click += async (_, _) => await _manager.StartAsync();
        PauseButton.Click += async (_, _) => await _manager.TogglePauseAsync();
        StopButton.Click += async (_, _) => await _manager.StopAsync();
        SettingsButton.Click += (_, _) => _settingsRequested();
        FolderButton.Click += (_, _) => OpenOutputFolder();
        ExitButton.Click += async (_, _) => await _exitRequested();

        // The toggles report intent; the manager owns the state and reports it back through
        // ApplyMuteState. The button's own IsChecked is never the source of truth.
        MuteMicButton.Click += (_, _) => _manager.SetMuted(AudioSourceKind.Microphone, MuteMicButton.IsChecked == true);
        MuteSystemButton.Click += (_, _) => _manager.SetMuted(AudioSourceKind.SystemAudio, MuteSystemButton.IsChecked == true);

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();

        Loaded += (_, _) => ApplyState(_manager.State);
        Closing += OnClosing;

        // The main window is excluded from capture too: the user often starts a recording from
        // here, and it should not be the first thing in the frame.
        CaptureExclusion.Exclude(this);

        RefreshSettingsText();
    }

    /// <summary>Called by the app when the recorder changes state.</summary>
    public void ApplyState(RecorderState state)
    {
        StartButton.IsEnabled = state.CanStart();
        PauseButton.IsEnabled = state.CanPause();
        StopButton.IsEnabled = state.CanStop();
        SettingsButton.IsEnabled = state == RecorderState.Idle;

        PauseButton.Content = state == RecorderState.Paused ? "Resume" : "Pause";

        ApplyMuteState();

        switch (state)
        {
            case RecorderState.CountingDown:
                SetStatus("Starting…", "#F2B13C");
                DetailText.Text = "Getting ready.";
                break;

            case RecorderState.Recording:
                SetStatus("Recording", "#E83E3E");
                DetailText.Text = DescribeActiveRecording();
                break;

            case RecorderState.Paused:
                SetStatus("Paused", "#F2B13C");
                DetailText.Text = DescribeActiveRecording();
                break;

            case RecorderState.Finalizing:
                SetStatus("Saving…", "#6CB6FF");
                DetailText.Text = "Finishing the MP4.";
                break;

            default:
                SetStatus("Ready", "#5C6066");
                DetailText.Text = string.Empty;
                ElapsedText.Text = string.Empty;
                break;
        }

        if (state.IsActive()) _elapsedTimer.Start();
        else _elapsedTimer.Stop();

        UpdateElapsed();
    }

    private string DescribeActiveRecording()
    {
        var dimensions = _manager.Dimensions;
        var encoder = _manager.EncoderDescription;

        if (dimensions is null) return string.Empty;
        var size = $"{dimensions.Value.Width}×{dimensions.Value.Height} @ {_settings.Current.FPS} FPS";
        return string.IsNullOrEmpty(encoder) ? size : $"{size} · {encoder}";
    }

    private void SetStatus(string text, string colorHex)
    {
        StatusText.Text = text;
        try
        {
            StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not apply the status colour.");
        }
    }

    private void UpdateElapsed()
    {
        if (!_manager.State.IsActive())
        {
            ElapsedText.Text = string.Empty;
            return;
        }

        var elapsed = _manager.Elapsed;
        ElapsedText.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    /// <summary>Refreshes the hotkey and output-folder lines after a settings change.</summary>
    public void RefreshSettingsText()
    {
        var settings = _settings.Current;

        var hotkeys = new List<string>
        {
            $"Start {settings.StartHotkey}",
            $"Pause {settings.PauseHotkey}",
            $"Stop {settings.StopHotkey}",
        };

        // Only mention the mute keys when they are actually assigned; listing an empty one would
        // read as a hotkey that exists but does nothing.
        if (!string.IsNullOrWhiteSpace(settings.MuteMicHotkey))
            hotkeys.Add($"Mute mic {settings.MuteMicHotkey}");
        if (!string.IsNullOrWhiteSpace(settings.MuteSystemHotkey))
            hotkeys.Add($"Mute system {settings.MuteSystemHotkey}");

        HotkeyText.Text = string.Join("   ·   ", hotkeys);
        OutputText.Text = "Saving to " + settings.OutputFolder;
    }

    /// <summary>
    /// Redraws both mute toggles from the manager's state.
    /// </summary>
    /// <remarks>
    /// Called on every state change and whenever mute changes anywhere — window, tray or hotkey.
    /// Reading the state rather than tracking it here is what keeps the three surfaces from
    /// disagreeing.
    /// </remarks>
    public void ApplyMuteState()
    {
        var settings = _settings.Current;
        var live = _manager.State is RecorderState.Recording or RecorderState.Paused;

        ApplyMuteToggle(
            MuteMicButton, MicGlyph, MicStatusText,
            enabled: live && settings.RecordMicrophone,
            muted: _manager.IsMicrophoneMuted,
            configured: settings.RecordMicrophone,
            hotkey: settings.MuteMicHotkey,
            liveGlyph: "🎤",
            mutedGlyph: "🔇",
            disabledReason: settings.RecordMicrophone ? "Only while recording" : "Off in Settings");

        ApplyMuteToggle(
            MuteSystemButton, SystemGlyph, SystemStatusText,
            enabled: live && settings.RecordSystemAudio,
            muted: _manager.IsSystemAudioMuted,
            configured: settings.RecordSystemAudio,
            hotkey: settings.MuteSystemHotkey,
            liveGlyph: "🔊",
            mutedGlyph: "🔇",
            disabledReason: settings.RecordSystemAudio ? "Only while recording" : "Off in Settings");
    }

    private static void ApplyMuteToggle(
        System.Windows.Controls.Primitives.ToggleButton button,
        TextBlock glyph,
        TextBlock status,
        bool enabled,
        bool muted,
        bool configured,
        string hotkey,
        string liveGlyph,
        string mutedGlyph,
        string disabledReason)
    {
        button.IsEnabled = enabled;
        button.IsChecked = muted;

        glyph.Text = muted ? mutedGlyph : liveGlyph;

        if (!enabled)
        {
            status.Text = disabledReason;
            button.ToolTip = configured
                ? "Available once a recording is running."
                : "This source is switched off in Settings.";
            return;
        }

        status.Text = string.IsNullOrWhiteSpace(hotkey)
            ? (muted ? "Muted" : "Live")
            : $"{(muted ? "Muted" : "Live")}  ·  {hotkey}";

        button.ToolTip = muted ? "Click to unmute" : "Click to mute";
    }

    private void OpenOutputFolder()
    {
        try
        {
            var folder = OutputFolder.Resolve(_settings.Current.OutputFolder).Path;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open the output folder.");
            ShowMessage("The recordings folder could not be opened.");
        }
    }

    /// <summary>Shows a transient message in the detail line rather than a modal dialog.</summary>
    public void ShowMessage(string message)
    {
        DetailText.Text = message;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;

        // The default is to minimise to the tray, per the spec, but a user who would rather the
        // close button really closed can say so in Settings.
        if (_settings.Current.Behavior.CloseButtonActionValue == CloseAction.Exit)
        {
            e.Cancel = true;
            _ = _exitRequested();
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
