using System.Numerics;

namespace Puck.Text;

/// <summary>
/// Evaluates one glyph cell's multi-channel true signed distance field directly from its quadratic outline
/// segments: RGB carry per-channel signed pseudo-distances whose median reconstructs corners, and alpha carries the
/// exact signed distance a marcher may trust.
/// </summary>
/// <remarks>
/// Every texel stores <c>encoded = 0.5 + signedDistance / distanceRange</c> clamped to <c>[0, 1]</c>, distance in
/// texels, positive inside — KEEP IN SYNC with <see cref="SdfCoverageAtlas"/> and the shader decode. Inside/outside
/// is the nonzero winding rule evaluated analytically against the outline; a texel whose channel median disagrees
/// with that fill, or drifts from the true distance by more than <see cref="ClashThresholdRangeFraction"/> of the
/// band, is flattened to the alpha value so channel clashes can never out-vote the exact field.
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

    private readonly record struct ColoredSegment(FontOutlineSegment Segment, byte Color);
    // Orthogonality breaks |distance| ties at shared endpoints: the edge whose direction is more perpendicular to
    // the query offset owns the texel.
    private readonly record struct SegmentDistance(float Distance, float Orthogonality, float Parameter);

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
    private static float Median(float r, float g, float b) {
        return MathF.Max(
            x: MathF.Min(
                x: r,
                y: g
            ),
            y: MathF.Min(
                x: MathF.Max(
                    x: r,
                    y: g
                ),
                y: b
            )
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
    // Returns the unclamped-nearest real roots of a·t³+b·t²+c·t+d over [0,1] candidates; degenerate leading terms
    // fall through to the quadratic and linear cases.
    private static int SolveCubic(float a, float b, float c, float d, Span<float> roots) {
        const float Epsilon = 1e-9f;

        if (MathF.Abs(x: a) < Epsilon) {
            if (MathF.Abs(x: b) < Epsilon) {
                if (MathF.Abs(x: c) < Epsilon) {
                    return 0;
                }

                roots[0] = (-d / c);
                return 1;
            }

            var quadraticDiscriminant = ((c * c) - ((4f * b) * d));

            if (quadraticDiscriminant < 0f) {
                return 0;
            }

            var sqrt = MathF.Sqrt(x: quadraticDiscriminant);

            roots[0] = ((-c + sqrt) / (2f * b));
            roots[1] = ((-c - sqrt) / (2f * b));
            return 2;
        }

        // Depressed cubic t = s - b/(3a); solved by the trigonometric method for three real roots and by the
        // hyperbolic/Cardano single-root form otherwise.
        var inverseA = (1f / a);
        var b2 = (b * inverseA);
        var c2 = (c * inverseA);
        var d2 = (d * inverseA);
        var offset = (b2 / 3f);
        var p = (c2 - ((b2 * b2) / 3f));
        var q = ((((((2f * b2) * b2) * b2) / 27f) - ((b2 * c2) / 3f)) + d2);
        var halfQ = (q * 0.5f);
        var thirdP = (p / 3f);
        var discriminant = ((halfQ * halfQ) + ((thirdP * thirdP) * thirdP));

        if (discriminant > 0f) {
            var sqrt = MathF.Sqrt(x: discriminant);
            var u = MathF.Cbrt(x: (-halfQ + sqrt));
            var v = MathF.Cbrt(x: (-halfQ - sqrt));

            roots[0] = ((u + v) - offset);
            return 1;
        }

        if (thirdP >= 0f) {
            roots[0] = -offset;
            return 1;
        }

        var radius = MathF.Sqrt(x: -thirdP);
        var cosine = Math.Clamp(
            max: 1f,
            min: -1f,
            value: (halfQ / (((-radius) * radius) * radius))
        );
        var angle = (MathF.Acos(x: cosine) / 3f);

        for (var index = 0; (index < 3); index++) {
            roots[index] = (((2f * radius) * MathF.Cos(x: (angle - (((2f * MathF.PI) * index) / 3f)))) - offset);
        }

        return 3;
    }
    // The exact signed distance to one segment: magnitude from the nearest point, sign from which side of the local
    // direction the query falls (left of travel positive; the winding pass owns the global inside convention).
    private static SegmentDistance TrueDistance(in FontOutlineSegment segment, Vector2 point) {
        float parameter;

        if (!segment.IsCurve) {
            var direction = (segment.End - segment.Start);
            var lengthSquared = direction.LengthSquared();

            parameter = ((lengthSquared > 0f)
                ? Math.Clamp(
                    value: (Vector2.Dot(
                        value1: (point - segment.Start),
                        value2: direction
                    ) / lengthSquared),
                    min: 0f,
                    max: 1f
                )
                : 0f
            );
        } else {
            var a = ((segment.Start - (2f * segment.Control)) + segment.End);
            var b1 = (segment.Control - segment.Start);
            var b0 = (segment.Start - point);
            Span<float> roots = stackalloc float[3];
            var rootCount = SolveCubic(
                a: Vector2.Dot(
                    value1: a,
                    value2: a
                ),
                b: (3f * Vector2.Dot(
                    value1: a,
                    value2: b1
                )),
                c: ((2f * Vector2.Dot(
                    value1: b1,
                    value2: b1
                )) + Vector2.Dot(
                    value1: a,
                    value2: b0
                )),
                d: Vector2.Dot(
                    value1: b1,
                    value2: b0
                ),
                roots: roots
            );

            parameter = 0f;

            var bestLengthSquared = (segment.PositionAt(t: 0f) - point).LengthSquared();
            var endLengthSquared = (segment.PositionAt(t: 1f) - point).LengthSquared();

            if (endLengthSquared < bestLengthSquared) {
                bestLengthSquared = endLengthSquared;
                parameter = 1f;
            }

            for (var index = 0; (index < rootCount); index++) {
                var candidate = roots[index];

                if (
                    (candidate <= 0f) ||
                    (candidate >= 1f)
                ) {
                    continue;
                }

                var candidateLengthSquared = (segment.PositionAt(t: candidate) - point).LengthSquared();

                if (candidateLengthSquared < bestLengthSquared) {
                    bestLengthSquared = candidateLengthSquared;
                    parameter = candidate;
                }
            }
        }

        var nearest = segment.PositionAt(t: parameter);
        var offset = (point - nearest);
        var tangent = segment.DirectionAt(t: parameter);
        var magnitude = offset.Length();
        var sign = ((Cross(
            a: tangent,
            b: offset
        ) >= 0f)
            ? 1f
            : -1f
        );
        var orthogonality = (((magnitude > 0f) && (tangent.LengthSquared() > 0f))
            ? MathF.Abs(x: Vector2.Dot(
                value1: Vector2.Normalize(value: tangent),
                value2: (offset / magnitude)
            ))
            : 0f
        );

        return new SegmentDistance(
            Distance: (sign * magnitude),
            Orthogonality: orthogonality,
            Parameter: parameter
        );
    }
    // Nonzero winding via analytic crossings of the +X ray: quadratic roots for curves, half-open [0,1) per segment
    // so shared vertices count once; tangent (zero-derivative) roots are grazes, not crossings.
    private static int WindingAt(IReadOnlyList<ColoredSegment> segments, Vector2 point) {
        var winding = 0;
        Span<float> roots = stackalloc float[2];

        foreach (var colored in segments) {
            var segment = colored.Segment;

            if (!segment.IsCurve) {
                var start = segment.Start;
                var end = segment.End;

                if (start.Y == end.Y) {
                    continue;
                }

                var t = ((point.Y - start.Y) / (end.Y - start.Y));

                if (
                    (t < 0f) ||
                    (t >= 1f)
                ) {
                    continue;
                }

                if ((start.X + (t * (end.X - start.X))) > point.X) {
                    winding += ((end.Y > start.Y)
                        ? 1
                        : -1
                    );
                }

                continue;
            }

            var a = ((segment.Start.Y - (2f * segment.Control.Y)) + segment.End.Y);
            var b = (2f * (segment.Control.Y - segment.Start.Y));
            var c = (segment.Start.Y - point.Y);
            int rootCount;

            if (MathF.Abs(x: a) < 1e-9f) {
                if (MathF.Abs(x: b) < 1e-9f) {
                    continue;
                }

                roots[0] = (-c / b);
                rootCount = 1;
            } else {
                var discriminant = ((b * b) - ((4f * a) * c));

                if (discriminant < 0f) {
                    continue;
                }

                var sqrt = MathF.Sqrt(x: discriminant);

                roots[0] = ((-b + sqrt) / (2f * a));
                roots[1] = ((-b - sqrt) / (2f * a));
                rootCount = 2;
            }

            for (var index = 0; (index < rootCount); index++) {
                var t = roots[index];

                if (
                    (t < 0f) ||
                    (t >= 1f)
                ) {
                    continue;
                }

                var derivativeY = (b + ((2f * a) * t));

                if (derivativeY == 0f) {
                    continue;
                }

                if (segment.PositionAt(t: t).X > point.X) {
                    winding += ((derivativeY > 0f)
                        ? 1
                        : -1
                    );
                }
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
        float offsetY
    ) {
        if (geometry.IsEmpty) {
            return;
        }

        var colored = new List<ColoredSegment>();

        foreach (var contour in geometry.Contours) {
            ColorContour(
                output: colored,
                segments: contour
            );
        }


        for (var y = 0; (y < cellHeight); y++) {
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
                var median = Median(
                    b: blue,
                    g: green,
                    r: red
                );

                if ((median < 0f) != (alpha < 0f)) {
                    red = -red;
                    green = -green;
                    blue = -blue;
                    median = Median(
                        b: blue,
                        g: green,
                        r: red
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
}
