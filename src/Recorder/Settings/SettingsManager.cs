using System.IO;
using System.Text.Json;
using Recorder.Encoding;
using Recorder.Hotkeys;
using Recorder.Utils;

namespace Recorder.Settings;

/// <summary>
/// Loads, validates and persists <see cref="AppSettings"/>.
/// </summary>
/// <remarks>
/// A corrupt or hand-mangled settings file must never stop the app from starting, so loading is
/// total: anything unparseable falls back to defaults, and out-of-range values are clamped rather
/// than rejected. Saving is atomic (write to a temp file, then replace) so a crash mid-write cannot
/// leave a truncated config behind.
/// </remarks>
public sealed class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // Without this the default encoder escapes '+' as +, turning "Ctrl+Shift+R" into
        // something nobody wants to hand-edit.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private AppSettings _current = new();

    /// <summary>Raised after <see cref="Save"/> completes, on the caller's thread.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>A snapshot of the current settings. Never null.</summary>
    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public string SettingsPath => AppPaths.SettingsPath;

    /// <summary>The version <see cref="AppSettings"/> is written at. Bumped when the shape changes.</summary>
    private const int CurrentVersion = 3;

    public void Load()
    {
        AppSettings loaded;
        var existed = false;
        var wasOlder = false;

        try
        {
            var path = AppPaths.SettingsPath;
            existed = File.Exists(path);
            if (existed)
            {
                var json = File.ReadAllText(path);
                loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                wasOlder = loaded.SettingsVersion < CurrentVersion;
            }
            else
            {
                loaded = new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "settings.json could not be read; falling back to defaults.");
            loaded = new AppSettings();
        }

        Validate(loaded);

        lock (_gate) _current = loaded;

        // Write the defaults out on first run so the file is there to hand-edit.
        //
        // Rewriting an older file matters for the same reason. Settings added since it was written
        // are absent from it, and a file that does not mention them is one nobody can discover them
        // in — which for a configuration meant to be edited by hand is the whole point. Existing
        // values are untouched: they were read back before this runs, and anything the file did not
        // mention was already at its default.
        if (!existed || wasOlder)
        {
            // Warn rather than Info to match the convention elsewhere for notable once-per-launch
            // lifecycle events, and because the default minimum level would otherwise drop it.
            if (wasOlder) Log.Warn($"Upgraded settings.json to version {CurrentVersion}; existing values preserved.");
            Persist(loaded);
        }
    }

    /// <summary>Replaces the current settings and writes them to disk.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        lock (_gate) _current = settings;

        Persist(settings);
        Changed?.Invoke(this, settings);
    }

    /// <summary>
    /// Mutates the current settings in place and persists them, without raising <see cref="Changed"/>.
    /// Used for incidental state such as the overlay position and the output-folder fallback, where
    /// re-applying hotkeys and UI bindings would be pointless churn.
    /// </summary>
    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        AppSettings snapshot;
        lock (_gate)
        {
            mutate(_current);
            Validate(_current);
            snapshot = _current.Clone();
        }
        Persist(snapshot);
    }

    private void Persist(AppSettings settings)
    {
        try
        {
            var path = AppPaths.SettingsPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);

            // File.Replace needs an existing destination; Move covers the first save.
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not persist settings.json.");
        }
    }

    /// <summary>
    /// Clamps every field into a usable range. Mutates in place.
    /// </summary>
    /// <remarks>
    /// Split per section purely for readability; the contract is the same throughout — clamp, never
    /// reject, and never let a bad value reach the recording path.
    /// </remarks>
    private static void Validate(AppSettings s)
    {
        s.SettingsVersion = CurrentVersion;

        ValidateCapture(s);
        ValidateHotkeys(s);
        ValidateAudio(s);
        ValidateNoiseSuppression(s);
        ValidateVideo(s);
        ValidateNaming(s);
        ValidateOverlay(s);
        ValidateCamera(s);
        ValidateBehavior(s);
        ValidateLogging(s);
    }

    private static void ValidateCapture(AppSettings s)
    {
        s.Resolution = AppSettings.FormatResolution(AppSettings.ParseResolution(s.Resolution));

        // Even heights only: the encoders all want 4:2:0 chroma, which cannot represent an odd one.
        s.CustomHeight = Math.Clamp(s.CustomHeight, 240, 4320);
        if (s.CustomHeight % 2 != 0) s.CustomHeight++;

        s.FPS = Math.Clamp(s.FPS, 10, 240);
        s.Countdown = Math.Clamp(s.Countdown, 0, 60);
        s.AudioBitrateKbps = Math.Clamp(s.AudioBitrateKbps, 64, 320);

        if (string.IsNullOrWhiteSpace(s.OutputFolder))
            s.OutputFolder = AppPaths.FallbackOutputFolder;

        if (string.IsNullOrWhiteSpace(s.MonitorDeviceId))
            s.MonitorDeviceId = null;
    }

    private static void ValidateHotkeys(AppSettings s)
    {
        s.StartHotkey = NormalizeHotkey(s.StartHotkey, "Ctrl+Shift+R");
        s.PauseHotkey = NormalizeHotkey(s.PauseHotkey, "Ctrl+Shift+P");
        s.StopHotkey = NormalizeHotkey(s.StopHotkey, "Ctrl+Shift+S");

        // The mute hotkeys are optional, so a blank is a legitimate value meaning "do not register"
        // rather than something to substitute a default for.
        s.MuteMicHotkey = NormalizeOptionalHotkey(s.MuteMicHotkey);
        s.MuteSystemHotkey = NormalizeOptionalHotkey(s.MuteSystemHotkey);
    }

    private static void ValidateAudio(AppSettings s)
    {
        var a = s.Audio ??= new AudioSettings();

        if (string.IsNullOrWhiteSpace(a.MicrophoneDeviceId)) a.MicrophoneDeviceId = null;
        if (string.IsNullOrWhiteSpace(a.SystemAudioDeviceId)) a.SystemAudioDeviceId = null;

        a.MicrophoneGainDb = ClampDouble(a.MicrophoneGainDb, -24, 24, 0);
        a.SystemAudioGainDb = ClampDouble(a.SystemAudioGainDb, -24, 24, 0);

        a.SampleRate = a.SampleRate == 44_100 ? 44_100 : 48_000;
        a.Channels = a.Channels == 1 ? 1 : 2;
    }

    private static void ValidateNoiseSuppression(AppSettings s)
    {
        var n = s.NoiseSuppression ??= new NoiseSuppressionSettings();

        n.Preset = NoiseSuppressionSettings.FormatPreset(NoiseSuppressionSettings.ParsePreset(n.Preset));

        // 0 is the documented bypass, so it has to survive the clamp that otherwise floors at 20 Hz.
        n.HighPassHz = n.HighPassHz <= 0 ? 0 : ClampDouble(n.HighPassHz, 20, 300, 80);

        n.SpectralStrength = ClampDouble(n.SpectralStrength, 0, 2, 0.65);
        n.SpectralFloorDb = ClampDouble(n.SpectralFloorDb, -60, 0, -18);
        n.GateThresholdDb = ClampDouble(n.GateThresholdDb, -90, 0, -45);
        n.GateRatio = ClampDouble(n.GateRatio, 1, 20, 4);
        n.GateAttackMs = ClampDouble(n.GateAttackMs, 0.5, 200, 5);
        n.GateReleaseMs = ClampDouble(n.GateReleaseMs, 5, 2000, 120);
        n.MuteRampMs = ClampDouble(n.MuteRampMs, 0, 250, 12);
    }

    private static void ValidateVideo(AppSettings s)
    {
        var v = s.Video ??= new VideoSettings();

        v.QualityMode = VideoSettings.FormatQualityMode(VideoSettings.ParseQualityMode(v.QualityMode));
        v.Quality = Math.Clamp(v.Quality, 15, 35);

        // 0 means "derive from the frame height", so only positive values get a floor.
        v.MaxBitrateKbps = v.MaxBitrateKbps <= 0 ? 0 : Math.Clamp(v.MaxBitrateKbps, 500, 200_000);

        v.EncoderOverride = Enum.TryParse<VideoEncoder>(v.EncoderOverride, ignoreCase: true, out var encoder)
            ? encoder.ToString()
            : "Auto";
    }

    private static void ValidateNaming(AppSettings s)
    {
        var n = s.Naming ??= new NamingSettings();

        if (!FilenameTemplate.IsUsable(n.FilenameTemplate))
        {
            if (!string.IsNullOrWhiteSpace(n.FilenameTemplate))
                Log.Warn($"Filename template '{n.FilenameTemplate}' produces no usable name; using the default.");

            n.FilenameTemplate = new NamingSettings().FilenameTemplate;
        }
    }

    private static void ValidateOverlay(AppSettings s)
    {
        var o = s.Overlay ??= new OverlaySettings();

        o.Opacity = ClampDouble(o.Opacity, 0.2, 1.0, 0.95);
        o.Scale = ClampDouble(o.Scale, 0.75, 2.0, 1.0);
    }

    private static void ValidateCamera(AppSettings s)
    {
        var c = s.Camera ??= new CameraSettings();

        if (string.IsNullOrWhiteSpace(c.DeviceId)) c.DeviceId = null;

        c.Shape = CameraSettings.FormatShape(CameraSettings.ParseShape(c.Shape));

        // Even dimensions only: the composite ends up in a 4:2:0 frame like everything else, and an
        // odd-sized source would round differently on the two chroma planes.
        c.CaptureWidth = Math.Clamp(c.CaptureWidth, 160, 4096) & ~1;
        c.CaptureHeight = Math.Clamp(c.CaptureHeight, 120, 2160) & ~1;

        // The camera rate is also the rate at which a still screen gets re-composited, so the
        // ceiling is about cost, not about what webcams can do.
        c.Fps = Math.Clamp(c.Fps, 5, 60);

        // The upper bound is a cost ceiling as much as a taste one: the blend is proportional to
        // the bubble's area, and the window is layered (AllowsTransparency), which WPF renders in
        // software.
        c.Size = ClampDouble(c.Size, 120, 900, 260);
        c.BorderThickness = ClampDouble(c.BorderThickness, 0, 16, 4);
        c.Opacity = ClampDouble(c.Opacity, 0.2, 1.0, 1.0);
        c.SnapDistance = ClampDouble(c.SnapDistance, 0, 600, 160);
        c.SnapMargin = ClampDouble(c.SnapMargin, 0, 300, 32);

        if (!IsParsableColor(c.BorderColor)) c.BorderColor = "#FFFFFFFF";

        // A saved position is only meaningful as a pair; half of one would place the bubble at zero
        // on the other axis, which is not where the user left it.
        if (c.Left is null || c.Top is null || !double.IsFinite(c.Left.Value) || !double.IsFinite(c.Top.Value))
        {
            c.Left = null;
            c.Top = null;
        }
    }

    /// <summary>Whether a colour string is one WPF can parse, without throwing to find out.</summary>
    private static bool IsParsableColor(string? value)
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

    private static void ValidateBehavior(AppSettings s)
    {
        var b = s.Behavior ??= new BehaviorSettings();

        b.CloseButtonAction = BehaviorSettings.FormatCloseAction(
            BehaviorSettings.ParseCloseAction(b.CloseButtonAction));

        b.NotificationDurationMs = Math.Clamp(b.NotificationDurationMs, 1000, 30_000);
    }

    private static void ValidateLogging(AppSettings s)
    {
        var l = s.Logging ??= new LoggingSettings();

        l.MinimumLevel = LoggingSettings.FormatLevel(LoggingSettings.ParseLevel(l.MinimumLevel));
        l.RetainedDays = Math.Clamp(l.RetainedDays, 1, 90);
        l.MaxFileSizeMb = Math.Clamp(l.MaxFileSizeMb, 1, 256);
    }

    /// <summary>Clamps, substituting <paramref name="fallback"/> for NaN and infinity.</summary>
    private static double ClampDouble(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static string NormalizeHotkey(string? value, string fallback)
    {
        if (HotkeyGesture.TryParse(value, out var gesture) && gesture is not null)
            return gesture.ToString();

        if (!string.IsNullOrWhiteSpace(value))
            Log.Warn($"Unrecognised hotkey '{value}'; using {fallback}.");

        return fallback;
    }

    /// <summary>Normalises a hotkey that is allowed to be absent. Blank stays blank.</summary>
    private static string NormalizeOptionalHotkey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        if (HotkeyGesture.TryParse(value, out var gesture) && gesture is not null)
            return gesture.ToString();

        Log.Warn($"Unrecognised hotkey '{value}'; leaving it unassigned.");
        return string.Empty;
    }
}
