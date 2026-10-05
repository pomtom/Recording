using Recorder.Capture;
using Recorder.Encoding;

namespace Recorder.Platform;

/// <summary>
/// Captures one display into raw frames the encoder can take as-is.
/// </summary>
/// <remarks>
/// <para>Frames are pulled, not pushed: the frame pacer asks for the latest frame at exactly the
/// output rate and a capture that has nothing new simply hands back the previous one. That is what
/// makes the output constant-frame-rate however often the OS delivers frames.</para>
///
/// <para>The sizes form a chain. <c>Source</c> is the display's native pixel size; <c>Output</c> is
/// the size of the frames <see cref="TryCopyLatestFrame"/> produces; <c>Target</c> is the size the
/// encoder must finally write. Output equals Target unless the platform could not scale, in which
/// case ffmpeg does it.</para>
/// </remarks>
public interface IScreenCapture : IDisposable
{
    /// <summary>Raised once if the display goes away or capture stops by itself.</summary>
    event EventHandler? CaptureLost;

    int SourceWidth { get; }
    int SourceHeight { get; }
    int OutputWidth { get; }
    int OutputHeight { get; }
    int TargetWidth { get; }
    int TargetHeight { get; }

    /// <summary>Layout of the bytes <see cref="TryCopyLatestFrame"/> writes.</summary>
    FramePixelFormat PixelFormat { get; }

    /// <summary>
    /// Opens capture. The size and format properties are valid once this completes.
    /// </summary>
    /// <param name="targetHeight">Output height, or null for the display's native size.</param>
    /// <param name="suppressBorder">Hide any OS "being captured" indicator, where there is one.</param>
    Task StartAsync(MonitorInfo monitor, int? targetHeight, bool captureCursor, bool suppressBorder);

    /// <summary>
    /// Copies the most recent frame into <paramref name="destination"/>.
    /// </summary>
    /// <returns>False if no frame has arrived yet; the buffer is left untouched.</returns>
    bool TryCopyLatestFrame(byte[] destination);
}
