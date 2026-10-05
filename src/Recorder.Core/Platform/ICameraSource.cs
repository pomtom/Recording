namespace Recorder.Platform;

/// <summary>
/// A running webcam, as seen by the compositor that stamps it into each video frame.
/// </summary>
/// <remarks>Frames are packed 32-bit BGRA, <see cref="Width"/> × <see cref="Height"/>.</remarks>
public interface ICameraSource
{
    bool IsRunning { get; }

    int Width { get; }

    int Height { get; }

    /// <summary>Increments with every frame delivered, so a stalled camera can be told apart.</summary>
    long FramesCaptured { get; }

    /// <summary>Copies the newest frame. False if there is none yet.</summary>
    bool TryCopyLatestFrame(byte[] destination);
}
