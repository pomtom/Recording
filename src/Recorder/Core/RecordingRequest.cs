using Recorder.Capture;
using Recorder.Encoding;

namespace Recorder.Core;

/// <summary>Everything a single recording needs, resolved from settings before it starts.</summary>
public sealed record RecordingRequest
{
    public required MonitorInfo Monitor { get; init; }

    /// <summary>Target output height, or null to keep the monitor's native size.</summary>
    public required int? TargetHeight { get; init; }

    public required int Fps { get; init; }
    public required bool CaptureCursor { get; init; }
    public required bool RecordSystemAudio { get; init; }
    public required bool RecordMicrophone { get; init; }
    public required int AudioBitrateKbps { get; init; }

    /// <summary>Where the finished MP4 goes.</summary>
    public required string FinalPath { get; init; }

    /// <summary>The fragmented capture file written during recording.</summary>
    public required string PartPath { get; init; }

    public required string FFmpegPath { get; init; }

    /// <summary>The encoder <see cref="EncoderProbe"/> validated on this machine.</summary>
    public required VideoEncoder Encoder { get; init; }
}
