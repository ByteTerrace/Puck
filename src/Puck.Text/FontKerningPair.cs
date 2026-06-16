namespace Puck.Text;

/// <summary>
/// An ordered pair of code points and the horizontal advance correction to apply when the second glyph
/// immediately follows the first.
/// </summary>
/// <remarks>
/// Kerning is directional: the pair is keyed on the left-then-right ordering, so swapping
/// <paramref name="Unicode1"/> and <paramref name="Unicode2"/> describes a different pair.
/// <see cref="FontAtlas"/> indexes these pairs and exposes the lookup through
/// <see cref="FontAtlas.GetKerningAdjustment(int, int)"/>, which <see cref="TextLayout"/> consults while
/// advancing the pen.
/// </remarks>
/// <param name="Unicode1">The Unicode scalar value of the left (preceding) glyph.</param>
/// <param name="Unicode2">The Unicode scalar value of the right (following) glyph.</param>
/// <param name="AdvanceAdjustment">
/// The amount, in em units, added to the pen advance between the two glyphs. Negative values pull the
/// glyphs closer together; positive values push them apart.
/// </param>
public readonly record struct FontKerningPair(int Unicode1, int Unicode2, float AdvanceAdjustment);
