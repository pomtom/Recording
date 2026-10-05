using Recorder.Encoding;

namespace Recorder.Platform;

/// <summary>
/// Everything a recording needs from the operating system, supplied by the app that hosts Core.
/// </summary>
/// <remarks>
/// <para>This is the seam between the shared recording engine and a particular OS. The Windows app
/// implements it with Windows Graphics Capture, WASAPI and named pipes; the macOS app with
/// ScreenCaptureKit, AVFoundation and FIFOs. Core never asks which OS it is on — it only ever talks
/// to this interface — so a change made for one platform cannot reach the other.</para>
///
/// <para>Implementations are created once at startup and live for the whole process.</para>
/// </remarks>
public interface IPlatformServices
{
    /// <summary>The displays that can be recorded.</summary>
    IDisplayEnumerator Displays { get; }

    /// <summary>Opens system audio and microphone devices.</summary>
    IAudioEndpointFactory AudioEndpoints { get; }

    /// <summary>Creates the channels raw video and audio travel through on their way to ffmpeg.</summary>
    IPipeTransport Pipes { get; }

    /// <summary>
    /// Hardware H.264 encoders worth trying on this platform, best first.
    /// </summary>
    /// <remarks>
    /// <see cref="VideoEncoder.X264"/> is never listed: it is the universal fallback and
    /// <see cref="EncoderProbe"/> adds it on its own. Every candidate is still proved with a real
    /// test encode before it is trusted.
    /// </remarks>
    IReadOnlyList<VideoEncoder> HardwareEncoderCandidates { get; }

    /// <summary>A new, unstarted screen capture. One per recording.</summary>
    IScreenCapture CreateScreenCapture();

    /// <summary>
    /// Asks the OS for fine-grained sleep timing for as long as the returned handle is alive.
    /// </summary>
    /// <remarks>
    /// The frame pacer sleeps to within a millisecond of each frame's due time. Platforms whose
    /// scheduler is already that precise return a handle that does nothing. May throw; the caller
    /// treats failure as "pacing may be slightly less even", never as fatal.
    /// </remarks>
    IDisposable BeginHighResolutionTimer();
}
