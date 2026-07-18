namespace Puck.Text;

/// <summary>
/// The distance-field sampling math shared between layout and rendering. These helpers translate between a
/// <see cref="FontAtlas"/>'s encoded distance range and the screen-space quantities a shader needs to
/// anti-alias glyph edges, and they classify how a given <see cref="FontAtlasKind"/> should be decoded.
/// </summary>
/// <remarks>
/// In a (multi-channel) signed distance field, each texel stores the distance to the nearest glyph edge,
/// normalized so that the value <c>0.5</c> lies exactly on the edge. The <em>unit range</em> is the width
/// of that encoded band in em units; multiplying it by the on-screen pixels-per-em gives the
/// <em>screen pixel range</em>, the band width in destination pixels, which sets the slope of the
/// anti-aliasing ramp. The naming follows the committed atlas metadata's own convention (see the
/// font-atlas bake pipeline, <c>tools/font-atlas</c>).
/// </remarks>
public static class MtsdfSampling {
    /// <summary>
    /// Maps an encoded signed distance to anti-aliased pixel coverage in the range <c>[0, 1]</c>: it
    /// recenters the sample on the edge (<c>0.5</c>), scales it by the screen-space band width, and clamps.
    /// </summary>
    /// <param name="signedDistance">The sampled, normalized signed distance, where <c>0.5</c> is the glyph edge.</param>
    /// <param name="screenPixelRange">The distance-field band width in destination screen pixels; see <see cref="ComputeScreenPixelRange(float, float)"/>.</param>
    /// <returns>The pixel coverage, clamped to <c>[0, 1]</c>.</returns>
    public static float ComputeCoverage(float signedDistance, float screenPixelRange) {
        return Math.Clamp(
            max: 1.0f,
            min: 0.0f,
            value: ((screenPixelRange * (signedDistance - 0.5f)) + 0.5f)
        );
    }
    /// <summary>Converts a distance-field unit range into the band width in destination screen pixels.</summary>
    /// <param name="unitRange">The distance-field band width in em units. Must be greater than zero.</param>
    /// <param name="screenScale">The number of destination screen pixels per em. Must be greater than zero.</param>
    /// <returns>The band width in screen pixels, never less than <c>1</c> so that the edge always spans at least one pixel.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="unitRange"/> or <paramref name="screenScale"/> is not greater than zero.</exception>
    public static float ComputeScreenPixelRange(float unitRange, float screenScale) {
        if (unitRange <= 0.0f) {
            throw new ArgumentOutOfRangeException(
                message: "MTSDF unit range must be greater than zero.",
                paramName: nameof(unitRange)
            );
        }

        if (screenScale <= 0.0f) {
            throw new ArgumentOutOfRangeException(
                message: "MTSDF screen scale must be greater than zero.",
                paramName: nameof(screenScale)
            );
        }

        return MathF.Max(
            x: (unitRange * screenScale),
            y: 1.0f
        );
    }
    /// <summary>Computes the screen-pixel band width for a glyph drawn from the given atlas, resolving the unit range from the atlas and optional glyph.</summary>
    /// <param name="atlas">The atlas being sampled.</param>
    /// <param name="glyph">The glyph being drawn, whose per-glyph range overrides are honored when present, or <see langword="null"/> to use the atlas-wide range.</param>
    /// <param name="screenScale">The number of destination screen pixels per em. Must be greater than zero for distance-field atlases.</param>
    /// <returns>The band width in screen pixels for distance-field atlases, or <c>1</c> for mask atlases.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">For a distance-field atlas, the resolved unit range or <paramref name="screenScale"/> is not greater than zero.</exception>
    public static float ComputeScreenPixelRange(FontAtlas atlas, FontAtlasGlyph? glyph, float screenScale) {
        ArgumentNullException.ThrowIfNull(atlas);

        return (UsesDistanceField(kind: atlas.Kind)
            ? ComputeScreenPixelRange(
                screenScale: screenScale,
                unitRange: ComputeUnitRange(
                    atlas: atlas,
                    glyph: glyph
                )
            )
            : 1.0f);
    }

    /// <summary>
    /// Computes the screen pixels per em for a glyph rendered into a destination rectangle, taking the
    /// worst case of the two axes so the anti-aliasing band never under-covers when the aspect ratio is
    /// not preserved.
    /// </summary>
    /// <param name="planeWidth">The glyph plane-bounds width, in em units.</param>
    /// <param name="planeHeight">The glyph plane-bounds height, in em units.</param>
    /// <param name="rectWidthPixels">The destination rectangle width, in screen pixels.</param>
    /// <param name="rectHeightPixels">The destination rectangle height, in screen pixels.</param>
    /// <returns>The larger of the per-axis pixels-per-em ratios, suitable for passing as the screen scale to <see cref="ComputeScreenPixelRange(float, float)"/>. Plane dimensions are floored to a small epsilon to avoid division by zero.</returns>
    public static float ComputeScreenScale(float planeWidth, float planeHeight, float rectWidthPixels, float rectHeightPixels) {
        var safePlaneWidth = MathF.Max(
            x: 0.0001f,
            y: planeWidth
        );
        var safePlaneHeight = MathF.Max(
            x: 0.0001f,
            y: planeHeight
        );

        return MathF.Max(
            x: (rectWidthPixels / safePlaneWidth),
            y: (rectHeightPixels / safePlaneHeight)
        );
    }
    /// <summary>Resolves the atlas-wide distance-field band width, in em units.</summary>
    /// <param name="atlas">The atlas whose <see cref="FontAtlas.DistanceRange"/> and <see cref="FontAtlas.Size"/> define the range.</param>
    /// <returns>The band width in em units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The atlas distance range is not greater than zero.</exception>
    public static float ComputeUnitRange(FontAtlas atlas) {
        return ComputeUnitRange(
            atlas,
            glyph: null
        );
    }
    /// <summary>Resolves the distance-field band width, in em units, preferring a glyph's per-glyph range over the atlas-wide range.</summary>
    /// <param name="atlas">The atlas providing the fallback range and the em size.</param>
    /// <param name="glyph">The glyph whose <see cref="FontAtlasGlyph.EmRange"/> or <see cref="FontAtlasGlyph.PxRange"/> overrides the atlas range when present, or <see langword="null"/> to use the atlas-wide range.</param>
    /// <returns>The band width in em units, chosen as the first positive of: the glyph em range, the glyph pixel range divided by <see cref="FontAtlas.Size"/>, then the atlas distance range divided by <see cref="FontAtlas.Size"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">No per-glyph range applies and the atlas distance range is not greater than zero.</exception>
    public static float ComputeUnitRange(FontAtlas atlas, FontAtlasGlyph? glyph) {
        ArgumentNullException.ThrowIfNull(atlas);

        if (
            (glyph?.EmRange is float emRange) &&
            (emRange > 0.0f)
        ) {
            return emRange;
        }

        if (
            (glyph?.PxRange is float pxRange) &&
            (pxRange > 0.0f)
        ) {
            return (pxRange / atlas.Size);
        }

        if (atlas.DistanceRange <= 0.0f) {
            throw new ArgumentOutOfRangeException(
                message: "MTSDF atlas distance range must be greater than zero.",
                paramName: nameof(atlas)
            );
        }

        return (atlas.DistanceRange / atlas.Size);
    }
    /// <summary>Maps a storage <see cref="FontAtlasKind"/> to the <see cref="TextGlyphSamplingMode"/> a renderer should decode it with.</summary>
    /// <param name="kind">The atlas encoding.</param>
    /// <returns>The matching sampling mode; the mask kinds map to <see cref="TextGlyphSamplingMode.Mask"/>.</returns>
    public static TextGlyphSamplingMode ExpectedMode(FontAtlasKind kind) {
        return kind switch {
            FontAtlasKind.Msdf => TextGlyphSamplingMode.Msdf,
            FontAtlasKind.Mtsdf => TextGlyphSamplingMode.Mtsdf,
            FontAtlasKind.Sdf or FontAtlasKind.Psdf => TextGlyphSamplingMode.Sdf,
            _ => TextGlyphSamplingMode.Mask
        };
    }

    /// <summary>Reconstructs the true signed distance for a multi-channel field by taking the median of its three channel pseudo-distances.</summary>
    /// <param name="first">The first channel's pseudo-distance.</param>
    /// <param name="second">The second channel's pseudo-distance.</param>
    /// <param name="third">The third channel's pseudo-distance.</param>
    /// <returns>The median of the three values.</returns>
    public static float Median(float first, float second, float third) {
        return MathF.Max(
            x: MathF.Min(
                x: first,
                y: second
            ),
            y: MathF.Min(
                x: MathF.Max(
                    x: first,
                    y: second
                ),
                y: third
            )
        );
    }
    /// <summary>Indicates whether an atlas kind encodes a signed distance field rather than direct coverage.</summary>
    /// <param name="kind">The atlas encoding.</param>
    /// <returns><see langword="true"/> for <see cref="FontAtlasKind.Sdf"/>, <see cref="FontAtlasKind.Psdf"/>, <see cref="FontAtlasKind.Msdf"/>, and <see cref="FontAtlasKind.Mtsdf"/>; otherwise <see langword="false"/>.</returns>
    public static bool UsesDistanceField(FontAtlasKind kind) {
        return (kind is FontAtlasKind.Sdf or FontAtlasKind.Psdf or FontAtlasKind.Msdf or FontAtlasKind.Mtsdf);
    }
}
