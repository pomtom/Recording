using Recorder.Settings;

namespace Recorder.Capture;

/// <summary>
/// One baked copy of the camera image, shaped and coloured ready to be stamped into a video frame.
/// </summary>
/// <remarks>
/// <para>Baking is separated from blending because they run at different rates. The bubble only
/// changes when a camera frame arrives (30/s) or the user resizes it, while the blend runs on every
/// encoded frame (up to 240/s). Doing the crop, resample, masking and colour conversion once and
/// reusing the result is what keeps this feature's CPU cost in the noise.</para>
///
/// <para>Both representations are kept because the pipeline has two output formats: NV12 from the
/// GPU converter, and BGRA when no video processor is available
/// (see <see cref="FrameConverter"/>).</para>
/// </remarks>
public sealed class CameraSprite
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Straight-alpha BGRA, for blending into a BGRA frame.</summary>
    public byte[] Bgra { get; private set; } = [];

    /// <summary>Limited-range BT.709 luma, one byte per pixel.</summary>
    public byte[] Luma { get; private set; } = [];

    /// <summary>Cb/Cr interleaved at half resolution, in NV12's order.</summary>
    public byte[] Chroma { get; private set; } = [];

    /// <summary>Coverage per pixel, 0 transparent to 255 opaque.</summary>
    public byte[] Alpha { get; private set; } = [];

    /// <summary>Coverage per 2x2 chroma block, averaged from <see cref="Alpha"/>.</summary>
    public byte[] ChromaAlpha { get; private set; } = [];

    // ---------------------------------------------------------------- colour
    //
    // The pipeline emits limited-range BT.709 (FrameConverter.ConfigureColorSpaces sets
    // YCbCr_Matrix = 1 and an output Nominal_Range of 16-235), and FFmpegArgumentBuilder writes the
    // matching tags into the MP4. The camera has to be converted with the same matrix or the bubble
    // sits visibly washed out against the desktop around it — which is the exact failure that method
    // exists to prevent.
    //
    // Coefficients are full-range RGB in, limited-range YCbCr out, in 16.16 fixed point:
    //   Y  =  16 + (219/255)·(0.2126·R + 0.7152·G + 0.0722·B)
    //   Cb = 128 + (224/255)·(B - Y') / (2·(1 - 0.0722))
    //   Cr = 128 + (224/255)·(R - Y') / (2·(1 - 0.2126))
    // Each row sums to zero for Cb/Cr, so a grey input lands exactly on 128.

    private const int Shift = 16;
    private const int Half = 1 << (Shift - 1);

    private const int YR = 11966, YG = 40254, YB = 4063;
    private const int CbR = -6596, CbG = -22188, CbB = 28784;
    private const int CrR = 28784, CrG = -26144, CrB = -2640;

    /// <summary>Rounded-rect corner radius as a fraction of the shorter side.</summary>
    private const double CornerFraction = 0.18;

    /// <summary>
    /// Rebuilds the sprite from a camera frame.
    /// </summary>
    /// <param name="camera">Packed BGRA camera frame.</param>
    /// <param name="crop">The part of the camera image to use, from <see cref="CameraBubbleGeometry.ComputeCoverCrop"/>.</param>
    /// <param name="width">Target width in output pixels. Must be even.</param>
    /// <param name="borderThickness">Ring width in output pixels. 0 draws no ring.</param>
    /// <param name="borderColor">Ring colour, packed 0xAARRGGBB.</param>
    /// <param name="opacity">0 to 1, multiplied into the coverage.</param>
    public void Bake(
        byte[] camera,
        int cameraWidth,
        int cameraHeight,
        PixelRect crop,
        int width,
        int height,
        CameraShape shape,
        bool mirror,
        double borderThickness,
        uint borderColor,
        double opacity)
    {
        if (width <= 0 || height <= 0 || cameraWidth <= 0 || cameraHeight <= 0) return;

        EnsureCapacity(width, height);

        var borderB = (byte)(borderColor & 0xFF);
        var borderG = (byte)((borderColor >> 8) & 0xFF);
        var borderR = (byte)((borderColor >> 16) & 0xFF);
        var borderA = ((borderColor >> 24) & 0xFF) / 255.0;

        var cx = (width - 1) / 2.0;
        var cy = (height - 1) / 2.0;
        var radius = Math.Min(width, height) * CornerFraction;
        var border = Math.Max(0, borderThickness);
        var alphaScale = Math.Clamp(opacity, 0, 1);

        // Bilinear step across the cropped region. Half-pixel offsets keep the sample grid centred,
        // which stops the image creeping by half a pixel each time the bubble is resized.
        var stepX = crop.Width / (double)width;
        var stepY = crop.Height / (double)height;

        for (var ty = 0; ty < height; ty++)
        {
            var sv = crop.Y + ((ty + 0.5) * stepY) - 0.5;
            var rowBgra = ty * width * 4;
            var rowAlpha = ty * width;

            for (var tx = 0; tx < width; tx++)
            {
                var coverage = Coverage(tx, ty, cx, cy, width, height, radius, shape);

                var i4 = rowBgra + (tx * 4);
                if (coverage <= 0)
                {
                    Bgra[i4] = Bgra[i4 + 1] = Bgra[i4 + 2] = Bgra[i4 + 3] = 0;
                    Alpha[rowAlpha + tx] = 0;
                    Luma[rowAlpha + tx] = 16;
                    continue;
                }

                // Shrinking the shape inward by the border width gives the ring for free: whatever
                // the outline covers but the inset shape does not is the ring.
                var inner = border > 0
                    ? Coverage(tx, ty, cx, cy, width, height, radius, shape, border)
                    : coverage;
                var ring = Math.Max(0, coverage - inner);

                var sourceX = mirror ? width - 1 - tx : tx;
                var su = crop.X + ((sourceX + 0.5) * stepX) - 0.5;

                SampleBilinear(camera, cameraWidth, cameraHeight, su, sv, out var b, out var g, out var r);

                if (ring > 0 && borderA > 0)
                {
                    // Straight-alpha average of ring colour and camera colour, weighted by how much
                    // of this pixel each one covers.
                    var ringWeight = ring * borderA;
                    var total = ringWeight + inner;
                    if (total > 0)
                    {
                        b = (byte)Math.Clamp(((borderB * ringWeight) + (b * inner)) / total, 0, 255);
                        g = (byte)Math.Clamp(((borderG * ringWeight) + (g * inner)) / total, 0, 255);
                        r = (byte)Math.Clamp(((borderR * ringWeight) + (r * inner)) / total, 0, 255);
                    }
                }

                var a = (byte)Math.Clamp(coverage * alphaScale * 255.0, 0, 255);

                Bgra[i4] = b;
                Bgra[i4 + 1] = g;
                Bgra[i4 + 2] = r;
                Bgra[i4 + 3] = a;

                Alpha[rowAlpha + tx] = a;
                Luma[rowAlpha + tx] = (byte)Math.Clamp(
                    ((16 << Shift) + (YR * r) + (YG * g) + (YB * b) + Half) >> Shift, 16, 235);
            }
        }

        BuildChromaPlanes(width, height);
    }

    /// <summary>
    /// Averages each 2x2 block down into the chroma planes.
    /// </summary>
    /// <remarks>
    /// Averaging in RGB before converting, rather than converting then averaging, is what keeps the
    /// antialiased rim from fringing: a half-covered edge pixel contributes its own colour in
    /// proportion to its coverage instead of pulling a fully-weighted vote.
    /// </remarks>
    private void BuildChromaPlanes(int width, int height)
    {
        var halfW = width / 2;
        var halfH = height / 2;

        for (var cyIndex = 0; cyIndex < halfH; cyIndex++)
        {
            for (var cxIndex = 0; cxIndex < halfW; cxIndex++)
            {
                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;

                for (var dy = 0; dy < 2; dy++)
                {
                    var row = ((cyIndex * 2) + dy) * width;
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var px = row + (cxIndex * 2) + dx;
                        var a = Alpha[px];
                        var i4 = px * 4;

                        sumB += Bgra[i4] * a;
                        sumG += Bgra[i4 + 1] * a;
                        sumR += Bgra[i4 + 2] * a;
                        sumA += a;
                    }
                }

                var chromaIndex = (cyIndex * halfW) + cxIndex;
                ChromaAlpha[chromaIndex] = (byte)(sumA / 4);

                if (sumA == 0)
                {
                    Chroma[chromaIndex * 2] = 128;
                    Chroma[(chromaIndex * 2) + 1] = 128;
                    continue;
                }

                var b = sumB / sumA;
                var g = sumG / sumA;
                var r = sumR / sumA;

                Chroma[chromaIndex * 2] = (byte)Math.Clamp(
                    ((128 << Shift) + (CbR * r) + (CbG * g) + (CbB * b) + Half) >> Shift, 16, 240);
                Chroma[(chromaIndex * 2) + 1] = (byte)Math.Clamp(
                    ((128 << Shift) + (CrR * r) + (CrG * g) + (CrB * b) + Half) >> Shift, 16, 240);
            }
        }
    }

    /// <summary>
    /// How much of one pixel the bubble outline covers, 0 to 1.
    /// </summary>
    /// <param name="inset">Shrinks the shape by this many pixels, which is how the ring is derived.</param>
    private static double Coverage(
        int x, int y, double cx, double cy, int width, int height, double radius, CameraShape shape, double inset = 0)
    {
        double distance;

        if (shape == CameraShape.Circle)
        {
            // An ellipse inscribed in the window, so a square window gives a true circle and a
            // stretched one degrades gracefully instead of clipping.
            var rx = (width / 2.0) - inset;
            var ry = (height / 2.0) - inset;
            if (rx <= 0 || ry <= 0) return 0;

            var nx = (x - cx) / rx;
            var ny = (y - cy) / ry;
            var normalized = Math.Sqrt((nx * nx) + (ny * ny));

            // Scale the normalised distance back into pixels so the antialiasing width is uniform.
            distance = (normalized - 1.0) * Math.Min(rx, ry);
        }
        else
        {
            var halfW = (width / 2.0) - inset;
            var halfH = (height / 2.0) - inset;
            if (halfW <= 0 || halfH <= 0) return 0;

            var r = Math.Min(radius, Math.Min(halfW, halfH));
            var qx = Math.Abs(x - cx) - (halfW - r);
            var qy = Math.Abs(y - cy) - (halfH - r);

            var outside = Math.Sqrt((Math.Max(qx, 0) * Math.Max(qx, 0)) + (Math.Max(qy, 0) * Math.Max(qy, 0)));
            distance = outside + Math.Min(Math.Max(qx, qy), 0) - r;
        }

        // One pixel of feather centred on the boundary.
        return Math.Clamp(0.5 - distance, 0, 1);
    }

    private static void SampleBilinear(
        byte[] source, int width, int height, double u, double v, out byte b, out byte g, out byte r)
    {
        var x0 = (int)Math.Floor(u);
        var y0 = (int)Math.Floor(v);
        var fx = u - x0;
        var fy = v - y0;

        var x1 = Math.Clamp(x0 + 1, 0, width - 1);
        var y1 = Math.Clamp(y0 + 1, 0, height - 1);
        x0 = Math.Clamp(x0, 0, width - 1);
        y0 = Math.Clamp(y0, 0, height - 1);

        var i00 = ((y0 * width) + x0) * 4;
        var i10 = ((y0 * width) + x1) * 4;
        var i01 = ((y1 * width) + x0) * 4;
        var i11 = ((y1 * width) + x1) * 4;

        var w00 = (1 - fx) * (1 - fy);
        var w10 = fx * (1 - fy);
        var w01 = (1 - fx) * fy;
        var w11 = fx * fy;

        b = Mix(source[i00], source[i10], source[i01], source[i11], w00, w10, w01, w11);
        g = Mix(source[i00 + 1], source[i10 + 1], source[i01 + 1], source[i11 + 1], w00, w10, w01, w11);
        r = Mix(source[i00 + 2], source[i10 + 2], source[i01 + 2], source[i11 + 2], w00, w10, w01, w11);
    }

    private static byte Mix(byte a, byte b, byte c, byte d, double wa, double wb, double wc, double wd) =>
        (byte)Math.Clamp((a * wa) + (b * wb) + (c * wc) + (d * wd) + 0.5, 0, 255);

    private void EnsureCapacity(int width, int height)
    {
        if (Width == width && Height == height) return;

        Width = width;
        Height = height;

        var pixels = width * height;
        var chroma = (width / 2) * (height / 2);

        Bgra = new byte[pixels * 4];
        Luma = new byte[pixels];
        Alpha = new byte[pixels];
        Chroma = new byte[chroma * 2];
        ChromaAlpha = new byte[chroma];
    }
}
