namespace Puck.Text;

/// <summary>
/// The missing distance-field generator piece: turns a raster COVERAGE atlas (a <see cref="FontAtlasKind.HardMask"/>
/// or <see cref="FontAtlasKind.SoftMask"/> image, e.g. the GDI+/System.Drawing glyph raster the diegetic terminal
/// already produces) into a single-channel signed distance field atlas (<see cref="FontAtlasKind.Sdf"/>) an SDF
/// renderer can march. This is the runtime, no-toolchain fallback source; the higher-fidelity marchable source is a
/// pre-baked <c>msdf-atlas-gen</c> MTSDF atlas whose true single-channel distance lives in alpha (loaded, not
/// generated here). Both encode the SAME convention so a decoder is source-agnostic.
/// </summary>
/// <remarks>
/// <para><b>Encoding convention (KEEP IN SYNC with the shader decode).</b> Every texel stores
/// <c>encoded = 0.5 + signedDistance / distanceRange</c>, clamped to <c>[0, 1]</c>, where the signed distance is in
/// TEXELS and is POSITIVE inside the glyph — the <c>msdfgen</c> / <c>msdf-atlas-gen</c> convention, matching
/// <see cref="MtsdfSampling.ComputeUnitRange(FontAtlas)"/> (which reads <see cref="FontAtlas.DistanceRange"/> in
/// texels and divides by <see cref="FontAtlas.Size"/> for the em range). The single-channel field is REPLICATED into
/// all four RGBA channels, so a decoder that samples the true single-channel distance from ALPHA works uniformly for
/// this generated atlas AND for a real MTSDF atlas (whose alpha is the true distance). The RGB median that MTSDF
/// tooling reconstructs is NOT written here and must never be marched — it is only C0 at channel-crossover lines and
/// so kinks a sphere-tracer's field; geometry samples the true channel.</para>
/// <para><b>The transform is an EXACT Euclidean distance transform</b> (Felzenszwalb &amp; Huttenlocher's separable
/// O(n) lower-envelope algorithm over the inside- and outside-feature sets), NOT a chamfer approximation. The
/// chamfer(1, √2) transform the earlier prototype used OVERESTIMATES true distance by up to ~8.24% off-axis (worst at
/// the 22.5° direction), and an overestimating field breaks conservative sphere tracing — so a glyph shape reading a
/// chamfer atlas would need a <c>1 / 1.0824</c> step-scale penalty. The exact EDT keeps the field 1-Lipschitz in
/// texel space (the Eikonal property), so the glyph shape is factor-1 with no step clamp; bilinear reconstruction on
/// the GPU preserves that bound. The transform is deterministic (a pure function of the coverage raster, plain float
/// arithmetic), so the same font + charset always yields a byte-identical atlas.</para>
/// <para>Bake guidance for any atlas fed here or authored externally: a distance range of at least 4 texels (this
/// generator defaults to <see cref="DefaultDistanceRange"/> = 8 for engrave-depth / outline headroom) and glyph-cell
/// padding at least as wide as the range, so the encoded band never bleeds across a cell boundary.</para>
/// </remarks>
public static class SdfCoverageAtlas {
    /// <summary>The default signed-distance band width, in texels — the ±range/2 spread of usable encoded distance
    /// around each glyph edge. Chosen wide (8) rather than the msdfgen default (2) so engraved lettering has depth
    /// and outline headroom; keep glyph-cell padding at least this wide.</summary>
    public const float DefaultDistanceRange = 8.0f;

    /// <summary>Generates a single-channel SDF atlas from a raster coverage atlas.</summary>
    /// <param name="coverage">The source coverage atlas. Must carry in-memory <see cref="FontAtlas.ImageData"/>; a
    /// distance-field atlas is returned unchanged (already marchable). Glyphs, metrics, kerning, and dimensions carry
    /// through untouched — only the image and kind change.</param>
    /// <param name="distanceRange">The signed-distance band width in texels; the encoded value saturates beyond
    /// ±<paramref name="distanceRange"/>/2 of an edge. Must be greater than zero. Defaults to
    /// <see cref="DefaultDistanceRange"/>.</param>
    /// <returns>A <see cref="FontAtlasKind.Sdf"/> atlas whose image encodes the exact signed distance in every
    /// channel, or <paramref name="coverage"/> unchanged when it is already a distance-field atlas.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="coverage"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="coverage"/> carries no in-memory image data.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="distanceRange"/> is not greater than zero.</exception>
    public static FontAtlas Generate(FontAtlas coverage, float distanceRange = DefaultDistanceRange) {
        ArgumentNullException.ThrowIfNull(coverage);

        if (distanceRange <= 0.0f) {
            throw new ArgumentOutOfRangeException(
                message: "SDF distance range must be greater than zero.",
                paramName: nameof(distanceRange)
            );
        }

        if (MtsdfSampling.UsesDistanceField(kind: coverage.Kind)) {
            return coverage;
        }

        if (coverage.ImageData is not FontAtlasImageData imageData) {
            throw new ArgumentException(
                message: "SDF generation requires an in-memory coverage image (FontAtlas.ImageData).",
                paramName: nameof(coverage)
            );
        }

        return new FontAtlas(
            distanceRange: distanceRange,
            glyphs: coverage.Glyphs,
            height: coverage.Height,
            imageData: EncodeSignedDistance(imageData: imageData, distanceRange: distanceRange),
            imagePath: (coverage.ImagePath + "#sdf"),
            kerningPairs: coverage.KerningPairs,
            kind: FontAtlasKind.Sdf,
            metrics: coverage.Metrics,
            size: coverage.Size,
            width: coverage.Width
        );
    }

    // Per-pixel coverage in [0, 1]: alpha when present, else the brightest color channel (a white-on-transparent GDI+
    // raster stores coverage in alpha; a white-on-black one stores it in the channels). Matches the terminal's raster
    // convention so either source decodes the same.
    private static float Coverage(byte[] rgba, int pixelIndex) {
        var offset = (pixelIndex * 4);
        var alpha = (rgba[(offset + 3)] / 255.0f);

        if (alpha > 0.0f) {
            return alpha;
        }

        return MathF.Max(
            x: (rgba[offset] / 255.0f),
            y: MathF.Max(
                x: (rgba[(offset + 1)] / 255.0f),
                y: (rgba[(offset + 2)] / 255.0f)
            )
        );
    }
    // The exact 1D squared Euclidean distance transform (Felzenszwalb & Huttenlocher, "Distance Transforms of Sampled
    // Functions"): the lower envelope of the parabolas rooted at each sample. Runs in O(n); called once per row then
    // once per column, which composes into the exact 2D squared-distance transform. `f` is the input cost per sample
    // (0 at a feature, +inf elsewhere); `d` receives the squared distance to the nearest feature.
    private static void SquaredDistanceTransform1D(float[] f, int length, float[] d, int[] v, float[] z) {
        var k = 0;

        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;

        for (var q = 1; (q < length); q++) {
            var s = (((f[q] + (q * q)) - (f[v[k]] + (v[k] * v[k]))) / (float)((2 * q) - (2 * v[k])));

            while (s <= z[k]) {
                k--;
                s = (((f[q] + (q * q)) - (f[v[k]] + (v[k] * v[k]))) / (float)((2 * q) - (2 * v[k])));
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[(k + 1)] = float.PositiveInfinity;
        }

        k = 0;

        for (var q = 0; (q < length); q++) {
            while (z[(k + 1)] < q) {
                k++;
            }

            var delta = (q - v[k]);

            d[q] = ((delta * delta) + f[v[k]]);
        }
    }
    // The exact squared Euclidean distance transform over the whole image for the given feature predicate (inside- or
    // outside-of-glyph), returned as a flat row-major squared-distance grid.
    private static float[] SquaredDistanceTransform2D(bool[] features, int width, int height) {
        var distances = new float[features.Length];

        for (var index = 0; (index < features.Length); index++) {
            distances[index] = (features[index] ? 0.0f : float.PositiveInfinity);
        }

        var span = Math.Max(width, height);
        var column = new float[span];
        var result = new float[span];
        var v = new int[span];
        var z = new float[(span + 1)];

        // Columns first, then rows: either order gives the exact separable transform.
        for (var x = 0; (x < width); x++) {
            for (var y = 0; (y < height); y++) {
                column[y] = distances[((y * width) + x)];
            }

            SquaredDistanceTransform1D(f: column, length: height, d: result, v: v, z: z);

            for (var y = 0; (y < height); y++) {
                distances[((y * width) + x)] = result[y];
            }
        }

        for (var y = 0; (y < height); y++) {
            var rowBase = (y * width);

            for (var x = 0; (x < width); x++) {
                column[x] = distances[(rowBase + x)];
            }

            SquaredDistanceTransform1D(f: column, length: width, d: result, v: v, z: z);

            for (var x = 0; (x < width); x++) {
                distances[(rowBase + x)] = result[x];
            }
        }

        return distances;
    }
    private static FontAtlasImageData EncodeSignedDistance(FontAtlasImageData imageData, float distanceRange) {
        var pixelCount = checked(imageData.Width * imageData.Height);
        var inside = new bool[pixelCount];
        var outside = new bool[pixelCount];

        for (var index = 0; (index < pixelCount); index++) {
            inside[index] = (Coverage(rgba: imageData.RgbaPixels, pixelIndex: index) >= 0.5f);
            outside[index] = !inside[index];
        }

        // Distance to the nearest OUTSIDE pixel (positive inside the glyph) and to the nearest INSIDE pixel (positive
        // outside): the classic two-transform signed field. The transforms return SQUARED distance, so sqrt once.
        var toOutside = SquaredDistanceTransform2D(features: outside, width: imageData.Width, height: imageData.Height);
        var toInside = SquaredDistanceTransform2D(features: inside, width: imageData.Width, height: imageData.Height);
        var rgba = new byte[checked(pixelCount * 4)];

        for (var index = 0; (index < pixelCount); index++) {
            var signedDistance = (inside[index]
                ? MathF.Sqrt(toOutside[index])
                : -MathF.Sqrt(toInside[index]));
            var encoded = Math.Clamp(
                max: 1.0f,
                min: 0.0f,
                value: (0.5f + (signedDistance / distanceRange))
            );
            var value = (byte)Math.Clamp(
                max: 255,
                min: 0,
                value: (int)MathF.Round(encoded * 255.0f)
            );
            var offset = (index * 4);

            // Replicate into every channel: alpha carries the true single-channel distance a decoder marches, and RGB
            // mirror it so a coverage-style debug view still reads.
            rgba[offset] = value;
            rgba[(offset + 1)] = value;
            rgba[(offset + 2)] = value;
            rgba[(offset + 3)] = value;
        }

        return new FontAtlasImageData(height: imageData.Height, rgbaPixels: rgba, width: imageData.Width);
    }
}
