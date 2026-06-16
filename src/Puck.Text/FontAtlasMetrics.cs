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
public readonly record struct FontAtlasMetrics(
    float LineHeight,
    float Ascender,
    float Descender,
    float UnderlineY,
    float UnderlineThickness
);
