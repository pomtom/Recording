using System.IO;
using Recorder.Platform;

namespace Recorder.Encoding;

/// <summary>
/// Finds a usable ffmpeg and remembers where it is.
/// </summary>
/// <remarks>
/// Where ffmpeg lives is a platform matter — the Windows build carries it embedded in the exe and
/// unpacks it on first use, the macOS build ships it inside the app bundle — so the search itself is
/// delegated to an <see cref="IFFmpegLocator"/>. This class only makes the answer cheap to ask for
/// repeatedly and re-resolves if the file disappears.
/// </remarks>
public sealed class FFmpegProvisioner
{
    private readonly IFFmpegLocator _locator;
    private readonly object _gate = new();
    private string? _resolved;

    public FFmpegProvisioner(IFFmpegLocator locator)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <summary>Absolute path to ffmpeg. Throws <see cref="FFmpegUnavailableException"/> if none can be found.</summary>
    public string GetFFmpegPath()
    {
        lock (_gate)
        {
            if (_resolved is not null && File.Exists(_resolved)) return _resolved;
            _resolved = _locator.TryLocate() ?? throw new FFmpegUnavailableException(_locator.UnavailableMessage);
            return _resolved;
        }
    }
}

public sealed class FFmpegUnavailableException : Exception
{
    public FFmpegUnavailableException(string message) : base(message) { }
}
