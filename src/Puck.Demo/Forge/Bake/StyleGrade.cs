namespace Puck.Demo.Forge.Bake;

/// <summary>
/// The deterministic per-pixel look stages that run BEFORE the palette fit: the contrast/gamma/saturation grade (at
/// raster resolution), the sprite transparency mask, the outline darkening, and the position-based ordered-dither
/// offset the quantizer adds at native resolution. Everything here is position-and-value pure — no wall clock, no
/// RNG — so the same input always grades to the same bytes.
/// </summary>
internal static class StyleGrade {
    // The classic 4×4 Bayer matrix; threshold t = (B[y&3][x&3] + 0.5)/16 − 0.5 spans (−0.5, +0.47).
    private static readonly int[,] Bayer4 = new int[4, 4] {
        { 0, 8, 2, 10 },
        { 12, 4, 14, 6 },
        { 3, 11, 1, 9 },
        { 15, 7, 13, 5 },
    };

    // How far an outline pixel darkens (multiplies its channels): sprites get the harder rim, backgrounds a softer edge.
    private const float SpriteOutlineScale = 0.35f;
    private const float BackgroundOutlineScale = 0.55f;
    // The foreground/background classification threshold (squared 8-bit RGB distance), matching the avatar forge's.
    private const int TransparencyThresholdSquared = 1600;

    /// <summary>Grades an RGBA buffer in place: a contrast stretch about mid-grey per channel, then a gamma curve on
    /// Rec.709 luma (hue-preserving), then an optional saturation boost (the CGB-only pre-fit lift).</summary>
    /// <param name="rgba">The pixels to grade (modified in place).</param>
    /// <param name="style">The style whose curve parameters apply.</param>
    /// <param name="applySaturation">Whether the saturation boost applies (CGB targets only).</param>
    public static void Grade(byte[] rgba, BakeStyle style, bool applySaturation) {
        ArgumentNullException.ThrowIfNull(rgba);

        var saturation = (applySaturation ? (1f + style.SaturationBoost) : 1f);

        for (var offset = 0; (offset < rgba.Length); offset += 4) {
            var r = Stretch(channel: (rgba[offset] / 255f), contrast: style.Contrast);
            var g = Stretch(channel: (rgba[(offset + 1)] / 255f), contrast: style.Contrast);
            var b = Stretch(channel: (rgba[(offset + 2)] / 255f), contrast: style.Contrast);
            var luma = Luma(b: b, g: g, r: r);

            if ((style.Gamma != 1f) && (luma > 0f)) {
                var lift = (MathF.Pow(x: luma, y: style.Gamma) / luma);

                r *= lift;
                g *= lift;
                b *= lift;
                luma = Luma(b: b, g: g, r: r);
            }

            if (saturation != 1f) {
                r = (luma + ((r - luma) * saturation));
                g = (luma + ((g - luma) * saturation));
                b = (luma + ((b - luma) * saturation));
            }

            rgba[offset] = ToByte(value: r);
            rgba[(offset + 1)] = ToByte(value: g);
            rgba[(offset + 2)] = ToByte(value: b);
        }
    }

    /// <summary>The ordered-dither offset for a native pixel, in 8-BIT channel units: threshold
    /// <c>t = (B[y&amp;3][x&amp;3] + 0.5)/16 − 0.5</c> scaled by the style's strength and one RGB555 step
    /// (<c>255/31</c>). Zero when the style dithers not at all.</summary>
    /// <param name="style">The style whose dither applies.</param>
    /// <param name="x">The native pixel column.</param>
    /// <param name="y">The native pixel row.</param>
    /// <returns>The signed per-channel offset.</returns>
    public static float DitherOffset(BakeStyle style, int x, int y) {
        if (style.Dither != BakeDither.Ordered4x4) {
            return 0f;
        }

        var threshold = (((Bayer4[y & 3, x & 3] + 0.5f) / 16f) - 0.5f);

        return ((threshold * style.DitherStrength) * (255f / 31f));
    }

    /// <summary>Classifies a sprite view's pixels against its sampled empty-corner background: true = foreground.
    /// The corner sample (0,0) is the same convention the avatar forge proved out — the SDF background is a flat
    /// field, so a distance threshold separates subject from emptiness deterministically.</summary>
    /// <param name="rgba">The native pixels.</param>
    /// <param name="width">The native width.</param>
    /// <param name="height">The native height.</param>
    /// <returns>The per-pixel foreground mask.</returns>
    public static bool[] ForegroundMask(byte[] rgba, int width, int height) {
        ArgumentNullException.ThrowIfNull(rgba);

        var mask = new bool[(width * height)];
        var backgroundR = (int)rgba[0];
        var backgroundG = (int)rgba[1];
        var backgroundB = (int)rgba[2];

        for (var pixel = 0; (pixel < mask.Length); pixel++) {
            var offset = (pixel * 4);
            var dr = (rgba[offset] - backgroundR);
            var dg = (rgba[(offset + 1)] - backgroundG);
            var db = (rgba[(offset + 2)] - backgroundB);

            mask[pixel] = ((((dr * dr) + (dg * dg)) + (db * db)) > TransparencyThresholdSquared);
        }

        return mask;
    }

    /// <summary>Darkens a sprite's silhouette in place: every foreground pixel with a transparent 4-neighbour (or an
    /// image edge) pulls toward the palette's darkest by scaling its channels down.</summary>
    /// <param name="rgba">The native pixels (modified in place).</param>
    /// <param name="mask">The foreground mask.</param>
    /// <param name="width">The native width.</param>
    /// <param name="height">The native height.</param>
    public static void OutlineSprite(byte[] rgba, bool[] mask, int width, int height) {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(mask);

        // Classified first, applied second — darkening in one pass would let an already-darkened neighbour skew the rim.
        var rim = new bool[mask.Length];

        for (var y = 0; (y < height); y++) {
            for (var x = 0; (x < width); x++) {
                var pixel = ((y * width) + x);

                rim[pixel] = (mask[pixel] && HasTransparentNeighbour(height: height, mask: mask, width: width, x: x, y: y));
            }
        }

        DarkenWhere(rgba: rgba, scale: SpriteOutlineScale, where: rim);
    }

    /// <summary>Darkens a background's strong edges in place: a Sobel operator on Rec.709 luma, thresholded by the
    /// style, marks the edge pixels.</summary>
    /// <param name="rgba">The native pixels (modified in place).</param>
    /// <param name="width">The native width.</param>
    /// <param name="height">The native height.</param>
    /// <param name="threshold">The normalized gradient magnitude an edge must exceed (the style's knob).</param>
    public static void OutlineBackground(byte[] rgba, int width, int height, float threshold) {
        ArgumentNullException.ThrowIfNull(rgba);

        var luma = new float[(width * height)];

        for (var pixel = 0; (pixel < luma.Length); pixel++) {
            var offset = (pixel * 4);

            luma[pixel] = Luma(b: (rgba[(offset + 2)] / 255f), g: (rgba[(offset + 1)] / 255f), r: (rgba[offset] / 255f));
        }

        var edges = new bool[luma.Length];

        for (var y = 1; (y < (height - 1)); y++) {
            for (var x = 1; (x < (width - 1)); x++) {
                edges[((y * width) + x)] = (SobelMagnitude(luma: luma, width: width, x: x, y: y) > threshold);
            }
        }

        DarkenWhere(rgba: rgba, scale: BackgroundOutlineScale, where: edges);
    }

    /// <summary>The DMG shade for a graded (and dithered) luma: <c>min(3, floor((1−L′)·4))</c> — bright lands on
    /// shade 0 (the lightest ramp entry), dark on shade 3.</summary>
    /// <param name="luma">The graded luma in [0, 1].</param>
    /// <returns>The 2-bit shade index.</returns>
    public static int DmgShade(float luma) =>
        Math.Min(val1: 3, val2: (int)MathF.Floor(x: ((1f - Math.Clamp(value: luma, max: 1f, min: 0f)) * 4f)));

    /// <summary>Rec.709 luma of graded [0, 1] channels.</summary>
    /// <param name="r">The red channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="b">The blue channel.</param>
    /// <returns>The luma in [0, 1] (for in-gamut inputs).</returns>
    public static float Luma(float r, float g, float b) =>
        (((0.2126f * r) + (0.7152f * g)) + (0.0722f * b));

    private static float Stretch(float channel, float contrast) =>
        (((channel - 0.5f) * contrast) + 0.5f);
    private static byte ToByte(float value) =>
        (byte)Math.Clamp(value: (int)MathF.Round(x: (value * 255f)), max: 255, min: 0);
    private static bool HasTransparentNeighbour(bool[] mask, int width, int height, int x, int y) =>
        ((x == 0) || !mask[((y * width) + (x - 1))]
            || (x == (width - 1)) || !mask[((y * width) + (x + 1))]
            || (y == 0) || !mask[(((y - 1) * width) + x)]
            || (y == (height - 1)) || !mask[(((y + 1) * width) + x)]);
    private static float SobelMagnitude(float[] luma, int width, int x, int y) {
        var topLeft = luma[(((y - 1) * width) + (x - 1))];
        var top = luma[(((y - 1) * width) + x)];
        var topRight = luma[(((y - 1) * width) + (x + 1))];
        var left = luma[((y * width) + (x - 1))];
        var right = luma[((y * width) + (x + 1))];
        var bottomLeft = luma[(((y + 1) * width) + (x - 1))];
        var bottom = luma[(((y + 1) * width) + x)];
        var bottomRight = luma[(((y + 1) * width) + (x + 1))];
        var gx = (((topRight + (2f * right)) + bottomRight) - ((topLeft + (2f * left)) + bottomLeft));
        var gy = (((bottomLeft + (2f * bottom)) + bottomRight) - ((topLeft + (2f * top)) + topRight));

        // Normalized by the operator's ±4 range so the style threshold reads in luma units.
        return (MathF.Sqrt(x: ((gx * gx) + (gy * gy))) / 4f);
    }
    private static void DarkenWhere(byte[] rgba, bool[] where, float scale) {
        for (var pixel = 0; (pixel < where.Length); pixel++) {
            if (!where[pixel]) {
                continue;
            }

            var offset = (pixel * 4);

            rgba[offset] = (byte)(rgba[offset] * scale);
            rgba[(offset + 1)] = (byte)(rgba[(offset + 1)] * scale);
            rgba[(offset + 2)] = (byte)(rgba[(offset + 2)] * scale);
        }
    }
}
