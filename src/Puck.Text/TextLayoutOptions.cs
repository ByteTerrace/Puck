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
/// <summary>
/// Optional layout controls for <see cref="TextLayout"/> beyond the plain scale: greedy wrapping, block
/// alignment, per-glyph tracking, and line-spacing. The defaults reproduce the option-free layout exactly.
/// </summary>
/// <param name="MaxLineWidth">The maximum line width, in the layout's scaled units, beyond which glyphs wrap
/// to a new line (greedy, glyph-granular); <see langword="null"/> = only explicit line feeds break lines.
/// Must be greater than zero when provided.</param>
/// <param name="Alignment">How lines position horizontally within the block.</param>
/// <param name="Tracking">Extra advance added after every glyph, in em units (scaled with the layout scale).
/// Negative values tighten; must be finite.</param>
/// <param name="LineHeightScale">A multiplier on <see cref="FontAtlasMetrics.LineHeight"/> for the baseline
/// step between lines. Must be finite and greater than zero.</param>
public sealed record TextLayoutOptions(
    float? MaxLineWidth = null,
    TextAlignment Alignment = TextAlignment.Left,
    float Tracking = 0f,
    float LineHeightScale = 1f
) {
    /// <summary>The option-free layout: no wrap, left-aligned, no tracking, unit line spacing.</summary>
    public static TextLayoutOptions Default { get; } = new();
}
