namespace Puck.Text;

/// <summary>
/// An axis-aligned rectangle defined by four independent edge coordinates. The structure is a neutral
/// geometric container: the unit system and axis orientation it carries are defined by the property that
/// exposes it, not by the type itself.
/// </summary>
/// <remarks>
/// Two distinct interpretations are used across the library:
/// <list type="bullet">
///   <item>
///     <description>
///       As <see cref="FontAtlasGlyph.PlaneBounds"/> (and the transformed plane bounds on
///       <see cref="TextGlyphPlacement.PlaneBounds"/>) the edges are in em units, measured in a y-up
///       space relative to the pen origin on the baseline. <see cref="Top"/> is therefore numerically
///       greater than <see cref="Bottom"/>, and <see cref="Bottom"/> is negative for glyphs that descend
///       below the baseline.
///     </description>
///   </item>
///   <item>
///     <description>
///       As <see cref="FontAtlasGlyph.AtlasBounds"/> the edges are in texels and locate the glyph's
///       source rectangle inside the atlas image. Dividing each edge by
///       <see cref="FontAtlas.Width"/> or <see cref="FontAtlas.Height"/> yields normalized texture
///       coordinates. The orientation of the vertical axis follows the generator that produced the atlas.
///     </description>
///   </item>
/// </list>
/// </remarks>
/// <param name="Left">The coordinate of the left edge.</param>
/// <param name="Bottom">The coordinate of the bottom edge.</param>
/// <param name="Right">The coordinate of the right edge.</param>
/// <param name="Top">The coordinate of the top edge.</param>
public readonly record struct FontAtlasBounds(float Left, float Bottom, float Right, float Top);
