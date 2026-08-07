using Recorder.Encoding;
using Recorder.Utils;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Recorder.Capture;

/// <summary>
/// Captures one monitor with Windows Graphics Capture and keeps the newest converted frame ready.
/// </summary>
/// <remarks>
/// <para>WGC only delivers a frame when something on screen actually changes, so this class is
/// deliberately <em>not</em> the thing that decides the output frame rate. It maintains a
/// "most recent frame" buffer; the pacer in <c>RecordingSession</c> samples that buffer on a fixed
/// schedule, repeating the last frame when the screen is idle. That split is what produces a
/// constant-frame-rate file from a variable-rate source.</para>
///
/// <para>Frames arrive on a pool thread (the frame pool is created free-threaded, so no dispatcher
/// is involved) and are published through a three-buffer rotation: one buffer being filled, one
/// holding the latest complete frame, one possibly being read. Three is the minimum that lets the
/// capture callback and the pacer run concurrently without ever blocking each other.</para>
/// </remarks>
public sealed class ScreenCaptureService : IDisposable
{
    private const int BufferCount = 3;

    private readonly object _gate = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private FrameConverter? _converter;

    private byte[][] _buffers = [];
    private int _latestIndex = -1;
    private int _readIndex = -1;
    private long _framesCaptured;

    private bool _started;
    private bool _disposed;

    /// <summary>Raised when the captured monitor disappears (unplugged, or the session was closed).</summary>
    public event EventHandler? CaptureLost;

    public int OutputWidth => _converter?.OutputWidth ?? 0;
    public int OutputHeight => _converter?.OutputHeight ?? 0;
    public int TargetWidth => _converter?.TargetWidth ?? 0;
    public int TargetHeight => _converter?.TargetHeight ?? 0;
    public FramePixelFormat PixelFormat => _converter?.PixelFormat ?? FramePixelFormat.Bgra;
    public int FrameBytes => _converter?.FrameBytes ?? 0;
    public bool UsesGpuConversion => _converter?.UsesGpuConversion ?? false;

    /// <summary>Frames delivered by Windows so far — a health signal, not a timeline.</summary>
    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);

    public static bool IsSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch (Exception ex)
        {
            Log.Error(ex, "GraphicsCaptureSession.IsSupported threw.");
            return false;
        }
    }

    /// <summary>
    /// Opens a capture session for <paramref name="monitor"/>, producing frames at
    /// <paramref name="targetHeight"/> (width derived from the monitor's aspect ratio).
    /// </summary>
    /// <param name="targetHeight">Desired output height, or null to keep the native size.</param>
    public void Start(MonitorInfo monitor, int? targetHeight, bool captureCursor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("Capture has already been started.");

            if (!IsSupported())
                throw new NotSupportedException(
                    "Windows Graphics Capture is not available on this system. Windows 10 version 1903 or newer is required.");

            CreateDevice();

            _item = Direct3DInterop.CreateItemForMonitor(monitor.Handle);
            _item.Closed += OnItemClosed;

            var sourceWidth = _item.Size.Width;
            var sourceHeight = _item.Size.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0)
                throw new InvalidOperationException($"The monitor reported an unusable size ({sourceWidth}×{sourceHeight}).");

            var (targetW, targetH) = ComputeTargetSize(sourceWidth, sourceHeight, targetHeight);

            _converter = FrameConverter.Create(_device!, _context!, sourceWidth, sourceHeight, targetW, targetH);
            AllocateBuffers(_converter.FrameBytes);

            // Two buffers in the pool is enough: we consume each frame immediately and never
            // hold one across callbacks, so a deeper pool would only add latency and memory.
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                size: new SizeInt32(sourceWidth, sourceHeight));

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            ApplySessionOptions(_session, captureCursor);
            _session.StartCapture();

            _started = true;

            Log.Warn($"Capture started: {monitor.DeviceId} {sourceWidth}×{sourceHeight} -> " +
                     $"{_converter.OutputWidth}×{_converter.OutputHeight} " +
                     $"{_converter.PixelFormat} (GPU conversion: {_converter.UsesGpuConversion}).");
        }
    }

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

    private void CreateDevice()
    {
        // BgraSupport is required for WGC surfaces; VideoSupport is what lets FrameConverter
        // query out an ID3D11VideoDevice for the GPU scale/convert path.
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };

        var result = D3D11.D3D11CreateDevice(
            IntPtr.Zero, DriverType.Hardware, flags, levels, out var device, out var context);

        if (result.Failure || device is null)
        {
            // WARP is a software rasteriser: slower, but it keeps the app working inside VMs and
            // on machines with a broken or disabled GPU driver.
            Log.Warn($"Hardware D3D11 device creation failed ({result.Description}); trying WARP.");
            result = D3D11.D3D11CreateDevice(
                IntPtr.Zero, DriverType.Warp, flags, levels, out device, out context);
            result.CheckError();
        }

        _device = device;
        _context = context;
        _winrtDevice = Direct3DInterop.CreateWinRtDevice(_device!);
    }

    private static void ApplySessionOptions(GraphicsCaptureSession session, bool captureCursor)
    {
        try
        {
            session.IsCursorCaptureEnabled = captureCursor;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "IsCursorCaptureEnabled is unavailable on this build of Windows.");
        }

        // Windows 11 draws a yellow "being captured" border around the recorded monitor. The
        // property that turns it off only exists on 22000+; on Windows 10 there is no border to
        // suppress in the first place.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            try
            {
                session.IsBorderRequired = false;
            }
            catch (Exception ex)
            {
                // Some 22000 builds gate this behind a capability; the border is cosmetic.
                Log.Warn(ex, "Could not suppress the capture border.");
            }
        }
    }

    private void AllocateBuffers(int frameBytes)
    {
        _buffers = new byte[BufferCount][];
        for (var i = 0; i < BufferCount; i++) _buffers[i] = new byte[frameBytes];
        _latestIndex = -1;
        _readIndex = -1;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            lock (_gate)
            {
                if (_disposed || _converter is null) return;

                // A resolution change invalidates both the pool and the converter's input size.
                if (frame.ContentSize.Width != _converter.SourceWidth ||
                    frame.ContentSize.Height != _converter.SourceHeight)
                {
                    HandleContentSizeChanged(frame.ContentSize);
                    return;   // this frame belongs to the old geometry; skip it
                }

                var index = PickWriteBuffer();
                if (index < 0) return;

                using var texture = Direct3DInterop.GetTexture(frame.Surface);
                if (_converter.TryConvert(texture, _buffers[index]))
                {
                    _latestIndex = index;
                    Interlocked.Increment(ref _framesCaptured);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "A captured frame could not be processed; skipping it.");
        }
    }

    /// <summary>
    /// Rebuilds the pipeline after the monitor changed resolution mid-recording.
    /// </summary>
    /// <remarks>
    /// On the GPU path the output size is fixed independently of the input, so the recording
    /// continues seamlessly at the same dimensions. On the CPU fallback the wire format is tied to
    /// the source size and cannot change once ffmpeg has started, so the recording keeps its last
    /// good frame rather than being corrupted.
    /// </remarks>
    private void HandleContentSizeChanged(SizeInt32 newSize)
    {
        if (newSize.Width <= 0 || newSize.Height <= 0) return;

        try
        {
            var previous = _converter!;
            if (!previous.UsesGpuConversion)
            {
                Log.Error($"The display changed to {newSize.Width}×{newSize.Height} during a recording " +
                          "and this machine has no GPU scaler, so the video will hold its last frame.");
                return;
            }

            _framePool!.Recreate(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                size: newSize);

            var rebuilt = FrameConverter.Create(
                _device!, _context!, newSize.Width, newSize.Height,
                previous.TargetWidth, previous.TargetHeight);

            if (rebuilt.FrameBytes != previous.FrameBytes)
            {
                // Would change the wire format under a running ffmpeg; keep the old converter.
                Log.Error("Display resolution changed in a way that cannot be applied mid-recording.");
                rebuilt.Dispose();
                return;
            }

            _converter = rebuilt;
            previous.Dispose();

            Log.Warn($"Display resolution changed to {newSize.Width}×{newSize.Height}; capture re-initialised.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to handle a display resolution change.");
        }
    }

    /// <summary>Finds a buffer that is neither published nor being read.</summary>
    private int PickWriteBuffer()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            if (i != _latestIndex && i != _readIndex) return i;
        }
        return -1;   // unreachable with three buffers and at most two exclusions
    }

    /// <summary>
    /// Copies the newest frame into <paramref name="destination"/>.
    /// </summary>
    /// <returns>False when no frame has been captured yet.</returns>
    public bool TryCopyLatestFrame(byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        byte[]? source = null;
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

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        Log.Warn("The capture item closed — the monitor was disconnected or the session ended.");
        try { CaptureLost?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Warn(ex, "CaptureLost handler threw."); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;

            if (_framePool is not null)
            {
                try { _framePool.FrameArrived -= OnFrameArrived; } catch { }
            }
            if (_item is not null)
            {
                try { _item.Closed -= OnItemClosed; } catch { }
            }

            try { _session?.Dispose(); } catch (Exception ex) { Log.Warn(ex, "Disposing the capture session failed."); }
            try { _framePool?.Dispose(); } catch (Exception ex) { Log.Warn(ex, "Disposing the frame pool failed."); }
            try { _converter?.Dispose(); } catch { }
            try { _winrtDevice?.Dispose(); } catch { }
            try { _context?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }

            _session = null;
            _framePool = null;
            _item = null;
            _converter = null;
            _winrtDevice = null;
            _context = null;
            _device = null;
            _buffers = [];
        }
    }
}
