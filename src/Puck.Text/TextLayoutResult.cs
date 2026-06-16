namespace Puck.Text;

/// <summary>
/// The output of <see cref="TextLayout.Layout(FontAtlas, string, float, float?)"/>: the positioned glyph
/// placements together with the overall bounds of the laid-out text.
/// </summary>
/// <remarks>
/// <see cref="Width"/> and <see cref="Height"/> are in the same scaled units as the placements and
/// describe the extent of the text block, allowing a caller to align or fit it before mapping it to screen
/// pixels.
/// </remarks>
/// <param name="placements">The positioned glyphs, in input order.</param>
/// <param name="width">The maximum horizontal extent of the laid-out text, in scaled units.</param>
/// <param name="height">The total vertical extent of the laid-out text across all lines, in scaled units.</param>
public sealed class TextLayoutResult(
    IReadOnlyList<TextGlyphPlacement> placements,
    float width,
    float height
) {
    /// <summary>Gets the total vertical extent of the laid-out text across all lines, in scaled units.</summary>
    public float Height { get; } = height;
    /// <summary>Gets the positioned glyphs, in input order.</summary>
    public IReadOnlyList<TextGlyphPlacement> Placements { get; } = placements;
    /// <summary>Gets the maximum horizontal extent of the laid-out text, in scaled units.</summary>
    public float Width { get; } = width;
}
