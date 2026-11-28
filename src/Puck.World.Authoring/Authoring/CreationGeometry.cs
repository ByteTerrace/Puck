namespace Puck.World.Authoring;

/// <summary>
/// The whole-document geometry a <see cref="CreationDocument"/> implies — the reach a stamp of it needs. Per-shape
/// reach is <see cref="CreationStampEmitter.ShapeReach"/>; this is the document-shaped half that folds a catalog-free
/// text estimate in beside the shapes.
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

    /// <summary>Returns the box a creation's shapes fill at rest, in its own frame at unit scale: every shape that adds
    /// to the solid (a carving shape is left out, since it only removes), each by its stamp box, or by its bound sphere's
    /// box where no box holds it (a flared, twisted or swept shape).</summary>
    /// <param name="document">The creation's engine document.</param>
    /// <returns>The least and greatest corners; an inverted box (positive infinity low, negative infinity high) for a
    /// creation with no finite solid shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static (System.Numerics.Vector3 Low, System.Numerics.Vector3 High) RestBox(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var shapes = (document.Shapes ?? []);
        var low = new System.Numerics.Vector3(value: float.PositiveInfinity);
        var high = new System.Numerics.Vector3(value: float.NegativeInfinity);

        for (var index = 0; (index < shapes.Count); index++) {
            if (Puck.SignedDistance.SdfBoundAlgebra.Carves(blend: (shapes[index].Blend ?? Puck.SignedDistance.SdfBlendOp.Union))) {
                continue;
            }

            var box = CreationStampEmitter.ShapeStampBox(document: document, shapeIndex: index, transform: RestTransform);

            if (box is not { } corners) {
                var (center, radius) = CreationStampEmitter.ShapeStampBound(document: document, shapeIndex: index, transform: RestTransform);

                corners = ((center - new System.Numerics.Vector3(value: radius)), (center + new System.Numerics.Vector3(value: radius)));
            }
            if (!float.IsFinite(f: ((corners.Low.X + corners.Low.Y) + corners.Low.Z)) || !float.IsFinite(f: ((corners.High.X + corners.High.Y) + corners.High.Z))) {
                continue;
            }

            low = System.Numerics.Vector3.Min(value1: low, value2: corners.Low);
            high = System.Numerics.Vector3.Max(value1: high, value2: corners.High);
        }

        return (low, high);
    }
    private static readonly CreationStampTransform RestTransform = new(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity, 1f, null);

    /// <summary>A whole creation's worst-case reach from its own local origin — the largest
    /// <see cref="CreationStampEmitter.ShapeReach"/> across every authored shape (folds, warps, cells, dilation and
    /// onion charged) and a catalog-free estimate of every text run, the instance bound a stamp of it needs (a
    /// masked-out tile must never clip a glyph that reaches past the boxes).</summary>
    /// <param name="document">The creation (normalized or not; absent lists read as empty).</param>
    /// <returns>The reach in creation-local units (a small floor for an empty document).</returns>
    public static float Reach(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var reach = 0f;
        var any = false;

        foreach (var shape in (document.Shapes ?? [])) {
            reach = MathF.Max(
                x: reach,
                y: CreationStampEmitter.ShapeReach(shape: shape)
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
            )) + run.Relief);

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
