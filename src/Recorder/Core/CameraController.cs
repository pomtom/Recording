using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Recorder.Capture;
using Recorder.Overlay;
using Recorder.Settings;
using Recorder.Utils;

namespace Recorder.Core;

/// <summary>
/// Owns the camera, the bubble window and the compositor that bakes one into the other.
/// </summary>
/// <remarks>
/// <para>Deliberately app-lifetime rather than recording-scoped. The bubble is live while the app
/// sits idle — that is how someone frames themselves before pressing record — so the camera cannot
/// belong to <see cref="RecordingSession"/>. It keeps the shape of the mute controls in
/// <see cref="RecordingManager"/>, where one owner holds the state and every surface reads it, for
/// the same reason: the main window, the bubble's own close button and its context menu must never
/// be able to disagree about whether the camera is on.</para>
///
/// <para>The one place it departs from mute is persistence. Mute resets with every recording;
/// a camera that has been switched off stays off until it is switched back on.</para>
/// </remarks>
public sealed class CameraController : IDisposable
{
    private readonly SettingsManager _settings;
    private readonly Dispatcher _dispatcher;
    private readonly CameraCaptureService _camera = new();
    private readonly CameraFrameCompositor _compositor;

    private CameraBubbleWindow? _bubble;
    private CancellationTokenSource? _startCts;
    private bool _recording;
    private bool _exclusionFailed;
    private bool _suspended;
    private bool _disposed;

    public CameraController(SettingsManager settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _compositor = new CameraFrameCompositor(_camera);

        _camera.CameraLost += (_, _) => _dispatcher.BeginInvoke(() => OnCameraLost());
    }

    /// <summary>Handed to each recording so the bubble is drawn into every encoded frame.</summary>
    public IVideoFrameOverlay Overlay => _compositor;

    /// <summary>Whether the camera is switched on.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Whether any camera exists to switch on.</summary>
    public bool IsAvailable { get; private set; } = true;

    /// <summary>User-facing explanation of the current state, or null when all is well.</summary>
    public string? Status { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="IsEnabled"/> or <see cref="Status"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Restores the camera from settings at startup.</summary>
    public async Task InitializeAsync()
    {
        var cameras = await CameraCaptureService.EnumerateAsync().ConfigureAwait(true);
        IsAvailable = cameras.Count > 0;

        if (!IsAvailable)
        {
            Status = "No camera was found on this machine.";
            Raise();
            return;
        }

        if (_settings.Current.Camera.Enabled) await SetEnabledAsync(true).ConfigureAwait(true);
        else Raise();
    }

    public Task ToggleAsync() => SetEnabledAsync(!IsEnabled);

    /// <summary>Switches the camera on or off, opening or closing the device as it goes.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        if (_disposed || IsEnabled == enabled) return;

        IsEnabled = enabled;
        _settings.Update(s => s.Camera.Enabled = enabled);

        // Suspended, the device belongs to the settings window; record the intent and let
        // ResumeAsync act on it. Shutdown is still safe to run, since it only releases things.
        if (enabled)
        {
            if (!_suspended) await StartAsync().ConfigureAwait(true);
        }
        else
        {
            Shutdown();
        }

        Raise();
    }

    private async Task StartAsync()
    {
        // While the settings window is up it holds the device exclusively. Saving from that window
        // raises Changed before the dialog closes, so without this guard the controller would race
        // its own settings preview for the camera and both would lose. ResumeAsync starts it.
        if (_suspended) return;

        _startCts?.Cancel();
        _startCts = new CancellationTokenSource();
        var token = _startCts.Token;

        var options = _settings.Current.Camera;

        ShowBubble(options);
        _bubble?.ShowStatus("Starting camera…");
        Status = null;
        Raise();

        var started = await _camera
            .StartAsync(options.DeviceId, options.CaptureWidth, options.CaptureHeight, options.Fps)
            .ConfigureAwait(true);

        if (token.IsCancellationRequested || !IsEnabled) return;

        if (!started)
        {
            Status = _camera.Error ?? "The camera could not be started.";
            _bubble?.ShowStatus(Status);
            _compositor.Enabled = false;
            Raise();
            return;
        }

        Status = null;
        _compositor.Enabled = true;
        ApplyStyle(options);
        Raise();
    }

    private void Shutdown()
    {
        _startCts?.Cancel();
        _startCts = null;

        _compositor.Enabled = false;
        _compositor.BubbleHandle = IntPtr.Zero;

        CloseBubble();

        // Closing the device is the point, not an optimisation: this is a privacy control, and a
        // camera whose hardware light stays on after it has been switched off is not switched off.
        _camera.Stop();
    }

    /// <summary>
    /// Handles the camera dying under us. Always leaves any recording running.
    /// </summary>
    /// <remarks>
    /// Arrives on the UI thread via the dispatcher, because the event itself is raised on a Media
    /// Foundation thread and stopping a <c>MediaCapture</c> from inside its own failure callback
    /// deadlocks. The compositor is switched off first so no further frames are baked; its own
    /// staleness watchdog would have caught this within two seconds regardless, but there is no
    /// reason to bake two seconds of a frozen face while waiting for it.
    /// </remarks>
    private void OnCameraLost()
    {
        if (!IsEnabled) return;

        Log.Warn("The camera was lost; dropping the bubble and leaving any recording running.");

        _compositor.Enabled = false;
        Status = _camera.Error ?? "The camera was disconnected.";
        _bubble?.ShowStatus(Status);

        // Release the dead device so switching the camera off and on again can retry it.
        try { _camera.Stop(); }
        catch (Exception ex) { Log.Warn(ex, "Releasing the lost camera failed."); }

        Raise();
    }

    // ---------------------------------------------------------------- bubble

    private void ShowBubble(CameraSettings options)
    {
        if (_bubble is not null || _suspended) return;

        var bubble = new CameraBubbleWindow(_camera, options);

        bubble.GeometryChanged += (_, g) => _settings.Update(s =>
        {
            s.Camera.Left = g.X;
            s.Camera.Top = g.Y;
            s.Camera.Size = g.Width;
        });

        bubble.HideRequested += async (_, _) => await SetEnabledAsync(false).ConfigureAwait(true);

        bubble.ShapeRequested += (_, shape) =>
        {
            _settings.Update(s => s.Camera.Shape = CameraSettings.FormatShape(shape));
            ApplyLiveSettings();
        };

        bubble.MirrorRequested += (_, mirror) =>
        {
            _settings.Update(s => s.Camera.Mirror = mirror);
            ApplyLiveSettings();
        };

        bubble.ExclusionFailed += (_, _) =>
        {
            _exclusionFailed = true;
            Log.Warn("The camera bubble could not be hidden from screen capture; it will be hidden " +
                     "while recording so the camera is not captured twice.");
            ApplyRecordingVisibility();
        };

        _bubble = bubble;
        bubble.Show();

        _compositor.BubbleHandle = new WindowInteropHelper(bubble).Handle;
        ApplyStyle(options);
        ApplyRecordingVisibility();
    }

    private void CloseBubble()
    {
        var bubble = _bubble;
        _bubble = null;
        if (bubble is null) return;

        try { bubble.Close(); }
        catch (Exception ex) { Log.Warn(ex, "Closing the camera bubble failed."); }
    }

    /// <summary>
    /// Hides the bubble while recording when Windows refused to exclude it from capture.
    /// </summary>
    /// <remarks>
    /// Without this the camera would land in the file twice — once as the window the screen capture
    /// picked up, and once as the composited bake, a frame apart and very slightly offset. Losing
    /// the on-screen preview is much the lesser evil, and only happens on builds too old to support
    /// <c>WDA_EXCLUDEFROMCAPTURE</c>.
    /// </remarks>
    private void ApplyRecordingVisibility()
    {
        if (_bubble is null) return;

        var hide = _recording && _exclusionFailed;
        _bubble.Visibility = hide ? System.Windows.Visibility.Hidden : System.Windows.Visibility.Visible;
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Re-reads settings after the settings window has saved.</summary>
    public async Task ApplySettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var options = settings.Camera;

        if (options.Enabled != IsEnabled)
        {
            await SetEnabledAsync(options.Enabled).ConfigureAwait(true);
            return;
        }

        if (!IsEnabled || _suspended) return;

        // A different device, resolution or frame rate cannot be applied to a running reader.
        var needsRestart =
            !string.Equals(options.DeviceId, _lastDeviceId, StringComparison.Ordinal) ||
            options.CaptureWidth != _lastWidth ||
            options.CaptureHeight != _lastHeight ||
            options.Fps != _lastFps;

        if (needsRestart)
        {
            _camera.Stop();
            await StartAsync().ConfigureAwait(true);
            return;
        }

        ApplyLiveSettings();
    }

    private string? _lastDeviceId;
    private int _lastWidth;
    private int _lastHeight;
    private int _lastFps;

    /// <summary>Applies the settings that can change without reopening the device.</summary>
    private void ApplyLiveSettings()
    {
        var options = _settings.Current.Camera;
        _bubble?.ApplySettings(options);
        ApplyStyle(options);
    }

    private void ApplyStyle(CameraSettings options)
    {
        _lastDeviceId = options.DeviceId;
        _lastWidth = options.CaptureWidth;
        _lastHeight = options.CaptureHeight;
        _lastFps = options.Fps;

        _compositor.Style = new BubbleStyle(
            CameraSettings.ParseShape(options.Shape),
            options.Mirror,
            options.BorderThickness,
            ToArgb(options.BorderColor),
            options.Opacity);
    }

    /// <summary>Packs a WPF colour string into 0xAARRGGBB, defaulting to opaque white.</summary>
    private static uint ToArgb(string? color)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(color) && ColorConverter.ConvertFromString(color) is Color c)
                return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, $"Could not parse the bubble border colour '{color}'.");
        }

        return 0xFFFFFFFF;
    }

    // ---------------------------------------------------------------- recording

    /// <summary>Tells the controller a recording has started or finished.</summary>
    public void SetRecording(bool recording)
    {
        _recording = recording;
        ApplyRecordingVisibility();
    }

    /// <summary>
    /// Pulls the bubble onto the monitor about to be recorded, if it is somewhere else.
    /// </summary>
    /// <remarks>
    /// A bubble on the second monitor while the first is being recorded would simply not appear,
    /// which looks exactly like the feature being broken. Moving it is the only outcome that leaves
    /// the user with what they asked for.
    /// </remarks>
    public void EnsureOnMonitor(MonitorInfo monitor)
    {
        if (_bubble is null || !IsEnabled) return;

        var handle = new WindowInteropHelper(_bubble).Handle;
        if (CameraBubbleGeometry.IsCentredOn(handle, monitor)) return;

        var g = _bubble.Geometry;
        var margin = (int)Math.Round(_settings.Current.Camera.SnapMargin);

        var x = monitor.Left + monitor.Width - margin - g.Width;
        var y = monitor.Top + monitor.Height - margin - g.Height;

        Log.Warn($"The camera bubble was not on {monitor.DeviceId}; moving it onto the recorded display.");
        _bubble.SetGeometry(x, y, g.Width, g.Height);

        _settings.Update(s => { s.Camera.Left = x; s.Camera.Top = y; });
    }

    // ---------------------------------------------------------------- settings window

    /// <summary>
    /// Releases the camera so the settings window can preview it, and hides the bubble.
    /// </summary>
    /// <remarks>
    /// The device is opened with <c>ExclusiveControl</c>, so the preview in the settings window and
    /// the bubble cannot both hold it. Handing it over is better than making the settings preview a
    /// second-class citizen, since choosing a camera is precisely when you most need to see one.
    /// </remarks>
    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true;

        _compositor.Enabled = false;
        _compositor.BubbleHandle = IntPtr.Zero;
        CloseBubble();
        _camera.Stop();
    }

    public async Task ResumeAsync()
    {
        if (!_suspended) return;
        _suspended = false;

        if (IsEnabled) await StartAsync().ConfigureAwait(true);
    }

    private void Raise()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Warn(ex, "A camera Changed handler threw."); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _startCts?.Cancel();
        CloseBubble();
        _camera.Dispose();
    }
}
