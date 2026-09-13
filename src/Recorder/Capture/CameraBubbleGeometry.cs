using Recorder.Utils;

namespace Recorder.Capture;

/// <summary>A rectangle in output-frame pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Turns the bubble window's position on screen into the rectangle it occupies in the video frame,
/// and decides which part of the camera image fills it.
/// </summary>
/// <remarks>
/// <para>Both rules live here rather than in the compositor because the bubble's preview and the
/// baked-in copy have to agree exactly — the whole premise of the feature is that the recording
/// looks like what was on screen. A second implementation of "which bit of the camera do we show"
/// is a second chance to disagree.</para>
///
/// <para>Everything is done in physical pixels via <c>GetWindowRect</c>. It is tempting to convert
/// WPF's device-independent <c>Left</c>/<c>Top</c> with <c>GetMonitorScale</c>, but WPF's global DIP
/// space maps to physical pixels per-monitor-affinely, so a single scale factor is only right when
/// every monitor shares one DPI — and the error grows with distance from the origin, which makes it
/// invisible on exactly the single-monitor machines it gets tested on.</para>
/// </remarks>
public static class CameraBubbleGeometry
{
    /// <summary>
    /// Maps the bubble window onto the output frame.
    /// </summary>
    /// <param name="hwnd">The bubble window's handle.</param>
    /// <param name="monitor">The monitor being recorded; its bounds are physical pixels.</param>
    /// <param name="sourceWidth">Capture source width, in physical pixels.</param>
    /// <param name="outputWidth">Frame width the encoder receives.</param>
    /// <returns>False when the window is gone or lands entirely outside the frame.</returns>
    public static bool TryComputeTargetRect(
        IntPtr hwnd,
        MonitorInfo monitor,
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight,
        out PixelRect rect)
    {
        rect = default;

        if (hwnd == IntPtr.Zero || sourceWidth <= 0 || sourceHeight <= 0) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var window)) return false;

        // Monitor-relative, still physical pixels — rcMonitor and GetWindowRect share a space.
        var left = window.Left - monitor.Left;
        var top = window.Top - monitor.Top;

        // The recording may be downscaled from the monitor's native size.
        var scaleX = outputWidth / (double)sourceWidth;
        var scaleY = outputHeight / (double)sourceHeight;

        // NV12 subsamples chroma 2x2, so an odd origin or extent would split a chroma pair between
        // the bubble and the desktop and fringe the edge.
        var x = (int)Math.Round(left * scaleX) & ~1;
        var y = (int)Math.Round(top * scaleY) & ~1;
        var w = Math.Max(2, (int)Math.Round(window.Width * scaleX) & ~1);
        var h = Math.Max(2, (int)Math.Round(window.Height * scaleY) & ~1);

        // Entirely off-frame is not an error, just nothing to draw.
        if (x >= outputWidth || y >= outputHeight || x + w <= 0 || y + h <= 0) return false;

        rect = new PixelRect(x, y, w, h);
        return true;
    }

    /// <summary>
    /// Picks the part of the camera image that fills a destination of the given shape without
    /// letterboxing — the centre crop of whichever dimension is proportionally too large.
    /// </summary>
    /// <remarks>
    /// This is the same rule as CSS <c>object-fit: cover</c> and WPF's
    /// <c>Stretch="UniformToFill"</c>, which is exactly how the preview renders it. Cropping rather
    /// than letterboxing is what lets a 16:9 webcam fill a circular bubble with a face instead of a
    /// face between two black bars.
    /// </remarks>
    public static PixelRect ComputeCoverCrop(int cameraWidth, int cameraHeight, int targetWidth, int targetHeight)
    {
        if (cameraWidth <= 0 || cameraHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
            return new PixelRect(0, 0, Math.Max(1, cameraWidth), Math.Max(1, cameraHeight));

        var cameraAspect = cameraWidth / (double)cameraHeight;
        var targetAspect = targetWidth / (double)targetHeight;

        if (cameraAspect > targetAspect)
        {
            // Camera is wider than the bubble: trim the sides.
            var w = Math.Max(1, (int)Math.Round(cameraHeight * targetAspect));
            return new PixelRect((cameraWidth - w) / 2, 0, Math.Min(w, cameraWidth), cameraHeight);
        }

        // Camera is taller than the bubble: trim top and bottom.
        var h = Math.Max(1, (int)Math.Round(cameraWidth / targetAspect));
        return new PixelRect(0, (cameraHeight - h) / 2, cameraWidth, Math.Min(h, cameraHeight));
    }

    /// <summary>Whether a window's centre lies on a given monitor.</summary>
    public static bool IsCentredOn(IntPtr hwnd, MonitorInfo monitor)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;

        var cx = r.Left + (r.Width / 2);
        var cy = r.Top + (r.Height / 2);

        return cx >= monitor.Left && cx < monitor.Left + monitor.Width &&
               cy >= monitor.Top && cy < monitor.Top + monitor.Height;
    }
}
