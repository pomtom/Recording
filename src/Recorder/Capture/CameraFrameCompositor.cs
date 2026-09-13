using Recorder.Encoding;
using Recorder.Settings;
using Recorder.Utils;

namespace Recorder.Capture;

/// <summary>What the overlay is drawing into, fixed for the life of one recording.</summary>
public readonly record struct OverlayTarget(
    int OutputWidth,
    int OutputHeight,
    int SourceWidth,
    int SourceHeight,
    FramePixelFormat PixelFormat,
    MonitorInfo Monitor);

/// <summary>Something that stamps itself onto each encoded frame.</summary>
public interface IVideoFrameOverlay
{
    void Begin(OverlayTarget target);

    /// <summary>Draws into a converted frame, in place. Called on the pacer thread.</summary>
    void Compose(byte[] frame);

    void End();
}

/// <summary>The bubble's appearance, snapshotted so the pacer thread never reads live settings.</summary>
public readonly record struct BubbleStyle(
    CameraShape Shape,
    bool Mirror,
    double BorderThickness,
    uint BorderColor,
    double Opacity);

/// <summary>
/// Bakes the camera into each encoded frame at the bubble's on-screen position.
/// </summary>
/// <remarks>
/// <para><b>Why this runs on the pacer thread.</b> Windows Graphics Capture only delivers a frame
/// when the screen changes — <see cref="ScreenCaptureService"/> says so in its own remarks, and the
/// pacer exists precisely to turn that variable-rate source into constant-rate output. Compositing
/// where the screen frames arrive would therefore freeze the bubble the moment the screen went
/// still, which for a screencast is not an edge case but the normal case: someone talking over a
/// motionless slide would record a photograph of themselves. Blending after conversion, on the
/// thread that writes every frame, makes that failure unrepresentable.</para>
///
/// <para><b>Why it is cheap.</b> The expensive part — crop, mirror, resample, mask, border, colour
/// conversion — happens once per <em>camera</em> frame and is cached in a <see cref="CameraSprite"/>.
/// What runs per encoded frame is an integer lerp over the bubble's rectangle, and the bubble's
/// interior is fully opaque so most of it is a straight row copy. A 300x300 bubble in a 1080p NV12
/// frame touches about 154 KB, against the 186 MB/s the pipeline already moves.</para>
/// </remarks>
public sealed class CameraFrameCompositor : IVideoFrameOverlay
{
    /// <summary>
    /// How long the camera may go quiet before the bubble stops being drawn.
    /// </summary>
    /// <remarks>
    /// The second guard against recording a frozen face. A camera that has been unplugged, suspended
    /// or hijacked by another application stops delivering without necessarily raising anything;
    /// leaving the last good frame stamped onto every subsequent frame for the rest of a two-hour
    /// recording is far worse than dropping the bubble.
    /// </remarks>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2);

    private readonly CameraCaptureService _camera;
    private readonly CameraSprite _sprite = new();
    private readonly object _gate = new();

    private OverlayTarget _target;
    private bool _active;

    private byte[] _cameraFrame = [];
    private int _cameraWidth;
    private int _cameraHeight;

    private PixelRect _rect;
    private long _bakedFrames = -1;
    private long _lastFrameTicks;
    private long _seenFrames = -1;
    private BubbleStyle _bakedStyle;
    private bool _staleLogged;

    public CameraFrameCompositor(CameraCaptureService camera)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
    }

    /// <summary>
    /// The bubble window's handle. Zero suppresses the overlay entirely.
    /// </summary>
    /// <remarks>
    /// Written from the UI thread when the bubble opens or closes and read from the pacer thread
    /// every frame, so it is a plain volatile word rather than anything lock-shaped — the value is
    /// self-consistent either way and a frame either side of the change is harmless.
    /// </remarks>
    public IntPtr BubbleHandle
    {
        get => (IntPtr)Volatile.Read(ref _bubbleHandle);
        set => Volatile.Write(ref _bubbleHandle, (long)value);
    }

    private long _bubbleHandle;

    /// <summary>Whether the camera should currently be drawn.</summary>
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set => Volatile.Write(ref _enabled, value);
    }

    private bool _enabled;

    /// <summary>Appearance, replaced wholesale so a half-applied style can never be read.</summary>
    public BubbleStyle Style
    {
        get { lock (_gate) return _style; }
        set { lock (_gate) _style = value; }
    }

    private BubbleStyle _style = new(CameraShape.Circle, true, 3, 0xFFFFFFFF, 1.0);

    public void Begin(OverlayTarget target)
    {
        lock (_gate)
        {
            _target = target;
            _active = target.OutputWidth > 0 && target.OutputHeight > 0;
            _bakedFrames = -1;
            _seenFrames = -1;
            _staleLogged = false;
            _lastFrameTicks = Environment.TickCount64;
        }
    }

    public void End()
    {
        lock (_gate)
        {
            _active = false;
        }
    }

    public void Compose(byte[] frame)
    {
        if (!Enabled || !_active) return;

        try
        {
            lock (_gate)
            {
                if (!_active || !ShouldDraw()) return;
                if (!TryUpdateRect()) return;
                if (!TryRefreshSprite()) return;

                if (_target.PixelFormat == FramePixelFormat.Nv12) BlendNv12(frame);
                else BlendBgra(frame);
            }
        }
        catch (Exception ex)
        {
            // A frame without the bubble is a far better outcome than a failed recording.
            Log.Warn(ex, "Compositing the camera into a frame failed; skipping the bubble.");
        }
    }

    /// <summary>Whether the camera is alive and recent enough to trust. Caller holds the gate.</summary>
    private bool ShouldDraw()
    {
        if (!_camera.IsRunning) return false;

        var frames = _camera.FramesCaptured;
        if (frames != _seenFrames)
        {
            _seenFrames = frames;
            _lastFrameTicks = Environment.TickCount64;
            _staleLogged = false;
        }
        else if (Environment.TickCount64 - _lastFrameTicks > StaleAfter.TotalMilliseconds)
        {
            if (!_staleLogged)
            {
                Log.Warn("The camera stopped delivering frames; dropping the bubble from the recording " +
                         "rather than baking a still image into it.");
                _staleLogged = true;
            }
            return false;
        }

        return frames > 0;
    }

    /// <summary>Caller holds the gate.</summary>
    private bool TryUpdateRect()
    {
        var handle = BubbleHandle;
        if (handle == IntPtr.Zero) return false;

        if (!CameraBubbleGeometry.TryComputeTargetRect(
                handle, _target.Monitor,
                _target.SourceWidth, _target.SourceHeight,
                _target.OutputWidth, _target.OutputHeight,
                out var rect))
        {
            return false;
        }

        _rect = rect;
        return true;
    }

    /// <summary>Re-bakes only when the camera, the size or the style has actually changed.</summary>
    private bool TryRefreshSprite()
    {
        var width = _camera.Width;
        var height = _camera.Height;
        if (width <= 0 || height <= 0) return false;

        if (_cameraFrame.Length < width * height * 4 || _cameraWidth != width || _cameraHeight != height)
        {
            _cameraFrame = new byte[width * height * 4];
            _cameraWidth = width;
            _cameraHeight = height;
            _bakedFrames = -1;
        }

        var style = _style;
        var fresh = _seenFrames != _bakedFrames;
        var resized = _sprite.Width != _rect.Width || _sprite.Height != _rect.Height;
        var restyled = !style.Equals(_bakedStyle);

        if (!fresh && !resized && !restyled) return _sprite.Width > 0;

        if (!_camera.TryCopyLatestFrame(_cameraFrame)) return _sprite.Width > 0 && !resized && !restyled;

        var crop = CameraBubbleGeometry.ComputeCoverCrop(width, height, _rect.Width, _rect.Height);

        // The border is authored against the on-screen bubble, so it has to shrink by however much
        // the bubble shrank on its way into the frame — otherwise a 1080p recording of a 4K screen
        // gets a ring twice as thick as the one the user chose.
        var border = style.BorderThickness * ScaleFactor();

        _sprite.Bake(
            _cameraFrame, width, height, crop,
            _rect.Width, _rect.Height,
            style.Shape, style.Mirror,
            border, style.BorderColor, style.Opacity);

        _bakedFrames = _seenFrames;
        _bakedStyle = style;
        return true;
    }

    /// <summary>How much the recording is scaled down from the monitor's native pixels.</summary>
    private double ScaleFactor() =>
        _target.SourceWidth > 0 ? _target.OutputWidth / (double)_target.SourceWidth : 1.0;

    // ---------------------------------------------------------------- blending

    private void BlendNv12(byte[] frame)
    {
        var outW = _target.OutputWidth;
        var outH = _target.OutputHeight;
        var spriteW = _sprite.Width;

        if (frame.Length < outW * outH * 3 / 2) return;

        // Luma, full resolution.
        var x0 = Math.Max(0, -_rect.X);
        var y0 = Math.Max(0, -_rect.Y);
        var x1 = Math.Min(_rect.Width, outW - _rect.X);
        var y1 = Math.Min(_rect.Height, outH - _rect.Y);

        for (var ty = y0; ty < y1; ty++)
        {
            var dstRow = ((_rect.Y + ty) * outW) + _rect.X;
            var srcRow = ty * spriteW;

            for (var tx = x0; tx < x1; tx++)
            {
                var a = _sprite.Alpha[srcRow + tx];
                if (a == 0) continue;

                var di = dstRow + tx;
                frame[di] = a == 255
                    ? _sprite.Luma[srcRow + tx]
                    : Lerp(frame[di], _sprite.Luma[srcRow + tx], a);
            }
        }

        // Chroma, half resolution, Cb and Cr interleaved.
        var uvBase = outW * outH;
        var halfSpriteW = spriteW / 2;

        var cx0 = Math.Max(0, -_rect.X / 2);
        var cy0 = Math.Max(0, -_rect.Y / 2);
        var cx1 = Math.Min(_rect.Width / 2, (outW - _rect.X) / 2);
        var cy1 = Math.Min(_rect.Height / 2, (outH - _rect.Y) / 2);

        for (var cy = cy0; cy < cy1; cy++)
        {
            var dstRow = uvBase + (((_rect.Y / 2) + cy) * outW) + _rect.X;
            var srcRow = cy * halfSpriteW;

            for (var cxi = cx0; cxi < cx1; cxi++)
            {
                var a = _sprite.ChromaAlpha[srcRow + cxi];
                if (a == 0) continue;

                var di = dstRow + (cxi * 2);
                var si = (srcRow + cxi) * 2;

                if (a == 255)
                {
                    frame[di] = _sprite.Chroma[si];
                    frame[di + 1] = _sprite.Chroma[si + 1];
                }
                else
                {
                    frame[di] = Lerp(frame[di], _sprite.Chroma[si], a);
                    frame[di + 1] = Lerp(frame[di + 1], _sprite.Chroma[si + 1], a);
                }
            }
        }
    }

    private void BlendBgra(byte[] frame)
    {
        var outW = _target.OutputWidth;
        var outH = _target.OutputHeight;
        var spriteW = _sprite.Width;

        if (frame.Length < outW * outH * 4) return;

        var x0 = Math.Max(0, -_rect.X);
        var y0 = Math.Max(0, -_rect.Y);
        var x1 = Math.Min(_rect.Width, outW - _rect.X);
        var y1 = Math.Min(_rect.Height, outH - _rect.Y);

        for (var ty = y0; ty < y1; ty++)
        {
            var dstRow = (((_rect.Y + ty) * outW) + _rect.X) * 4;
            var srcRow = ty * spriteW * 4;

            for (var tx = x0; tx < x1; tx++)
            {
                var si = srcRow + (tx * 4);
                var a = _sprite.Bgra[si + 3];
                if (a == 0) continue;

                var di = dstRow + (tx * 4);
                if (a == 255)
                {
                    frame[di] = _sprite.Bgra[si];
                    frame[di + 1] = _sprite.Bgra[si + 1];
                    frame[di + 2] = _sprite.Bgra[si + 2];
                }
                else
                {
                    frame[di] = Lerp(frame[di], _sprite.Bgra[si], a);
                    frame[di + 1] = Lerp(frame[di + 1], _sprite.Bgra[si + 1], a);
                    frame[di + 2] = Lerp(frame[di + 2], _sprite.Bgra[si + 2], a);
                }
            }
        }
    }

    /// <summary>Integer alpha blend: <paramref name="source"/> over <paramref name="destination"/>.</summary>
    private static byte Lerp(byte destination, byte source, byte alpha) =>
        (byte)(((source * alpha) + (destination * (255 - alpha)) + 127) / 255);
}
