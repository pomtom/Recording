using System.Text.Json.Serialization;

namespace Recorder.Settings;

public enum ResolutionPreset
{
    P720,
    P1080,
    P1440,
    Native
}

/// <summary>
/// The persisted configuration, serialised verbatim to settings.json.
/// </summary>
/// <remarks>
/// Property names match the JSON keys exactly so the file stays hand-editable. Every value is
/// validated by <see cref="SettingsManager"/> on load; nothing here is trusted to be in range.
/// </remarks>
public sealed class AppSettings
{
    public string OutputFolder { get; set; } = @"D:\Recordings";

    /// <summary>One of <c>720p</c>, <c>1080p</c>, <c>1440p</c>, <c>Native</c>.</summary>
    public string Resolution { get; set; } = "1080p";

    /// <summary>30 or 60.</summary>
    public int FPS { get; set; } = 60;

    /// <summary>Countdown seconds before capture starts. 0 disables it.</summary>
    public int Countdown { get; set; } = 3;

    public bool CaptureCursor { get; set; } = true;

    public bool RecordMicrophone { get; set; } = true;

    public bool RecordSystemAudio { get; set; } = true;

    /// <summary>Device name of the monitor to record (e.g. <c>\\.\DISPLAY1</c>). Null means primary.</summary>
    public string? MonitorDeviceId { get; set; }

    public string StartHotkey { get; set; } = "Ctrl+Shift+R";

    public string PauseHotkey { get; set; } = "Ctrl+Shift+P";

    public string StopHotkey { get; set; } = "Ctrl+Shift+S";

    public int AudioBitrateKbps { get; set; } = 192;

    /// <summary>Last position of the floating REC indicator, in physical pixels. Null centres it.</summary>
    public double? OverlayLeft { get; set; }

    public double? OverlayTop { get; set; }

    [JsonIgnore]
    public ResolutionPreset ResolutionPreset => ParseResolution(Resolution);

    public static ResolutionPreset ParseResolution(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "720p" or "720" => ResolutionPreset.P720,
        "1440p" or "1440" => ResolutionPreset.P1440,
        "native" or "source" => ResolutionPreset.Native,
        _ => ResolutionPreset.P1080,
    };

    public static string FormatResolution(ResolutionPreset preset) => preset switch
    {
        ResolutionPreset.P720 => "720p",
        ResolutionPreset.P1440 => "1440p",
        ResolutionPreset.Native => "Native",
        _ => "1080p",
    };

    /// <summary>Target height in pixels, or null for "keep the source size".</summary>
    public static int? TargetHeight(ResolutionPreset preset) => preset switch
    {
        ResolutionPreset.P720 => 720,
        ResolutionPreset.P1080 => 1080,
        ResolutionPreset.P1440 => 1440,
        _ => null,
    };

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
