namespace Recorder.Capture.Dsp;

/// <summary>
/// Applies the user's gain, the mute state, and a ceiling — the last stage on every source.
/// </summary>
/// <remarks>
/// <para>Mute lives here rather than in the capture layer so that it can <em>ramp</em>. Zeroing a
/// buffer outright is a step discontinuity, which is an audible click in the recording at both ends
/// of every muted stretch. Fading across a few milliseconds costs nothing and removes it entirely.
/// The ramp is driven per sample by the audio thread, so the length is exact regardless of how the
/// mixer happens to have carved up its reads.</para>
///
/// <para><see cref="Muted"/> is written from the UI thread and read from the mixer thread; it is a
/// single bool used only to pick a ramp target, so a torn read is impossible and the worst a race
/// can cost is that the fade starts one buffer later.</para>
/// </remarks>
public sealed class GainStage : IAudioProcessor
{
    private readonly float _gain;
    private readonly float _rampStep;

    private volatile bool _muted;
    private float _muteGain = 1f;

    /// <param name="rampMs">Fade length for mute and unmute. 0 switches instantly.</param>
    public GainStage(int sampleRate, double gainDb, double rampMs)
    {
        _gain = (float)Math.Pow(10, gainDb / 20.0);

        var rampSamples = rampMs <= 0 ? 0 : sampleRate * rampMs / 1000.0;
        _rampStep = rampSamples <= 1 ? 1f : (float)(1.0 / rampSamples);
    }

    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    public void Process(float[] buffer, int offset, int count)
    {
        var target = _muted ? 0f : 1f;

        for (var i = 0; i < count; i++)
        {
            if (_muteGain < target) _muteGain = MathF.Min(_muteGain + _rampStep, target);
            else if (_muteGain > target) _muteGain = MathF.Max(_muteGain - _rampStep, target);

            // Gain can legitimately push past full scale; the ceiling catches that here rather than
            // letting it reach the mixer's sum already distorted.
            buffer[offset + i] = SoftClip.Apply(buffer[offset + i] * _gain * _muteGain);
        }
    }

    public void Reset()
    {
        // Snap to the current mute state rather than fading in from wherever the ramp had got to.
        _muteGain = _muted ? 0f : 1f;
    }
}
