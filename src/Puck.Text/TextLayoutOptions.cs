namespace Puck.Text;

/// <summary>How lines of a laid-out block are positioned horizontally relative to the block's widest line.</summary>
public enum TextAlignment {
    /// <summary>Every line starts at <c>x = 0</c> (the default).</summary>
    Left = 0,
    /// <summary>Each line is centered on the block's widest line.</summary>
    Center = 1,
    /// <summary>Each line's right edge meets the block's widest line's right edge.</summary>
    Right = 2,
}
/// <summary>Which line of a laid-out block sits at <c>y = 0</c>: the typographic lines a designer anchors text by (the
/// baseline, the capitals' top and middle, the font's ascent and descent).</summary>
public enum TextVerticalAnchor {
    /// <summary>The first line's baseline, the line the letters stand on (the default).</summary>
    Baseline = 0,
    /// <summary>The top of the first line's flat capitals (<see cref="FontAtlasMetrics.EffectiveCapHeight"/> above its
    /// baseline).</summary>
    CapTop = 1,
    /// <summary>Halfway between the top of the first line's capitals and the last line's baseline: the middle of a
    /// block of capitals, where a label of capitals reads as centred.</summary>
    CapMiddle = 2,
    /// <summary>The first line's ascent (<see cref="FontAtlasMetrics.Ascender"/> above its baseline), the top of the text's
    /// box.</summary>
    Top = 3,
    /// <summary>Halfway between <see cref="Top"/> and <see cref="Bottom"/>, the middle of the text's box.</summary>
    Middle = 4,
    /// <summary>The last line's descent (<see cref="FontAtlasMetrics.Descender"/> below its baseline), the bottom of the
    /// text's box.</summary>
    Bottom = 5,
}
/// <summary>
/// Optional layout controls for <see cref="TextLayout"/> beyond the plain scale: greedy wrapping, block
/// alignment, per-glyph tracking, line-spacing and the vertical anchor. The defaults reproduce the option-free layout
/// exactly.
/// </summary>
/// <param name="MaxLineWidth">The maximum line width, in the layout's scaled units, beyond which glyphs wrap
/// to a new line (greedy at word boundaries, with glyph breaks only for an oversized word); <see langword="null"/> = only explicit line feeds break lines.
/// Must be greater than zero when provided.</param>
/// <param name="Alignment">How lines position horizontally within the block.</param>
/// <param name="Tracking">Extra advance added after every glyph, in em units (scaled with the layout scale).
/// Negative values tighten; must be finite.</param>
/// <param name="LineHeightScale">A multiplier on <see cref="FontAtlasMetrics.LineHeight"/> for the baseline
/// step between lines. Must be finite and greater than zero.</param>
/// <param name="VerticalAnchor">Which line of the block sits at <c>y = 0</c>.</param>
public sealed record TextLayoutOptions(
    float? MaxLineWidth = null,
    TextAlignment Alignment = TextAlignment.Left,
    float Tracking = 0f,
    float LineHeightScale = 1f,
    TextVerticalAnchor VerticalAnchor = TextVerticalAnchor.Baseline
) {
    /// <summary>The option-free layout: no wrap, left-aligned, no tracking, unit line spacing, on the baseline.</summary>
    public static TextLayoutOptions Default { get; } = new();
}
