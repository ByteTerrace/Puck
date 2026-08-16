namespace Puck.Text;

/// <summary>
/// A single glyph within a <see cref="FontAtlas"/>: its advance, the quad it occupies in em space, the
/// rectangle it occupies in the atlas image, and optional per-glyph distance-field range overrides.
/// </summary>
/// <remarks>
/// A glyph is keyed by its Unicode scalar value and, when <see cref="GlyphId"/> is available, can also be looked up
/// by the source font's glyph identifier. Whitespace and control glyphs may have a non-zero <see cref="Advance"/>
/// but no <see cref="PlaneBounds"/> or <see cref="AtlasBounds"/>, in which case they advance the pen without
/// contributing a drawn quad.
/// </remarks>
/// <param name="unicode">The Unicode scalar value this glyph directly represents, or <c>-1</c> for a glyph-ID-only entry selected by shaping.</param>
/// <param name="advance">
/// The horizontal advance, in em units, applied to the pen after this glyph.
/// </param>
/// <param name="planeBounds">
/// The glyph quad in em units, in a y-up space relative to the pen origin on the baseline, or
/// <see langword="null"/> for a glyph that occupies no area (such as a space).
/// </param>
/// <param name="atlasBounds">
/// The glyph's source rectangle in the atlas image, in texels, or <see langword="null"/> when the glyph
/// has no rasterized coverage.
/// </param>
/// <param name="emRange">
/// An optional per-glyph distance-field range expressed in em units. When present and positive it takes
/// precedence over <paramref name="pxRange"/> and the atlas-wide <see cref="FontAtlas.DistanceRange"/>.
/// </param>
/// <param name="pxRange">
/// An optional per-glyph distance-field range expressed in atlas pixels. Used when
/// <paramref name="emRange"/> is absent; it is converted to em units by dividing by
/// <see cref="FontAtlas.Size"/>.
/// </param>
/// <param name="glyphId">
/// The source font's glyph identifier, or <c>-1</c> when the atlas source does not preserve one. Glyph IDs are
/// independent of Unicode mappings and let a future shaping stage address ligatures and contextual substitutions.
/// </param>
public sealed class FontAtlasGlyph(
    int unicode,
    float advance,
    FontAtlasBounds? planeBounds,
    FontAtlasBounds? atlasBounds,
    float? emRange = null,
    float? pxRange = null,
    int glyphId = -1
) {
    /// <summary>Gets the horizontal advance, in em units, applied to the pen after this glyph.</summary>
    public float Advance { get; } = advance;
    /// <summary>Gets the glyph's source rectangle in the atlas image, in texels, or <see langword="null"/> when the glyph has no rasterized coverage.</summary>
    public FontAtlasBounds? AtlasBounds { get; } = atlasBounds;
    /// <summary>Gets an optional per-glyph distance-field range in em units; see <see cref="MtsdfSampling.ComputeUnitRange(FontAtlas, FontAtlasGlyph)"/> for how it is resolved.</summary>
    public float? EmRange { get; } = emRange;
    /// <summary>Gets the source font's glyph identifier, or <c>-1</c> when the atlas source did not preserve one.</summary>
    public int GlyphId { get; } = glyphId;
    /// <summary>Gets the glyph quad in em units relative to the baseline pen origin, or <see langword="null"/> for a glyph that occupies no area.</summary>
    public FontAtlasBounds? PlaneBounds { get; } = planeBounds;
    /// <summary>Gets an optional per-glyph distance-field range in atlas pixels; see <see cref="MtsdfSampling.ComputeUnitRange(FontAtlas, FontAtlasGlyph)"/> for how it is resolved.</summary>
    public float? PxRange { get; } = pxRange;
    /// <summary>Gets the Unicode scalar value this glyph directly represents, or <c>-1</c> when it is addressable only by glyph identifier.</summary>
    public int Unicode { get; } = unicode;
}
