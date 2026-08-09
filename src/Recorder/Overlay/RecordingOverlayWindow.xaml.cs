using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Recorder.Core;
using Recorder.Settings;
using Recorder.Utils;

namespace Recorder.Overlay;

/// <summary>
/// The floating "🔴 REC 00:12:36" indicator.
/// </summary>
/// <remarks>
/// Always on top, draggable, and — via <see cref="CaptureExclusion"/> — invisible to the recording
/// itself, which is the whole point: a recording indicator that appears in the recording is worse
/// than none at all.
/// </remarks>
public partial class RecordingOverlayWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Func<TimeSpan> _elapsedProvider;
    private readonly Action<double, double> _positionPersister;
    private readonly OverlaySettings _options;

    private Storyboard? _pulse;

    public RecordingOverlayWindow(
        Func<TimeSpan> elapsedProvider,
        Action<double, double> positionPersister,
        OverlaySettings options)
    {
        _elapsedProvider = elapsedProvider;
        _positionPersister = positionPersister;
        _options = options;

        InitializeComponent();

        Opacity = options.Opacity;
        RootScale.ScaleX = options.Scale;
        RootScale.ScaleY = options.Scale;

        if (!options.ShowElapsed) TimeText.Visibility = Visibility.Collapsed;

        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            // Twice a second: the display only has second resolution, and this keeps the
            // indicator from lagging a second behind after a pause.
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timer.Tick += (_, _) => UpdateElapsed();

        Loaded += OnLoaded;
        Closed += OnClosed;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        CaptureExclusion.Exclude(handle);
        CaptureExclusion.MakeToolWindow(handle);

        // Click-through and dragging are mutually exclusive by construction: a window that never
        // receives the mouse cannot be picked up. The settings hint says as much.
        if (_options.ClickThrough)
        {
            CaptureExclusion.MakeClickThrough(handle);
            Root.Cursor = null;
        }

        _pulse = TryFindResource("PulseStoryboard") as Storyboard;
        StartPulse();

        UpdateElapsed();
        _timer.Start();
    }

    /// <summary>Shows or hides the muted-microphone badge.</summary>
    public void SetMuted(bool muted)
    {
        MuteBadge.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        PersistPosition();
    }

    /// <summary>Drag from anywhere on the badge; there is no title bar to grab.</summary>
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
            PersistPosition();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws if the button was already released; harmless.
        }
    }

    /// <summary>Places the overlay, falling back to the top-centre of the given work area.</summary>
    public void PlaceAt(double? left, double? top, Rect workArea)
    {
        // Ensure a measured size before positioning, otherwise centring uses zero width.
        if (double.IsNaN(ActualWidth) || ActualWidth <= 0)
        {
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        var width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        if (width <= 0) width = 160;

        if (left is not null && top is not null && IsOnScreen(left.Value, top.Value, workArea))
        {
            Left = left.Value;
            Top = top.Value;
            return;
        }

        Left = workArea.Left + ((workArea.Width - width) / 2);
        Top = workArea.Top + 24;
    }

    /// <summary>Rejects a saved position that would land off-screen after a monitor change.</summary>
    private static bool IsOnScreen(double left, double top, Rect workArea)
    {
        var inflated = Rect.Inflate(workArea, 80, 80);
        return inflated.Contains(new Point(left, top));
    }

    public void SetState(RecorderState state)
    {
        switch (state)
        {
            case RecorderState.Paused:
                Label.Text = "PAUSED";
                Dot.Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x3C));
                StopPulse();
                break;

            case RecorderState.Finalizing:
                Label.Text = "SAVING";
                Dot.Fill = new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xFF));
                StopPulse();
                break;

            default:
                Label.Text = "REC";
                Dot.Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x3E, 0x3E));
                StartPulse();
                break;
        }
    }

    private void StartPulse()
    {
        if (!_options.PulseWhileRecording)
        {
            Dot.Opacity = 1.0;
            return;
        }

        try { _pulse?.Begin(Dot, true); }
        catch (Exception ex) { Log.Warn(ex, "Could not start the overlay pulse."); }
    }

    private void StopPulse()
    {
        try
        {
            _pulse?.Stop(Dot);
            Dot.Opacity = 1.0;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not stop the overlay pulse.");
        }
    }

    private void UpdateElapsed()
    {
        try
        {
            var elapsed = _elapsedProvider();
            TimeText.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Updating the overlay clock failed.");
        }
    }

    private void PersistPosition()
    {
        try { _positionPersister(Left, Top); }
        catch (Exception ex) { Log.Warn(ex, "Could not save the overlay position."); }
    }
}
