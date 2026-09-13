using System.Runtime.InteropServices;
using Recorder.Utils;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace Recorder.Capture;

/// <summary>A camera that can be used for the bubble.</summary>
public sealed record CameraDeviceInfo(string DeviceId, string FriendlyName);

/// <summary>
/// Captures webcam frames and keeps the newest one ready as packed BGRA.
/// </summary>
/// <remarks>
/// <para>Built to the same shape as <see cref="ScreenCaptureService"/> — frames arrive on a pool
/// thread and are published through a three-buffer rotation so the producer and the consumers never
/// block each other. There are two consumers here rather than one: the bubble window's preview and
/// the compositor that bakes the camera into the recording.</para>
///
/// <para>Frames are requested as BGRA8 so Windows does the NV12/YUY2/MJPG conversion inside the
/// capture pipeline, where it is already optimised, rather than leaving us to do it per frame.</para>
///
/// <para>There is deliberately no frame-arrived event. Both consumers poll
/// <see cref="FramesCaptured"/> on their own schedule — the preview at the camera's rate, the
/// compositor at the encoder's — because neither wants to be driven by the camera. That is what
/// keeps the bubble live in a recording of a still screen: the pacer asks for the newest frame on
/// every frame it writes, rather than waiting to be told one exists.</para>
/// </remarks>
public sealed class CameraCaptureService : IDisposable
{
    private const int BufferCount = 3;

    private readonly object _gate = new();

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;

    private byte[][] _buffers = [];
    private int _latestIndex = -1;
    private int _readIndex = -1;
    private long _framesCaptured;

    private bool _disposed;

    /// <summary>Raised when the camera stops delivering frames for reasons we cannot recover from.</summary>
    public event EventHandler? CameraLost;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public int FrameBytes => Width * Height * 4;

    public bool IsRunning => _reader is not null;

    /// <summary>Non-null when the camera could not be opened, phrased for the user.</summary>
    public string? Error { get; private set; }

    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);

    /// <summary>
    /// Lists the cameras attached to this machine.
    /// </summary>
    /// <remarks>
    /// Enumerated as frame-source groups rather than as raw video devices because that is what
    /// <see cref="MediaFrameReader"/> consumes, and because a single physical camera can expose
    /// several streams (colour, infrared, depth) of which only one is the one anybody means.
    /// </remarks>
    public static async Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync()
    {
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            return groups
                .Where(HasColorVideoSource)
                .Select(g => new CameraDeviceInfo(g.Id, g.DisplayName))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Enumerating cameras failed.");
            return [];
        }
    }

    private static bool HasColorVideoSource(MediaFrameSourceGroup group) =>
        group.SourceInfos.Any(IsColorVideoSource);

    private static bool IsColorVideoSource(MediaFrameSourceInfo info) =>
        info.SourceKind == MediaFrameSourceKind.Color &&
        info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord;

    /// <summary>
    /// Opens a camera and starts delivering frames.
    /// </summary>
    /// <param name="deviceId">Frame-source group id, or null for the first camera found.</param>
    /// <returns>True when frames are flowing; false leaves <see cref="Error"/> set.</returns>
    public async Task<bool> StartAsync(string? deviceId, int width, int height, int fps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();
        Error = null;

        try
        {
            var group = await ResolveGroupAsync(deviceId);
            if (group is null)
            {
                Error = "No camera was found on this machine.";
                return false;
            }

            var capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                // CPU memory: the frames have to be reshaped (mask, mirror, border) and shown in a
                // WPF preview anyway, so a GPU surface would only have to be read back — and it
                // would live on a different D3D device from the capture pipeline in any case.
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });

            _capture = capture;

            var sourceInfo = group.SourceInfos.First(IsColorVideoSource);
            var source = capture.FrameSources[sourceInfo.Id];

            await ApplyFormatAsync(source, width, height, fps);

            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;

            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnFrameArrived;
                reader.Dispose();
                Error = DescribeStartStatus(status);
                Stop();
                return false;
            }

            _reader = reader;
            capture.Failed += OnCaptureFailed;

            Log.Warn($"Camera started: {group.DisplayName} {Width}x{Height}.");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            // The Windows camera privacy switch. Worth its own message: nothing in the generic
            // failure text would tell someone to go and look in Windows Settings.
            Log.Warn(ex, "Camera access was denied.");
            Error = "Windows is blocking camera access. Turn it on under " +
                    "Settings > Privacy & security > Camera.";
            Stop();
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Opening the camera failed.");
            Error = "The camera could not be opened. It may be in use by another application.";
            Stop();
            return false;
        }
    }

    private static string DescribeStartStatus(MediaFrameReaderStartStatus status) => status switch
    {
        MediaFrameReaderStartStatus.DeviceNotAvailable => "The camera is no longer available.",
        MediaFrameReaderStartStatus.ExclusiveControlNotAvailable =>
            "Another application is using the camera.",
        MediaFrameReaderStartStatus.OutputFormatNotSupported =>
            "This camera does not offer a format the recorder can use.",
        _ => "The camera could not be started.",
    };

    private static async Task<MediaFrameSourceGroup?> ResolveGroupAsync(string? deviceId)
    {
        var groups = (await MediaFrameSourceGroup.FindAllAsync()).Where(HasColorVideoSource).ToList();
        if (groups.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var match = groups.FirstOrDefault(g => g.Id == deviceId);
            if (match is not null) return match;

            // A remembered camera that has been unplugged should not leave the user with nothing;
            // falling back is friendlier than failing, and the settings window still shows the truth.
            Log.Warn("The configured camera was not found; using the first available one.");
        }

        return groups[0];
    }

    /// <summary>
    /// Picks the closest format the camera actually offers and applies it.
    /// </summary>
    /// <remarks>
    /// <para>This has to happen <em>before</em> the frame reader is created, or the reader's Bgra8
    /// request is satisfied against whatever the driver defaults to. Asking a 1080p60 MJPG default
    /// for BGRA means Windows JPEG-decodes 1080p sixty times a second to fill a bubble a few hundred
    /// pixels across — all of it on the CPU, all of it thrown away.</para>
    ///
    /// <para>Scored on pixel-count distance first, then frame-rate distance, then subtype: a bubble
    /// is far more forgiving of the wrong resolution than of the wrong frame rate, which shows up as
    /// visible judder, and MJPG is the last resort because it is the only one that costs a decode.</para>
    /// </remarks>
    private async Task ApplyFormatAsync(MediaFrameSource source, int width, int height, int fps)
    {
        var candidates = source.SupportedFormats
            .Where(f => f.VideoFormat is not null && f.VideoFormat.Width > 0 && f.VideoFormat.Height > 0)
            .ToList();

        if (candidates.Count == 0)
        {
            var current = source.CurrentFormat?.VideoFormat;
            Width = current is not null && current.Width > 0 ? (int)current.Width : width;
            Height = current is not null && current.Height > 0 ? (int)current.Height : height;
            AllocateBuffers();
            return;
        }

        var wanted = (long)width * height;
        var best = candidates
            .OrderBy(f => Math.Abs(((long)f.VideoFormat.Width * f.VideoFormat.Height) - wanted))
            .ThenBy(f => Math.Abs(RateOf(f) - fps))
            .ThenBy(DecodeCost)
            .First();

        try
        {
            await source.SetFormatAsync(best);
        }
        catch (Exception ex)
        {
            // Some drivers refuse a format they themselves listed. The current one still works.
            Log.Warn(ex, "Setting the camera format failed; keeping the driver's default.");
            best = source.CurrentFormat ?? best;
        }

        Width = (int)best.VideoFormat.Width;
        Height = (int)best.VideoFormat.Height;
        AllocateBuffers();
    }

    /// <summary>Ranks a format's subtype by what it costs to turn into BGRA. Lower is cheaper.</summary>
    private static int DecodeCost(MediaFrameFormat format) => format.Subtype?.ToUpperInvariant() switch
    {
        "NV12" or "YUY2" or "RGB32" or "ARGB32" or "BGRA8" => 0,
        "MJPG" or "JPEG" => 2,
        _ => 1,
    };

    private static double RateOf(MediaFrameFormat format)
    {
        var rate = format.FrameRate;
        if (rate is null || rate.Denominator == 0) return 0;
        return rate.Numerator / (double)rate.Denominator;
    }

    private void AllocateBuffers()
    {
        lock (_gate)
        {
            _buffers = new byte[BufferCount][];
            for (var i = 0; i < BufferCount; i++) _buffers[i] = new byte[FrameBytes];
            _latestIndex = -1;
            _readIndex = -1;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;

            lock (_gate)
            {
                if (_disposed) return;

                // A camera that renegotiates its format mid-stream would otherwise overflow the
                // buffers sized for the old one.
                if (bitmap.PixelWidth != Width || bitmap.PixelHeight != Height)
                {
                    Width = bitmap.PixelWidth;
                    Height = bitmap.PixelHeight;
                    _buffers = new byte[BufferCount][];
                    for (var i = 0; i < BufferCount; i++) _buffers[i] = new byte[FrameBytes];
                    _latestIndex = -1;
                    _readIndex = -1;
                }

                var index = PickWriteBuffer();
                if (index >= 0 && CopyBitmap(bitmap, _buffers[index]))
                {
                    _latestIndex = index;
                    Interlocked.Increment(ref _framesCaptured);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "A camera frame could not be processed; skipping it.");
        }
    }

    /// <summary>Copies a locked BGRA8 bitmap into a tightly packed buffer, stripping row padding.</summary>
    private static unsafe bool CopyBitmap(SoftwareBitmap bitmap, byte[] destination)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out var data, out var capacity);
        if (data is null || capacity == 0) return false;

        var plane = buffer.GetPlaneDescription(0);
        var rowBytes = plane.Width * 4;
        if (destination.Length < rowBytes * plane.Height) return false;

        fixed (byte* target = destination)
        {
            if (plane.Stride == rowBytes)
            {
                Buffer.MemoryCopy(
                    data + plane.StartIndex, target, destination.Length, (long)rowBytes * plane.Height);
                return true;
            }

            for (var y = 0; y < plane.Height; y++)
            {
                Buffer.MemoryCopy(
                    data + plane.StartIndex + ((long)y * plane.Stride),
                    target + ((long)y * rowBytes),
                    destination.Length - ((long)y * rowBytes),
                    rowBytes);
            }
        }

        return true;
    }

    /// <summary>Finds a buffer that is neither published nor being read.</summary>
    private int PickWriteBuffer()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            if (i != _latestIndex && i != _readIndex) return i;
        }
        return -1;
    }

    /// <summary>
    /// Copies the newest camera frame into <paramref name="destination"/>.
    /// </summary>
    /// <returns>False when no frame has arrived yet.</returns>
    public bool TryCopyLatestFrame(byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        byte[] source;
        int index;

        lock (_gate)
        {
            if (_disposed || _latestIndex < 0) return false;
            index = _latestIndex;
            _readIndex = index;
            source = _buffers[index];
        }

        try
        {
            var length = Math.Min(source.Length, destination.Length);
            Buffer.BlockCopy(source, 0, destination, 0, length);
            return true;
        }
        finally
        {
            lock (_gate)
            {
                if (_readIndex == index) _readIndex = -1;
            }
        }
    }

    /// <summary>Closes the camera. Safe to call when nothing is open.</summary>
    public void Stop()
    {
        var reader = _reader;
        var capture = _capture;

        _reader = null;
        _capture = null;

        if (capture is not null)
        {
            try { capture.Failed -= OnCaptureFailed; } catch { }
        }

        if (reader is not null)
        {
            try { reader.FrameArrived -= OnFrameArrived; } catch { }
            try { reader.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { Log.Warn(ex, "Stopping the camera reader failed."); }
            try { reader.Dispose(); } catch { }
        }

        try { capture?.Dispose(); }
        catch (Exception ex) { Log.Warn(ex, "Disposing the camera failed."); }

        lock (_gate)
        {
            _latestIndex = -1;
            _readIndex = -1;
        }
    }

    /// <summary>
    /// The camera died under us — unplugged, suspended, or taken by something else.
    /// </summary>
    /// <remarks>
    /// Raised on a Media Foundation thread, so it only records the fact and hands it on; the owner
    /// is responsible for getting to its own thread before touching any UI. Nothing is stopped here
    /// either, because stopping a <see cref="MediaCapture"/> from inside its own failure callback is
    /// a good way to deadlock.
    /// </remarks>
    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        Log.Warn($"The camera failed (0x{args.Code:X}): {args.Message}");

        Error = "The camera stopped working. It may have been disconnected.";

        try { CameraLost?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Warn(ex, "A CameraLost handler threw."); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>
    /// The COM escape hatch from a WinRT memory buffer to the bytes behind it.
    /// </summary>
    /// <remarks>
    /// <c>IBuffer.AsBuffer()</c> and the rest of the WindowsRuntimeBuffer extensions were dropped in
    /// .NET 5, so this interface is the supported way to read a <see cref="SoftwareBitmap"/> without
    /// a per-frame marshalling copy.
    /// </remarks>
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
