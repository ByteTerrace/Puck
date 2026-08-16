using System.Numerics;

namespace Puck.Text;

/// <summary>One outline edge in atlas pixel space: a line when <see cref="IsCurve"/> is false (ignore
/// <see cref="Control"/>), otherwise a quadratic Bézier.</summary>
internal readonly record struct FontOutlineSegment(Vector2 Start, Vector2 Control, Vector2 End, bool IsCurve) {
    public Vector2 DirectionAt(float t) {
        if (!IsCurve) {
            return (End - Start);
        }

        var direction = ((Control - Start) + (t * ((Start - (2f * Control)) + End)));

        // A degenerate tangent (coincident control point at an endpoint) falls back to the chord.
        return ((direction.LengthSquared() > 0f)
            ? direction
            : (End - Start)
        );
    }
    public Vector2 PositionAt(float t) {
        if (!IsCurve) {
            return (Start + (t * (End - Start)));
        }

        var oneMinusT = (1f - t);

        return ((((oneMinusT * oneMinusT) * Start) + (((2f * oneMinusT) * t) * Control)) + ((t * t) * End));
    }
    /// <summary>Splits the segment into three parameter-equal parts (the teardrop coloring case).</summary>
    public (FontOutlineSegment First, FontOutlineSegment Second, FontOutlineSegment Third) SplitInThirds() {
        if (!IsCurve) {
            var oneThird = PositionAt(t: (1f / 3f));
            var twoThirds = PositionAt(t: (2f / 3f));

            return (
                First: new FontOutlineSegment(
                Start: Start,
                Control: default,
                End: oneThird,
                IsCurve: false
            ),
                Second: new FontOutlineSegment(
                Control: default,
                End: twoThirds,
                IsCurve: false,
                Start: oneThird
            ),
                Third: new FontOutlineSegment(
                Start: twoThirds,
                Control: default,
                End: End,
                IsCurve: false
            )
            );
        }

        // De Casteljau control points for the [0,1/3], [1/3,2/3], [2/3,1] spans of a quadratic.
        var p13 = PositionAt(t: (1f / 3f));
        var p23 = PositionAt(t: (2f / 3f));
        var c1 = Vector2.Lerp(
            value1: Start,
            value2: Control,
            amount: (1f / 3f)
        );
        var c2 = Vector2.Lerp(
            value1: Vector2.Lerp(
                value1: Start,
                value2: Control,
                amount: (2f / 3f)
            ),
            value2: Vector2.Lerp(
                value1: Control,
                value2: End,
                amount: (1f / 3f)
            ),
            amount: 0.5f
        );
        var c3 = Vector2.Lerp(
            value1: Control,
            value2: End,
            amount: (2f / 3f)
        );

        return (
            First: new FontOutlineSegment(
            Start: Start,
            Control: c1,
            End: p13,
            IsCurve: true
        ),
            Second: new FontOutlineSegment(
            Control: c2,
            End: p23,
            IsCurve: true,
            Start: p13
        ),
            Third: new FontOutlineSegment(
            Start: p23,
            Control: c3,
            End: End,
            IsCurve: true
        )
        );
    }
}
/// <summary>Converts a parsed glyph outline into closed contours of pixel-space edge segments — the one place
/// TrueType's implied on-curve midpoints and off-curve contour starts are resolved.</summary>
/// <remarks>Pixel space is font units scaled by pixels-per-unit with Y negated (screen-down rows), matching the
/// cell layout in <see cref="ManagedFontAtlasGenerator"/>.</remarks>
internal static class TrueTypeOutlineSegments {
    private static IReadOnlyList<TrueTypeGlyphPoint> ExpandImpliedPoints(IReadOnlyList<TrueTypeGlyphPoint> points) {
        if (points.Count == 0) {
            return [];
        }

        var expanded = new List<TrueTypeGlyphPoint>(capacity: checked((points.Count * 2)));

        for (var index = 0; (index < points.Count); index++) {
            var point = points[index];
            var next = points[((index + 1) % points.Count)];

            expanded.Add(item: point);

            if (
                !point.OnCurve &&
                !next.OnCurve
            ) {
                expanded.Add(item: new TrueTypeGlyphPoint(
                    OnCurve: true,
                    Position: ((point.Position + next.Position) * 0.5f)
                ));
            }
        }

        return expanded;
    }
    private static Vector2 ToPixelPosition(Vector2 position, float scale) {
        return new Vector2(
            x: (position.X * scale),
            y: (-position.Y * scale)
        );
    }

    /// <summary>Builds the pixel-space contours and their bounds for one glyph outline.</summary>
    public static FontGlyphGeometry Build(TrueTypeGlyphOutline outline, float scale) {
        var contours = new List<IReadOnlyList<FontOutlineSegment>>(capacity: outline.Contours.Count);
        var hasPoint = false;
        var left = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var top = float.PositiveInfinity;
        var bottom = float.NegativeInfinity;

        foreach (var contour in outline.Contours) {
            foreach (var point in contour.Points) {
                var pixelPosition = ToPixelPosition(
                    position: point.Position,
                    scale: scale
                );

                hasPoint = true;
                left = MathF.Min(
                    x: left,
                    y: pixelPosition.X
                );
                right = MathF.Max(
                    x: right,
                    y: pixelPosition.X
                );
                top = MathF.Min(
                    x: top,
                    y: pixelPosition.Y
                );
                bottom = MathF.Max(
                    x: bottom,
                    y: pixelPosition.Y
                );
            }

            var expanded = ExpandImpliedPoints(points: contour.Points);

            if (expanded.Count < 2) {
                continue;
            }

            var firstOnCurve = -1;

            for (var index = 0; (index < expanded.Count); index++) {
                if (expanded[index].OnCurve) {
                    firstOnCurve = index;
                    break;
                }
            }

            if (firstOnCurve < 0) {
                throw new InvalidDataException(message: "A TrueType contour could not be normalized to an on-curve start point.");
            }

            var ordered = new List<TrueTypeGlyphPoint>(capacity: checked((expanded.Count + 1)));

            for (var index = 0; (index < expanded.Count); index++) {
                ordered.Add(item: expanded[((firstOnCurve + index) % expanded.Count)]);
            }

            ordered.Add(item: ordered[0]);

            var segments = new List<FontOutlineSegment>(capacity: ordered.Count);
            var current = ToPixelPosition(
                position: ordered[0].Position,
                scale: scale
            );

            for (var index = 1; (index < ordered.Count);) {
                var point = ordered[index];

                if (point.OnCurve) {
                    var end = ToPixelPosition(
                        position: point.Position,
                        scale: scale
                    );

                    if (end != current) {
                        segments.Add(item: new FontOutlineSegment(
                            Control: default,
                            End: end,
                            IsCurve: false,
                            Start: current
                        ));
                    }

                    current = end;
                    index++;
                    continue;
                }

                if (
                    ((index + 1) >= ordered.Count) ||
                    !ordered[(index + 1)].OnCurve
                ) {
                    throw new InvalidDataException(message: "A TrueType contour contains adjacent unresolved control points.");
                }

                var control = ToPixelPosition(
                    position: point.Position,
                    scale: scale
                );
                var curveEnd = ToPixelPosition(
                    position: ordered[(index + 1)].Position,
                    scale: scale
                );

                if (
                    (curveEnd != current) ||
                    (control != current)
                ) {
                    segments.Add(item: new FontOutlineSegment(
                        Control: control,
                        End: curveEnd,
                        IsCurve: true,
                        Start: current
                    ));
                }

                current = curveEnd;
                index += 2;
            }

            if (segments.Count >= 2) {
                contours.Add(item: segments);
            }
        }

        return new FontGlyphGeometry(
            Bottom: (hasPoint
            ? bottom
            : 0f),
            Contours: contours,
            Left: (hasPoint
            ? left
            : 0f),
            Right: (hasPoint
            ? right
            : 0f),
            Top: (hasPoint
            ? top
            : 0f)
        );
    }
}
/// <summary>A glyph's pixel-space edge contours and point bounds (control points included, so the bounds are
/// conservative for curves).</summary>
internal sealed record FontGlyphGeometry(
    float Bottom,
    IReadOnlyList<IReadOnlyList<FontOutlineSegment>> Contours,
    float Left,
    float Right,
    float Top
) {
    public bool IsEmpty => (Contours.Count == 0);
}
