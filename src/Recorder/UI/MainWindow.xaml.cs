using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
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
        HotkeyText.Text = $"Start {settings.StartHotkey}   ·   Pause {settings.PauseHotkey}   ·   Stop {settings.StopHotkey}";
        OutputText.Text = "Saving to " + settings.OutputFolder;
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

        // Per the spec, the close button minimises to the tray rather than exiting.
        e.Cancel = true;
        Hide();
    }
}
