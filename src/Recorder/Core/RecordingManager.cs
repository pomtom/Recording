using System.IO;
using Recorder.Capture;
using Recorder.Encoding;
using Recorder.Recovery;
using Recorder.Settings;
using Recorder.Utils;

namespace Recorder.Core;

public sealed class RecordingStateChangedEventArgs(RecorderState state) : EventArgs
{
    public RecorderState State { get; } = state;
}

public sealed class RecordingCompletedEventArgs(string path, TimeSpan duration, string? warning) : EventArgs
{
    public string Path { get; } = path;
    public TimeSpan Duration { get; } = duration;
    public string? Warning { get; } = warning;
}

/// <summary>
/// The single entry point for starting, pausing and stopping a recording.
/// </summary>
/// <remarks>
/// Commands arrive from four independent places — the window, the tray menu, global hotkeys and
/// power events — and any of them can fire at any moment, including while a recording is being
/// finalized. Every transition therefore runs under one semaphore and is validated against the
/// current <see cref="RecorderState"/>, so a double-tapped hotkey or a lock screen firing during
/// shutdown cannot start two recordings or tear one down twice.
/// </remarks>
public sealed class RecordingManager : IAsyncDisposable
{
    private readonly SettingsManager _settings;
    private readonly FFmpegProvisioner _provisioner;
    private readonly EncoderProbe _encoderProbe;
    private readonly Mp4Finalizer _finalizer;
    private readonly CameraController? _cameras;

    private readonly SemaphoreSlim _commandLock = new(1, 1);

    private RecordingSession? _session;
    private string? _journalId;
    private RecorderState _state = RecorderState.Idle;
    private bool _disposed;

    /// <param name="cameras">
    /// The camera bubble, or null when there is none. Optional because a recording is complete
    /// without one — every camera failure has to leave the screen recording running, and the
    /// simplest expression of that is a manager that never required a camera in the first place.
    /// </param>
    public RecordingManager(
        SettingsManager settings,
        FFmpegProvisioner provisioner,
        EncoderProbe encoderProbe,
        Mp4Finalizer finalizer,
        CameraController? cameras = null)
    {
        _settings = settings;
        _provisioner = provisioner;
        _encoderProbe = encoderProbe;
        _finalizer = finalizer;
        _cameras = cameras;
    }

    public RecorderState State => _state;

    public TimeSpan Elapsed => _session?.Elapsed ?? TimeSpan.Zero;

    /// <summary>Human-readable encoder name once a recording is running.</summary>
    public string? EncoderDescription => _session?.EncoderDescription;

    /// <summary>Video dimensions of the running recording, or null when idle.</summary>
    public (int Width, int Height)? Dimensions =>
        _session is null ? null : (_session.Width, _session.Height);

    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when a recording lands on disk.</summary>
    public event EventHandler<RecordingCompletedEventArgs>? Completed;

    /// <summary>Raised with a user-facing message when something goes wrong.</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Raised when the countdown should be shown. The handler must return once the countdown has
    /// finished, and may return early if the recording was cancelled meanwhile.
    /// </summary>
    public Func<int, CancellationToken, Task>? CountdownHandler { get; set; }

    // ---------------------------------------------------------------- mute

    /// <summary>
    /// Whether the microphone is currently silenced in the recording.
    /// </summary>
    /// <remarks>
    /// The manager owns this rather than any one piece of UI. Mute can be changed from the window,
    /// the tray menu or a global hotkey, and all three have to agree — a state that lives in
    /// whichever control happened to be clicked last cannot give that guarantee.
    /// </remarks>
    public bool IsMicrophoneMuted { get; private set; }

    public bool IsSystemAudioMuted { get; private set; }

    /// <summary>Raised whenever either source's mute state changes, on the caller's thread.</summary>
    public event EventHandler? MuteChanged;

    public bool IsMuted(AudioSourceKind kind) =>
        kind == AudioSourceKind.Microphone ? IsMicrophoneMuted : IsSystemAudioMuted;

    /// <summary>Mutes or unmutes one source for the active recording.</summary>
    public void SetMuted(AudioSourceKind kind, bool muted)
    {
        if (IsMuted(kind) == muted) return;

        if (kind == AudioSourceKind.Microphone) IsMicrophoneMuted = muted;
        else IsSystemAudioMuted = muted;

        _session?.SetMuted(kind, muted);
        RaiseMuteChanged();
    }

    public void ToggleMute(AudioSourceKind kind) => SetMuted(kind, !IsMuted(kind));

    /// <summary>Clears both mute flags. Called as each recording starts.</summary>
    private void ResetMute()
    {
        if (!IsMicrophoneMuted && !IsSystemAudioMuted) return;

        IsMicrophoneMuted = false;
        IsSystemAudioMuted = false;
        RaiseMuteChanged();
    }

    private void RaiseMuteChanged()
    {
        try { MuteChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Warn(ex, "A MuteChanged handler threw."); }
    }

    // ---------------------------------------------------------------- commands

    public async Task StartAsync()
    {
        if (!await _commandLock.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
        {
            Log.Warn("Start ignored: another command is still in progress.");
            return;
        }

        try
        {
            if (!_state.CanStart())
            {
                Log.Warn($"Start ignored in state {_state}.");
                return;
            }

            await StartCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Starting a recording failed.");
            await AbortAsync(DescribeStartFailure(ex)).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        // A snapshot, so editing settings mid-recording cannot change a capture already running.
        var settings = _settings.Current.Clone();

        ResetMute();

        var monitor = MonitorEnumerator.Select(settings.MonitorDeviceId)
            ?? throw new InvalidOperationException("No display was found to record.");

        // Probe the folder now rather than at the moment ffmpeg tries to write, so the fallback
        // happens before the countdown instead of failing the recording three seconds in.
        var folder = OutputFolder.Resolve(settings.OutputFolder);
        if (folder.UsedFallback)
        {
            _settings.Update(s => s.OutputFolder = folder.Path);
            RaiseError($"'{settings.OutputFolder}' is not writable. Recordings will be saved to {folder.Path}.");
        }

        var finalPath = OutputFolder.BuildRecordingPath(
            folder.Path, DateTime.Now, settings.Naming.FilenameTemplate, monitor.FriendlyName);
        var partPath = finalPath + ".part";

        var encoder = _encoderProbe.Resolve(ParseEncoderOverride(settings.Video.EncoderOverride), out var encoderWarning);
        if (encoderWarning is not null) RaiseError(encoderWarning);

        // A bubble parked on another display would simply not appear in the recording, which looks
        // identical to the feature being broken. Move it before the geometry is sampled.
        _cameras?.EnsureOnMonitor(monitor);

        var request = new RecordingRequest
        {
            Monitor = monitor,
            TargetHeight = settings.EffectiveTargetHeight,
            Fps = settings.FPS,
            CaptureCursor = settings.CaptureCursor,
            SuppressCaptureBorder = settings.SuppressCaptureBorder,
            RecordSystemAudio = settings.RecordSystemAudio,
            RecordMicrophone = settings.RecordMicrophone,
            AudioBitrateKbps = settings.AudioBitrateKbps,
            Settings = settings,
            Quality = settings.Video.Quality,
            MaxBitrateBps = ResolveMaxBitrate(settings.Video),
            FinalPath = finalPath,
            PartPath = partPath,
            FFmpegPath = _provisioner.GetFFmpegPath(),
            Encoder = encoder,
            Overlay = _cameras?.Overlay,
        };

        SetState(RecorderState.CountingDown);

        _journalId = RecordingJournal.Write(partPath, finalPath);

        var session = await RecordingSession.StartAsync(request, CancellationToken.None).ConfigureAwait(false);
        session.Failed += OnSessionFailed;
        _session = session;

        if (session.AudioWarning is not null) RaiseError(session.AudioWarning);

        // The pipeline is already live, so the countdown costs nothing but the wait — the first
        // recorded frame is captured the instant the clock starts.
        if (settings.Countdown > 0 && CountdownHandler is not null)
        {
            try
            {
                await CountdownHandler(settings.Countdown, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "The countdown failed; starting the recording anyway.");
            }
        }

        // A stop or failure during the countdown leaves us out of CountingDown; do not start.
        if (_state != RecorderState.CountingDown)
        {
            Log.Warn("The recording was cancelled during the countdown.");
            return;
        }

        session.BeginTimeline();
        SetState(RecorderState.Recording);
    }

    /// <summary>Toggles between recording and paused.</summary>
    public async Task TogglePauseAsync()
    {
        if (!await _commandLock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)) return;

        try
        {
            var session = _session;
            if (session is null || !_state.CanPause())
            {
                Log.Warn($"Pause ignored in state {_state}.");
                return;
            }

            if (_state == RecorderState.Recording)
            {
                session.Pause();
                SetState(RecorderState.Paused);
            }
            else
            {
                session.Resume();
                SetState(RecorderState.Recording);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Pause/resume failed.");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task StopAsync()
    {
        if (!await _commandLock.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
        {
            Log.Warn("Stop ignored: another command is still in progress.");
            return;
        }

        try
        {
            if (!_state.CanStop())
            {
                Log.Warn($"Stop ignored in state {_state}.");
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Stopping the recording failed.");
            await AbortAsync("The recording could not be finalized cleanly. Check the log for details.")
                .ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        var session = _session;
        if (session is null)
        {
            SetState(RecorderState.Idle);
            return;
        }

        SetState(RecorderState.Finalizing);

        var duration = session.Elapsed;
        var partPath = session.PartPath;
        var finalPath = session.FinalPath;

        session.Failed -= OnSessionFailed;
        var encodedCleanly = await session.CompleteAsync().ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
        _session = null;

        if (!encodedCleanly)
            Log.Warn("ffmpeg did not exit cleanly; attempting to salvage the recording anyway.");

        var result = await _finalizer.FinalizeAsync(partPath, finalPath).ConfigureAwait(false);

        if (_journalId is not null)
        {
            RecordingJournal.Remove(_journalId);
            _journalId = null;
        }

        SetState(RecorderState.Idle);

        if (result.Success)
        {
            RaiseCompleted(result.Path, duration, result.Warning);
        }
        else
        {
            RaiseError(result.Warning ?? "The recording could not be saved.");
        }
    }

    /// <summary>
    /// Tears everything down after a failure, keeping whatever was already encoded.
    /// </summary>
    /// <remarks>
    /// The capture file is fragmented, so anything written before the failure is still valid video.
    /// Salvaging it is almost always better than deleting it.
    /// </remarks>
    private async Task AbortAsync(string message)
    {
        var session = _session;
        _session = null;

        string? partPath = null;
        string? finalPath = null;
        var duration = TimeSpan.Zero;

        if (session is not null)
        {
            session.Failed -= OnSessionFailed;
            partPath = session.PartPath;
            finalPath = session.FinalPath;
            duration = session.Elapsed;

            try { await session.CompleteAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn(ex, "Draining the failed session threw."); }

            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn(ex, "Disposing the failed session threw."); }
        }

        SetState(RecorderState.Idle);
        RaiseError(message);

        if (partPath is not null && finalPath is not null && File.Exists(partPath))
        {
            try
            {
                var result = await _finalizer.FinalizeAsync(partPath, finalPath).ConfigureAwait(false);
                if (result.Success) RaiseCompleted(result.Path, duration, "This recording was cut short.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not salvage the partial recording.");
            }
        }

        if (_journalId is not null)
        {
            RecordingJournal.Remove(_journalId);
            _journalId = null;
        }
    }

    private void OnSessionFailed(object? sender, string message)
    {
        // The failure arrives on a capture thread; hand it to a worker so the command lock can be
        // taken without blocking the thread that is reporting the problem.
        _ = Task.Run(async () =>
        {
            if (!await _commandLock.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false)) return;
            try
            {
                if (_state == RecorderState.Idle || _state == RecorderState.Finalizing) return;
                await AbortAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Handling a session failure threw.");
            }
            finally
            {
                _commandLock.Release();
            }
        });
    }

    /// <summary>The user's encoder choice, or null when they left it on Auto.</summary>
    private static VideoEncoder? ParseEncoderOverride(string? value) =>
        Enum.TryParse<VideoEncoder>(value, ignoreCase: true, out var encoder) ? encoder : null;

    /// <summary>
    /// The bitrate ceiling in bits per second, or 0 to let the frame height decide.
    /// </summary>
    /// <remarks>
    /// In quality mode the configured ceiling is deliberately ignored: it exists as a safety valve
    /// against a pathological scene, and letting a user's low ceiling silently override the quality
    /// target they just chose would make the quality slider look broken.
    /// </remarks>
    private static long ResolveMaxBitrate(VideoSettings video) =>
        video.QualityModeValue == VideoQualityMode.Bitrate && video.MaxBitrateKbps > 0
            ? video.MaxBitrateKbps * 1000L
            : 0;

    private static string DescribeStartFailure(Exception ex) => ex switch
    {
        FFmpegUnavailableException => ex.Message,
        NotSupportedException => ex.Message,
        UnauthorizedAccessException => "Access was denied while preparing the recording. Try a different save folder.",
        IOException io => $"The recording could not be started: {io.Message}",
        _ => "The recording could not be started. Check the log for details.",
    };

    private void SetState(RecorderState state)
    {
        if (_state == state) return;
        _state = state;

        try { StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(state)); }
        catch (Exception ex) { Log.Warn(ex, "A StateChanged handler threw."); }
    }

    private void RaiseError(string message)
    {
        try { ErrorOccurred?.Invoke(this, message); }
        catch (Exception ex) { Log.Warn(ex, "An ErrorOccurred handler threw."); }
    }

    private void RaiseCompleted(string path, TimeSpan duration, string? warning)
    {
        try { Completed?.Invoke(this, new RecordingCompletedEventArgs(path, duration, warning)); }
        catch (Exception ex) { Log.Warn(ex, "A Completed handler threw."); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Never lose a recording just because the app is closing.
        if (_state.IsActive())
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Error(ex, "Stopping during shutdown failed."); }
        }

        var session = _session;
        _session = null;
        if (session is not null)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        _commandLock.Dispose();
    }
}
