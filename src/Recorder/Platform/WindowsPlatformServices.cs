using Recorder.Capture;
using Recorder.Encoding;
using Recorder.Utils;

namespace Recorder.Platform;

/// <summary>
/// The Windows implementation of everything Core needs from the OS: Windows Graphics Capture,
/// WASAPI, named pipes, and NVENC / Quick Sync / AMF.
/// </summary>
public sealed class WindowsPlatformServices : IPlatformServices
{
    /// <summary>The encoders offered in Settings on Windows, in the order they are shown.</summary>
    public static IReadOnlyList<VideoEncoder> SelectableEncoders { get; } =
        [VideoEncoder.Nvenc, VideoEncoder.QuickSync, VideoEncoder.Amf, VideoEncoder.X264];

    public IDisplayEnumerator Displays { get; } = new WindowsDisplayEnumerator();

    public IAudioEndpointFactory AudioEndpoints { get; } = new WindowsAudioEndpoints();

    public IPipeTransport Pipes { get; } = new WindowsNamedPipeTransport();

    public IReadOnlyList<VideoEncoder> HardwareEncoderCandidates { get; } =
        [VideoEncoder.Nvenc, VideoEncoder.QuickSync, VideoEncoder.Amf];

    public IScreenCapture CreateScreenCapture() => new ScreenCaptureService();

    public IDisposable BeginHighResolutionTimer()
    {
        NativeMethods.TimeBeginPeriod(1);
        return new TimerResolution();
    }

    private sealed class TimerResolution : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { NativeMethods.TimeEndPeriod(1); } catch { }
        }
    }

    private sealed class WindowsDisplayEnumerator : IDisplayEnumerator
    {
        public IReadOnlyList<MonitorInfo> Enumerate() => MonitorEnumerator.Enumerate();
    }
}
