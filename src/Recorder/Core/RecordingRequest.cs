using Recorder.Capture;
using Recorder.Encoding;
using Recorder.Settings;

namespace Recorder.Core;

/// <summary>Everything a single recording needs, resolved from settings before it starts.</summary>
public sealed record RecordingRequest
{
    public required MonitorInfo Monitor { get; init; }

    /// <summary>Target output height, or null to keep the monitor's native size.</summary>
    public required int? TargetHeight { get; init; }

    public required int Fps { get; init; }
    public required bool CaptureCursor { get; init; }
    public required bool SuppressCaptureBorder { get; init; }
    public required bool RecordSystemAudio { get; init; }
    public required bool RecordMicrophone { get; init; }
    public required int AudioBitrateKbps { get; init; }

    /// <summary>
    /// A snapshot of the settings the audio pipeline reads.
    /// </summary>
    /// <remarks>
    /// Held as the settings object rather than as unpacked fields because the DSP chain reads a
    /// dozen values from it and would otherwise need every one threaded through by hand. It is a
    /// snapshot taken at start, so editing settings mid-recording cannot change a running capture.
    /// </remarks>
    public required AppSettings Settings { get; init; }

    /// <summary>Quality index handed to whichever encoder is chosen.</summary>
    public required int Quality { get; init; }

    /// <summary>Bitrate ceiling in bits per second, or 0 to derive it from the frame height.</summary>
    public required long MaxBitrateBps { get; init; }

    /// <summary>Where the finished MP4 goes.</summary>
    public required string FinalPath { get; init; }

    /// <summary>The fragmented capture file written during recording.</summary>
    public required string PartPath { get; init; }

    public required string FFmpegPath { get; init; }

    /// <summary>The encoder <see cref="EncoderProbe"/> validated on this machine.</summary>
    public required VideoEncoder Encoder { get; init; }

    /// <summary>
    /// Something to stamp onto every encoded frame — the camera bubble, when one is enabled.
    /// </summary>
    /// <remarks>
    /// Not required, and deliberately an interface rather than the camera itself: the session's job
    /// is to hand each converted frame to whatever wants to draw on it, not to know what a webcam
    /// is. A null overlay is the ordinary case and costs a null check per frame.
    /// </remarks>
    public IVideoFrameOverlay? Overlay { get; init; }
}
