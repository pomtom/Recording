using NAudio.Dsp;

namespace Recorder.Capture.Dsp;

/// <summary>
/// Second-order high-pass filter, the first stage of the microphone chain.
/// </summary>
/// <remarks>
/// Desk thumps, air conditioning, traffic and mains hum all live below ~80 Hz, where speech has
/// nothing to lose. Removing them here rather than later matters: the spectral stage estimates a
/// noise floor per frequency bin, and a large stationary rumble would otherwise dominate that
/// estimate and drag the whole thing off.
/// </remarks>
public sealed class HighPassProcessor : IAudioProcessor
{
    /// <summary>Butterworth Q. Flattest passband, no resonant bump at the corner.</summary>
    private const float Q = 0.7071f;

    private readonly BiQuadFilter _filter;

    public HighPassProcessor(int sampleRate, double cutoffHz)
    {
        _filter = BiQuadFilter.HighPassFilter(sampleRate, (float)cutoffHz, Q);
    }

    public void Process(float[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++)
            buffer[offset + i] = _filter.Transform(buffer[offset + i]);
    }

    /// <summary>
    /// Clears the filter's delay line.
    /// </summary>
    /// <remarks>
    /// <see cref="BiQuadFilter"/> exposes no reset, so the state is flushed by running silence
    /// through it — a handful of samples is more than enough for a two-pole filter to settle.
    /// </remarks>
    public void Reset()
    {
        for (var i = 0; i < 8; i++) _filter.Transform(0f);
    }
}
