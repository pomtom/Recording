using Recorder.Capture;

namespace Recorder.Platform;

/// <summary>What <see cref="Core.RecordingManager"/> needs from the app's camera bubble.</summary>
public interface ICameraHost
{
    /// <summary>The compositor that draws the camera into each recorded frame.</summary>
    IVideoFrameOverlay? Overlay { get; }

    /// <summary>
    /// Moves the bubble onto <paramref name="monitor"/> if it is elsewhere, so it ends up in the
    /// recording rather than on a display nobody is capturing.
    /// </summary>
    void EnsureOnMonitor(MonitorInfo monitor);
}
