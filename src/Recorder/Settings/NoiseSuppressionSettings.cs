using System.Text.Json.Serialization;

namespace Recorder.Settings;

/// <summary>Named strengths for the microphone cleanup chain.</summary>
public enum NoiseSuppressionPreset
{
    Off,
    Light,
    Standard,
    Strong,

    /// <summary>The parameters below are used verbatim because the user edited one.</summary>
    Custom,
}

/// <summary>
/// Parameters for the microphone noise-suppression chain.
/// </summary>
/// <remarks>
/// <para>Choosing a named preset writes that preset's concrete values into these properties rather
/// than leaving them to be looked up later, so settings.json always shows the numbers actually in
/// effect and there is never a second, hidden source of truth. "Custom" therefore just means the
/// user moved a slider.</para>
///
/// <para>These apply to the microphone only. System audio is program material, not a noisy room —
/// denoising it would damage the recording.</para>
/// </remarks>
public sealed class NoiseSuppressionSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>One of <c>Off</c>, <c>Light</c>, <c>Standard</c>, <c>Strong</c>, <c>Custom</c>.</summary>
    public string Preset { get; set; } = "Standard";

    /// <summary>High-pass corner in Hz. 0 bypasses the filter.</summary>
    public double HighPassHz { get; set; } = 80;

    /// <summary>Spectral over-subtraction factor. 0 is a no-op; above ~1.2 starts to warble.</summary>
    public double SpectralStrength { get; set; } = 0.65;

    /// <summary>Floor a suppressed bin may be pushed down to, in dB. Less negative is gentler.</summary>
    public double SpectralFloorDb { get; set; } = -18;

    /// <summary>Whether the noise estimate keeps tracking, or freezes after the opening moments.</summary>
    public bool AdaptiveNoiseFloor { get; set; } = true;

    /// <summary>Level below which the gate starts attenuating, in dBFS.</summary>
    public double GateThresholdDb { get; set; } = -45;

    /// <summary>Expansion ratio below the threshold. 1 disables the gate.</summary>
    public double GateRatio { get; set; } = 4.0;

    public double GateAttackMs { get; set; } = 5;

    public double GateReleaseMs { get; set; } = 120;

    /// <summary>Fade length applied when muting or unmuting, so the transition does not click.</summary>
    public double MuteRampMs { get; set; } = 12;

    [JsonIgnore]
    public NoiseSuppressionPreset PresetValue => ParsePreset(Preset);

    public static NoiseSuppressionPreset ParsePreset(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" or "none" => NoiseSuppressionPreset.Off,
        "light" or "low" => NoiseSuppressionPreset.Light,
        "strong" or "high" => NoiseSuppressionPreset.Strong,
        "custom" => NoiseSuppressionPreset.Custom,
        _ => NoiseSuppressionPreset.Standard,
    };

    public static string FormatPreset(NoiseSuppressionPreset preset) => preset.ToString();

    /// <summary>
    /// Overwrites the tuning parameters with a named preset's values.
    /// </summary>
    /// <remarks><see cref="NoiseSuppressionPreset.Custom"/> leaves them alone by design.</remarks>
    public void ApplyPreset(NoiseSuppressionPreset preset)
    {
        Preset = FormatPreset(preset);

        switch (preset)
        {
            case NoiseSuppressionPreset.Off:
                Enabled = false;
                break;

            case NoiseSuppressionPreset.Light:
                Enabled = true;
                HighPassHz = 60;
                SpectralStrength = 0.35;
                SpectralFloorDb = -12;
                GateThresholdDb = -55;
                GateRatio = 2.0;
                break;

            case NoiseSuppressionPreset.Strong:
                Enabled = true;
                HighPassHz = 100;
                SpectralStrength = 1.10;
                SpectralFloorDb = -26;
                GateThresholdDb = -38;
                GateRatio = 6.0;
                break;

            case NoiseSuppressionPreset.Standard:
                Enabled = true;
                HighPassHz = 80;
                SpectralStrength = 0.65;
                SpectralFloorDb = -18;
                GateThresholdDb = -45;
                GateRatio = 4.0;
                break;

            // Custom: the stored values are the user's own; nothing to overwrite.
        }
    }

    public NoiseSuppressionSettings Clone() => (NoiseSuppressionSettings)MemberwiseClone();
}
