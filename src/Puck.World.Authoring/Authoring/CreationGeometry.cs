using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>
/// The whole-document geometry a <see cref="CreationDocument"/> implies — the reach a stamp of it needs. Per-primitive
/// dimensions live in <see cref="SdfSolidGeometry"/>; this is the document-shaped half that folds text runs in beside
/// the shapes.
/// </summary>
public static class CreationGeometry {
    // The authored line structure of a run's text: line-feed-separated line count, the largest per-line scalar count,
    // and the total scalar count. Carriage returns match TextLayout's ignored control; spaces count because a mapped
    // whitespace glyph advances the pen even though it emits no shape.
    private static (int Lines, int WidestLineScalars, int ScalarCount) CountAuthoredLines(string? text) {
        if (text is not { Length: > 0 }) {
            return (Lines: 1, WidestLineScalars: 0, ScalarCount: 0);
        }

        var lines = 1;
        var widest = 0;
        var current = 0;
        var scalarCount = 0;

        foreach (var rune in text.EnumerateRunes()) {
            if (rune.Value == '\n') {
                lines++;
                widest = Math.Max(
                    val1: widest,
                    val2: current
                );
                current = 0;

                continue;
            }

            if (rune.Value == '\r') {
                continue;
            }

            current++;
            scalarCount++;
        }

        return (Lines: lines, WidestLineScalars: Math.Max(
            val1: widest,
            val2: current
        ), ScalarCount: scalarCount);
    }

    /// <summary>A whole creation's worst-case reach from its own local origin — the largest per-shape reach across
    /// every authored shape and text run, the instance bound a stamp of it needs (a masked-out tile must never clip a
    /// glyph that reaches past the boxes).</summary>
    /// <param name="document">The creation (normalized or not; absent lists read as empty).</param>
    /// <returns>The reach in creation-local units (a small floor for an empty document).</returns>
    public static float Reach(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var reach = 0f;
        var any = false;

        foreach (var shape in (document.Shapes ?? [])) {
            reach = MathF.Max(
                x: reach,
                y: (shape.Position.Length() + SdfSolidGeometry.Reach(
                    scale: shape.Scale,
                    type: shape.Type
                ))
            );
            any = true;
        }

        foreach (var run in (document.TextRuns ?? [])) {
            // A catalog-free conservative run reach for server/editor callers: its anchor offset + the larger of the
            // block's horizontal and vertical extents + the relief depth. The renderer replaces this estimate with
            // CreationStampEmitter.RenderReach, which measures the resolved atlas/layout exactly. Horizontal includes
            // every scalar that can advance the pen (spaces included) plus the full authored tracking magnitude.
            // Vertical: a 2-em line step (covering ordinary font metrics)
            // times the line-spacing multiplier, times the worst-case line count (every glyph on its own wrapped
            // line when wrapping; the authored line count otherwise). A fat bound only costs a rare extra
            // evaluation; a too-tight one would cull real glyphs.
            var em = MathF.Max(
                x: run.EmHeight,
                y: 0.001f
            );
            var glyphs = MathF.Max(
                x: run.GlyphCount,
                y: 1
            );
            var lineSpacing = (run.LineSpacing ?? 1f);

            var (authoredLines, widestLineScalars, scalarCount) = CountAuthoredLines(text: run.Text);
            var scalarAdvance = ((0.6f + MathF.Abs(x: (run.Tracking ?? 0f))) * em);
            var unwrappedHorizontal = (scalarAdvance * MathF.Max(
                x: widestLineScalars,
                y: 1
            ));
            var horizontal = ((run.MaxWidth is { } maxWidth)
                ? MathF.Max(
                    x: (maxWidth + scalarAdvance),
                    y: unwrappedHorizontal
                )
                : unwrappedHorizontal
            );
            var vertical = (((2f * em) * lineSpacing) * ((run.MaxWidth is null)
                ? MathF.Max(
                    x: authoredLines,
                    y: 1
                )
                : MathF.Max(
                    x: scalarCount,
                    y: glyphs
                )));
            var runReach = ((run.Position.Length() + MathF.Max(
                x: horizontal,
                y: vertical
            )) + (run.Depth ?? 0.02f));

            reach = MathF.Max(
                x: reach,
                y: runReach
            );
            any = true;
        }

        return (any
            ? reach
            : 0.6f
        );
    }
}
