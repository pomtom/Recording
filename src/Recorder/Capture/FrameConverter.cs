using System.Runtime.InteropServices;
using Recorder.Encoding;
using Recorder.Utils;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Recorder.Capture;

/// <summary>
/// Turns a captured desktop texture into the raw bytes ffmpeg expects.
/// </summary>
/// <remarks>
/// <para><b>Primary path.</b> A D3D11 video processor does the scale <em>and</em> the RGB→NV12
/// colour conversion in a single GPU pass. That halves the data crossing the pipe — NV12 is
/// 1.5 bytes/pixel against BGRA's 4, so 1080p60 drops from roughly 500 MB/s to 186 MB/s — and
/// keeps the CPU out of the pixel-format business entirely. This is where most of the "low CPU
/// usage" requirement is won.</para>
///
/// <para><b>Fallback path.</b> Video processors are not universally available (basic display
/// adapters, remote sessions, some virtualised GPUs). When one cannot be created the converter
/// copies BGRA straight off the capture surface at native resolution and lets ffmpeg scale and
/// convert on the CPU. Slower and heavier on the pipe, but it always works.</para>
/// </remarks>
public sealed class FrameConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly object _gate = new();

    // GPU path
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _nv12Target;
    private ID3D11VideoProcessorOutputView? _outputView;
    private readonly Dictionary<IntPtr, ID3D11VideoProcessorInputView> _inputViews = [];

    // Shared
    private ID3D11Texture2D? _staging;
    private bool _disposed;

    public int SourceWidth { get; }
    public int SourceHeight { get; }

    /// <summary>Dimensions of the frames this converter emits.</summary>
    public int OutputWidth { get; }
    public int OutputHeight { get; }

    /// <summary>Dimensions the finished video should have. Equals the output size on the GPU path.</summary>
    public int TargetWidth { get; }
    public int TargetHeight { get; }

    public FramePixelFormat PixelFormat { get; }

    public bool UsesGpuConversion => _processor is not null;

    /// <summary>Bytes one converted frame occupies.</summary>
    public int FrameBytes => PixelFormat == FramePixelFormat.Nv12
        ? OutputWidth * OutputHeight * 3 / 2
        : OutputWidth * OutputHeight * 4;

    private FrameConverter(
        ID3D11Device device,
        ID3D11DeviceContext context,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        bool gpu)
    {
        _device = device;
        _context = context;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        TargetWidth = targetWidth;
        TargetHeight = targetHeight;

        if (gpu)
        {
            PixelFormat = FramePixelFormat.Nv12;
            OutputWidth = targetWidth;
            OutputHeight = targetHeight;
        }
        else
        {
            // No GPU scaler: ship the frame at native size and let ffmpeg resize it.
            PixelFormat = FramePixelFormat.Bgra;
            OutputWidth = sourceWidth;
            OutputHeight = sourceHeight;
        }
    }

    /// <summary>
    /// Builds a converter, preferring the GPU path and silently degrading if it cannot be set up.
    /// </summary>
    /// <param name="targetWidth">Desired output width. Rounded down to an even number for NV12.</param>
    public static FrameConverter Create(
        ID3D11Device device,
        ID3D11DeviceContext context,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        // NV12 subsamples chroma 2×2, so both dimensions must be even.
        targetWidth = Math.Max(2, targetWidth & ~1);
        targetHeight = Math.Max(2, targetHeight & ~1);

        var converter = new FrameConverter(device, context, sourceWidth, sourceHeight, targetWidth, targetHeight, gpu: true);
        try
        {
            converter.InitializeGpuPath();
            converter.CreateStaging(Format.NV12, targetWidth, targetHeight);
            return converter;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "GPU frame conversion unavailable; falling back to CPU-side BGRA.");
            converter.DisposeGpuResources();
        }

        var fallback = new FrameConverter(device, context, sourceWidth, sourceHeight, targetWidth, targetHeight, gpu: false);
        fallback.CreateStaging(Format.B8G8R8A8_UNorm, sourceWidth, sourceHeight);
        return fallback;
    }

    private void InitializeGpuPath()
    {
        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)SourceWidth,
            InputHeight = (uint)SourceHeight,
            OutputWidth = (uint)TargetWidth,
            OutputHeight = (uint)TargetHeight,
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.PlaybackNormal,
        };

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);

        // Ask before assuming: a driver that cannot write NV12 would fail later at Blt time,
        // by which point we would already be recording.
        var support = _enumerator.CheckVideoProcessorFormat(Format.NV12);
        if (!support.HasFlag(VideoProcessorFormatSupport.Output))
            throw new NotSupportedException("The video processor cannot output NV12.");

        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        var targetDesc = new Texture2DDescription
        {
            Width = (uint)TargetWidth,
            Height = (uint)TargetHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,   // required to create an output view
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _nv12Target = _device.CreateTexture2D(targetDesc);

        _outputView = _videoDevice.CreateVideoProcessorOutputView(
            _nv12Target, _enumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });

        ConfigureColorSpaces();
    }

    /// <summary>
    /// Pins down how the driver interprets input and output pixels.
    /// </summary>
    /// <remarks>
    /// Desktop content is full-range RGB; H.264 conventionally carries limited-range BT.709.
    /// Leaving these at their defaults makes the driver guess, and different vendors guess
    /// differently, which shows up as visibly washed-out or crushed recordings on some machines.
    /// The matching tags are written into the MP4 by <see cref="FFmpegArgumentBuilder"/>.
    /// </remarks>
    private void ConfigureColorSpaces()
    {
        if (_videoContext is null || _processor is null) return;

        try
        {
            var input = new VideoProcessorColorSpace
            {
                Usage = 0,           // playback
                RGB_Range = 0,       // full range 0-255
                YCbCr_Matrix = 1,    // BT.709
                YCbCr_xvYCC = 0,
                Nominal_Range = 2,   // 0-255
            };
            _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, input);

            var output = new VideoProcessorColorSpace
            {
                Usage = 0,
                RGB_Range = 0,
                YCbCr_Matrix = 1,    // BT.709
                YCbCr_xvYCC = 0,
                Nominal_Range = 1,   // 16-235
            };
            _videoContext.VideoProcessorSetOutputColorSpace(_processor, output);

            _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        }
        catch (Exception ex)
        {
            // Not fatal: the driver default is usually right, just not guaranteed.
            Log.Warn(ex, "Could not set video processor colour spaces; using driver defaults.");
        }
    }

    private void CreateStaging(Format format, int width, int height)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        _staging = _device.CreateTexture2D(desc);
    }

    /// <summary>
    /// Converts one captured texture into <paramref name="destination"/>.
    /// </summary>
    /// <returns>False if the frame could not be converted; the caller should keep the previous frame.</returns>
    public bool TryConvert(ID3D11Texture2D source, byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        lock (_gate)
        {
            if (_disposed || _staging is null) return false;

            try
            {
                if (_processor is not null && _videoContext is not null && _outputView is not null)
                {
                    BlitThroughVideoProcessor(source);
                    _context.CopyResource(_staging, _nv12Target!);
                }
                else
                {
                    _context.CopyResource(_staging, source);
                }

                return CopyOut(destination);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Frame conversion failed; dropping this frame.");
                return false;
            }
        }
    }

    private void BlitThroughVideoProcessor(ID3D11Texture2D source)
    {
        var inputView = GetOrCreateInputView(source);

        var srcRect = new RawRect(0, 0, SourceWidth, SourceHeight);
        var dstRect = new RawRect(0, 0, TargetWidth, TargetHeight);

        _videoContext!.VideoProcessorSetStreamSourceRect(_processor!, 0, true, srcRect);
        _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, dstRect);
        _videoContext.VideoProcessorSetOutputTargetRect(_processor!, true, dstRect);

        var stream = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = inputView,
        };

        _videoContext.VideoProcessorBlt(_processor!, _outputView!, 0, [stream]).CheckError();
    }

    /// <summary>
    /// Input views are cached per source texture.
    /// </summary>
    /// <remarks>
    /// The capture frame pool rotates through a small fixed set of textures, so without a cache we
    /// would create and destroy a view 60 times a second for the same handful of resources.
    /// </remarks>
    private ID3D11VideoProcessorInputView GetOrCreateInputView(ID3D11Texture2D source)
    {
        var key = source.NativePointer;
        if (_inputViews.TryGetValue(key, out var cached)) return cached;

        var view = _videoDevice!.CreateVideoProcessorInputView(
            source, _enumerator!,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });

        // Guard against unbounded growth if a driver hands out fresh textures every frame.
        if (_inputViews.Count >= 16) ClearInputViews();

        _inputViews[key] = view;
        return view;
    }

    /// <summary>Copies the mapped staging texture into a tightly packed managed buffer.</summary>
    private bool CopyOut(byte[] destination)
    {
        var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            if (mapped.DataPointer == IntPtr.Zero) return false;

            var rowPitch = (int)mapped.RowPitch;

            if (PixelFormat == FramePixelFormat.Nv12)
            {
                // NV12 staging memory is the full-resolution Y plane followed immediately by the
                // half-height interleaved UV plane, both using the same row pitch.
                var required = OutputWidth * OutputHeight * 3 / 2;
                if (destination.Length < required) return false;

                CopyPlane(mapped.DataPointer, rowPitch, destination, 0, OutputWidth, OutputHeight);

                var uvSource = mapped.DataPointer + (rowPitch * OutputHeight);
                CopyPlane(uvSource, rowPitch, destination, OutputWidth * OutputHeight, OutputWidth, OutputHeight / 2);
            }
            else
            {
                var stride = OutputWidth * 4;
                var required = stride * OutputHeight;
                if (destination.Length < required) return false;

                CopyPlane(mapped.DataPointer, rowPitch, destination, 0, stride, OutputHeight);
            }

            return true;
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    /// <summary>Row-by-row copy that strips the driver's row padding.</summary>
    private static void CopyPlane(IntPtr source, int sourcePitch, byte[] destination, int destinationOffset, int rowBytes, int rows)
    {
        if (sourcePitch == rowBytes)
        {
            // No padding — one bulk copy.
            Marshal.Copy(source, destination, destinationOffset, rowBytes * rows);
            return;
        }

        for (var y = 0; y < rows; y++)
        {
            Marshal.Copy(source + (y * sourcePitch), destination, destinationOffset + (y * rowBytes), rowBytes);
        }
    }

    private void ClearInputViews()
    {
        foreach (var view in _inputViews.Values)
        {
            try { view.Dispose(); } catch { }
        }
        _inputViews.Clear();
    }

    private void DisposeGpuResources()
    {
        ClearInputViews();
        try { _outputView?.Dispose(); } catch { }
        try { _nv12Target?.Dispose(); } catch { }
        try { _processor?.Dispose(); } catch { }
        try { _enumerator?.Dispose(); } catch { }
        try { _videoContext?.Dispose(); } catch { }
        try { _videoDevice?.Dispose(); } catch { }
        _outputView = null;
        _nv12Target = null;
        _processor = null;
        _enumerator = null;
        _videoContext = null;
        _videoDevice = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeGpuResources();
            try { _staging?.Dispose(); } catch { }
            _staging = null;
        }
    }
}
