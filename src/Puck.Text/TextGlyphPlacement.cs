using System.Numerics;

namespace Puck.Text;

/// <summary>
/// A single glyph positioned by <see cref="TextLayout"/>: its source glyph, the pen origin it was placed
/// at, the quad it occupies in the result's coordinate space, and the atlas rectangle to sample from.
/// </summary>
/// <remarks>
/// Unlike <see cref="FontAtlasGlyph.PlaneBounds"/>, which are baseline-relative em units,
/// <see cref="PlaneBounds"/> here are already offset by the pen and scaled into the layout's coordinate
/// space, so they describe where the glyph sits within the laid-out text block. <see cref="AtlasBounds"/>
/// are the glyph's texels in the atlas image and are used to derive texture coordinates.
/// </remarks>
/// <param name="Unicode">The Unicode scalar value of the placed glyph.</param>
/// <param name="BaselineOrigin">The pen position, in the result's coordinate space, at which the glyph was placed.</param>
/// <param name="Glyph">The source <see cref="FontAtlasGlyph"/> that was placed.</param>
/// <param name="PlaneBounds">The glyph quad in the result's coordinate space (pen-offset and scaled).</param>
/// <param name="AtlasBounds">The glyph's source rectangle in the atlas image, in texels.</param>
/// <param name="Atlas">The atlas the glyph belongs to, when the placement needs to carry its own atlas; otherwise <see langword="null"/>.</param>
public readonly record struct TextGlyphPlacement(
    int Unicode,
    Vector2 BaselineOrigin,
    FontAtlasGlyph Glyph,
    FontAtlasBounds PlaneBounds,
    FontAtlasBounds AtlasBounds,
    FontAtlas? Atlas = null
);
