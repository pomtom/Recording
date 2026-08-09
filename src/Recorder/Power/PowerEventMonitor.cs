using Microsoft.Win32;
using Recorder.Utils;

namespace Recorder.Power;

/// <summary>Why the system asked the recording to stop.</summary>
public enum PowerStopReason
{
    Sleep,
    Lock,
    LogOff,
    Shutdown,
}

/// <summary>
/// Watches for the system events that make continuing a recording pointless or impossible.
/// </summary>
/// <remarks>
/// Sleep, lock and shutdown all end up freezing or blanking the display, so anything captured past
/// that point is worthless — and a shutdown that catches ffmpeg mid-write would leave an unfinalized
/// file. Stopping proactively turns all of those into ordinary, clean endings.
/// <para>
/// <see cref="SystemEvents"/> raises on a dedicated window thread, so handlers must not block; each
/// one hands off immediately. The exception is <see cref="SystemEvents.SessionEnding"/>, where
/// Windows genuinely is waiting on us and blocking briefly is the only way to finish the file.
/// </para>
/// </remarks>
public sealed class PowerEventMonitor : IDisposable
{
    /// <summary>How long shutdown handling may block before Windows loses patience.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(20);

    private readonly Func<PowerStopReason, Task> _stopAsync;
    private readonly Func<bool> _isRecording;
    private readonly Func<PowerStopReason, bool> _shouldStop;
    private bool _subscribed;
    private bool _disposed;

    /// <param name="shouldStop">
    /// Whether this particular event should end a recording. Each of the four is separately
    /// configurable because the reasons are not equally compelling: a shutdown genuinely has to be
    /// handled or the file is left unfinalized, while plenty of people lock their machine fully
    /// expecting a long capture to keep running.
    /// </param>
    public PowerEventMonitor(
        Func<bool> isRecording,
        Func<PowerStopReason, bool> shouldStop,
        Func<PowerStopReason, Task> stopAsync)
    {
        _isRecording = isRecording;
        _shouldStop = shouldStop;
        _stopAsync = stopAsync;
    }

    public void Start()
    {
        if (_subscribed || _disposed) return;

        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.SessionEnding += OnSessionEnding;
            _subscribed = true;
        }
        catch (Exception ex)
        {
            // Rare, but a service-like context can refuse these. The app still works; it just
            // will not auto-stop on sleep or lock.
            Log.Error(ex, "Could not subscribe to system power events.");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Suspend) return;
        RequestStop(PowerStopReason.Sleep, blocking: false);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var reason = e.Reason switch
        {
            SessionSwitchReason.SessionLock => (PowerStopReason?)PowerStopReason.Lock,
            SessionSwitchReason.SessionLogoff => PowerStopReason.LogOff,
            _ => null,
        };

        if (reason is not null) RequestStop(reason.Value, blocking: false);
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        // Windows is waiting for us here, so this one is worth blocking on: it is the difference
        // between a finalized MP4 and one that needs crash recovery on the next launch.
        RequestStop(
            e.Reason == SessionEndReasons.Logoff ? PowerStopReason.LogOff : PowerStopReason.Shutdown,
            blocking: true);
    }

    private void RequestStop(PowerStopReason reason, bool blocking)
    {
        try
        {
            if (!_isRecording()) return;

            if (!_shouldStop(reason))
            {
                Log.Warn($"A system {reason} event fired; leaving the recording running, as configured.");
                return;
            }

            Log.Warn($"Stopping the recording because of a system {reason} event.");

            var task = Task.Run(() => _stopAsync(reason));

            if (blocking && !task.Wait(ShutdownGrace))
                Log.Error("The recording did not finalize before the shutdown grace period expired.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Handling the {reason} event failed.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_subscribed) return;
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionEnding -= OnSessionEnding;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not unsubscribe from system power events.");
        }
        _subscribed = false;
    }
}
