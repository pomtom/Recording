using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Recorder.Capture.Dsp;
using Recorder.Settings;
using Recorder.Utils;

namespace Recorder.Capture;

/// <summary>Which of the two things the recorder can listen to a source is.</summary>
public enum AudioSourceKind
{
    Microphone,
    SystemAudio,
}

/// <summary>
/// Captures system audio and the microphone, normalising both to the mixer's common format.
/// </summary>
/// <remarks>
/// <para>Each source is independent: a missing microphone, a muted output device or a driver that
/// refuses to open must degrade the recording to whichever sources <em>do</em> work rather than
/// aborting it. <see cref="FailureSummary"/> reports what was lost so the UI can mention it once.</para>
///
/// <para>Every source lands in a <see cref="BufferedWaveProvider"/> with <c>ReadFully</c> left on.
/// That is deliberate and important: when a source has nothing queued — WASAPI loopback commonly
/// goes quiet when no application is playing anything — reads return silence instead of short
/// counts. The mixer therefore always gets exactly the number of samples it asked for, which is
/// what keeps the audio timeline locked to the recording clock.</para>
///
/// <para>Each source ends in a <see cref="DspSampleProvider"/> carrying its gain, its mute state and,
/// for the microphone only, the noise-suppression chain.</para>
/// </remarks>
public sealed class AudioCaptureService : IDisposable
{
    /// <summary>How much audio each source may queue before old data is dropped.</summary>
    private static readonly TimeSpan BufferDuration = TimeSpan.FromSeconds(5);

    private readonly AppSettings _settings;
    private readonly List<AudioSource> _sources = [];
    private readonly List<string> _failures = [];
    private bool _disposed;

    public AudioCaptureService(AppSettings settings)
    {
        _settings = settings;
        SampleRate = settings.Audio.SampleRate;
        Channels = settings.Audio.Channels;
    }

    /// <summary>Mixer sample rate every source is resampled to.</summary>
    public int SampleRate { get; }

    /// <summary>Channel count every source is mapped to.</summary>
    public int Channels { get; }

    /// <summary>The sources that opened successfully.</summary>
    public IReadOnlyList<AudioSource> Sources => _sources;

    public bool HasAnySource => _sources.Count > 0;

    /// <summary>A one-line description of what could not be captured, or null if all was well.</summary>
    public string? FailureSummary => _failures.Count == 0 ? null : string.Join(" ", _failures);

    /// <summary>Opens the requested sources. Never throws for an individual source failure.</summary>
    public void Start(bool systemAudio, bool microphone)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (systemAudio) TryAddSource(AudioSourceKind.SystemAudio, "System audio", CreateLoopbackCapture);
        if (microphone) TryAddSource(AudioSourceKind.Microphone, "Microphone", CreateMicrophoneCapture);

        if (!HasAnySource && (systemAudio || microphone))
            Log.Error("No audio source could be opened; the recording will be silent.");
    }

    private void TryAddSource(AudioSourceKind kind, string label, Func<IWaveIn> factory)
    {
        IWaveIn? capture = null;
        try
        {
            capture = factory();

            var chainFactory = kind == AudioSourceKind.Microphone
                ? () => AudioProcessorChain.ForMicrophone(SampleRate, _settings)
                : (Func<AudioProcessorChain>)(() => AudioProcessorChain.ForSystemAudio(SampleRate, _settings));

            var source = new AudioSource(kind, label, capture, BufferDuration, SampleRate, Channels, chainFactory);
            capture.StartRecording();
            _sources.Add(source);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"{label} could not be captured.");
            _failures.Add($"{label} is unavailable.");
            try { capture?.Dispose(); } catch { }
        }
    }

    private IWaveIn CreateLoopbackCapture()
    {
        var device = AudioDeviceEnumerator.GetDevice(_settings.Audio.SystemAudioDeviceId, DataFlow.Render);
        return new WasapiLoopbackCapture(device);
    }

    private IWaveIn CreateMicrophoneCapture()
    {
        var device = AudioDeviceEnumerator.GetDevice(_settings.Audio.MicrophoneDeviceId, DataFlow.Capture);
        return new WasapiCapture(device);
    }

    /// <summary>
    /// Discards everything queued beyond <paramref name="keep"/>.
    /// </summary>
    /// <remarks>
    /// Called when the recording clock starts and after a resume. Leaving a small cushion rather
    /// than emptying the buffers outright means the mixer never has to invent silence to cover
    /// ordinary WASAPI jitter; the price is a fixed audio delay equal to the cushion, which is well
    /// under the threshold where a viewer notices lip-sync error.
    /// </remarks>
    public void TrimBuffers(TimeSpan keep)
    {
        foreach (var source in _sources) source.Trim(keep);
    }

    /// <summary>Empties every queue. Used while paused so paused audio never reaches the file.</summary>
    public void ClearBuffers()
    {
        foreach (var source in _sources) source.Clear();
    }

    /// <summary>Mutes or unmutes one kind of source. Silently does nothing if it is not open.</summary>
    public void SetMuted(AudioSourceKind kind, bool muted)
    {
        foreach (var source in _sources)
            if (source.Kind == kind)
                source.IsMuted = muted;
    }

    public void Stop()
    {
        foreach (var source in _sources)
        {
            try { source.Capture.StopRecording(); }
            catch (Exception ex) { Log.Warn(ex, $"Stopping {source.Label} failed."); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        foreach (var source in _sources)
        {
            try { source.Dispose(); } catch (Exception ex) { Log.Warn(ex, $"Disposing {source.Label} failed."); }
        }
        _sources.Clear();
    }
}

/// <summary>One capture device, resampled and cleaned up into the mixer's common format.</summary>
public sealed class AudioSource : IDisposable
{
    private readonly BufferedWaveProvider _buffer;
    private readonly DspSampleProvider _dsp;
    private readonly object _gate = new();
    private volatile bool _muted;

    public AudioSourceKind Kind { get; }

    public string Label { get; }

    public IWaveIn Capture { get; }

    /// <summary>Resampled, cleaned, interleaved. Reads always return the full requested count.</summary>
    public ISampleProvider Output => _dsp;

    /// <summary>
    /// Whether this source is silenced in the recording.
    /// </summary>
    /// <remarks>
    /// Capture keeps running while muted. Stopping it instead would mean the device had to be
    /// reopened on unmute, which takes long enough to be heard as a gap, and would leave the source
    /// buffer to be re-primed mid-recording.
    /// </remarks>
    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            _dsp.SetMuted(value);
        }
    }

    public AudioSource(
        AudioSourceKind kind,
        string label,
        IWaveIn capture,
        TimeSpan bufferDuration,
        int sampleRate,
        int channels,
        Func<AudioProcessorChain> chainFactory)
    {
        Kind = kind;
        Label = label;
        Capture = capture;

        _buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = bufferDuration,
            // A stalled mixer must not be able to grow memory without bound; dropping the oldest
            // audio is the correct trade for a recorder.
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        _dsp = BuildChain(_buffer, sampleRate, channels, chainFactory);

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
    }

    private static DspSampleProvider BuildChain(
        BufferedWaveProvider buffer, int sampleRate, int channels, Func<AudioProcessorChain> chainFactory)
    {
        ISampleProvider provider = buffer.ToSampleProvider();

        var sourceChannels = buffer.WaveFormat.Channels;
        var sourceWasMono = sourceChannels == 1;

        if (sourceChannels == 1 && channels > 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (sourceChannels > 1 && channels == 1)
        {
            provider = new StereoToMonoSampleProvider(provider);
        }
        else if (sourceChannels > channels)
        {
            // Surround devices exist; take the front pair rather than refusing to record.
            var multiplexer = new MultiplexingSampleProvider([provider], channels);
            for (var i = 0; i < channels; i++) multiplexer.ConnectInputToOutput(i, i);
            provider = multiplexer;
        }

        if (provider.WaveFormat.SampleRate != sampleRate)
            provider = new WdlResamplingSampleProvider(provider, sampleRate);

        return new DspSampleProvider(provider, chainFactory, sourceWasMono);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        try
        {
            lock (_gate) _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, $"Dropping an audio packet from {Label}.");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Log.Error(e.Exception, $"{Label} capture stopped unexpectedly.");
    }

    /// <summary>Reads exactly <paramref name="count"/> samples, padding with silence if needed.</summary>
    public int Read(float[] destination, int offset, int count)
    {
        // Mute is applied inside the chain, as a ramp, so that it does not click.
        lock (_gate) return Output.Read(destination, offset, count);
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { _buffer.ClearBuffer(); } catch (Exception ex) { Log.Warn(ex, $"Clearing {Label} failed."); }
        }
    }

    /// <summary>Drops queued audio older than <paramref name="keep"/>.</summary>
    public void Trim(TimeSpan keep)
    {
        lock (_gate)
        {
            try
            {
                var excess = _buffer.BufferedDuration - keep;
                if (excess <= TimeSpan.Zero) return;

                // BufferedWaveProvider has no seek, so read the excess away and discard it.
                var bytes = (int)(excess.TotalSeconds * _buffer.WaveFormat.AverageBytesPerSecond);
                bytes -= bytes % _buffer.WaveFormat.BlockAlign;
                if (bytes <= 0) return;

                var scratch = new byte[Math.Min(bytes, 1 << 16)];
                var remaining = bytes;
                while (remaining > 0)
                {
                    var chunk = Math.Min(remaining, scratch.Length);
                    var read = _buffer.Read(scratch, 0, chunk);
                    if (read <= 0) break;
                    remaining -= read;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, $"Trimming {Label} failed.");
            }
        }
    }

    public void Dispose()
    {
        try { Capture.DataAvailable -= OnDataAvailable; } catch { }
        try { Capture.RecordingStopped -= OnRecordingStopped; } catch { }
        try { Capture.Dispose(); } catch { }
    }
}
