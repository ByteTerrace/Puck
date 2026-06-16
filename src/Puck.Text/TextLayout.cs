using System.Numerics;

namespace Puck.Text;

/// <summary>
/// Lays out a string against a <see cref="FontAtlas"/>, producing the positioned glyph quads of a
/// <see cref="TextLayoutResult"/>. The layout is render-agnostic: it computes geometry in a scaled em
/// space and leaves the mapping to screen pixels to the caller.
/// </summary>
/// <remarks>
/// Layout walks the input one Unicode scalar at a time, advancing a pen by each glyph's
/// <see cref="FontAtlasGlyph.Advance"/> and applying kerning between consecutive glyphs. Carriage returns
/// are ignored and line feeds start a new line; code points the atlas does not contain are skipped.
/// </remarks>
public sealed class TextLayout {
    /// <summary>Lays out <paramref name="text"/> against <paramref name="atlas"/> at the given scale.</summary>
    /// <param name="atlas">The atlas providing glyph geometry, metrics, and kerning.</param>
    /// <param name="text">The text to lay out. May contain line feeds (<c>\n</c>); carriage returns (<c>\r</c>) are ignored.</param>
    /// <param name="scale">The multiplier applied to every em-space measurement, yielding the units of the result. Must be greater than zero. Defaults to <c>1.0</c>.</param>
    /// <param name="maxLineWidth">An optional maximum line width, in the same scaled units as the result, beyond which glyphs wrap to a new line. When <see langword="null"/>, only explicit line feeds break lines. When provided, must be greater than zero.</param>
    /// <returns>A <see cref="TextLayoutResult"/> whose placements, width, and height are expressed in scaled units.</returns>
    /// <remarks>
    /// The result is in a y-up coordinate space: the first line's baseline sits at <c>y = 0</c> and each
    /// subsequent line steps the baseline down by <see cref="FontAtlasMetrics.LineHeight"/> (more negative
    /// <c>y</c>). Wrapping is greedy and operates at glyph granularity — it breaks before the glyph that
    /// would exceed <paramref name="maxLineWidth"/> rather than at word boundaries. Glyphs without plane
    /// bounds (such as spaces) advance the pen without contributing a placement.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> or <paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not greater than zero, or <paramref name="maxLineWidth"/> is supplied and is not greater than zero.</exception>
    public TextLayoutResult Layout(FontAtlas atlas, string text, float scale = 1.0f, float? maxLineWidth = null) {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(text);

        if (scale <= 0.0f) {
            throw new ArgumentOutOfRangeException(
                message: "Text scale must be greater than zero.",
                paramName: nameof(scale)
            );
        }

        if (maxLineWidth is <= 0.0f) {
            throw new ArgumentOutOfRangeException(
                message: "Text max line width must be greater than zero when provided.",
                paramName: nameof(maxLineWidth)
            );
        }

        var placements = new List<TextGlyphPlacement>();
        var cursorX = 0.0f;
        var baselineY = 0.0f;
        var maxRight = 0.0f;
        var lineCount = 1;
        int? previousUnicode = null;

        foreach (var rune in text.EnumerateRunes()) {
            var unicode = rune.Value;

            if (unicode == '\r') {
                continue;
            }

            if (unicode == '\n') {
                lineCount++;
                cursorX = 0.0f;
                baselineY -= (atlas.Metrics.LineHeight * scale);
                previousUnicode = null;
                continue;
            }

            if (!atlas.TryGetGlyph(
                glyph: out var glyph,
                unicode: unicode
            )) {
                previousUnicode = null;
                continue;
            }

            if (previousUnicode is int previous) {
                cursorX += (atlas.GetKerningAdjustment(
                    leftUnicode: previous,
                    rightUnicode: unicode
                ) * scale);
            }

            if (ShouldWrapGlyph(
                cursorX: cursorX,
                glyph: glyph,
                maxLineWidth: maxLineWidth,
                scale: scale
            )) {
                lineCount++;
                cursorX = 0.0f;
                baselineY -= (atlas.Metrics.LineHeight * scale);
                previousUnicode = null;
            }

            if (
                (glyph.PlaneBounds is FontAtlasBounds planeBounds) &&
                (glyph.AtlasBounds is FontAtlasBounds atlasBounds)
            ) {
                var transformedPlaneBounds = new FontAtlasBounds(
                    Bottom: (baselineY + (planeBounds.Bottom * scale)),
                    Left: (cursorX + (planeBounds.Left * scale)),
                    Right: (cursorX + (planeBounds.Right * scale)),
                    Top: (baselineY + (planeBounds.Top * scale))
                );

                placements.Add(item: new TextGlyphPlacement(
                    Atlas: atlas,
                    AtlasBounds: atlasBounds,
                    BaselineOrigin: new Vector2(
                        x: cursorX,
                        y: baselineY
                    ),
                    Glyph: glyph,
                    PlaneBounds: transformedPlaneBounds,
                    Unicode: unicode
                ));
                maxRight = MathF.Max(
                    x: maxRight,
                    y: transformedPlaneBounds.Right
                );
            }

            cursorX += (glyph.Advance * scale);
            maxRight = MathF.Max(
                x: maxRight,
                y: cursorX
            );
            previousUnicode = unicode;
        }

        var metrics = atlas.Metrics;

        return new TextLayoutResult(
            height: (((metrics.Ascender - metrics.Descender) + ((lineCount - 1) * metrics.LineHeight)) * scale),
            placements: placements,
            width: maxRight
        );
    }

    private static bool ShouldWrapGlyph(FontAtlasGlyph glyph, float cursorX, float scale, float? maxLineWidth) {
        if (
            (maxLineWidth is not float width) ||
            (cursorX <= 0.0f)
        ) {
            return false;
        }

        var right = ((glyph.PlaneBounds is FontAtlasBounds planeBounds)
            ? (cursorX + (planeBounds.Right * scale))
            : (cursorX + (glyph.Advance * scale)));

        return (right > width);
    }
}
