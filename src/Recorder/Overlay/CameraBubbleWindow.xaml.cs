using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Recorder.Capture;
using Recorder.Settings;
using Recorder.Utils;

namespace Recorder.Overlay;

/// <summary>Where a bubble can be parked.</summary>
public enum BubbleCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// The floating camera bubble: a live preview that can be dragged, resized and reshaped.
/// </summary>
/// <remarks>
/// <para>Built on the same foundations as <see cref="RecordingOverlayWindow"/> — borderless,
/// transparent, always on top, kept out of alt-tab and excluded from capture. The one rule it adds
/// is that the window rectangle must equal the visible bubble exactly: its position is measured with
/// <c>GetWindowRect</c> and used to place the camera inside the video, so a drop shadow or a margin
/// would put the recorded copy somewhere other than where the user sees it. That is also why
/// everything here is positioned with <c>SetWindowPos</c> in physical pixels rather than through
/// WPF's device-independent <c>Left</c>/<c>Top</c>.</para>
///
/// <para>The preview deliberately uses <c>UniformToFill</c>, which is the same centre-crop rule
/// <see cref="CameraBubbleGeometry.ComputeCoverCrop"/> applies when baking. Two implementations of
/// "which part of the camera do we show" would be two chances to disagree, and the whole premise of
/// the feature is that the recording matches the preview.</para>
/// </remarks>
public partial class CameraBubbleWindow : Window
{
    /// <summary>Fallback aspect ratio until the camera reports its own.</summary>
    private const double DefaultAspect = 16.0 / 9.0;

    private const int MinimumSize = 120;
    private const int MaximumSize = 900;

    private readonly CameraCaptureService _camera;
    private readonly DispatcherTimer _timer;

    private CameraSettings _options;
    private WriteableBitmap? _bitmap;
    private byte[] _frame = [];
    private int _frameWidth;
    private int _frameHeight;
    private long _shownFrames = -1;
    private bool _statusShowing = true;
    private bool _suppressMenuEvents;

    /// <summary>Raised after a move or resize settles, with the new physical-pixel geometry.</summary>
    public event EventHandler<BubbleGeometry>? GeometryChanged;

    /// <summary>Raised when the user asks for the camera to be turned off.</summary>
    public event EventHandler? HideRequested;

    public event EventHandler<CameraShape>? ShapeRequested;

    public event EventHandler<bool>? MirrorRequested;

    /// <summary>
    /// Raised when the window could not be excluded from screen capture.
    /// </summary>
    /// <remarks>
    /// Not cosmetic here, unlike for the REC indicator. The camera is also composited into the
    /// frame, so a bubble that Windows refuses to hide from the recording would be captured twice —
    /// once as a window and once as the bake, slightly offset. The owner hides the window while
    /// recording instead.
    /// </remarks>
    public event EventHandler? ExclusionFailed;

    public CameraBubbleWindow(CameraCaptureService camera, CameraSettings options)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(8, 1000.0 / Math.Max(1, options.Fps))),
        };
        _timer.Tick += (_, _) => RefreshPreview();

        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += (_, _) => UpdateOutline();

        Root.MouseLeftButtonDown += OnDragStart;
        ResizeGrip.DragDelta += OnResize;
        ResizeGrip.DragCompleted += (_, _) => PersistGeometry();
        HideButton.Click += (_, _) => HideRequested?.Invoke(this, EventArgs.Empty);

        WireMenu();
    }

    /// <summary>The bubble's rectangle in physical pixels.</summary>
    public BubbleGeometry Geometry => ReadGeometry();

    // ---------------------------------------------------------------- lifetime

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        if (!CaptureExclusion.Exclude(handle))
        {
            try { ExclusionFailed?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Warn(ex, "An ExclusionFailed handler threw."); }
        }

        CaptureExclusion.MakeToolWindow(handle);

        ApplySettings(_options);
        PlaceInitially();
        UpdateOutline();

        _timer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _bitmap = null;
    }

    // ---------------------------------------------------------------- appearance

    /// <summary>Re-reads everything the bubble's look depends on.</summary>
    public void ApplySettings(CameraSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(8, 1000.0 / Math.Max(1, options.Fps)));

        // The whole bubble carries the opacity, so the preview and the bake fade together.
        Opacity = Math.Clamp(options.Opacity, 0.2, 1.0);

        Preview.RenderTransform = options.Mirror
            ? new ScaleTransform(-1, 1)
            : Transform.Identity;

        _suppressMenuEvents = true;
        try
        {
            var shape = CameraSettings.ParseShape(options.Shape);
            ShapeCircleItem.IsChecked = shape == CameraShape.Circle;
            ShapeRectItem.IsChecked = shape == CameraShape.RoundedRect;
            MirrorItem.IsChecked = options.Mirror;
        }
        finally
        {
            _suppressMenuEvents = false;
        }

        ApplyShapeAspect();
        UpdateOutline();
    }

    /// <summary>
    /// Shows a message in place of the preview, or clears it when null.
    /// </summary>
    /// <remarks>
    /// The preview is hidden outright rather than merely covered. It sits above the backdrop in the
    /// visual tree, so leaving it visible would show the last frame the camera ever produced with
    /// "The camera was disconnected." written across it — the one thing the message is there to say
    /// is not happening.
    /// </remarks>
    public void ShowStatus(string? message)
    {
        var showing = !string.IsNullOrEmpty(message);
        if (showing == _statusShowing && (!showing || StatusText.Text == message)) return;

        _statusShowing = showing;
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        Backdrop.Visibility = showing || _bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Rebuilds the clip and the border ring for the current size and shape.
    /// </summary>
    /// <remarks>
    /// The ring is inset by half its own thickness so it sits entirely inside the window. A stroke
    /// centred on the outline would spill half of itself outside the window rectangle, where it
    /// would be clipped on screen but still drawn by the bake — the two would not match.
    /// </remarks>
    private void UpdateOutline()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var shape = CameraSettings.ParseShape(_options.Shape);

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var strokeDip = dpi > 0 ? _options.BorderThickness / dpi : _options.BorderThickness;
        var inset = strokeDip / 2.0;

        Clipped.Clip = BuildGeometry(shape, 0, w, h);
        Ring.Data = BuildGeometry(shape, inset, w, h);
        Ring.StrokeThickness = strokeDip;
        Ring.Stroke = strokeDip > 0 ? BrushFrom(_options.BorderColor) : null;
    }

    private static Geometry BuildGeometry(CameraShape shape, double inset, double width, double height)
    {
        var w = Math.Max(0, width - (inset * 2));
        var h = Math.Max(0, height - (inset * 2));

        if (shape == CameraShape.Circle)
            return new EllipseGeometry(new Point(width / 2, height / 2), w / 2, h / 2);

        // Matches CameraSprite.CornerFraction so the preview and the bake round identically.
        var radius = Math.Min(w, h) * 0.18;
        return new RectangleGeometry(new Rect(inset, inset, w, h), radius, radius);
    }

    private static Brush BrushFrom(string? color)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(color) &&
                ColorConverter.ConvertFromString(color) is Color parsed)
            {
                return new SolidColorBrush(parsed);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, $"Could not parse the bubble border colour '{color}'.");
        }

        return Brushes.White;
    }

    // ---------------------------------------------------------------- preview

    private void RefreshPreview()
    {
        try
        {
            var width = _camera.Width;
            var height = _camera.Height;
            if (width <= 0 || height <= 0) return;

            if (_bitmap is null || _frameWidth != width || _frameHeight != height)
            {
                _frameWidth = width;
                _frameHeight = height;
                _frame = new byte[width * height * 4];
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                Preview.Source = _bitmap;
                _shownFrames = -1;
                ApplyShapeAspect();
            }

            var frames = _camera.FramesCaptured;
            if (frames == _shownFrames) return;
            if (!_camera.TryCopyLatestFrame(_frame)) return;

            _shownFrames = frames;
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _frame, width * 4, 0);

            ShowStatus(null);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Refreshing the camera preview failed.");
        }
    }

    // ---------------------------------------------------------------- geometry

    private BubbleGeometry ReadGeometry()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && NativeMethods.GetWindowRect(handle, out var r))
            return new BubbleGeometry(r.Left, r.Top, r.Width, r.Height);

        return new BubbleGeometry(0, 0, (int)_options.Size, (int)_options.Size);
    }

    /// <summary>Moves and resizes the bubble in physical pixels.</summary>
    public void SetGeometry(int x, int y, int width, int height)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(
            handle, IntPtr.Zero, x, y,
            Math.Max(MinimumSize, width), Math.Max(MinimumSize, height),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Height the current shape wants for a given width.</summary>
    private int HeightFor(int width)
    {
        if (CameraSettings.ParseShape(_options.Shape) == CameraShape.Circle) return width;

        // A rounded rectangle keeps the camera's own aspect ratio, which is the point of choosing it
        // over a circle: nothing is cropped away.
        var aspect = _camera.Width > 0 && _camera.Height > 0
            ? _camera.Width / (double)_camera.Height
            : DefaultAspect;

        return Math.Max(MinimumSize / 2, (int)Math.Round(width / aspect));
    }

    /// <summary>Re-applies the shape's aspect ratio to the current width, keeping the top-left put.</summary>
    private void ApplyShapeAspect()
    {
        var current = ReadGeometry();
        if (current.Width <= 0) return;

        var height = HeightFor(current.Width);
        if (height == current.Height) return;

        SetGeometry(current.X, current.Y, current.Width, height);
    }

    private void PlaceInitially()
    {
        var width = Math.Clamp((int)Math.Round(_options.Size), MinimumSize, MaximumSize);
        var height = HeightFor(width);

        if (_options.Left is not null && _options.Top is not null)
        {
            var x = (int)Math.Round(_options.Left.Value);
            var y = (int)Math.Round(_options.Top.Value);

            SetGeometry(x, y, width, height);

            // A position saved against a monitor that has since been unplugged would strand the
            // bubble off-screen, where it would be neither visible nor recoverable by dragging.
            if (IsOnAScreen(x, y, width, height)) return;

            Log.Warn("The saved camera bubble position is off-screen; moving it back into view.");
        }

        SnapTo(BubbleCorner.BottomRight, width, height);
    }

    private static bool IsOnAScreen(int x, int y, int width, int height)
    {
        var probe = new Rect(x, y, Math.Max(1, width), Math.Max(1, height));

        foreach (var monitor in MonitorEnumerator.Enumerate())
        {
            var bounds = new Rect(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
            bounds.Intersect(probe);

            // A sliver poking onto a screen is not enough to grab; insist on a real handle.
            if (bounds.Width >= 40 && bounds.Height >= 40) return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- interaction

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try
        {
            DragMove();
            SnapToNearestCorner();
            PersistGeometry();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws if the button was already released; harmless.
        }
    }

    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        var current = ReadGeometry();

        // The thumb reports device-independent units; the window lives in physical pixels.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var delta = (int)Math.Round(e.HorizontalChange * (scale > 0 ? scale : 1));

        var width = Math.Clamp(current.Width + delta, MinimumSize, MaximumSize);

        // The top-left stays put, so the bubble grows towards the grip the user is dragging.
        SetGeometry(current.X, current.Y, width, HeightFor(width));
    }

    /// <summary>
    /// Parks the bubble in whichever corner it was dropped near, if any.
    /// </summary>
    /// <remarks>
    /// Only a drop that lands inside the snap distance is captured. Snapping unconditionally would
    /// make it impossible to place the bubble anywhere but a corner, which is not what "move it
    /// anywhere" means.
    /// </remarks>
    private void SnapToNearestCorner()
    {
        if (_options.SnapDistance <= 0) return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !NativeMethods.TryGetWorkArea(handle, out var work)) return;

        var g = ReadGeometry();
        var margin = (int)Math.Round(_options.SnapMargin);

        var targets = new (BubbleCorner Corner, int X, int Y)[]
        {
            (BubbleCorner.TopLeft, work.Left + margin, work.Top + margin),
            (BubbleCorner.TopRight, work.Right - margin - g.Width, work.Top + margin),
            (BubbleCorner.BottomLeft, work.Left + margin, work.Bottom - margin - g.Height),
            (BubbleCorner.BottomRight, work.Right - margin - g.Width, work.Bottom - margin - g.Height),
        };

        var best = targets
            .Select(t => (t, Distance: Math.Sqrt(Math.Pow(t.X - g.X, 2) + Math.Pow(t.Y - g.Y, 2))))
            .OrderBy(t => t.Distance)
            .First();

        if (best.Distance > _options.SnapDistance) return;

        SetGeometry(best.t.X, best.t.Y, g.Width, g.Height);
    }

    private void SnapTo(BubbleCorner corner) => SnapTo(corner, 0, 0);

    private void SnapTo(BubbleCorner corner, int width, int height)
    {
        var handle = new WindowInteropHelper(this).Handle;

        var g = ReadGeometry();
        if (width <= 0) width = g.Width;
        if (height <= 0) height = g.Height;

        NativeMethods.RECT work;
        if (handle == IntPtr.Zero || !NativeMethods.TryGetWorkArea(handle, out work))
        {
            var primary = MonitorEnumerator.Enumerate().FirstOrDefault(m => m.IsPrimary);
            if (primary is null) return;

            work = new NativeMethods.RECT
            {
                Left = primary.Left,
                Top = primary.Top,
                Right = primary.Left + primary.Width,
                Bottom = primary.Top + primary.Height,
            };
        }

        var margin = (int)Math.Round(_options.SnapMargin);

        var x = corner is BubbleCorner.TopLeft or BubbleCorner.BottomLeft
            ? work.Left + margin
            : work.Right - margin - width;

        var y = corner is BubbleCorner.TopLeft or BubbleCorner.TopRight
            ? work.Top + margin
            : work.Bottom - margin - height;

        SetGeometry(x, y, width, height);
        PersistGeometry();
    }

    private void PersistGeometry()
    {
        try { GeometryChanged?.Invoke(this, ReadGeometry()); }
        catch (Exception ex) { Log.Warn(ex, "Persisting the camera bubble geometry failed."); }
    }

    // ---------------------------------------------------------------- menu

    private void WireMenu()
    {
        ShapeCircleItem.Click += (_, _) => RequestShape(CameraShape.Circle);
        ShapeRectItem.Click += (_, _) => RequestShape(CameraShape.RoundedRect);

        MirrorItem.Click += (_, _) =>
        {
            if (_suppressMenuEvents) return;
            MirrorRequested?.Invoke(this, MirrorItem.IsChecked);
        };

        SizeSmallItem.Click += (_, _) => Resize(180);
        SizeMediumItem.Click += (_, _) => Resize(280);
        SizeLargeItem.Click += (_, _) => Resize(420);

        SnapTopLeftItem.Click += (_, _) => SnapTo(BubbleCorner.TopLeft);
        SnapTopRightItem.Click += (_, _) => SnapTo(BubbleCorner.TopRight);
        SnapBottomLeftItem.Click += (_, _) => SnapTo(BubbleCorner.BottomLeft);
        SnapBottomRightItem.Click += (_, _) => SnapTo(BubbleCorner.BottomRight);

        HideItem.Click += (_, _) => HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestShape(CameraShape shape)
    {
        if (_suppressMenuEvents) return;
        ShapeRequested?.Invoke(this, shape);
    }

    private void Resize(int width)
    {
        var g = ReadGeometry();
        var clamped = Math.Clamp(width, MinimumSize, MaximumSize);

        SetGeometry(g.X, g.Y, clamped, HeightFor(clamped));
        PersistGeometry();
    }
}

/// <summary>A bubble rectangle in physical pixels.</summary>
public readonly record struct BubbleGeometry(int X, int Y, int Width, int Height);
