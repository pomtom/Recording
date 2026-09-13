using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Linq;
using Recorder.Capture;
using Recorder.Core;
using Recorder.Encoding;
using Recorder.Hotkeys;
using Recorder.Overlay;
using Recorder.Power;
using Recorder.Recovery;
using Recorder.Settings;
using Recorder.Tray;
using Recorder.UI;
using Recorder.Utils;

namespace Recorder;

/// <summary>
/// Composition root: builds every service, wires the four command sources together and owns shutdown.
/// </summary>
/// <remarks>
/// <para><c>ShutdownMode</c> is <c>OnExplicitShutdown</c> because the app deliberately outlives its
/// main window — closing that window hides it to the tray. Shutdown happens only through
/// <see cref="ExitAsync"/>, which finalizes any recording in progress first.</para>
///
/// <para>Start, pause and stop can each be triggered from the window, the tray menu, a global
/// hotkey or a power event. All four funnel into the same <see cref="RecordingManager"/>, which
/// serialises them; nothing here needs to coordinate.</para>
/// </remarks>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\PomtomRecorder.SingleInstance";

    private Mutex? _instanceMutex;
    private SettingsManager _settings = null!;
    private FFmpegProvisioner _provisioner = null!;
    private EncoderProbe _encoderProbe = null!;
    private Mp4Finalizer _finalizer = null!;
    private RecordingManager _manager = null!;
    private GlobalHotkeyManager _hotkeys = null!;
    private TrayManager _tray = null!;
    private PowerEventMonitor _power = null!;
    private CameraController _cameras = null!;
    private MainWindow? _mainWindow;
    private RecordingOverlayWindow? _overlay;

    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ClaimSingleInstance())
        {
            // Another copy owns the tray icon and the hotkeys; a second one would fight it.
            Shutdown();
            return;
        }

        AppPaths.EnsureDirectories();
        Log.Initialize();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            BuildServices();
            BuildUi();

            // Provisioning ffmpeg and probing encoders touches ~100 MB of I/O on the very first
            // run. Doing it off the UI thread keeps startup inside the 2-second budget.
            _ = Task.Run(WarmUpAsync);

            // Enumerating cameras and opening one takes a few hundred milliseconds, but it has to
            // stay on the UI thread because it may put the bubble window on screen. Left unawaited
            // so it runs after startup returns rather than inside the budget.
            _ = Dispatcher.InvokeAsync(async () => await _cameras.InitializeAsync());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Startup failed.");
            MessageBox.Show(
                "Pomtom Recorder could not start.\n\n" + ex.Message,
                "Pomtom Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private bool ClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            return createdNew;
        }
        catch (Exception)
        {
            // An unavailable mutex (locked-down session) should not block the app entirely.
            return true;
        }
    }

    private void BuildServices()
    {
        _settings = new SettingsManager();
        _settings.Load();

        // Logging came up with defaults before this point, because Load itself reports a corrupt
        // settings file. Now that the user's preferences are known, apply them.
        Log.Reconfigure(_settings.Current.Logging);

        _provisioner = new FFmpegProvisioner();
        _encoderProbe = new EncoderProbe(_provisioner);
        _finalizer = new Mp4Finalizer(_provisioner);

        // Built before the manager because every recording is handed the camera overlay, and
        // app-lifetime because the bubble is live while the app is idle.
        _cameras = new CameraController(_settings);

        _manager = new RecordingManager(_settings, _provisioner, _encoderProbe, _finalizer, _cameras);
        _manager.StateChanged += OnStateChanged;
        _manager.Completed += OnRecordingCompleted;
        _manager.ErrorOccurred += OnRecordingError;
        _manager.CountdownHandler = RunCountdownAsync;

        _hotkeys = new GlobalHotkeyManager();
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.RegistrationFailed += (_, message) => OnUi(() => _tray?.ShowWarning("Hotkey unavailable", message));
        _hotkeys.Apply(BuildHotkeyMap(_settings.Current));

        _settings.Changed += (_, updated) => OnUi(() =>
        {
            _hotkeys.Apply(BuildHotkeyMap(updated));
            Log.Reconfigure(updated.Logging);
            StartupRegistration.Apply(updated.Behavior.StartWithWindows);
            _mainWindow?.RefreshSettingsText();
            _mainWindow?.ApplyState(_manager.State);
            _ = _cameras.ApplySettingsAsync(updated);
        });

        _power = new PowerEventMonitor(
            isRecording: () => _manager.State.IsActive(),
            shouldStop: ShouldStopFor,
            stopAsync: async reason =>
            {
                await _manager.StopAsync().ConfigureAwait(false);
                OnUi(() => _tray?.ShowInfo("Recording stopped", $"Saved because the system went to {reason}."));
            });
        _power.Start();

        StartupRegistration.Apply(_settings.Current.Behavior.StartWithWindows);
    }

    private static Dictionary<HotkeyAction, string> BuildHotkeyMap(AppSettings settings) => new()
    {
        [HotkeyAction.Start] = settings.StartHotkey,
        [HotkeyAction.PauseResume] = settings.PauseHotkey,
        [HotkeyAction.Stop] = settings.StopHotkey,
        [HotkeyAction.MuteMicrophone] = settings.MuteMicHotkey,
        [HotkeyAction.MuteSystemAudio] = settings.MuteSystemHotkey,
    };

    /// <summary>Whether the user wants a recording stopped for this particular system event.</summary>
    private bool ShouldStopFor(PowerStopReason reason)
    {
        var behavior = _settings.Current.Behavior;
        return reason switch
        {
            PowerStopReason.Sleep => behavior.StopOnSleep,
            PowerStopReason.Lock => behavior.StopOnLock,
            PowerStopReason.LogOff => behavior.StopOnLogOff,
            PowerStopReason.Shutdown => behavior.StopOnShutdown,
            _ => true,
        };
    }

    private void BuildUi()
    {
        _tray = new TrayManager(() => _settings.Current.Behavior);
        _tray.StartRequested += async (_, _) => await _manager.StartAsync();
        _tray.PauseRequested += async (_, _) => await _manager.TogglePauseAsync();
        _tray.StopRequested += async (_, _) => await _manager.StopAsync();
        _tray.MuteMicrophoneRequested += (_, _) => _manager.ToggleMute(AudioSourceKind.Microphone);
        _tray.MuteSystemAudioRequested += (_, _) => _manager.ToggleMute(AudioSourceKind.SystemAudio);
        _tray.OpenRequested += (_, _) => OnUi(ShowMainWindow);
        _tray.ExitRequested += async (_, _) => await ExitAsync();
        _tray.ApplyState(_manager.State);

        _mainWindow = new MainWindow(
            _manager,
            _settings,
            settingsRequested: ShowSettingsWindow,
            exitRequested: ExitAsync,
            cameras: _cameras);

        // Both surfaces track mute from the manager, so they cannot drift apart.
        _manager.MuteChanged += (_, _) => OnUi(() =>
        {
            _mainWindow?.ApplyMuteState();
            _tray?.ApplyMuteState(_manager.IsMicrophoneMuted, _manager.IsSystemAudioMuted);
            _overlay?.SetMuted(_manager.IsMicrophoneMuted && _settings.Current.Overlay.ShowMuteState);
        });

        // The camera has exactly the mute controls' problem — the main window, the bubble's close
        // button and its context menu can all change it — and exactly the mute controls' answer.
        _cameras.Changed += (_, _) => OnUi(() => _mainWindow?.ApplyCameraState());
        _mainWindow.ApplyCameraState();

        if (!_settings.Current.Behavior.StartMinimized) _mainWindow.Show();
    }

    /// <summary>Extracts ffmpeg, probes encoders and recovers interrupted recordings.</summary>
    private async Task WarmUpAsync()
    {
        try
        {
            _provisioner.GetFFmpegPath();
            _encoderProbe.GetPreferredEncoder();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FFmpeg could not be prepared.");
            OnUi(() => _tray?.ShowWarning(
                "FFmpeg unavailable",
                "Recording will not work. Place ffmpeg.exe next to Recorder.exe."));
            return;
        }

        if (!_settings.Current.Behavior.RecoverInterruptedRecordings) return;

        try
        {
            var recovery = new CrashRecoveryService(_finalizer);
            var recovered = await recovery.RecoverAsync().ConfigureAwait(false);

            if (recovered.Count > 0)
            {
                var message = recovered.Count == 1
                    ? $"Recovered '{System.IO.Path.GetFileName(recovered[0].Path)}' from an interrupted session."
                    : $"Recovered {recovered.Count} interrupted recordings.";
                OnUi(() => _tray?.ShowInfo("Recording recovered", message));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Crash recovery failed.");
        }
    }

    // ---------------------------------------------------------------- events

    private void OnHotkeyPressed(object? sender, HotkeyAction action) =>
        OnUi(() => _ = HandleHotkeyAsync(action));

    private async Task HandleHotkeyAsync(HotkeyAction action)
    {
        try
        {
            switch (action)
            {
                case HotkeyAction.Start:
                    // One key for both is friendlier than remembering which is which: if a
                    // recording is already running, the start hotkey stops it.
                    if (_manager.State.CanStart()) await _manager.StartAsync();
                    else await _manager.StopAsync();
                    break;

                case HotkeyAction.PauseResume:
                    await _manager.TogglePauseAsync();
                    break;

                case HotkeyAction.Stop:
                    await _manager.StopAsync();
                    break;

                case HotkeyAction.MuteMicrophone:
                    ToggleMuteFromHotkey(AudioSourceKind.Microphone, "Microphone");
                    break;

                case HotkeyAction.MuteSystemAudio:
                    ToggleMuteFromHotkey(AudioSourceKind.SystemAudio, "System audio");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Handling the {action} hotkey failed.");
        }
    }

    /// <summary>
    /// Toggles a source's mute and says so, since the hotkey may have been pressed from another app.
    /// </summary>
    /// <remarks>
    /// Mute only means anything while a recording is live. Pressing the key when idle is confirmed
    /// with a notification rather than silently ignored — a key that appears to do nothing is
    /// indistinguishable from one that failed to register.
    /// </remarks>
    private void ToggleMuteFromHotkey(AudioSourceKind kind, string label)
    {
        if (!_manager.State.IsActive())
        {
            _tray?.ShowInfo("Pomtom Recorder", $"{label} mute only applies while recording.");
            return;
        }

        _manager.ToggleMute(kind);
        _tray?.ShowInfo("Pomtom Recorder", $"{label} {(_manager.IsMuted(kind) ? "muted" : "unmuted")}.");
    }

    private void OnStateChanged(object? sender, RecordingStateChangedEventArgs e) => OnUi(() =>
    {
        _tray?.ApplyState(e.State);
        _mainWindow?.ApplyState(e.State);

        _cameras?.SetRecording(e.State.IsActive());

        if (e.State.IsActive() || e.State == RecorderState.Finalizing) ShowOverlay(e.State);
        else HideOverlay();
    });

    private void OnRecordingCompleted(object? sender, RecordingCompletedEventArgs e) => OnUi(() =>
    {
        var duration = $"{(int)e.Duration.TotalHours:00}:{e.Duration.Minutes:00}:{e.Duration.Seconds:00}";
        var name = System.IO.Path.GetFileName(e.Path);

        _tray?.ShowInfo("Recording saved", $"{name} ({duration})");
        _mainWindow?.ShowMessage($"Saved {name} ({duration}).");

        if (e.Warning is not null) Log.Warn(e.Warning);
    });

    private void OnRecordingError(object? sender, string message) => OnUi(() =>
    {
        _tray?.ShowWarning("Pomtom Recorder", message);
        _mainWindow?.ShowMessage(message);
    });

    // ---------------------------------------------------------------- windows

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ShowSettingsWindow()
    {
        try
        {
            var window = new SettingsWindow(_settings, () => _manager.State.IsActive());
            if (_mainWindow is not null && _mainWindow.IsVisible) window.Owner = _mainWindow;

            // The camera is opened with exclusive control, so the settings preview and the bubble
            // cannot both hold it. Choosing a camera is exactly when you need to see one, so the
            // settings window wins for as long as it is open.
            _cameras.Suspend();
            try { window.ShowDialog(); }
            finally { _ = _cameras.ResumeAsync(); }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The settings window failed.");
            MessageBox.Show("Settings could not be opened.", "Pomtom Recorder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowOverlay(RecorderState state)
    {
        try
        {
            var settings = _settings.Current;
            if (!settings.Overlay.Enabled)
            {
                HideOverlay();
                return;
            }

            if (_overlay is null)
            {
                _overlay = new RecordingOverlayWindow(
                    elapsedProvider: () => _manager.Elapsed,
                    positionPersister: (left, top) => _settings.Update(s =>
                    {
                        s.OverlayLeft = left;
                        s.OverlayTop = top;
                    }),
                    options: settings.Overlay);

                _overlay.Show();
                _overlay.PlaceAt(settings.OverlayLeft, settings.OverlayTop, ResolveOverlayArea());
                _overlay.SetMuted(_manager.IsMicrophoneMuted && settings.Overlay.ShowMuteState);
            }

            _overlay.SetState(state);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The recording overlay could not be shown.");
        }
    }

    /// <summary>Work area of the monitor being recorded, in WPF units.</summary>
    private Rect ResolveOverlayArea()
    {
        try
        {
            var monitor = MonitorEnumerator.Select(_settings.Current.MonitorDeviceId);
            if (monitor is not null)
            {
                // Monitor geometry is in physical pixels (PerMonitorV2) but WPF positions windows
                // in device-independent units, so divide by that monitor's own scale factor.
                var scale = NativeMethods.GetMonitorScale(monitor.Handle);
                return new Rect(
                    monitor.Left / scale,
                    monitor.Top / scale,
                    monitor.Width / scale,
                    monitor.Height / scale);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not resolve the overlay area; using the primary screen.");
        }

        return SystemParameters.WorkArea;
    }

    private void HideOverlay()
    {
        var overlay = _overlay;
        _overlay = null;
        if (overlay is null) return;

        try { overlay.Close(); }
        catch (Exception ex) { Log.Warn(ex, "Closing the overlay failed."); }
    }

    /// <summary>Shows the 3-2-1 countdown. Returns when it has finished.</summary>
    private async Task RunCountdownAsync(int seconds, CancellationToken cancellationToken)
    {
        CountdownWindow? window = null;

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                window = new CountdownWindow();
                window.Show();
                window.CenterOn(ResolveOverlayArea());
            });

            for (var remaining = seconds; remaining > 0; remaining--)
            {
                if (cancellationToken.IsCancellationRequested) return;

                var value = remaining;
                await Dispatcher.InvokeAsync(() => window?.ShowCount(value));
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }

            await Dispatcher.InvokeAsync(() => window?.ShowStarting());
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled countdown: fall through and close the window.
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "The countdown failed.");
        }
        finally
        {
            try { await Dispatcher.InvokeAsync(() => window?.Close()); }
            catch (Exception ex) { Log.Warn(ex, "Closing the countdown window failed."); }
        }
    }

    // ---------------------------------------------------------------- shutdown

    private async Task ExitAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        try
        {
            // A recording in progress is finalized rather than abandoned.
            if (_manager.State.IsActive())
            {
                OnUi(() => _mainWindow?.ShowMessage("Finishing the recording…"));
                await _manager.StopAsync().ConfigureAwait(false);
            }

            await _manager.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Shutdown failed while stopping the recording.");
        }

        OnUi(() =>
        {
            try { _power?.Dispose(); } catch { }
            try { _hotkeys?.Dispose(); } catch { }
            try { _cameras?.Dispose(); } catch { }
            HideOverlay();
            try { _tray?.Dispose(); } catch { }

            if (_mainWindow is not null)
            {
                _mainWindow.AllowClose = true;
                try { _mainWindow.Close(); } catch { }
                _mainWindow = null;
            }

            Log.Shutdown();
            Shutdown();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        try { _instanceMutex?.Dispose(); } catch { }
        Log.Shutdown();
        base.OnExit(e);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Runs an action on the UI thread, whichever thread the caller is on.
    /// </summary>
    /// <remarks>
    /// There is deliberately no <c>Func&lt;Task&gt;</c> overload. Adding one is a trap: the natural
    /// forwarding body <c>OnUi(() =&gt; _ = action())</c> has an inferred return type of
    /// <c>Task</c>, so overload resolution picks the <c>Func&lt;Task&gt;</c> overload again and the
    /// method recurses into itself until the stack overflows — which kills the process outright,
    /// with no exception to catch and nothing in the log. Callers with async work post an
    /// <see cref="Action"/> that starts the task instead.
    /// </remarks>
    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception on the UI thread.");
        e.Handled = true;

        // Keep the app alive: a UI glitch must not cost the user an in-progress recording.
        try { _mainWindow?.ShowMessage("Something went wrong. The recording was not affected."); }
        catch { }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error(e.ExceptionObject as Exception ?? new Exception("Unknown fatal error"),
                  "Unhandled exception; the process is terminating.");
        Log.Shutdown();
    }
}
