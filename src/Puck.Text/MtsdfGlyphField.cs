using System.Numerics;

namespace Puck.Text;

/// <summary>
/// Evaluates one glyph cell's multi-channel true signed distance field after resolving its filled boundary:
/// RGB carry per-channel signed pseudo-distances whose median reconstructs corners, and alpha carries the
/// sampled distance to the resolved filled boundary. Texture quantization and filtering are not exact distance operations.
/// </summary>
/// <remarks>
/// Every texel stores <c>encoded = 0.5 + signedDistance / distanceRange</c> clamped to <c>[0, 1]</c>, distance in
/// texels, positive inside — keep in sync with <see cref="MtsdfSampling"/> and the shader decode. Inside/outside
/// is the nonzero winding rule evaluated against the normalized polygon; a texel whose channel median disagrees
/// with that fill, or drifts from the true distance by more than <see cref="ClashThresholdRangeFraction"/> of the
/// band, is flattened to the alpha value so channel clashes cannot override the sampled fill classification.
/// </remarks>
internal static class MtsdfGlyphField {
    // Corner reconstruction legitimately overshoots true distance by (1/sin(θ/2) − 1)·|d| — ~0.41·|d| at a right
    // angle — so only divergence beyond half the band is a clash worth flattening.
    private const float ClashThresholdRangeFraction = 0.5f;
    private const byte ColorBlue = 4;
    private const byte ColorCyan = 6;
    private const byte ColorGreen = 2;
    private const byte ColorMagenta = 5;
    private const byte ColorRed = 1;
    private const byte ColorWhite = 7;
    private const byte ColorYellow = 3;
    // sin of the smallest direction change treated as a corner (≈8.1°); smoother joins share all three channels.
    private const float CornerTurnSine = 0.1411f;

    internal readonly record struct ColoredSegment(FontOutlineSegment Segment, byte Color);
    // Rasterization walks every edge twice per texel. Keep the prepared edges contiguous so those walks use
    // indexed array/span iteration without allocating interface enumerators for each pixel.
    internal sealed record PreparedCell(FontGlyphGeometry Geometry, ColoredSegment[] Edges);

    // Orthogonality breaks |distance| ties at shared endpoints: the edge whose direction is more perpendicular to
    // the query offset owns the texel.
    private readonly record struct SegmentDistance(float Distance, float Orthogonality, float Parameter);

    internal static void EvaluateCell(PreparedCell prepared, byte[] atlasRgba, int atlasWidth,
        int cellHeight, int cellWidth, int cellX, int cellY, float distanceRange,
        float offsetX, float offsetY, FontGenerationBudget? budget) {
        var geometry = prepared.Geometry;
        var colored = prepared.Edges;

        if (colored.Length == 0) { return; }
        for (var y = 0; (y < cellHeight); y++) {
            budget?.CancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; (x < cellWidth); x++) {
                var point = new Vector2(
                    x: ((x + 0.5f) - offsetX),
                    y: ((y + 0.5f) - offsetY)
                );
                var bestTrue = new SegmentDistance(
                    Distance: float.MaxValue,
                    Orthogonality: 0f,
                    Parameter: 0f
                );
                var bestRed = bestTrue;
                var bestGreen = bestTrue;
                var bestBlue = bestTrue;
                FontOutlineSegment redSegment = default;
                FontOutlineSegment greenSegment = default;
                FontOutlineSegment blueSegment = default;

                foreach (var entry in colored) {
                    var candidate = TrueDistance(
                        segment: entry.Segment,
                        point: point
                    );

                    if (IsCloser(
                        best: bestTrue,
                        candidate: candidate
                    )) {
                        bestTrue = candidate;
                    }

                    if (
                        ((entry.Color & ColorRed) != 0) &&
                        IsCloser(
                        best: bestRed,
                        candidate: candidate
                    )
                    ) {
                        bestRed = candidate;
                        redSegment = entry.Segment;
                    }

                    if (
                        ((entry.Color & ColorGreen) != 0) &&
                        IsCloser(
                        best: bestGreen,
                        candidate: candidate
                    )
                    ) {
                        bestGreen = candidate;
                        greenSegment = entry.Segment;
                    }

                    if (
                        ((entry.Color & ColorBlue) != 0) &&
                        IsCloser(
                        best: bestBlue,
                        candidate: candidate
                    )
                    ) {
                        bestBlue = candidate;
                        blueSegment = entry.Segment;
                    }
                }

                var fillSign = ((WindingAt(
                    point: point,
                    segments: colored
                ) != 0)
                    ? 1f
                    : -1f
                );
                var alpha = (fillSign * MathF.Abs(x: bestTrue.Distance));
                var red = PseudoDistance(
                    point: point,
                    segment: redSegment,
                    trueDistance: bestRed
                );
                var green = PseudoDistance(
                    point: point,
                    segment: greenSegment,
                    trueDistance: bestGreen
                );
                var blue = PseudoDistance(
                    point: point,
                    segment: blueSegment,
                    trueDistance: bestBlue
                );
                var median = MtsdfSampling.Median(
                    first: red,
                    second: green,
                    third: blue
                );

                if ((median < 0f) != (alpha < 0f)) {
                    red = -red;
                    green = -green;
                    blue = -blue;
                    median = MtsdfSampling.Median(
                        first: red,
                        second: green,
                        third: blue
                    );
                }

                if (
                    ((median < 0f) != (alpha < 0f)) ||
                    (MathF.Abs(x: (median - alpha)) > (ClashThresholdRangeFraction * distanceRange))
                ) {
                    red = alpha;
                    green = alpha;
                    blue = alpha;
                }

                var atlasOffset = ((((cellY + y) * atlasWidth) + (cellX + x)) * 4);

                atlasRgba[atlasOffset] = Encode(
                    distance: red,
                    distanceRange: distanceRange
                );
                atlasRgba[(atlasOffset + 1)] = Encode(
                    distance: green,
                    distanceRange: distanceRange
                );
                atlasRgba[(atlasOffset + 2)] = Encode(
                    distance: blue,
                    distanceRange: distanceRange
                );
                atlasRgba[(atlasOffset + 3)] = Encode(
                    distance: alpha,
                    distanceRange: distanceRange
                );
            }
        }
    }
    // Resolve geometry and reserve raster work before allocating the full atlas image.
    internal static PreparedCell PrepareCell(FontGlyphGeometry geometry, int cellWidth, int cellHeight, FontGenerationBudget? budget) {
        geometry = GlyphBoundaryNormalizer.Normalize(
            budget: budget,
            geometry: geometry
        );
        if (geometry.IsEmpty) {
            return new(
                Edges: [],
                Geometry: geometry
            );
        }

        var colored = new List<ColoredSegment>();

        foreach (var contour in geometry.Contours) {
            ColorContour(
                output: colored,
                segments: contour
            );
        }

        if (((((long)cellWidth) * cellHeight) * colored.Count) > 100_000_000) {
            throw new InvalidDataException(message: "A glyph exceeds the 100-million edge-sample rasterization limit.");
        }
        budget?.Work(amount: checked((((2L * cellWidth) * cellHeight) * colored.Count)));
        return new(
            Edges: colored.ToArray(),
            Geometry: geometry
        );
    }

    private static void AppendRun(List<ColoredSegment> output, IReadOnlyList<FontOutlineSegment> segments, int start, int count, byte color) {
        for (var index = 0; (index < count); index++) {
            output.Add(item: new ColoredSegment(
                Color: color,
                Segment: segments[((start + index) % segments.Count)]
            ));
        }
    }
    // Colors one closed contour so adjacent runs at a corner share exactly one channel (cyan→magenta→yellow each
    // share one), smooth contours stay white, and a single-corner contour splits into thirds (the teardrop case).
    private static void ColorContour(List<ColoredSegment> output, IReadOnlyList<FontOutlineSegment> segments) {
        var corners = new List<int>();

        for (var index = 0; (index < segments.Count); index++) {
            var previous = segments[(((index + segments.Count) - 1) % segments.Count)];

            if (IsCorner(
                incoming: previous.DirectionAt(t: 1f),
                outgoing: segments[index].DirectionAt(t: 0f)
            )) {
                corners.Add(item: index);
            }
        }

        if (corners.Count == 0) {
            AppendRun(
                color: ColorWhite,
                count: segments.Count,
                output: output,
                segments: segments,
                start: 0
            );
            return;
        }

        if (corners.Count == 1) {
            var start = corners[0];

            if (segments.Count >= 3) {
                var firstCount = (segments.Count / 3);
                var secondCount = ((segments.Count - firstCount) / 2);
                var thirdCount = ((segments.Count - firstCount) - secondCount);

                AppendRun(
                    color: ColorCyan,
                    count: firstCount,
                    output: output,
                    segments: segments,
                    start: start
                );
                AppendRun(
                    color: ColorMagenta,
                    count: secondCount,
                    output: output,
                    segments: segments,
                    start: (start + firstCount)
                );
                AppendRun(
                    color: ColorYellow,
                    count: thirdCount,
                    output: output,
                    segments: segments,
                    start: ((start + firstCount) + secondCount)
                );
                return;
            }

            // One or two edges cannot carry three runs, so each edge is split into thirds instead.
            for (var index = 0; (index < segments.Count); index++) {
                var (first, second, third) = segments[((start + index) % segments.Count)].SplitInThirds();

                output.Add(item: new ColoredSegment(
                    Color: ColorCyan,
                    Segment: first
                ));
                output.Add(item: new ColoredSegment(
                    Color: ColorMagenta,
                    Segment: second
                ));
                output.Add(item: new ColoredSegment(
                    Color: ColorYellow,
                    Segment: third
                ));
            }

            return;
        }

        for (var cornerIndex = 0; (cornerIndex < corners.Count); cornerIndex++) {
            var runStart = corners[cornerIndex];
            var runEnd = corners[((cornerIndex + 1) % corners.Count)];
            var runCount = (((runEnd - runStart) + segments.Count) % segments.Count);

            if (runCount == 0) {
                runCount = segments.Count;
            }

            var color = ((cornerIndex % 3) switch {
                0 => ColorCyan,
                1 => ColorMagenta,
                _ => ColorYellow,
            });

            // A closed loop whose first and last runs took the same color would erase their shared corner; the last
            // run takes the color distinct from both neighbors instead.
            if (
                (cornerIndex == (corners.Count - 1)) &&
                (color == ColorCyan)
            ) {
                color = ColorMagenta;
            }

            AppendRun(
                color: color,
                count: runCount,
                output: output,
                segments: segments,
                start: runStart
            );
        }
    }
    private static float Cross(Vector2 a, Vector2 b) {
        return ((a.X * b.Y) - (a.Y * b.X));
    }
    private static byte Encode(float distance, float distanceRange) {
        var normalized = (0.5f + (distance / distanceRange));

        return ((byte)Math.Clamp(
            value: ((int)MathF.Round(x: (normalized * 255f))),
            min: 0,
            max: 255
        ));
    }
    private static bool IsCloser(in SegmentDistance candidate, in SegmentDistance best) {
        var candidateMagnitude = MathF.Abs(x: candidate.Distance);
        var bestMagnitude = MathF.Abs(x: best.Distance);

        return (
            (candidateMagnitude < bestMagnitude) ||
            ((candidateMagnitude == bestMagnitude) && (candidate.Orthogonality < best.Orthogonality))
        );
    }
    private static bool IsCorner(Vector2 incoming, Vector2 outgoing) {
        var a = Vector2.Normalize(value: incoming);
        var b = Vector2.Normalize(value: outgoing);

        return (
            (Vector2.Dot(
            value1: a,
            value2: b
        ) <= 0f) ||
            (MathF.Abs(x: Cross(
            a: a,
            b: b
        )) > CornerTurnSine)
        );
    }
    // Extends an endpoint-clamped distance along the endpoint tangent; the extension only ever wins when it is
    // closer, which is what lets the median rebuild a corner outside both of its edges.
    private static float PseudoDistance(in FontOutlineSegment segment, Vector2 point, in SegmentDistance trueDistance) {
        if (trueDistance.Parameter is > 0f and < 1f) {
            return trueDistance.Distance;
        }

        var atStart = (trueDistance.Parameter <= 0f);
        var endpoint = (atStart
            ? segment.Start
            : segment.End
        );
        var tangent = Vector2.Normalize(value: segment.DirectionAt(t: (atStart
            ? 0f
            : 1f)));
        var toPoint = (point - endpoint);
        var beyond = (atStart
            ? (Vector2.Dot(
                value1: toPoint,
                value2: tangent
            ) < 0f)
            : (Vector2.Dot(
                value1: toPoint,
                value2: tangent
            ) > 0f)
        );

        if (!beyond) {
            return trueDistance.Distance;
        }

        var perpendicular = Cross(
            a: tangent,
            b: toPoint
        );

        return ((MathF.Abs(x: perpendicular) < MathF.Abs(x: trueDistance.Distance))
            ? perpendicular
            : trueDistance.Distance
        );
    }
    // Boundary normalization has already reduced curves and discarded interior contour edges.
    private static SegmentDistance TrueDistance(in FontOutlineSegment segment, Vector2 point) {
        var direction = (segment.End - segment.Start);
        var lengthSquared = direction.LengthSquared();
        var parameter = ((lengthSquared > 0f)
            ? Math.Clamp(
                (Vector2.Dot(
                    value1: (point - segment.Start),
                    value2: direction
                ) / lengthSquared),
                0f,
                1f
            )
            : 0f
        );
        var offset = (point - (segment.Start + (parameter * direction)));
        var magnitude = offset.Length();
        var sign = ((Cross(
            a: direction,
            b: offset
        ) >= 0f)
            ? 1f
            : -1f
        );
        var orthogonality = (((magnitude > 0f) && (lengthSquared > 0f))
            ? MathF.Abs(x: Vector2.Dot(
                value1: Vector2.Normalize(value: direction),
                value2: (offset / magnitude)
            ))
            : 0f
        );

        return new(
            Distance: (sign * magnitude),
            Orthogonality: orthogonality,
            Parameter: parameter
        );
    }
    // Half-open Y intervals (not half-open curve parameters) count shared vertices exactly once.
    private static int WindingAt(ReadOnlySpan<ColoredSegment> segments, Vector2 point) {
        var winding = 0;

        foreach (var colored in segments) {
            var start = colored.Segment.Start;
            var end = colored.Segment.End;
            var side = Cross(
                a: (end - start),
                b: (point - start)
            );

            if (start.Y <= point.Y) {
                if (
                    (end.Y > point.Y) &&
                    (side > 0f)
                ) {
                    winding++;
                }
            } else if (
                (end.Y <= point.Y) &&
                (side < 0f)
            ) {
                winding--;
            }
        }
        return winding;
    }

    /// <summary>Writes one glyph cell's MTSDF texels into the atlas image.</summary>
    /// <param name="geometry">The glyph's pixel-space contours.</param>
    /// <param name="atlasRgba">The atlas image, tightly packed RGBA.</param>
    /// <param name="atlasWidth">The atlas image width in texels.</param>
    /// <param name="cellHeight">The cell height in texels.</param>
    /// <param name="cellWidth">The cell width in texels.</param>
    /// <param name="cellX">The cell's left edge in the atlas.</param>
    /// <param name="cellY">The cell's top edge in the atlas.</param>
    /// <param name="distanceRange">The encoded band width in texels.</param>
    /// <param name="offsetX">The pixel-space X of the cell's left texel column origin.</param>
    /// <param name="offsetY">The pixel-space Y of the cell's top texel row origin.</param>
    /// <param name="budget">The optional shared whole-job budget and cancellation token.</param>
    public static void EvaluateCell(
        FontGlyphGeometry geometry,
        byte[] atlasRgba,
        int atlasWidth,
        int cellHeight,
        int cellWidth,
        int cellX,
        int cellY,
        float distanceRange,
        float offsetX,
        float offsetY,
        FontGenerationBudget? budget = null
    ) {
        EvaluateCell(
            PrepareCell(
                budget: budget,
                cellHeight: cellHeight,
                cellWidth: cellWidth,
                geometry: geometry
            ),
            atlasRgba,
            atlasWidth,
            cellHeight,
            cellWidth,
            cellX,
            cellY,
            distanceRange,
            offsetX,
            offsetY,
            budget
        );
    }
}
