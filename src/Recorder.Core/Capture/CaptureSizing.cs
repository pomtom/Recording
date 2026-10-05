namespace Recorder.Capture;

/// <summary>Output-size rules every screen capture follows, whatever the platform.</summary>
public static class CaptureSizing
{
    /// <summary>Derives an even-sided output size that preserves the monitor's aspect ratio.</summary>
    public static (int Width, int Height) ComputeTargetSize(int sourceWidth, int sourceHeight, int? targetHeight)
    {
        // Never upscale: asking for 1440p on a 1080p monitor just wastes bitrate.
        if (targetHeight is null || targetHeight.Value >= sourceHeight)
            return (sourceWidth & ~1, sourceHeight & ~1);

        var height = targetHeight.Value;
        var width = (int)Math.Round(sourceWidth * (double)height / sourceHeight);
        return (Math.Max(2, width & ~1), Math.Max(2, height & ~1));
    }
}
