using Recorder.Capture.Dsp;
using Recorder.Core;
using Recorder.Utils;

namespace Recorder.Capture;

/// <summary>
/// Mixes every audio source into the single interleaved 16-bit stream ffmpeg consumes.
/// </summary>
/// <remarks>
/// <para>The mixer is <em>clock-driven</em>, not data-driven. On every tick it works out how many
/// samples should exist by now — <c>Elapsed × 48000</c> — and produces exactly that many. It never
/// emits more because a source delivered a burst, and never fewer because a source went quiet.
/// Combined with the frame pacer reading the same clock, that is what keeps audio and video locked
/// together over a multi-hour recording instead of slowly drifting apart.</para>
///
/// <para>Pausing needs no special handling in the arithmetic: the clock stops advancing, the sample
/// target stops moving, and nothing is written. The source buffers are emptied meanwhile so audio
/// captured during the pause never reaches the file.</para>
/// </remarks>
public sealed class AudioMixer : IDisposable
{
    /// <summary>Mixer wake interval. Short enough to keep the pipe fed, long enough to stay cheap.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(10);

    private readonly int _sampleRate;
    private readonly int _channels;

    /// <summary>Upper bound on one tick's work (250 ms), so a stalled thread cannot burst.</summary>
    private readonly int _maxFramesPerTick;

    /// <summary>
    /// Audio deliberately left queued when the clock starts.
    /// </summary>
    /// <remarks>
    /// Without a cushion the first few ticks would outrun WASAPI's ~10 ms delivery cadence and pad
    /// with silence, producing an audible blip and a ragged start. The cushion costs a fixed audio
    /// delay of this length, far below the ~125 ms at which lip-sync error becomes noticeable.
    /// </remarks>
    public static readonly TimeSpan StartupCushion = TimeSpan.FromMilliseconds(50);

    private readonly AudioCaptureService _capture;
    private readonly RecordingClock _clock;
    private readonly Func<ReadOnlyMemory<byte>, bool> _writer;

    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    private float[] _mixBuffer = [];
    private float[] _sourceBuffer = [];
    private byte[] _pcmBuffer = [];

    private long _framesWritten;
    private bool _disposed;

    /// <summary>
    /// True once the queues hold a usable cushion.
    /// </summary>
    /// <remarks>
    /// Starts true because the session trims the queues just before the clock starts. The mixer
    /// itself only comes up once ffmpeg has opened its audio input, a little after that, and the
    /// audio buffered in between is exactly what covers the gap — re-trimming here would throw it
    /// away and force the opening moments to be padded with silence. After a pause the queues are
    /// deliberately emptied, so the cushion has to be re-established on resume.
    /// </remarks>
    private bool _primed = true;

    /// <summary>Raised once if writing to the encoder fails. The session treats this as fatal.</summary>
    public event EventHandler? WriteFailed;

    public AudioMixer(AudioCaptureService capture, RecordingClock clock, Func<ReadOnlyMemory<byte>, bool> writer)
    {
        _capture = capture;
        _clock = clock;
        _writer = writer;

        _sampleRate = capture.SampleRate;
        _channels = capture.Channels;
        _maxFramesPerTick = _sampleRate / 4;
    }

    /// <summary>Audio samples (per channel) written so far.</summary>
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    /// <summary>False until ffmpeg's audio input has opened and the mixer thread has been started.</summary>
    public bool IsRunning => _thread is not null;

    /// <summary>
    /// Waits for the mixer to finish emitting the tail after the clock stops.
    /// </summary>
    /// <returns>False if it did not finish in time.</returns>
    public bool WaitForDrain(TimeSpan timeout)
    {
        var thread = _thread;
        return thread is null || !thread.IsAlive || thread.Join(timeout);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Recorder.AudioMixer",
            // Above normal so ordinary UI work cannot starve the mixer and cause dropouts,
            // but not realtime, which would risk making the machine unresponsive.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private void Run()
    {
        var sawRunning = false;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!_clock.IsRunning)
                {
                    if (!sawRunning)
                    {
                        // Countdown or not started yet: keep the queues from filling up.
                        _capture.ClearBuffers();
                        Sleep();
                        continue;
                    }

                    // The clock has stopped, so Elapsed is frozen: emit the samples still owed for
                    // the final moments, then finish. Without this the audio track ends fractionally
                    // shorter than the video and the two drift apart at the very end.
                    if (Interlocked.Read(ref _framesWritten) >= DueFrames()) return;
                    if (!PumpOnce()) return;
                    continue;   // no sleep: catch up immediately
                }

                sawRunning = true;

                if (_clock.IsPaused)
                {
                    // Audio captured while paused must not appear in the output.
                    _capture.ClearBuffers();
                    _primed = false;   // re-establish the cushion on resume
                    Sleep();
                    continue;
                }

                if (!_primed)
                {
                    _capture.TrimBuffers(StartupCushion);
                    _primed = true;
                }

                if (!PumpOnce()) return;

                Sleep();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The audio mixer thread stopped unexpectedly.");
            RaiseWriteFailed();
        }
    }

    /// <summary>Total samples per channel that should exist by the current clock reading.</summary>
    private long DueFrames() => (long)(_clock.Elapsed.TotalSeconds * _sampleRate);

    /// <summary>Emits however many samples the clock says are now due. Returns false to stop the thread.</summary>
    private bool PumpOnce()
    {
        var deficit = DueFrames() - Interlocked.Read(ref _framesWritten);
        if (deficit <= 0) return true;

        var frames = (int)Math.Min(deficit, _maxFramesPerTick);
        EnsureCapacity(frames);

        var samples = frames * _channels;
        Array.Clear(_mixBuffer, 0, samples);

        foreach (var source in _capture.Sources)
        {
            try
            {
                // ReadFully means this always returns `samples`, padding silence when the source is
                // idle — the property that stops a quiet microphone from shortening the timeline.
                var read = source.Read(_sourceBuffer, 0, samples);
                for (var i = 0; i < read; i++) _mixBuffer[i] += _sourceBuffer[i];
            }
            catch (Exception ex)
            {
                Log.Warn(ex, $"Reading from {source.Label} failed; treating it as silence.");
            }
        }

        var bytes = samples * sizeof(short);
        ConvertToPcm16(_mixBuffer, _pcmBuffer, samples);

        if (!_writer(_pcmBuffer.AsMemory(0, bytes)))
        {
            RaiseWriteFailed();
            return false;
        }

        Interlocked.Add(ref _framesWritten, frames);
        return true;
    }

    private void EnsureCapacity(int frames)
    {
        var samples = frames * _channels;
        if (_mixBuffer.Length >= samples) return;

        _mixBuffer = new float[samples];
        _sourceBuffer = new float[samples];
        _pcmBuffer = new byte[samples * sizeof(short)];
    }

    /// <summary>
    /// Converts the float mix to little-endian 16-bit PCM, soft-clipping the loud parts.
    /// </summary>
    /// <remarks>
    /// Summing two full-scale sources can exceed ±1.0, and hard clipping there produces harsh
    /// distortion. <see cref="SoftClip"/> curves the overshoot into the ceiling instead; it is the
    /// same curve each source's gain stage uses, so the two cannot disagree about what full scale
    /// sounds like.
    /// </remarks>
    private static void ConvertToPcm16(float[] source, byte[] destination, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var value = SoftClip.Apply(source[i]);

            var scaled = (int)MathF.Round(value * short.MaxValue);
            var sample = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);

            var offset = i * 2;
            destination[offset] = (byte)(sample & 0xFF);
            destination[offset + 1] = (byte)((sample >> 8) & 0xFF);
        }
    }

    private void Sleep()
    {
        try { _cts.Token.WaitHandle.WaitOne(TickInterval); }
        catch (ObjectDisposedException) { }
    }

    private void RaiseWriteFailed()
    {
        try { WriteFailed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Warn(ex, "WriteFailed handler threw."); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }

        var thread = _thread;
        _thread = null;
        if (thread is not null && thread.IsAlive)
        {
            // The loop wakes at least every tick, so this returns almost immediately.
            if (!thread.Join(TimeSpan.FromSeconds(2)))
                Log.Warn("The audio mixer thread did not stop in time.");
        }

        try { _cts.Dispose(); } catch { }
    }
}
