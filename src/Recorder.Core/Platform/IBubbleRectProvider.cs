using Recorder.Capture;

namespace Recorder.Platform;

/// <summary>
/// Where the camera bubble currently sits on screen.
/// </summary>
/// <remarks>
/// Read from the frame pacer thread on every frame, so implementations must be cheap and must not
/// touch UI objects that only the UI thread may use.
/// </remarks>
public interface IBubbleRectProvider
{
    /// <summary>
    /// The bubble's bounds in the same desktop pixel space as <see cref="MonitorInfo"/>: physical
    /// pixels, origin at the top-left of the primary display, y growing downwards.
    /// </summary>
    /// <returns>False when the bubble is not on screen.</returns>
    bool TryGetBounds(out PixelRect bounds);
}
