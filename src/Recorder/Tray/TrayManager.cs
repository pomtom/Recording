using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Recorder.Core;
using Recorder.Utils;

namespace Recorder.Tray;

/// <summary>
/// The system-tray icon and its menu.
/// </summary>
/// <remarks>
/// This is the app's real home: closing the window only hides it, so the tray icon is what keeps
/// the recorder reachable and is the only place Exit genuinely quits. Menu items are enabled from
/// the current <see cref="RecorderState"/> so the menu can never offer an action that would be
/// rejected.
/// </remarks>
public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _exitItem;

    private Icon? _ownedIcon;
    private bool _disposed;

    public event EventHandler? StartRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    public TrayManager()
    {
        _startItem = new ToolStripMenuItem("Start recording", null, (_, _) => Raise(StartRequested));
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => Raise(PauseRequested));
        _stopItem = new ToolStripMenuItem("Stop", null, (_, _) => Raise(StopRequested));
        _openItem = new ToolStripMenuItem("Open", null, (_, _) => Raise(OpenRequested));
        _exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Raise(ExitRequested));

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _startItem,
            _pauseItem,
            _stopItem,
            new ToolStripSeparator(),
            _openItem,
            _exitItem,
        ]);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Pomtom Recorder",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Double-click is the conventional "show me the window" gesture.
        _icon.DoubleClick += (_, _) => Raise(OpenRequested);
    }

    public void ApplyState(RecorderState state)
    {
        if (_disposed) return;

        _startItem.Enabled = state.CanStart();
        _pauseItem.Enabled = state.CanPause();
        _stopItem.Enabled = state.CanStop();
        _pauseItem.Text = state == RecorderState.Paused ? "Resume" : "Pause";

        var status = state switch
        {
            RecorderState.CountingDown => "Starting…",
            RecorderState.Recording => "Recording",
            RecorderState.Paused => "Paused",
            RecorderState.Finalizing => "Saving…",
            _ => "Ready",
        };

        // The tray tooltip is capped at 63 characters by the shell.
        _icon.Text = Truncate($"Pomtom Recorder — {status}", 63);
    }

    public void ShowInfo(string title, string message) => Notify(title, message, ToolTipIcon.Info);

    public void ShowWarning(string title, string message) => Notify(title, message, ToolTipIcon.Warning);

    private void Notify(string title, string message, ToolTipIcon icon)
    {
        if (_disposed) return;
        try
        {
            _icon.BalloonTipTitle = Truncate(title, 63);
            _icon.BalloonTipText = Truncate(message, 255);
            _icon.BalloonTipIcon = icon;
            _icon.ShowBalloonTip(5000);
        }
        catch (Exception ex)
        {
            // Notifications can be disabled by policy; never let that break a recording.
            Log.Warn(ex, "Could not show a tray notification.");
        }
    }

    /// <summary>
    /// Loads the embedded application icon, falling back to a drawn one.
    /// </summary>
    /// <remarks>
    /// A tray icon that fails to load leaves the app running with no way to reach it, so the
    /// fallback matters more than it looks.
    /// </remarks>
    private Icon LoadIcon()
    {
        try
        {
            // Deliberately the process path rather than Assembly.Location: in a single-file
            // publish there is no assembly on disk and Location returns an empty string, whereas
            // the .exe itself always carries the icon resource.
            var iconSource = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(iconSource) && File.Exists(iconSource))
            {
                var extracted = Icon.ExtractAssociatedIcon(iconSource);
                if (extracted is not null)
                {
                    _ownedIcon = extracted;
                    return extracted;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not load the application icon; drawing a fallback.");
        }

        return _ownedIcon = DrawFallbackIcon();
    }

    private static Icon DrawFallbackIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var back = new SolidBrush(Color.FromArgb(235, 32, 34, 38));
            graphics.FillEllipse(back, 1, 1, 30, 30);
            using var dot = new SolidBrush(Color.FromArgb(255, 232, 62, 62));
            graphics.FillEllipse(dot, 9, 9, 14, 14);
        }

        // Icon.FromHandle does not own the handle, so clone into a self-contained Icon.
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private void Raise(EventHandler? handler)
    {
        try { handler?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error(ex, "A tray menu handler threw."); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Disposing the tray icon failed.");
        }

        try { _ownedIcon?.Dispose(); } catch { }
    }
}
