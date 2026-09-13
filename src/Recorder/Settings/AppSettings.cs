using System.Text.Json.Serialization;

namespace Recorder.Settings;

public enum ResolutionPreset
{
    P720,
    P1080,
    P1440,
    Native,

    /// <summary>Height comes from <see cref="AppSettings.CustomHeight"/>.</summary>
    Custom
}

/// <summary>
/// The persisted configuration, serialised verbatim to settings.json.
/// </summary>
/// <remarks>
/// <para>Property names match the JSON keys exactly so the file stays hand-editable. Every value is
/// validated by <see cref="SettingsManager"/> on load; nothing here is trusted to be in range.</para>
///
/// <para>The original fourteen keys stay at the top level and keep their names. Everything added
/// since lives in a nested section instead, which is what makes an older settings.json load without
/// a migration step: a missing section simply deserialises to its defaults, while the keys someone
/// actually took the trouble to change — their output folder, their hotkeys — are found where they
/// have always been.</para>
/// </remarks>
public sealed class AppSettings
{
    /// <summary>
    /// Bumped when a section is added, so an existing file is rewritten to mention it.
    /// </summary>
    /// <remarks>
    /// <para>A missing section deserialises to its defaults and the app runs perfectly well without
    /// the rewrite — but a configuration file meant to be hand-edited that does not mention a
    /// setting is one nobody can discover that setting in. <see cref="SettingsManager.Load"/> uses
    /// this to decide when to write the file back out; existing values are read first and preserved.
    /// Version 3 added the <see cref="Camera"/> section.</para>
    ///
    /// <para>The default is deliberately <b>0</b>, not the current version. A key absent from the
    /// JSON leaves the property at its C# default, so any other value would make the very oldest
    /// files — the ones written before this key existed, which are precisely the ones most in need
    /// of a rewrite — indistinguishable from current ones. <see cref="SettingsManager.Validate"/>
    /// stamps the real version on afterwards.</para>
    /// </remarks>
    public int SettingsVersion { get; set; }

    public string OutputFolder { get; set; } = @"D:\Recordings";

    /// <summary>One of <c>720p</c>, <c>1080p</c>, <c>1440p</c>, <c>Native</c>, <c>Custom</c>.</summary>
    public string Resolution { get; set; } = "1080p";

    /// <summary>Output height used when <see cref="Resolution"/> is <c>Custom</c>. 240–4320.</summary>
    public int CustomHeight { get; set; } = 1080;

    /// <summary>Frames per second. 10–240.</summary>
    public int FPS { get; set; } = 60;

    /// <summary>Countdown seconds before capture starts. 0 disables it.</summary>
    public int Countdown { get; set; } = 3;

    public bool CaptureCursor { get; set; } = true;

    /// <summary>
    /// Whether to suppress the yellow "being captured" border Windows 11 draws.
    /// </summary>
    /// <remarks>The border does not exist on Windows 10, where this has no effect either way.</remarks>
    public bool SuppressCaptureBorder { get; set; } = true;

    public bool RecordMicrophone { get; set; } = true;

    public bool RecordSystemAudio { get; set; } = true;

    /// <summary>Device name of the monitor to record (e.g. <c>\\.\DISPLAY1</c>). Null means primary.</summary>
    public string? MonitorDeviceId { get; set; }

    public string StartHotkey { get; set; } = "Ctrl+Shift+R";

    public string PauseHotkey { get; set; } = "Ctrl+Shift+P";

    public string StopHotkey { get; set; } = "Ctrl+Shift+S";

    /// <summary>Toggles the microphone mute. Empty means no hotkey is registered.</summary>
    public string MuteMicHotkey { get; set; } = "Ctrl+Shift+M";

    /// <summary>
    /// Toggles the system-audio mute. Empty by default, and empty means unregistered.
    /// </summary>
    /// <remarks>
    /// Every global hotkey is one more combination taken away from every other application on the
    /// machine, and muting system audio mid-recording is a far rarer thing to want than muting a
    /// microphone. Opt-in is the friendlier default.
    /// </remarks>
    public string MuteSystemHotkey { get; set; } = string.Empty;

    public int AudioBitrateKbps { get; set; } = 192;

    /// <summary>Last position of the floating REC indicator, in physical pixels. Null centres it.</summary>
    public double? OverlayLeft { get; set; }

    public double? OverlayTop { get; set; }

    // ---------------------------------------------------------------- sections

    public AudioSettings Audio { get; set; } = new();

    public NoiseSuppressionSettings NoiseSuppression { get; set; } = new();

    public VideoSettings Video { get; set; } = new();

    public NamingSettings Naming { get; set; } = new();

    public OverlaySettings Overlay { get; set; } = new();

    public CameraSettings Camera { get; set; } = new();

    public BehaviorSettings Behavior { get; set; } = new();

    public LoggingSettings Logging { get; set; } = new();

    // ---------------------------------------------------------------- derived

    [JsonIgnore]
    public ResolutionPreset ResolutionPreset => ParseResolution(Resolution);

    /// <summary>Target output height for the current resolution setting, or null for native.</summary>
    [JsonIgnore]
    public int? EffectiveTargetHeight =>
        ResolutionPreset == ResolutionPreset.Custom ? CustomHeight : TargetHeight(ResolutionPreset);

    public static ResolutionPreset ParseResolution(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "720p" or "720" => ResolutionPreset.P720,
        "1440p" or "1440" => ResolutionPreset.P1440,
        "native" or "source" => ResolutionPreset.Native,
        "custom" => ResolutionPreset.Custom,
        _ => ResolutionPreset.P1080,
    };

    public static string FormatResolution(ResolutionPreset preset) => preset switch
    {
        ResolutionPreset.P720 => "720p",
        ResolutionPreset.P1440 => "1440p",
        ResolutionPreset.Native => "Native",
        ResolutionPreset.Custom => "Custom",
        _ => "1080p",
    };

    /// <summary>
    /// Target height in pixels, or null for "keep the source size".
    /// </summary>
    /// <remarks>
    /// <see cref="ResolutionPreset.Custom"/> also returns null here because its height is not a
    /// property of the preset — use <see cref="EffectiveTargetHeight"/>, which knows about
    /// <see cref="CustomHeight"/>.
    /// </remarks>
    public static int? TargetHeight(ResolutionPreset preset) => preset switch
    {
        ResolutionPreset.P720 => 720,
        ResolutionPreset.P1080 => 1080,
        ResolutionPreset.P1440 => 1440,
        _ => null,
    };

    /// <summary>
    /// A copy that shares nothing with the original.
    /// </summary>
    /// <remarks>
    /// This has to be deep. The settings window clones the live instance, mutates the copy and only
    /// commits on Save — with a shallow copy the nested sections would still be the same objects,
    /// so editing them would take effect immediately and Cancel would quietly do nothing.
    /// </remarks>
    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Audio = Audio.Clone();
        copy.NoiseSuppression = NoiseSuppression.Clone();
        copy.Video = Video.Clone();
        copy.Naming = Naming.Clone();
        copy.Overlay = Overlay.Clone();
        copy.Camera = Camera.Clone();
        copy.Behavior = Behavior.Clone();
        copy.Logging = Logging.Clone();
        return copy;
    }
}
