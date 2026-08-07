using System.Diagnostics;

namespace Recorder.Core;

/// <summary>
/// The single source of truth for "how far into the recording are we".
/// </summary>
/// <remarks>
/// This is the mechanism that keeps audio and video in sync. The frame pacer emits frame <c>n</c>
/// when <see cref="Elapsed"/> reaches <c>n / fps</c>, and the audio mixer emits exactly
/// <c>Elapsed × 48000</c> samples. Because both streams are positioned from this one clock rather
/// than from their own arrival rates, they cannot drift apart no matter how long the recording runs
/// or how erratically the capture sources deliver data.
/// <para>
/// Paused time is excluded, so a pause is invisible in the output: the recording simply continues
/// from where it left off.
/// </para>
/// </remarks>
public sealed class RecordingClock
{
    private readonly Stopwatch _stopwatch = new();
    private readonly object _gate = new();
    private TimeSpan _pausedTotal = TimeSpan.Zero;
    private TimeSpan _pauseStartedAt = TimeSpan.Zero;
    private bool _paused;

    /// <summary>Recorded time so far, excluding any paused stretches.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                if (!_stopwatch.IsRunning) return _stopwatch.Elapsed - _pausedTotal;
                var raw = _stopwatch.Elapsed;
                var paused = _paused ? _pausedTotal + (raw - _pauseStartedAt) : _pausedTotal;
                return raw - paused;
            }
        }
    }

    public bool IsRunning => _stopwatch.IsRunning;

    public bool IsPaused { get { lock (_gate) return _paused; } }

    public void Start()
    {
        lock (_gate)
        {
            _stopwatch.Restart();
            _pausedTotal = TimeSpan.Zero;
            _paused = false;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused || !_stopwatch.IsRunning) return;
            _pauseStartedAt = _stopwatch.Elapsed;
            _paused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _pausedTotal += _stopwatch.Elapsed - _pauseStartedAt;
            _paused = false;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_paused)
            {
                _pausedTotal += _stopwatch.Elapsed - _pauseStartedAt;
                _paused = false;
            }
            _stopwatch.Stop();
        }
    }
}
