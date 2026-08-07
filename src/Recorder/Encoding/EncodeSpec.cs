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

    public int FinalWidth => EncodedWidth ?? Width;
    public int FinalHeight => EncodedHeight ?? Height;

    public bool NeedsScaleFilter => FinalWidth != Width || FinalHeight != Height;

    /// <summary>Bytes per frame on the wire.</summary>
    public int FrameBytes => PixelFormat == FramePixelFormat.Nv12
        ? Width * Height * 3 / 2
        : Width * Height * 4;

    /// <summary>
    /// Video bitrate ceiling in bits per second, scaled from the frame height.
    /// </summary>
    /// <remarks>
    /// Screen content is mostly static with occasional full-frame changes, so these sit a little
    /// above typical camera-video guidance to keep text crisp during scrolling.
    /// </remarks>
    public long VideoBitrate
    {
        get
        {
            long baseRate = FinalHeight switch
            {
                <= 720 => 8_000_000,
                <= 1080 => 14_000_000,
                <= 1440 => 26_000_000,
                _ => 45_000_000,
            };
            return Fps <= 30 ? (long)(baseRate * 0.7) : baseRate;
        }
    }
}
