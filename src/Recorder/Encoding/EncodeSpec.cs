namespace Recorder.Encoding;

/// <summary>Pixel layout of the raw frames handed to ffmpeg.</summary>
public enum FramePixelFormat
{
    /// <summary>Planar 4:2:0, 1.5 bytes/pixel. Produced by the GPU video processor.</summary>
    Nv12,

    /// <summary>Packed 32-bit BGRA straight off the capture surface. The fallback path.</summary>
    Bgra,
}

/// <summary>
/// Everything ffmpeg needs to know about one recording. Immutable for the life of a session.
/// </summary>
public sealed record EncodeSpec
{
    /// <summary>Width of the raw frames arriving on the pipe.</summary>
    public required int Width { get; init; }

    /// <summary>Height of the raw frames arriving on the pipe.</summary>
    public required int Height { get; init; }

    public required int Fps { get; init; }
    public required FramePixelFormat PixelFormat { get; init; }
    public required string PartPath { get; init; }
    public required string VideoPipeName { get; init; }
    public required string AudioPipeName { get; init; }

    /// <summary>
    /// Final encoded width. Differs from <see cref="Width"/> only on the fallback capture path,
    /// where the GPU could not scale and ffmpeg has to do it with a scale filter instead.
    /// </summary>
    public int? EncodedWidth { get; init; }

    public int? EncodedHeight { get; init; }

    public int AudioSampleRate { get; init; } = 48_000;
    public int AudioChannels { get; init; } = 2;
    public int AudioBitrateKbps { get; init; } = 192;

    /// <summary>
    /// Quality index, lower being better. Handed to whichever knob the chosen encoder exposes.
    /// </summary>
    /// <remarks>
    /// The four encoders spell this differently — <c>-cq</c>, <c>-global_quality</c>,
    /// <c>-qp_i</c>/<c>-qp_p</c>, <c>-crf</c> — but they agree closely enough on what a given number
    /// means that one value across all of them is honest, and it spares the user from having to know
    /// which encoder the probe settled on.
    /// </remarks>
    public int Quality { get; init; } = 23;

    /// <summary>Bitrate ceiling in bits per second. 0 derives one from the frame height.</summary>
    public long MaxBitrateBps { get; init; }

    public int FinalWidth => EncodedWidth ?? Width;
    public int FinalHeight => EncodedHeight ?? Height;

    public bool NeedsScaleFilter => FinalWidth != Width || FinalHeight != Height;

    /// <summary>Bytes per frame on the wire.</summary>
    public int FrameBytes => PixelFormat == FramePixelFormat.Nv12
        ? Width * Height * 3 / 2
        : Width * Height * 4;

    /// <summary>
    /// Video bitrate ceiling in bits per second.
    /// </summary>
    /// <remarks>
    /// An explicit <see cref="MaxBitrateBps"/> wins outright. Otherwise it is scaled from the frame
    /// height: screen content is mostly static with occasional full-frame changes, so these sit a
    /// little above typical camera-video guidance to keep text crisp during scrolling.
    /// </remarks>
    public long VideoBitrate
    {
        get
        {
            if (MaxBitrateBps > 0) return MaxBitrateBps;

            long baseRate = FinalHeight switch
            {
                <= 720 => 8_000_000,
                <= 1080 => 14_000_000,
                <= 1440 => 26_000_000,
                _ => 45_000_000,
            };

            // Frame rate scales the ceiling either way: a 120 FPS capture genuinely needs more
            // headroom than the 60 the ladder was written for, and 30 needs less.
            var scale = Fps <= 30 ? 0.7 : Fps <= 60 ? 1.0 : 1.0 + ((Fps - 60) / 60.0 * 0.5);
            return (long)(baseRate * scale);
        }
    }
}
