using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Recorder.Capture;
using Recorder.Encoding;
using Recorder.Hotkeys;
using Recorder.Settings;
using Recorder.Utils;

namespace Recorder.UI;

/// <summary>
/// Editor for everything in settings.json.
/// </summary>
/// <remarks>
/// <para>Nothing is written until Save, and Save validates first — an unusable output folder, a
/// duplicated hotkey or a number outside its range is reported inline rather than persisted and
/// discovered later at record time. Editing works on a deep clone of the live settings, so Cancel
/// genuinely cancels.</para>
///
/// <para>The device meters open a real capture while this window is on screen. They are torn down
/// on close and never started while a recording is running.</para>
/// </remarks>
public partial class SettingsWindow : Window
{
    private sealed record MonitorChoice(string? DeviceId, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DeviceChoice(string? DeviceId, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private const string CapturePrompt = "Press a combination…";

    private readonly SettingsManager _settings;
    private readonly Func<bool> _isRecording;

    /// <summary>The working copy. Nothing here reaches the live settings until Save.</summary>
    private readonly AppSettings _draft;

    private readonly AudioLevelPreview _micPreview = new();
    private readonly AudioLevelPreview _systemPreview = new();
    private readonly DispatcherTimer _meterTimer;

    private HotkeyGesture? _startHotkey;
    private HotkeyGesture? _pauseHotkey;
    private HotkeyGesture? _stopHotkey;
    private HotkeyGesture? _muteMicHotkey;
    private HotkeyGesture? _muteSystemHotkey;

    /// <summary>Suppresses the change handlers while controls are being populated from settings.</summary>
    private bool _loading = true;

    public SettingsWindow(SettingsManager settings, Func<bool> isRecording)
    {
        _settings = settings;
        _isRecording = isRecording;
        _draft = settings.Current.Clone();

        InitializeComponent();

        CaptureExclusion.Exclude(this);

        PopulateChoices();
        LoadFrom(_draft);
        _loading = false;

        BrowseButton.Click += (_, _) => BrowseForFolder();
        CancelButton.Click += (_, _) => { DialogResult = false; Close(); };
        SaveButton.Click += (_, _) => Save();
        ResetButton.Click += (_, _) => ResetToDefaults();

        WireHotkeyBox(StartHotkeyBox, g => _startHotkey = g);
        WireHotkeyBox(PauseHotkeyBox, g => _pauseHotkey = g);
        WireHotkeyBox(StopHotkeyBox, g => _stopHotkey = g);
        WireHotkeyBox(MuteMicHotkeyBox, g => _muteMicHotkey = g);
        WireHotkeyBox(MuteSystemHotkeyBox, g => _muteSystemHotkey = g);

        ClearMuteMicButton.Click += (_, _) => ClearHotkey(MuteMicHotkeyBox, g => _muteMicHotkey = g);
        ClearMuteSystemButton.Click += (_, _) => ClearHotkey(MuteSystemHotkeyBox, g => _muteSystemHotkey = g);

        ResolutionCombo.SelectionChanged += (_, _) => UpdateCustomHeightVisibility();
        TemplateBox.TextChanged += (_, _) => UpdateTemplatePreview();

        MicGainSlider.ValueChanged += (_, _) => MicGainText.Text = FormatDb(MicGainSlider.Value);
        SystemGainSlider.ValueChanged += (_, _) => SystemGainText.Text = FormatDb(SystemGainSlider.Value);
        QualitySlider.ValueChanged += (_, _) => QualityText.Text = ((int)QualitySlider.Value).ToString(CultureInfo.CurrentCulture);
        OverlayOpacitySlider.ValueChanged += (_, _) => OverlayOpacityText.Text = $"{OverlayOpacitySlider.Value:0%}";
        OverlayScaleSlider.ValueChanged += (_, _) => OverlayScaleText.Text = $"{OverlayScaleSlider.Value:0.00}×";

        MicDeviceCombo.SelectionChanged += (_, _) => RestartMicPreview();
        SystemDeviceCombo.SelectionChanged += (_, _) => RestartSystemPreview();

        // Selecting a named preset overwrites the advanced values, so the JSON always shows the
        // numbers actually in effect. Editing one of those values then means "Custom".
        NsOffRadio.Checked += (_, _) => OnPresetChosen(NoiseSuppressionPreset.Off);
        NsLightRadio.Checked += (_, _) => OnPresetChosen(NoiseSuppressionPreset.Light);
        NsStandardRadio.Checked += (_, _) => OnPresetChosen(NoiseSuppressionPreset.Standard);
        NsStrongRadio.Checked += (_, _) => OnPresetChosen(NoiseSuppressionPreset.Strong);
        NsCustomRadio.Checked += (_, _) => NsAdvanced.IsExpanded = true;

        foreach (var box in new[] { NsHighPassBox, NsStrengthBox, NsFloorBox, NsGateThresholdBox, NsGateRatioBox, NsAttackBox, NsReleaseBox })
            box.TextChanged += (_, _) => MarkPresetCustom();

        _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _meterTimer.Tick += (_, _) => UpdateMeters();

        Loaded += (_, _) => StartPreviews();
        Closed += (_, _) => StopPreviews();
    }

    // ---------------------------------------------------------------- population

    private void PopulateChoices()
    {
        MonitorCombo.Items.Add(new MonitorChoice(null, "Primary display (follow Windows)"));
        foreach (var monitor in MonitorEnumerator.Enumerate())
            MonitorCombo.Items.Add(new MonitorChoice(monitor.DeviceId, monitor.DisplayLabel));

        ResolutionCombo.Items.Add(new Choice<ResolutionPreset>(ResolutionPreset.P720, "720p"));
        ResolutionCombo.Items.Add(new Choice<ResolutionPreset>(ResolutionPreset.P1080, "1080p"));
        ResolutionCombo.Items.Add(new Choice<ResolutionPreset>(ResolutionPreset.P1440, "1440p"));
        ResolutionCombo.Items.Add(new Choice<ResolutionPreset>(ResolutionPreset.Native, "Native"));
        ResolutionCombo.Items.Add(new Choice<ResolutionPreset>(ResolutionPreset.Custom, "Custom…"));

        foreach (var fps in new[] { 24, 25, 30, 48, 50, 60, 120, 144 })
            FpsCombo.Items.Add(new Choice<int>(fps, $"{fps} FPS"));

        PopulateDeviceCombo(MicDeviceCombo, DataFlow.Capture, "Default microphone (follow Windows)");
        PopulateDeviceCombo(SystemDeviceCombo, DataFlow.Render, "Default playback device (follow Windows)");

        SampleRateCombo.Items.Add(new Choice<int>(44_100, "44.1 kHz"));
        SampleRateCombo.Items.Add(new Choice<int>(48_000, "48 kHz"));

        ChannelsCombo.Items.Add(new Choice<int>(1, "Mono"));
        ChannelsCombo.Items.Add(new Choice<int>(2, "Stereo"));

        foreach (var bitrate in new[] { 96, 128, 160, 192, 256, 320 })
            AudioBitrateCombo.Items.Add(new Choice<int>(bitrate, bitrate.ToString(CultureInfo.CurrentCulture)));

        EncoderCombo.Items.Add(new Choice<string>("Auto", "Automatic (recommended)"));
        foreach (var encoder in Enum.GetValues<VideoEncoder>())
            EncoderCombo.Items.Add(new Choice<string>(encoder.ToString(), EncoderProbe.FriendlyName(encoder)));

        foreach (var level in Enum.GetValues<Settings.LogLevel>())
            LogLevelCombo.Items.Add(new Choice<Settings.LogLevel>(level, level.ToString()));
    }

    private static void PopulateDeviceCombo(ComboBox combo, DataFlow flow, string defaultLabel)
    {
        combo.Items.Add(new DeviceChoice(null, defaultLabel));
        foreach (var device in AudioDeviceEnumerator.Enumerate(flow))
            combo.Items.Add(new DeviceChoice(device.DeviceId, device.DisplayLabel));
    }

    private void LoadFrom(AppSettings s)
    {
        // Capture
        SelectMonitor(s.MonitorDeviceId);
        SelectChoice(ResolutionCombo, s.ResolutionPreset);
        SelectChoice(FpsCombo, s.FPS);
        CountdownBox.Text = s.Countdown.ToString(CultureInfo.CurrentCulture);
        CustomHeightBox.Text = s.CustomHeight.ToString(CultureInfo.CurrentCulture);
        CursorCheck.IsChecked = s.CaptureCursor;
        BorderCheck.IsChecked = s.SuppressCaptureBorder;
        SystemAudioCheck.IsChecked = s.RecordSystemAudio;
        MicrophoneCheck.IsChecked = s.RecordMicrophone;
        UpdateCustomHeightVisibility();

        // Audio
        SelectDevice(MicDeviceCombo, s.Audio.MicrophoneDeviceId);
        SelectDevice(SystemDeviceCombo, s.Audio.SystemAudioDeviceId);
        MicGainSlider.Value = s.Audio.MicrophoneGainDb;
        SystemGainSlider.Value = s.Audio.SystemAudioGainDb;
        MicGainText.Text = FormatDb(s.Audio.MicrophoneGainDb);
        SystemGainText.Text = FormatDb(s.Audio.SystemAudioGainDb);
        SelectChoice(SampleRateCombo, s.Audio.SampleRate);
        SelectChoice(ChannelsCombo, s.Audio.Channels);
        SelectChoice(AudioBitrateCombo, s.AudioBitrateKbps);

        LoadNoiseSuppression(s.NoiseSuppression);

        // Video
        var quality = s.Video.QualityModeValue == VideoQualityMode.Bitrate;
        QualityModeRadio.IsChecked = !quality;
        BitrateModeRadio.IsChecked = quality;
        QualitySlider.Value = s.Video.Quality;
        QualityText.Text = s.Video.Quality.ToString(CultureInfo.CurrentCulture);
        MaxBitrateBox.Text = s.Video.MaxBitrateKbps.ToString(CultureInfo.CurrentCulture);
        SelectChoice(EncoderCombo, s.Video.EncoderOverride);

        // Output
        FolderBox.Text = s.OutputFolder;
        TemplateBox.Text = s.Naming.FilenameTemplate;
        RecoveryCheck.IsChecked = s.Behavior.RecoverInterruptedRecordings;
        UpdateTemplatePreview();

        // Hotkeys
        HotkeyGesture.TryParse(s.StartHotkey, out _startHotkey);
        HotkeyGesture.TryParse(s.PauseHotkey, out _pauseHotkey);
        HotkeyGesture.TryParse(s.StopHotkey, out _stopHotkey);
        HotkeyGesture.TryParse(s.MuteMicHotkey, out _muteMicHotkey);
        HotkeyGesture.TryParse(s.MuteSystemHotkey, out _muteSystemHotkey);

        StartHotkeyBox.Text = _startHotkey?.ToString() ?? s.StartHotkey;
        PauseHotkeyBox.Text = _pauseHotkey?.ToString() ?? s.PauseHotkey;
        StopHotkeyBox.Text = _stopHotkey?.ToString() ?? s.StopHotkey;
        MuteMicHotkeyBox.Text = _muteMicHotkey?.ToString() ?? string.Empty;
        MuteSystemHotkeyBox.Text = _muteSystemHotkey?.ToString() ?? string.Empty;

        // General
        StartWithWindowsCheck.IsChecked = s.Behavior.StartWithWindows;
        StartMinimizedCheck.IsChecked = s.Behavior.StartMinimized;
        CloseExitsRadio.IsChecked = s.Behavior.CloseButtonActionValue == CloseAction.Exit;
        CloseToTrayRadio.IsChecked = s.Behavior.CloseButtonActionValue != CloseAction.Exit;
        NotificationsCheck.IsChecked = s.Behavior.ShowTrayNotifications;
        NotificationDurationBox.Text = s.Behavior.NotificationDurationMs.ToString(CultureInfo.CurrentCulture);

        OverlayEnabledCheck.IsChecked = s.Overlay.Enabled;
        OverlayElapsedCheck.IsChecked = s.Overlay.ShowElapsed;
        OverlayMuteCheck.IsChecked = s.Overlay.ShowMuteState;
        OverlayPulseCheck.IsChecked = s.Overlay.PulseWhileRecording;
        OverlayClickThroughCheck.IsChecked = s.Overlay.ClickThrough;
        OverlayOpacitySlider.Value = s.Overlay.Opacity;
        OverlayScaleSlider.Value = s.Overlay.Scale;
        OverlayOpacityText.Text = $"{s.Overlay.Opacity:0%}";
        OverlayScaleText.Text = $"{s.Overlay.Scale:0.00}×";

        StopOnSleepCheck.IsChecked = s.Behavior.StopOnSleep;
        StopOnLockCheck.IsChecked = s.Behavior.StopOnLock;
        StopOnLogOffCheck.IsChecked = s.Behavior.StopOnLogOff;
        StopOnShutdownCheck.IsChecked = s.Behavior.StopOnShutdown;

        SelectChoice(LogLevelCombo, s.Logging.MinimumLevelValue);
        LogRetentionBox.Text = s.Logging.RetainedDays.ToString(CultureInfo.CurrentCulture);
        LogSizeBox.Text = s.Logging.MaxFileSizeMb.ToString(CultureInfo.CurrentCulture);
    }

    private void LoadNoiseSuppression(NoiseSuppressionSettings ns)
    {
        var preset = ns.Enabled ? ns.PresetValue : NoiseSuppressionPreset.Off;

        NsOffRadio.IsChecked = preset == NoiseSuppressionPreset.Off;
        NsLightRadio.IsChecked = preset == NoiseSuppressionPreset.Light;
        NsStandardRadio.IsChecked = preset == NoiseSuppressionPreset.Standard;
        NsStrongRadio.IsChecked = preset == NoiseSuppressionPreset.Strong;
        NsCustomRadio.IsChecked = preset == NoiseSuppressionPreset.Custom;

        NsHighPassBox.Text = FormatNumber(ns.HighPassHz);
        NsStrengthBox.Text = FormatNumber(ns.SpectralStrength);
        NsFloorBox.Text = FormatNumber(ns.SpectralFloorDb);
        NsGateThresholdBox.Text = FormatNumber(ns.GateThresholdDb);
        NsGateRatioBox.Text = FormatNumber(ns.GateRatio);
        NsAttackBox.Text = FormatNumber(ns.GateAttackMs);
        NsReleaseBox.Text = FormatNumber(ns.GateReleaseMs);
        MuteRampBox.Text = FormatNumber(ns.MuteRampMs);
        NsAdaptiveCheck.IsChecked = ns.AdaptiveNoiseFloor;

        NsAdvanced.IsExpanded = preset == NoiseSuppressionPreset.Custom;
    }

    /// <summary>Writes a preset's values into the advanced boxes so the two never disagree.</summary>
    private void OnPresetChosen(NoiseSuppressionPreset preset)
    {
        if (_loading) return;

        var probe = new NoiseSuppressionSettings { MuteRampMs = ParseDouble(MuteRampBox.Text, 12) };
        probe.ApplyPreset(preset);

        _loading = true;
        try
        {
            NsHighPassBox.Text = FormatNumber(probe.HighPassHz);
            NsStrengthBox.Text = FormatNumber(probe.SpectralStrength);
            NsFloorBox.Text = FormatNumber(probe.SpectralFloorDb);
            NsGateThresholdBox.Text = FormatNumber(probe.GateThresholdDb);
            NsGateRatioBox.Text = FormatNumber(probe.GateRatio);
            NsAttackBox.Text = FormatNumber(probe.GateAttackMs);
            NsReleaseBox.Text = FormatNumber(probe.GateReleaseMs);
        }
        finally
        {
            _loading = false;
        }

        if (preset == NoiseSuppressionPreset.Off) NsAdvanced.IsExpanded = false;
    }

    /// <summary>Editing any tuning value means the choice is no longer one of the named presets.</summary>
    private void MarkPresetCustom()
    {
        if (_loading) return;
        NsCustomRadio.IsChecked = true;
    }

    // ---------------------------------------------------------------- meters

    private void StartPreviews()
    {
        if (_isRecording())
        {
            // Settings is normally unreachable during a recording; this is the belt to that braces.
            MicMeterHint.Text = "Level meter unavailable while recording.";
            SystemMeterHint.Text = "Level meter unavailable while recording.";
            return;
        }

        RestartMicPreview();
        RestartSystemPreview();
        _meterTimer.Start();
    }

    private void RestartMicPreview()
    {
        if (_loading || _isRecording()) return;

        _micPreview.Start((MicDeviceCombo.SelectedItem as DeviceChoice)?.DeviceId, DataFlow.Capture);
        MicMeterHint.Text = _micPreview.Error ?? "Live input level — speak to check the device.";
    }

    private void RestartSystemPreview()
    {
        if (_loading || _isRecording()) return;

        _systemPreview.Start((SystemDeviceCombo.SelectedItem as DeviceChoice)?.DeviceId, DataFlow.Render);
        SystemMeterHint.Text = _systemPreview.Error ?? "Live output level — play something to check the device.";
    }

    private void UpdateMeters()
    {
        MicMeter.Value = _micPreview.ReadLevel();
        SystemMeter.Value = _systemPreview.ReadLevel();
    }

    private void StopPreviews()
    {
        _meterTimer.Stop();
        _micPreview.Dispose();
        _systemPreview.Dispose();
    }

    // ---------------------------------------------------------------- helpers

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

    private static void SelectDevice(ComboBox combo, string? deviceId)
    {
        foreach (var item in combo.Items)
        {
            if (item is DeviceChoice choice &&
                string.Equals(choice.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
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

    private static string FormatDb(double value) =>
        $"{value:+0.0;-0.0;0.0} dB";

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.CurrentCulture);

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            ? value
            : fallback;

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value : fallback;

    private void UpdateCustomHeightVisibility()
    {
        var custom = GetChoice(ResolutionCombo, ResolutionPreset.P1080) == ResolutionPreset.Custom;
        CustomHeightPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTemplatePreview()
    {
        var stem = FilenameTemplate.Expand(TemplateBox.Text, DateTime.Now, "Display 1", 1);
        TemplatePreview.Text = $"Example: {stem}.mp4";
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

    private void ResetToDefaults()
    {
        var answer = MessageBox.Show(
            this,
            "Reset every setting to its default? This does not take effect until you press Save.",
            "Pomtom Recorder",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        _loading = true;
        try { LoadFrom(new AppSettings()); }
        finally { _loading = false; }

        ClearError();
    }

    // ---------------------------------------------------------------- hotkeys

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
            box.Text = CapturePrompt;
            box.SelectAll();
        };

        box.LostFocus += (_, _) =>
        {
            if (box.Text == CapturePrompt) box.Text = box.Tag as string ?? string.Empty;
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

    private void ClearHotkey(TextBox box, Action<HotkeyGesture?> assign)
    {
        assign(null);
        box.Text = string.Empty;
        box.Tag = string.Empty;
        ClearError();
    }

    // ---------------------------------------------------------------- save

    private void Save()
    {
        ClearError();

        var folder = FolderBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowError("Choose a folder for recordings.", Tabs.Items[3]);
            return;
        }

        // Probe now so an unwritable folder is caught here rather than three seconds into a
        // countdown. A drive that is merely disconnected today still saves fine.
        if (!OutputFolder.TryPrepare(folder, out var reason))
        {
            ShowError($"That folder cannot be written to ({reason}). Recordings would be saved to " +
                      $"{AppPaths.FallbackOutputFolder} instead.", Tabs.Items[3]);
            return;
        }

        if (!FilenameTemplate.IsUsable(TemplateBox.Text))
        {
            ShowError("That filename pattern does not produce a usable name.", Tabs.Items[3]);
            return;
        }

        if (_startHotkey is null || _pauseHotkey is null || _stopHotkey is null)
        {
            ShowError("Start, pause and stop each need a valid hotkey.", Tabs.Items[4]);
            return;
        }

        // Blank mute hotkeys are legitimate and must not count as duplicates of each other.
        var assigned = new[] { _startHotkey, _pauseHotkey, _stopHotkey, _muteMicHotkey, _muteSystemHotkey }
            .Where(g => g is not null)
            .ToList();

        if (assigned.Distinct().Count() != assigned.Count)
        {
            ShowError("Each action needs its own hotkey.", Tabs.Items[4]);
            return;
        }

        var countdown = ParseInt(CountdownBox.Text, -1);
        if (countdown is < 0 or > 60)
        {
            ShowError("Countdown must be between 0 and 60 seconds.", Tabs.Items[0]);
            return;
        }

        var resolution = GetChoice(ResolutionCombo, ResolutionPreset.P1080);
        var customHeight = ParseInt(CustomHeightBox.Text, -1);
        if (resolution == ResolutionPreset.Custom && customHeight is < 240 or > 4320)
        {
            ShowError("Custom height must be between 240 and 4320 pixels.", Tabs.Items[0]);
            return;
        }

        var maxBitrate = ParseInt(MaxBitrateBox.Text, -1);
        if (maxBitrate < 0 || (maxBitrate > 0 && maxBitrate < 500))
        {
            ShowError("The bitrate ceiling must be 0 (automatic) or at least 500 kbps.", Tabs.Items[2]);
            return;
        }

        ApplyToDraft(folder, countdown, resolution, customHeight, maxBitrate);

        _settings.Save(_draft);

        DialogResult = true;
        Close();
    }

    private void ApplyToDraft(string folder, int countdown, ResolutionPreset resolution, int customHeight, int maxBitrate)
    {
        var s = _draft;

        // Capture
        s.MonitorDeviceId = (MonitorCombo.SelectedItem as MonitorChoice)?.DeviceId;
        s.Resolution = AppSettings.FormatResolution(resolution);
        s.CustomHeight = customHeight > 0 ? customHeight : s.CustomHeight;
        s.FPS = GetChoice(FpsCombo, 60);
        s.Countdown = countdown;
        s.CaptureCursor = CursorCheck.IsChecked == true;
        s.SuppressCaptureBorder = BorderCheck.IsChecked == true;
        s.RecordSystemAudio = SystemAudioCheck.IsChecked == true;
        s.RecordMicrophone = MicrophoneCheck.IsChecked == true;
        s.OutputFolder = folder;

        // Audio
        s.Audio.MicrophoneDeviceId = (MicDeviceCombo.SelectedItem as DeviceChoice)?.DeviceId;
        s.Audio.SystemAudioDeviceId = (SystemDeviceCombo.SelectedItem as DeviceChoice)?.DeviceId;
        s.Audio.MicrophoneGainDb = MicGainSlider.Value;
        s.Audio.SystemAudioGainDb = SystemGainSlider.Value;
        s.Audio.SampleRate = GetChoice(SampleRateCombo, 48_000);
        s.Audio.Channels = GetChoice(ChannelsCombo, 2);
        s.AudioBitrateKbps = GetChoice(AudioBitrateCombo, 192);

        // Noise suppression
        var preset = SelectedPreset();
        s.NoiseSuppression.Preset = NoiseSuppressionSettings.FormatPreset(preset);
        s.NoiseSuppression.Enabled = preset != NoiseSuppressionPreset.Off;
        s.NoiseSuppression.HighPassHz = ParseDouble(NsHighPassBox.Text, 80);
        s.NoiseSuppression.SpectralStrength = ParseDouble(NsStrengthBox.Text, 0.65);
        s.NoiseSuppression.SpectralFloorDb = ParseDouble(NsFloorBox.Text, -18);
        s.NoiseSuppression.GateThresholdDb = ParseDouble(NsGateThresholdBox.Text, -45);
        s.NoiseSuppression.GateRatio = ParseDouble(NsGateRatioBox.Text, 4);
        s.NoiseSuppression.GateAttackMs = ParseDouble(NsAttackBox.Text, 5);
        s.NoiseSuppression.GateReleaseMs = ParseDouble(NsReleaseBox.Text, 120);
        s.NoiseSuppression.MuteRampMs = ParseDouble(MuteRampBox.Text, 12);
        s.NoiseSuppression.AdaptiveNoiseFloor = NsAdaptiveCheck.IsChecked == true;

        // Video
        s.Video.QualityMode = VideoSettings.FormatQualityMode(
            BitrateModeRadio.IsChecked == true ? VideoQualityMode.Bitrate : VideoQualityMode.Quality);
        s.Video.Quality = (int)QualitySlider.Value;
        s.Video.MaxBitrateKbps = maxBitrate;
        s.Video.EncoderOverride = GetChoice(EncoderCombo, "Auto");

        // Output
        s.Naming.FilenameTemplate = TemplateBox.Text?.Trim() ?? FilenameTemplate.Default;
        s.Behavior.RecoverInterruptedRecordings = RecoveryCheck.IsChecked == true;

        // Hotkeys
        s.StartHotkey = _startHotkey!.ToString();
        s.PauseHotkey = _pauseHotkey!.ToString();
        s.StopHotkey = _stopHotkey!.ToString();
        s.MuteMicHotkey = _muteMicHotkey?.ToString() ?? string.Empty;
        s.MuteSystemHotkey = _muteSystemHotkey?.ToString() ?? string.Empty;

        // General
        s.Behavior.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        s.Behavior.StartMinimized = StartMinimizedCheck.IsChecked == true;
        s.Behavior.CloseButtonAction = BehaviorSettings.FormatCloseAction(
            CloseExitsRadio.IsChecked == true ? CloseAction.Exit : CloseAction.MinimizeToTray);
        s.Behavior.ShowTrayNotifications = NotificationsCheck.IsChecked == true;
        s.Behavior.NotificationDurationMs = ParseInt(NotificationDurationBox.Text, 5000);

        s.Overlay.Enabled = OverlayEnabledCheck.IsChecked == true;
        s.Overlay.ShowElapsed = OverlayElapsedCheck.IsChecked == true;
        s.Overlay.ShowMuteState = OverlayMuteCheck.IsChecked == true;
        s.Overlay.PulseWhileRecording = OverlayPulseCheck.IsChecked == true;
        s.Overlay.ClickThrough = OverlayClickThroughCheck.IsChecked == true;
        s.Overlay.Opacity = OverlayOpacitySlider.Value;
        s.Overlay.Scale = OverlayScaleSlider.Value;

        s.Behavior.StopOnSleep = StopOnSleepCheck.IsChecked == true;
        s.Behavior.StopOnLock = StopOnLockCheck.IsChecked == true;
        s.Behavior.StopOnLogOff = StopOnLogOffCheck.IsChecked == true;
        s.Behavior.StopOnShutdown = StopOnShutdownCheck.IsChecked == true;

        s.Logging.MinimumLevel = LoggingSettings.FormatLevel(GetChoice(LogLevelCombo, Settings.LogLevel.Warning));
        s.Logging.RetainedDays = ParseInt(LogRetentionBox.Text, 7);
        s.Logging.MaxFileSizeMb = ParseInt(LogSizeBox.Text, 8);
    }

    private NoiseSuppressionPreset SelectedPreset()
    {
        if (NsOffRadio.IsChecked == true) return NoiseSuppressionPreset.Off;
        if (NsLightRadio.IsChecked == true) return NoiseSuppressionPreset.Light;
        if (NsStrongRadio.IsChecked == true) return NoiseSuppressionPreset.Strong;
        if (NsCustomRadio.IsChecked == true) return NoiseSuppressionPreset.Custom;
        return NoiseSuppressionPreset.Standard;
    }

    /// <summary>Reports a problem and, where known, switches to the tab it is on.</summary>
    private void ShowError(string message, object? tab = null)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;

        if (tab is not null) Tabs.SelectedItem = tab;
    }

    private void ClearError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
