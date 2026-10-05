using System.Text.Json.Serialization;

namespace Recorder.Settings;

/// <summary>How the encoder is asked to trade file size against picture quality.</summary>
public enum VideoQualityMode
{
    /// <summary>Constant-quality: the encoder spends whatever bitrate the picture needs.</summary>
    Quality,

    /// <summary>Bitrate-targeted: the ceiling is respected even if quality suffers.</summary>
    Bitrate,
}

/// <summary>
/// Encoder quality controls.
/// </summary>
/// <remarks>
/// <see cref="Quality"/> is a single number across all four encoders even though each one spells it
/// differently (<c>-cq</c>, <c>-global_quality</c>, <c>-qp_i</c>/<c>-qp_p</c>, <c>-crf</c>). They are
/// close enough in meaning — lower is better, ~23 is visually transparent for screen content — that
/// one slider is honest, and it saves the user from having to know which encoder was picked.
/// </remarks>
public sealed class VideoSettings
{
    /// <summary><c>Quality</c> or <c>Bitrate</c>.</summary>
    public string QualityMode { get; set; } = "Quality";

    /// <summary>Quality index, 15 (best) to 35 (smallest). Ignored in bitrate mode.</summary>
    public int Quality { get; set; } = 23;

    /// <summary>Bitrate ceiling in kbps. 0 derives it from the frame height.</summary>
    public int MaxBitrateKbps { get; set; }

    /// <summary>One of <c>Auto</c>, <c>Nvenc</c>, <c>QuickSync</c>, <c>Amf</c>, <c>X264</c>.</summary>
    public string EncoderOverride { get; set; } = "Auto";

    [JsonIgnore]
    public VideoQualityMode QualityModeValue => ParseQualityMode(QualityMode);

    public static VideoQualityMode ParseQualityMode(string? value) =>
        value?.Trim().ToLowerInvariant() is "bitrate" or "cbr" or "vbr"
            ? VideoQualityMode.Bitrate
            : VideoQualityMode.Quality;

    public static string FormatQualityMode(VideoQualityMode mode) => mode.ToString();

    public VideoSettings Clone() => (VideoSettings)MemberwiseClone();
}
