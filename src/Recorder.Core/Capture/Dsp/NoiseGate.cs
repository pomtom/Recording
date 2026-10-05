namespace Recorder.Capture.Dsp;

/// <summary>
/// Downward expander: pushes what is left between words the rest of the way down to silence.
/// </summary>
/// <remarks>
/// <para>Spectral subtraction lowers the noise floor but does not remove it, and a floor that
/// breathes up and down with the gain is more distracting than a steady one. The gate finishes the
/// job on the quiet stretches. It runs after the spectral stage precisely so it can be gentle — it
/// is deciding between "already-cleaned speech" and "already-cleaned silence", which is a much
/// easier call than the one it would face on the raw signal.</para>
///
/// <para>An expander rather than a hard gate: below the threshold the signal is attenuated in
/// proportion to how far below it sits, so a trailing word fades out instead of being guillotined.
/// Attack and release act on the gain rather than on the signal, which is what stops the gate from
/// chattering on a level that hovers around the threshold.</para>
/// </remarks>
public sealed class NoiseGate : IAudioProcessor
{
    /// <summary>Deepest attenuation the gate will apply, in dB. Below this it is inaudible anyway.</summary>
    private const float MaxAttenuationDb = -60f;

    private const float Epsilon = 1e-9f;

    private readonly float _thresholdDb;
    private readonly float _ratio;
    private readonly float _attack;
    private readonly float _release;
    private readonly float _envelopeRise;
    private readonly float _envelopeFall;

    private float _envelope;
    private float _gainDb;

    public NoiseGate(int sampleRate, double thresholdDb, double ratio, double attackMs, double releaseMs)
    {
        _thresholdDb = (float)thresholdDb;
        _ratio = (float)Math.Max(1.0, ratio);

        _attack = Coefficient(sampleRate, attackMs);
        _release = Coefficient(sampleRate, releaseMs);

        // The level detector tracks peaks quickly and decays slowly, so a short gap inside a word
        // does not read as the end of one.
        _envelopeRise = Coefficient(sampleRate, 1.0);
        _envelopeFall = Coefficient(sampleRate, 60.0);
    }

    /// <summary>Per-sample smoothing coefficient for a time constant in milliseconds.</summary>
    private static float Coefficient(int sampleRate, double milliseconds)
    {
        if (milliseconds <= 0) return 0f;
        return (float)Math.Exp(-1.0 / (sampleRate * milliseconds / 1000.0));
    }

    public void Process(float[] buffer, int offset, int count)
    {
        // A ratio of 1 is an identity expander; skip the arithmetic entirely.
        if (_ratio <= 1f) return;

        for (var i = 0; i < count; i++)
        {
            var sample = buffer[offset + i];
            var magnitude = Math.Abs(sample);

            var coefficient = magnitude > _envelope ? _envelopeRise : _envelopeFall;
            _envelope = (coefficient * _envelope) + ((1f - coefficient) * magnitude);

            var levelDb = 20f * MathF.Log10(MathF.Max(_envelope, Epsilon));

            // Above the threshold the gate is fully open; below it, attenuation grows with distance.
            var targetDb = levelDb >= _thresholdDb
                ? 0f
                : Math.Max((_ratio - 1f) * (levelDb - _thresholdDb), MaxAttenuationDb);

            // Opening is "attack", closing is "release" — the gate should let a sound through the
            // instant it arrives but linger before shutting behind it.
            var smoothing = targetDb > _gainDb ? _attack : _release;
            _gainDb = (smoothing * _gainDb) + ((1f - smoothing) * targetDb);

            buffer[offset + i] = sample * MathF.Pow(10f, _gainDb / 20f);
        }
    }

    public void Reset()
    {
        _envelope = 0f;
        _gainDb = 0f;
    }
}
