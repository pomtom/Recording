namespace Recorder.Platform;

/// <summary>Knows where this platform keeps its ffmpeg binary.</summary>
public interface IFFmpegLocator
{
    /// <summary>
    /// Absolute path to a usable ffmpeg, preparing it first if the platform needs to (for example by
    /// extracting a bundled copy). Null when there is none.
    /// </summary>
    string? TryLocate();

    /// <summary>User-facing explanation for when <see cref="TryLocate"/> finds nothing.</summary>
    string UnavailableMessage { get; }
}
