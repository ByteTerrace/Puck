namespace Puck.Text;

/// <summary>
/// A single glyph within a <see cref="FontAtlas"/>: its advance, the quad it occupies in em space, the
/// rectangle it occupies in the atlas image, and optional per-glyph distance-field range overrides.
/// </summary>
/// <remarks>
/// A glyph is keyed by its Unicode scalar value and looked up through
/// <see cref="FontAtlas.TryGetGlyph(int, out FontAtlasGlyph)"/>. Whitespace and control glyphs may have a
/// non-zero <see cref="Advance"/> but no <see cref="PlaneBounds"/> or <see cref="AtlasBounds"/>, in which
/// case they advance the pen without contributing a drawn quad.
/// </remarks>
/// <param name="unicode">The Unicode scalar value this glyph represents.</param>
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
public sealed class FontAtlasGlyph(
    int unicode,
    float advance,
    FontAtlasBounds? planeBounds,
    FontAtlasBounds? atlasBounds,
    float? emRange = null,
    float? pxRange = null
) {
    /// <summary>Gets the horizontal advance, in em units, applied to the pen after this glyph.</summary>
    public float Advance { get; } = advance;
    /// <summary>Gets the glyph's source rectangle in the atlas image, in texels, or <see langword="null"/> when the glyph has no rasterized coverage.</summary>
    public FontAtlasBounds? AtlasBounds { get; } = atlasBounds;
    /// <summary>Gets an optional per-glyph distance-field range in em units; see <see cref="MtsdfSampling.ComputeUnitRange(FontAtlas, FontAtlasGlyph)"/> for how it is resolved.</summary>
    public float? EmRange { get; } = emRange;
    /// <summary>Gets the glyph quad in em units relative to the baseline pen origin, or <see langword="null"/> for a glyph that occupies no area.</summary>
    public FontAtlasBounds? PlaneBounds { get; } = planeBounds;
    /// <summary>Gets an optional per-glyph distance-field range in atlas pixels; see <see cref="MtsdfSampling.ComputeUnitRange(FontAtlas, FontAtlasGlyph)"/> for how it is resolved.</summary>
    public float? PxRange { get; } = pxRange;
    /// <summary>Gets the Unicode scalar value this glyph represents.</summary>
    public int Unicode { get; } = unicode;
}
