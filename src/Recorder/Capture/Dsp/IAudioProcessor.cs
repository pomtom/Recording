namespace Recorder.Capture.Dsp;

/// <summary>
/// One stage of the microphone cleanup chain.
/// </summary>
/// <remarks>
/// <para>Stages work on a single channel of 32-bit float samples, in place. Mono rather than
/// interleaved because every useful stage here — filtering, spectral estimation, level following —
/// is a per-channel operation, and de-interleaving once at the edge is cheaper and far easier to
/// reason about than teaching every stage a stride.</para>
///
/// <para>A stage may be stateful and is allowed to introduce latency, but the latency must be
/// <em>constant</em>: <see cref="Process"/> must always write exactly <paramref name="count"/>
/// samples. The recording timeline is derived from the clock, not from sample counts, so a stage
/// that returned short would desync audio from video.</para>
/// </remarks>
public interface IAudioProcessor
{
    /// <summary>Transforms <paramref name="count"/> samples in place.</summary>
    void Process(float[] buffer, int offset, int count);

    /// <summary>Drops any accumulated state, as after a pause.</summary>
    void Reset();
}
