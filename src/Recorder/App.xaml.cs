using System.Threading;
using System.Windows;
using System.Windows.Threading;
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

        _provisioner = new FFmpegProvisioner();
        _encoderProbe = new EncoderProbe(_provisioner);
        _finalizer = new Mp4Finalizer(_provisioner);

        _manager = new RecordingManager(_settings, _provisioner, _encoderProbe, _finalizer);
        _manager.StateChanged += OnStateChanged;
        _manager.Completed += OnRecordingCompleted;
        _manager.ErrorOccurred += OnRecordingError;
        _manager.CountdownHandler = RunCountdownAsync;

        _hotkeys = new GlobalHotkeyManager();
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.RegistrationFailed += (_, message) => OnUi(() => _tray?.ShowWarning("Hotkey unavailable", message));

        var current = _settings.Current;
        _hotkeys.Apply(current.StartHotkey, current.PauseHotkey, current.StopHotkey);

        _settings.Changed += (_, updated) => OnUi(() =>
        {
            _hotkeys.Apply(updated.StartHotkey, updated.PauseHotkey, updated.StopHotkey);
            _mainWindow?.RefreshSettingsText();
        });

        _power = new PowerEventMonitor(
            isRecording: () => _manager.State.IsActive(),
            stopAsync: async reason =>
            {
                await _manager.StopAsync().ConfigureAwait(false);
                OnUi(() => _tray?.ShowInfo("Recording stopped", $"Saved because the system went to {reason}."));
            });
        _power.Start();
    }

    private void BuildUi()
    {
        _tray = new TrayManager();
        _tray.StartRequested += async (_, _) => await _manager.StartAsync();
        _tray.PauseRequested += async (_, _) => await _manager.TogglePauseAsync();
        _tray.StopRequested += async (_, _) => await _manager.StopAsync();
        _tray.OpenRequested += (_, _) => OnUi(ShowMainWindow);
        _tray.ExitRequested += async (_, _) => await ExitAsync();
        _tray.ApplyState(_manager.State);

        _mainWindow = new MainWindow(
            _manager,
            _settings,
            settingsRequested: ShowSettingsWindow,
            exitRequested: ExitAsync);

        _mainWindow.Show();
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
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Handling the {action} hotkey failed.");
        }
    }

    private void OnStateChanged(object? sender, RecordingStateChangedEventArgs e) => OnUi(() =>
    {
        _tray?.ApplyState(e.State);
        _mainWindow?.ApplyState(e.State);

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
            var window = new SettingsWindow(_settings);
            if (_mainWindow is not null && _mainWindow.IsVisible) window.Owner = _mainWindow;
            window.ShowDialog();
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
            if (_overlay is null)
            {
                var settings = _settings.Current;
                _overlay = new RecordingOverlayWindow(
                    elapsedProvider: () => _manager.Elapsed,
                    positionPersister: (left, top) => _settings.Update(s =>
                    {
                        s.OverlayLeft = left;
                        s.OverlayTop = top;
                    }));

                _overlay.Show();
                _overlay.PlaceAt(settings.OverlayLeft, settings.OverlayTop, ResolveOverlayArea());
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
