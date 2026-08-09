using NAudio.Wave;

namespace Recorder.Capture.Dsp;

/// <summary>
/// Runs a per-channel <see cref="AudioProcessorChain"/> over an interleaved sample stream.
/// </summary>
/// <remarks>
/// <para>De-interleaves, processes each channel with its own chain instance, and re-interleaves. The
/// chains have to be separate objects because every stage here is stateful — sharing one across
/// channels would leak one channel's filter history and noise estimate into the other.</para>
///
/// <para>Sources that were originally mono are the common case for a microphone, and by the time the
/// stream reaches here they have already been upmixed to stereo with both channels identical.
/// Processing both would be exactly twice the work for a bit-identical result, so
/// <paramref name="sourceWasMono"/> makes this process channel 0 and copy it across.</para>
///
/// <para>Read length is passed through untouched. That is a contract, not an implementation detail:
/// upstream is a <c>BufferedWaveProvider</c> with <c>ReadFully</c> set, and the mixer relies on
/// always getting exactly what it asked for to keep the audio timeline locked to the clock.</para>
/// </remarks>
public sealed class DspSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly AudioProcessorChain[] _chains;
    private readonly int _channels;
    private readonly bool _mirrorFirstChannel;

    private float[] _scratch = [];

    public DspSampleProvider(ISampleProvider source, Func<AudioProcessorChain> chainFactory, bool sourceWasMono)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _mirrorFirstChannel = sourceWasMono && _channels > 1;

        var needed = _mirrorFirstChannel ? 1 : _channels;
        _chains = new AudioProcessorChain[needed];
        for (var i = 0; i < needed; i++) _chains[i] = chainFactory();
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>The chains, so mute can be toggled while a recording is running.</summary>
    public IReadOnlyList<AudioProcessorChain> Chains => _chains;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        var frames = read / _channels;
        if (frames <= 0) return read;

        if (_scratch.Length < frames) _scratch = new float[frames];

        for (var channel = 0; channel < _chains.Length; channel++)
        {
            for (var frame = 0; frame < frames; frame++)
                _scratch[frame] = buffer[offset + (frame * _channels) + channel];

            _chains[channel].Process(_scratch, 0, frames);

            for (var frame = 0; frame < frames; frame++)
                buffer[offset + (frame * _channels) + channel] = _scratch[frame];
        }

        if (_mirrorFirstChannel)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var value = buffer[offset + (frame * _channels)];
                for (var channel = 1; channel < _channels; channel++)
                    buffer[offset + (frame * _channels) + channel] = value;
            }
        }

        return read;
    }

    public void SetMuted(bool muted)
    {
        foreach (var chain in _chains) chain.Gain.Muted = muted;
    }

    public void Reset()
    {
        foreach (var chain in _chains) chain.Reset();
    }
}
