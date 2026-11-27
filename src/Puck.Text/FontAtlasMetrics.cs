namespace Puck.Text;

/// <summary>
/// The font-wide vertical metrics carried by a <see cref="FontAtlas"/>, used by <see cref="TextLayout"/>
/// to advance the baseline between lines and by callers that draw underlines.
/// </summary>
/// <remarks>
/// Every value is expressed in em units (1.0 equals one em). Multiply by a layout scale — typically
/// derived from <see cref="FontAtlas.Size"/> — to obtain pixels. Vertical values follow a y-up,
/// baseline-relative convention: the baseline sits at zero, ascending values are positive, and descending
/// values are negative.
/// </remarks>
/// <param name="LineHeight">
/// The baseline-to-baseline distance for consecutive lines of text, in em units.
/// </param>
/// <param name="Ascender">The maximum extent above the baseline, in em units (positive).</param>
/// <param name="Descender">The maximum extent below the baseline, in em units (typically negative).</param>
/// <param name="UnderlineY">
/// The vertical position of the underline relative to the baseline, in em units.
/// </param>
/// <param name="UnderlineThickness">The stroke thickness of the underline, in em units.</param>
/// <param name="CapHeight">The height of the font's flat capitals above the baseline, in em units (positive), or zero
/// when the atlas records none; <see cref="EffectiveCapHeight"/> reads it.</param>
public readonly record struct FontAtlasMetrics(
    float LineHeight,
    float Ascender,
    float Descender,
    float UnderlineY,
    float UnderlineThickness,
    float CapHeight = 0f
) {
    /// <summary>Gets the capitals' height in em units: <see cref="CapHeight"/>, or seven tenths of the ascender for an
    /// atlas that records none.</summary>
    public float EffectiveCapHeight => ((CapHeight > 0f) ? CapHeight : (Ascender * 0.7f));
}
