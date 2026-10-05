using Recorder.Capture;

namespace Recorder.Platform;

/// <summary>Lists the displays that can be recorded.</summary>
public interface IDisplayEnumerator
{
    /// <summary>
    /// Every display currently attached, primary first and then left to right.
    /// </summary>
    /// <remarks>Never throws; an enumeration failure is logged and yields an empty list.</remarks>
    IReadOnlyList<MonitorInfo> Enumerate();
}
