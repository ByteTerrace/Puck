using System.Collections;
using System.Numerics;
using System.Text;

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
/// <para>
/// Enrichment composes with layout, not around it: the
/// <see cref="Layout(FontAtlas, IEnumerable{TextEffectRune}, float, float?)"/> overload takes runes already paired
/// with their effect (from <see cref="TextEnrichmentTags"/> / <see cref="BbCodeTextMarkup"/>) and carries each effect
/// onto its <see cref="TextGlyphPlacement.Effect"/>, so one atlas and one layout serve every text tier. The plain
/// string overload is exactly this with <see cref="TextEffect.None"/> on every glyph.
/// </para>
/// </remarks>
public sealed class TextLayout {
    // Shifts each line so its visual span centers in (or right-aligns to) the block's widest visual span, then anchors
    // the BLOCK on the origin the same way: Center puts the block's midpoint at x = 0, Right its right edge — so the
    // origin is the point an author aligned about, not always the block's left edge. Both line edges matter: real
    // fonts may carry negative left side bearings, and trailing pen advance past the last placed glyph does not count
    // toward a line's visual width.
    private static float AlignLines(TextAlignment alignment, List<(int StartIndex, float VisualLeft, float VisualRight)> lineBreaks, List<TextGlyphPlacement> placements) {
        var blockWidth = 0.0f;

        foreach (var line in lineBreaks) {
            blockWidth = MathF.Max(
                x: blockWidth,
                y: (line.VisualRight - line.VisualLeft)
            );
        }

        var fraction = ((alignment == TextAlignment.Center)
            ? 0.5f
            : 1.0f
        );

        for (var lineIndex = 0; (lineIndex < lineBreaks.Count); lineIndex++) {
            var (startIndex, visualLeft, visualRight) = lineBreaks[lineIndex];
            var endIndex = (((lineIndex + 1) < lineBreaks.Count)
                ? lineBreaks[(lineIndex + 1)].StartIndex
                : placements.Count
            );
            var visualWidth = (visualRight - visualLeft);
            var shift = ((((blockWidth - visualWidth) * fraction) - visualLeft) - (blockWidth * fraction));

            if (shift == 0.0f) {
                continue;
            }

            for (var index = startIndex; (index < endIndex); index++) {
                var placement = placements[index];
                var bounds = placement.PlaneBounds;

                placements[index] = placement with {
                    BaselineOrigin = new Vector2(
                    x: (placement.BaselineOrigin.X + shift),
                    y: placement.BaselineOrigin.Y
                ),
                    PlaneBounds = new FontAtlasBounds(
                    Bottom: bounds.Bottom,
                    Left: (bounds.Left + shift),
                    Right: (bounds.Right + shift),
                    Top: bounds.Top
                ),
                };
            }
        }

        return blockWidth;
    }
    // The plain-string Layout overloads' fast path: every glyph carries TextEffect.None (there is no enrichment tier
    // to carry here), so this wraps the framework's own rune decoder (StringRuneEnumerator, itself a struct) instead
    // of routing through the enrichment-aware IEnumerable<TextEffectRune> shape. Because this type is a genuine
    // value type implementing IEnumerator<TextEffectRune> directly (not via a compiler-generated iterator class),
    // LayoutRunes's generic instantiation over it dispatches MoveNext/Current through a devirtualized constrained
    // call, so laying out a plain string allocates neither an iterator object nor a boxed enumerator.
    private struct PlainRuneEnumerator(string text) : IEnumerator<TextEffectRune> {
        private StringRuneEnumerator m_runes = text.EnumerateRunes();

        public readonly TextEffectRune Current => new(
            Effect: TextEffect.None,
            Rune: m_runes.Current
        );
        readonly object IEnumerator.Current => Current;

        public bool MoveNext() => m_runes.MoveNext();
        public readonly void Reset() => throw new NotSupportedException();
        public readonly void Dispose() { }
    }

    // Generic over the enumerator type rather than IEnumerable<TextEffectRune> so the plain-string fast path
    // (PlainRuneEnumerator, a value type) gets its own devirtualized instantiation while the enrichment-aware
    // overloads (an interface-typed IEnumerator<TextEffectRune>) share the ordinary interface-dispatch instantiation
    // exactly as a plain foreach over IEnumerable<TextEffectRune> would — MoveNext/Current/Dispose called manually
    // here reproduce foreach's own desugaring (including disposing the enumerator in a finally) bit for bit.
    // placementCapacity seeds the placements list; 0 reproduces List<T>'s own default (lazy, grow-from-empty)
    // behavior for a caller with no cheap upper bound to offer.
    private static TextLayoutResult LayoutRunes<TEnumerator>(FontAtlas atlas, TEnumerator runes, float scale, TextLayoutOptions options, int placementCapacity) where TEnumerator : IEnumerator<TextEffectRune> {
        if (
            !float.IsFinite(f: scale) ||
            (scale <= 0.0f)
        ) {
            throw new ArgumentOutOfRangeException(
                message: "Text scale must be greater than zero.",
                paramName: nameof(scale)
            );
        }

        var maxLineWidth = options.MaxLineWidth;

        if (
            (maxLineWidth is float lineWidth) &&
            (!float.IsFinite(f: lineWidth) || (lineWidth <= 0.0f))
        ) {
            throw new ArgumentOutOfRangeException(
                message: "Text max line width must be greater than zero when provided.",
                paramName: nameof(options)
            );
        }

        if (!float.IsFinite(f: options.Tracking)) {
            throw new ArgumentOutOfRangeException(
                message: "Text tracking must be finite.",
                paramName: nameof(options)
            );
        }

        if (
            !float.IsFinite(f: options.LineHeightScale) ||
            (options.LineHeightScale <= 0.0f)
        ) {
            throw new ArgumentOutOfRangeException(
                message: "Text line-height scale must be greater than zero.",
                paramName: nameof(options)
            );
        }

        if (!Enum.IsDefined(value: options.Alignment)) {
            throw new ArgumentOutOfRangeException(
                message: "Text alignment must be a defined alignment value.",
                paramName: nameof(options)
            );
        }

        var placements = new List<TextGlyphPlacement>(capacity: placementCapacity);
        var lineBreaks = ((options.Alignment == TextAlignment.Left)
            ? null
            : new List<(int StartIndex, float VisualLeft, float VisualRight)>()
        );
        var tracking = (options.Tracking * scale);
        var lineStep = ((atlas.Metrics.LineHeight * options.LineHeightScale) * scale);
        var cursorX = 0.0f;
        var baselineY = 0.0f;
        var maxRight = 0.0f;
        var lineVisualLeft = 0.0f;
        var lineVisualRight = 0.0f;
        var lineStartIndex = 0;
        var lineHasContent = false;
        var lineCount = 1;
        int? previousUnicode = null;

        void StartNewLine() {
            lineBreaks?.Add(item: (
                StartIndex: lineStartIndex,
                VisualLeft: lineVisualLeft,
                VisualRight: lineVisualRight
            ));
            lineStartIndex = placements.Count;
            lineVisualLeft = 0.0f;
            lineVisualRight = 0.0f;
            lineHasContent = false;
            lineCount++;
            cursorX = 0.0f;
            baselineY -= lineStep;
            previousUnicode = null;
        }

        try {
            while (runes.MoveNext()) {
                var enriched = runes.Current;
                var unicode = enriched.Rune.Value;

                if (unicode == '\r') {
                    continue;
                }

                if (unicode == '\n') {
                    StartNewLine();
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
                    lineHasContent: lineHasContent,
                    maxLineWidth: maxLineWidth,
                    scale: scale
                )) {
                    StartNewLine();
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
                        Effect: enriched.Effect,
                        Glyph: glyph,
                        PlaneBounds: transformedPlaneBounds,
                        Unicode: unicode
                    ));
                    maxRight = MathF.Max(
                        x: maxRight,
                        y: transformedPlaneBounds.Right
                    );
                    lineVisualLeft = MathF.Min(
                        x: lineVisualLeft,
                        y: transformedPlaneBounds.Left
                    );
                    lineVisualRight = MathF.Max(
                        x: lineVisualRight,
                        y: transformedPlaneBounds.Right
                    );
                }

                cursorX += ((glyph.Advance * scale) + tracking);
                maxRight = MathF.Max(
                    x: maxRight,
                    y: cursorX
                );
                lineHasContent = true;
                previousUnicode = unicode;
            }
        } finally {
            runes.Dispose();
        }

        if (lineBreaks is not null) {
            lineBreaks.Add(item: (
                StartIndex: lineStartIndex,
                VisualLeft: lineVisualLeft,
                VisualRight: lineVisualRight
            ));
            maxRight = MathF.Max(
                x: maxRight,
                y: AlignLines(
                    alignment: options.Alignment,
                    lineBreaks: lineBreaks,
                    placements: placements
                )
            );
        }

        var metrics = atlas.Metrics;

        return new TextLayoutResult(
            height: (((metrics.Ascender - metrics.Descender) * scale) + ((lineCount - 1) * lineStep)),
            placements: placements,
            width: maxRight
        );
    }
    private static bool ShouldWrapGlyph(FontAtlasGlyph glyph, float cursorX, bool lineHasContent, float scale, float? maxLineWidth) {
        if (
            (maxLineWidth is not float width) ||
            !lineHasContent
        ) {
            return false;
        }

        var right = ((glyph.PlaneBounds is FontAtlasBounds planeBounds)
            ? (cursorX + (planeBounds.Right * scale))
            : (cursorX + (glyph.Advance * scale))
        );

        return (right > width);
    }

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

        return LayoutRunes(
            atlas: atlas,
            options: ((maxLineWidth is null)
            ? TextLayoutOptions.Default
            : new TextLayoutOptions(MaxLineWidth: maxLineWidth)),
            placementCapacity: text.Length,
            runes: new PlainRuneEnumerator(text: text),
            scale: scale
        );
    }
    /// <summary>Lays out <paramref name="text"/> against <paramref name="atlas"/> under the full option set —
    /// wrapping, alignment, tracking, and line spacing (see <see cref="TextLayoutOptions"/>).</summary>
    /// <param name="atlas">The atlas providing glyph geometry, metrics, and kerning.</param>
    /// <param name="text">The text to lay out. May contain line feeds (<c>\n</c>); carriage returns (<c>\r</c>) are ignored.</param>
    /// <param name="options">The layout options; <see cref="TextLayoutOptions.Default"/> reproduces the option-free overload.</param>
    /// <param name="scale">The multiplier applied to every em-space measurement. Must be greater than zero.</param>
    /// <returns>A <see cref="TextLayoutResult"/> whose placements, width, and height are expressed in scaled units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/>, <paramref name="text"/>, or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not greater than zero, or an option
    /// is outside its documented domain.</exception>
    public TextLayoutResult Layout(FontAtlas atlas, string text, TextLayoutOptions options, float scale = 1.0f) {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);

        return LayoutRunes(
            atlas: atlas,
            options: options,
            placementCapacity: text.Length,
            runes: new PlainRuneEnumerator(text: text),
            scale: scale
        );
    }
    /// <summary>Lays out enrichment-aware runes — each already paired with its effect — carrying every effect onto its
    /// placement so a downstream tier can resolve per-glyph channels.</summary>
    /// <param name="atlas">The atlas providing glyph geometry, metrics, and kerning.</param>
    /// <param name="runes">The enriched runes, in order (from <see cref="TextEnrichmentTags.EnumerateRichTextRunes"/> or <see cref="BbCodeTextMarkup.EnrichRunes"/>). Line feeds (<c>\n</c>) break lines and carriage returns are ignored, exactly as the string overload.</param>
    /// <param name="scale">The multiplier applied to every em-space measurement. Must be greater than zero. Defaults to <c>1.0</c>.</param>
    /// <param name="maxLineWidth">An optional maximum line width for greedy glyph-level wrapping; must be greater than zero when provided.</param>
    /// <returns>A <see cref="TextLayoutResult"/> whose placements carry their <see cref="TextGlyphPlacement.Effect"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> or <paramref name="runes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not greater than zero, or <paramref name="maxLineWidth"/> is supplied and is not greater than zero.</exception>
    public TextLayoutResult Layout(FontAtlas atlas, IEnumerable<TextEffectRune> runes, float scale = 1.0f, float? maxLineWidth = null) {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(runes);

        return LayoutRunes(
            atlas: atlas,
            options: ((maxLineWidth is null)
            ? TextLayoutOptions.Default
            : new TextLayoutOptions(MaxLineWidth: maxLineWidth)),
            placementCapacity: 0,
            runes: runes.GetEnumerator(),
            scale: scale
        );
    }
    /// <summary>Lays out enrichment-aware runes under the full option set (see
    /// <see cref="Layout(FontAtlas, string, TextLayoutOptions, float)"/>).</summary>
    /// <param name="atlas">The atlas providing glyph geometry, metrics, and kerning.</param>
    /// <param name="runes">The enriched runes, in order.</param>
    /// <param name="options">The layout options; <see cref="TextLayoutOptions.Default"/> reproduces the option-free overload.</param>
    /// <param name="scale">The multiplier applied to every em-space measurement. Must be greater than zero.</param>
    /// <returns>A <see cref="TextLayoutResult"/> whose placements carry their <see cref="TextGlyphPlacement.Effect"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/>, <paramref name="runes"/>, or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not greater than zero, or an option
    /// is outside its documented domain.</exception>
    public TextLayoutResult Layout(FontAtlas atlas, IEnumerable<TextEffectRune> runes, TextLayoutOptions options, float scale = 1.0f) {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(runes);
        ArgumentNullException.ThrowIfNull(options);

        return LayoutRunes(
            atlas: atlas,
            options: options,
            placementCapacity: 0,
            runes: runes.GetEnumerator(),
            scale: scale
        );
    }
}
