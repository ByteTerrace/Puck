namespace Puck.Text;

/// <summary>
/// The fully resolved sampling parameters for drawing one glyph: the atlas encoding, the decoding mode,
/// and the distance-field band widths in both em and screen-pixel terms. It is the bundle a renderer
/// hands to a shader so it can anti-alias the glyph correctly at its target size.
/// </summary>
/// <remarks>
/// Construct instances with <see cref="Create(FontAtlas, FontAtlasGlyph, float)"/>, which selects the
/// appropriate <see cref="TextGlyphSamplingMode"/> and computes the band widths via
/// <see cref="MtsdfSampling"/>. For mask atlases the distance-field fields are inert
/// (<see cref="UnitRange"/> is zero and <see cref="ScreenPixelRange"/> is one).
/// </remarks>
/// <param name="AtlasKind">The encoding of the source atlas.</param>
/// <param name="Mode">The decoding strategy the renderer should apply.</param>
/// <param name="UnitRange">The signed-distance band width in em units, or zero for mask atlases.</param>
/// <param name="ScreenPixelRange">The signed-distance band width measured in destination screen pixels, which controls anti-aliasing sharpness.</param>
public readonly record struct TextGlyphSampling(FontAtlasKind AtlasKind, TextGlyphSamplingMode Mode, float UnitRange, float ScreenPixelRange) {
    /// <summary>Resolves the sampling parameters for a glyph at a given screen scale.</summary>
    /// <param name="atlas">The atlas the glyph belongs to; its <see cref="FontAtlas.Kind"/> selects mask versus distance-field handling.</param>
    /// <param name="glyph">The glyph being drawn, whose per-glyph range overrides are honored when present.</param>
    /// <param name="screenScale">The number of destination screen pixels per em for the glyph at its target size; see <see cref="MtsdfSampling.ComputeScreenScale(float, float, float, float)"/>.</param>
    /// <returns>The resolved <see cref="TextGlyphSampling"/>. For non-distance-field atlases this is the inert <see cref="TextGlyphSamplingMode.Mask"/> result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> or <paramref name="glyph"/> is <see langword="null"/>.</exception>
    public static TextGlyphSampling Create(FontAtlas atlas, FontAtlasGlyph glyph, float screenScale) {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(glyph);

        if (!MtsdfSampling.UsesDistanceField(kind: atlas.Kind)) {
            return new TextGlyphSampling(
                AtlasKind: atlas.Kind,
                Mode: TextGlyphSamplingMode.Mask,
                ScreenPixelRange: 1.0f,
                UnitRange: 0.0f
            );
        }

        var unitRange = MtsdfSampling.ComputeUnitRange(
            atlas: atlas,
            glyph: glyph
        );

        return new TextGlyphSampling(
            AtlasKind: atlas.Kind,
            Mode: MtsdfSampling.ExpectedMode(kind: atlas.Kind),
            ScreenPixelRange: MtsdfSampling.ComputeScreenPixelRange(
                screenScale: screenScale,
                unitRange: unitRange
            ),
            UnitRange: unitRange
        );
    }
}
