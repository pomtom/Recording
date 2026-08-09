using System.Diagnostics;
using Recorder.Capture;
using Recorder.Encoding;
using Recorder.Utils;

namespace Recorder.Core;

/// <summary>
/// One recording, from the moment capture opens until the MP4 is closed.
/// </summary>
/// <remarks>
/// The session owns the frame pacer — the thread that decides when a frame is written and is
/// therefore what makes the output constant-frame-rate. Windows Graphics Capture only delivers a
/// frame when the screen changes, so the pacer repeats the last captured frame whenever nothing
/// has happened. It never <em>skips</em> a frame: in a raw CFR stream every frame occupies exactly
/// 1/fps of the timeline, so a skipped frame would shorten the video relative to the audio and
/// desync the two. If the encoder falls behind, the pacer catches up by writing the missed frames
/// as fast as it can rather than dropping them.
/// </remarks>
public sealed class RecordingSession : IAsyncDisposable
{
    /// <summary>Backlog beyond which the encoder is clearly not keeping up.</summary>
    private static readonly TimeSpan BacklogWarningThreshold = TimeSpan.FromSeconds(2);

    /// <summary>How long the drain step waits for the pacer and mixer to reach the final position.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly RecordingRequest _request;
    private readonly RecordingClock _clock = new();
    private readonly CancellationTokenSource _cts = new();

    private ScreenCaptureService? _capture;
    private AudioCaptureService? _audio;
    private AudioMixer? _mixer;
    private FFmpegEncoder? _encoder;

    private Thread? _pacerThread;
    private byte[] _frameBuffer = [];
    private long _framesWritten;
    private bool _backlogReported;
    private bool _timerResolutionRaised;

    private int _failureRaised;
    private bool _disposed;

    private RecordingSession(RecordingRequest request) => _request = request;

    /// <summary>Raised once when the recording cannot continue. The message is user-facing.</summary>
    public event EventHandler<string>? Failed;

    public TimeSpan Elapsed => _clock.Elapsed;

    public bool IsPaused => _clock.IsPaused;

    public string FinalPath => _request.FinalPath;

    public string PartPath => _request.PartPath;

    /// <summary>Description of the active encoder, for the UI.</summary>
    public string EncoderDescription { get; private set; } = string.Empty;

    /// <summary>Video dimensions actually being written.</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Non-null when some audio source could not be opened.</summary>
    public string? AudioWarning { get; private set; }

    /// <summary>
    /// Opens capture, audio and the encoder. The timeline does not start until
    /// <see cref="BeginTimeline"/> is called, so a countdown can run against a warm pipeline.
    /// </summary>
    public static async Task<RecordingSession> StartAsync(RecordingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = new RecordingSession(request);
        try
        {
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Capture first: its negotiated output size and pixel format determine the encoder spec.
        _capture = new ScreenCaptureService();
        _capture.CaptureLost += OnCaptureLost;
        _capture.Start(_request.Monitor, _request.TargetHeight, _request.CaptureCursor, _request.SuppressCaptureBorder);

        Width = _capture.TargetWidth;
        Height = _capture.TargetHeight;

        var spec = new EncodeSpec
        {
            Width = _capture.OutputWidth,
            Height = _capture.OutputHeight,
            EncodedWidth = _capture.TargetWidth,
            EncodedHeight = _capture.TargetHeight,
            Fps = _request.Fps,
            PixelFormat = _capture.PixelFormat,
            PartPath = _request.PartPath,
            AudioBitrateKbps = _request.AudioBitrateKbps,
            AudioSampleRate = _request.Settings.Audio.SampleRate,
            AudioChannels = _request.Settings.Audio.Channels,
            Quality = _request.Quality,
            MaxBitrateBps = _request.MaxBitrateBps,
            // Replaced per attempt inside FFmpegEncoder.StartAsync.
            VideoPipeName = "pomrec-v",
            AudioPipeName = "pomrec-a",
        };

        _frameBuffer = new byte[spec.FrameBytes];

        _encoder = await FFmpegEncoder.StartAsync(
            _request.FFmpegPath, spec, _request.Encoder, cancellationToken).ConfigureAwait(false);

        EncoderDescription = EncoderProbe.FriendlyName(_encoder.ActiveEncoder);

        _audio = new AudioCaptureService(_request.Settings);
        _audio.Start(_request.RecordSystemAudio, _request.RecordMicrophone);
        AudioWarning = _audio.FailureSummary;

        _mixer = new AudioMixer(_audio, _clock, WriteAudio);
        _mixer.WriteFailed += (_, _) => RaiseFailure("The recording stopped because the encoder closed unexpectedly.");
    }

    /// <summary>
    /// Starts the clock and the writer threads. Call once, after any countdown.
    /// </summary>
    /// <remarks>
    /// The pacer starts first and the mixer follows once ffmpeg has opened its audio input, which
    /// it only does after video frames start arriving. The mixer losing that head start costs
    /// nothing: it positions itself from the clock, so its first tick simply emits everything due
    /// since zero.
    /// </remarks>
    public void BeginTimeline()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pacerThread is not null) return;

        // The pacer needs finer sleep granularity than the default 15.6 ms scheduler tick.
        try
        {
            NativeMethods.TimeBeginPeriod(1);
            _timerResolutionRaised = true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not raise the timer resolution; frame pacing may be less even.");
        }

        // Drop the audio queued during the countdown, keeping only the cushion the mixer needs.
        _audio!.TrimBuffers(AudioMixer.StartupCushion);

        _clock.Start();

        _pacerThread = new Thread(RunPacer)
        {
            IsBackground = true,
            Name = "Recorder.FramePacer",
            Priority = ThreadPriority.AboveNormal,
        };
        _pacerThread.Start();

        _ = StartMixerWhenAudioReadyAsync();
    }

    private async Task StartMixerWhenAudioReadyAsync()
    {
        try
        {
            await _encoder!.WaitForAudioAsync(_cts.Token).ConfigureAwait(false);
            if (_cts.IsCancellationRequested) return;

            _mixer!.Start();
        }
        catch (OperationCanceledException)
        {
            // The recording was stopped before ffmpeg got that far; nothing to do.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The audio input never opened.");
            RaiseFailure("The recording stopped because audio could not be started.");
        }
    }

    public void Pause()
    {
        if (_clock.IsPaused) return;
        _clock.Pause();
    }

    public void SetMuted(AudioSourceKind kind, bool muted) => _audio?.SetMuted(kind, muted);

    public void Resume()
    {
        if (!_clock.IsPaused) return;
        _clock.Resume();
    }

    private void RunPacer()
    {
        var token = _cts.Token;
        var frameInterval = 1.0 / _request.Fps;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_clock.IsPaused)
                {
                    token.WaitHandle.WaitOne(5);
                    continue;
                }

                var index = Interlocked.Read(ref _framesWritten);

                if (!_clock.IsRunning)
                {
                    // The clock has stopped, so Elapsed is frozen. Finish exactly the number of
                    // frames the final duration calls for and exit. Deriving the target from the
                    // stopped clock rather than from a value handed in by the stopping thread
                    // removes the window in which the pacer could race past it.
                    if (index >= FinalFrameCount()) return;
                    if (!WriteOneFrame()) return;
                    continue;   // no waiting: catch up immediately
                }

                var dueSeconds = index * frameInterval;
                var lead = dueSeconds - _clock.Elapsed.TotalSeconds;

                if (lead > 0)
                {
                    WaitPrecisely(lead, token);
                    if (token.IsCancellationRequested) return;
                }
                else
                {
                    ReportBacklogIfSevere(-lead);
                }

                if (!WriteOneFrame()) return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The frame pacer stopped unexpectedly.");
            RaiseFailure("The recording stopped because of an internal capture error.");
        }
    }

    /// <summary>Frames the video track must contain to match the final clock reading.</summary>
    private long FinalFrameCount() => (long)Math.Round(_clock.Elapsed.TotalSeconds * _request.Fps);

    private bool WriteOneFrame()
    {
        // No frame captured yet (the screen has not changed since capture opened): send the
        // buffer as-is. It is zeroed, which reads as one black frame rather than a stall.
        _capture!.TryCopyLatestFrame(_frameBuffer);

        if (!_encoder!.WriteVideoFrame(_frameBuffer))
        {
            RaiseFailure("The recording stopped because the encoder closed unexpectedly.");
            return false;
        }

        Interlocked.Increment(ref _framesWritten);
        return true;
    }

    /// <summary>
    /// Sleeps until <paramref name="seconds"/> have passed, coarsely at first then spinning.
    /// </summary>
    /// <remarks>
    /// Even at 1 ms timer resolution a plain Sleep overshoots by enough to visibly jitter a 60 FPS
    /// stream, so the last millisecond is spun out. Spinning for longer would waste a core;
    /// sleeping the whole way would drift.
    /// </remarks>
    private static void WaitPrecisely(double seconds, CancellationToken token)
    {
        const double spinThreshold = 0.0015;

        if (seconds > spinThreshold)
        {
            var coarse = (int)((seconds - spinThreshold) * 1000);
            if (coarse > 0) token.WaitHandle.WaitOne(coarse);
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        var spinner = new SpinWait();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (token.IsCancellationRequested) return;
            spinner.SpinOnce(-1);
        }
    }

    private void ReportBacklogIfSevere(double behindSeconds)
    {
        if (_backlogReported || behindSeconds < BacklogWarningThreshold.TotalSeconds) return;

        _backlogReported = true;
        Log.Error($"The encoder is {behindSeconds:0.0}s behind real time. The recording stays in sync " +
                  "but finalizing will take longer. Consider a lower resolution or frame rate.");
    }

    private bool WriteAudio(ReadOnlyMemory<byte> pcm) => _encoder?.WriteAudio(pcm.Span) ?? false;

    private void OnCaptureLost(object? sender, EventArgs e) =>
        RaiseFailure("The recording stopped because the display was disconnected.");

    private void RaiseFailure(string message)
    {
        // Only the first failure is interesting; the rest are consequences of it.
        if (Interlocked.Exchange(ref _failureRaised, 1) != 0) return;

        Log.Error(message);
        try { Failed?.Invoke(this, message); }
        catch (Exception ex) { Log.Warn(ex, "A recording failure handler threw."); }
    }

    /// <summary>
    /// Stops the timeline, lets both streams reach the same end position, and closes the encoder.
    /// </summary>
    /// <returns>True if ffmpeg finished cleanly.</returns>
    public async Task<bool> CompleteAsync()
    {
        if (_disposed) return false;

        // Freezing the clock is the stop signal: both writer threads read it, emit whatever the
        // final duration still owes, and exit on their own.
        _clock.Stop();

        await DrainAsync().ConfigureAwait(false);

        // Stop feeding before closing the pipes so no writer races the shutdown.
        _cts.Cancel();
        JoinPacer();

        try { _audio?.Stop(); } catch (Exception ex) { Log.Warn(ex, "Stopping audio capture failed."); }
        _mixer?.Dispose();
        _mixer = null;

        try { _capture?.Dispose(); } catch (Exception ex) { Log.Warn(ex, "Disposing the capture service failed."); }
        _capture = null;

        var encoder = _encoder;
        if (encoder is null) return false;

        return await encoder.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>Waits for both writer threads to finish emitting the tail of the recording.</summary>
    private async Task DrainAsync()
    {
        var frameTarget = FinalFrameCount();
        var deadline = DateTime.UtcNow + DrainTimeout;

        // The pacer exits once it reaches the final frame count.
        while (DateTime.UtcNow < deadline)
        {
            var pacer = _pacerThread;
            if (pacer is null || !pacer.IsAlive) break;
            if (_encoder?.IsBroken == true) return;

            await Task.Delay(15).ConfigureAwait(false);
        }

        // A mixer that never started (ffmpeg's audio input never opened) has nothing to drain.
        var mixer = _mixer;
        if (mixer is not null && mixer.IsRunning)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            if (!mixer.WaitForDrain(remaining))
            {
                Log.Warn($"The audio mixer did not drain in time ({mixer.FramesWritten} samples written). " +
                         "The tail of the recording may be slightly short.");
            }
        }

        var written = Interlocked.Read(ref _framesWritten);
        if (written < frameTarget)
        {
            Log.Warn($"Draining timed out: video {written}/{frameTarget} frames. " +
                     "The tail of the recording may be slightly short.");
        }
    }

    private void JoinPacer()
    {
        var thread = _pacerThread;
        _pacerThread = null;
        if (thread is null || !thread.IsAlive) return;

        if (!thread.Join(TimeSpan.FromSeconds(3)))
            Log.Warn("The frame pacer did not stop in time.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        JoinPacer();

        if (_capture is not null)
        {
            try { _capture.CaptureLost -= OnCaptureLost; } catch { }
        }

        try { _mixer?.Dispose(); } catch (Exception ex) { Log.Warn(ex, "Disposing the mixer failed."); }
        try { _audio?.Dispose(); } catch (Exception ex) { Log.Warn(ex, "Disposing audio capture failed."); }
        try { _capture?.Dispose(); } catch (Exception ex) { Log.Warn(ex, "Disposing the capture service failed."); }

        if (_encoder is not null)
        {
            try { await _encoder.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn(ex, "Disposing the encoder failed."); }
        }

        _mixer = null;
        _audio = null;
        _capture = null;
        _encoder = null;

        if (_timerResolutionRaised)
        {
            try { NativeMethods.TimeEndPeriod(1); } catch { }
            _timerResolutionRaised = false;
        }

        try { _cts.Dispose(); } catch { }
    }
}
