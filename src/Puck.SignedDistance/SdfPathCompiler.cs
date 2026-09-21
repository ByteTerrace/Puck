using System.Numerics;

namespace Puck.SignedDistance;

internal static class SdfPathCompiler {
    public static SdfPathEdge[] Compile(SdfPathProfile path) {
        if (!float.IsFinite(f: path.Tolerance) || (path.Tolerance < 0.00001f) || (path.Tolerance > 0.1f) ||
            (path.Contours is not { Count: > 0 and <= 16 })) {
            throw new ArgumentException(message: "A path needs 1..16 contours and tolerance in [0.00001, 0.1].", paramName: nameof(path));
        }
        var stroke = path.Stroke;

        if ((path.Shear is { } shear) && ((stroke is not null) || (((uint)shear.Target) > 1u) ||
            !float.IsFinite(f: shear.Linear) || !float.IsFinite(f: shear.Quadratic) || !float.IsFinite(f: shear.Cubic) ||
            !float.IsFinite(f: shear.Offset) || !float.IsFinite(f: shear.From) || !float.IsFinite(f: shear.To) ||
            (shear.From < -1f) || (shear.To > 1f) || (shear.From >= shear.To) ||
            (MathF.Abs(x: shear.Linear) > 4f) || (MathF.Abs(x: shear.Quadratic) > 4f) || (MathF.Abs(x: shear.Cubic) > 4f) || (MathF.Abs(x: shear.Offset) > 4f))) {
            throw new ArgumentException(message: "Path shear requires fill, target 0 or 1, finite coefficients in [-4,4], and -1 <= from < to <= 1.", paramName: nameof(path));
        }
        if ((stroke is not null) && (!float.IsFinite(f: stroke.RadiusStart) || !float.IsFinite(f: stroke.RadiusEnd) ||
            (stroke.RadiusStart <= 0f) || (stroke.RadiusEnd <= 0f) || !float.IsFinite(f: stroke.From) ||
            !float.IsFinite(f: stroke.To) || (stroke.From < 0f) || (stroke.To > 1f) || (stroke.From >= stroke.To))) {
            throw new ArgumentException(message: "Path stroke needs positive finite radii and 0 <= from < to <= 1.", paramName: nameof(path));
        }
        List<SdfPathEdge> edges = [];

        foreach (var contour in path.Contours) {
            if ((contour is null) || (contour.Segments is not { Count: > 0 and <= SdfPathProfile.MaxEdges })) {
                throw new ArgumentException(message: "A contour needs 1..128 segments.", paramName: nameof(path));
            }
            RequirePoint(p: contour.Start);
            if (contour.Closed && (stroke is not null) && (stroke.RadiusStart != stroke.RadiusEnd)) {
                throw new ArgumentException(message: "A closed stroke needs equal endpoint radii.", paramName: nameof(path));
            }
            var start = contour.Start;

            for (var i = 0; (i < contour.Segments.Count); i++) {
                var segment = contour.Segments[i];

                if (segment is null) { throw new ArgumentException(message: "A path segment cannot be null.", paramName: nameof(path)); }
                RequirePoint(p: segment.End);
                if (segment.Control is { } control) { RequirePoint(p: control); }
                if (segment.Control2 is { } control2) { RequirePoint(p: control2); }
                if (segment.ArcCenter is { } center) { RequirePoint(p: center); }
                if (((segment.Control2 is not null) && (segment.Control is null)) ||
                    ((segment.ArcCenter is not null) && (segment.Control is not null)) ||
                    (segment.Clockwise && (segment.ArcCenter is null))) {
                    throw new ArgumentException(message: "A segment is a line, Bezier, or arc, never a mixture.", paramName: nameof(path));
                }
                var curve = new Curve(a: start, segment: segment);
                var count = contour.Segments.Count;
                var t0 = 0d;
                // Split exactly at width knots so every interval has a bounded second derivative.
                if (stroke is not null) {
                    ReadOnlySpan<float> knots = [stroke.From, stroke.To];

                    foreach (var knot in knots) {
                        var t = ((((double)knot) * count) - i);

                        if ((t > t0) && (t < 1d)) {
                            Flatten(count: count, curve: curve, depth: 0, edges: edges, from: t0, path: path, segment: i, to: t);
                            t0 = t;
                        }
                    }
                }
                Flatten(count: count, curve: curve, depth: 0, edges: edges, from: t0, path: path, segment: i, to: 1d);
                start = segment.End;
            }
            if (((stroke is null) || contour.Closed) && (start != contour.Start)) {
                Flatten(new Curve(a: start, segment: new(contour.Start)), 0d, 1d, 0, 1, path, edges, 0);
            }
        }
        if (edges.Count == 0) { throw new ArgumentException(message: "A path cannot be empty.", paramName: nameof(path)); }
        if (stroke is null) { ValidateFill(edges: edges); }
        return [.. edges];
    }

    private static void Flatten(Curve curve, double from, double to, int segment, int count,
        SdfPathProfile path, List<SdfPathEdge> edges, int depth) {
        var span = (to - from);
        var error = ((((curve.SecondDerivativeBound * span) * span) / 8d) + ((to == 1d) ? curve.EndpointErrorBound : 0d));

        if (path.Shear is { } shear) {
            var startPoint = curve.At(t: from);
            var endPoint = curve.At(t: to);
            var d0 = ((shear.Target == 0) ? startPoint.Y : startPoint.X);
            var d1 = ((shear.Target == 0) ? endPoint.Y : endPoint.X);
            var low = (Math.Min(val1: d0, val2: d1) - error);
            var high = (Math.Max(val1: d0, val2: d1) + error);
            var slope = ((Math.Abs(value: shear.Linear) + (2d * Math.Abs(value: shear.Quadratic))) + (3d * Math.Abs(value: shear.Cubic)));
            var curvature = ((2d * Math.Abs(value: shear.Quadratic)) + (6d * Math.Abs(value: shear.Cubic)));

            if ((low >= shear.To) || (high <= shear.From)) { /* The offset is constant here. */ } else if (((low < shear.From) && (high > shear.From)) || ((low < shear.To) && (high > shear.To))) {
                // A clamp kink has no bounded second derivative, but its Lipschitz displacement still bounds
                // chord error. Only intervals crossing the kink take this slower-converging bound.
                error += (((slope * curve.FirstDerivativeBound) * span) * 0.5d);
            } else {
                error = ((error * (1d + slope)) + (((((curvature * curve.FirstDerivativeBound) * curve.FirstDerivativeBound) * span) * span) / 8d));
            }
        }
        var stroke = path.Stroke;

        if (stroke is { Smooth: true }) {
            var width = (((double)stroke.To) - stroke.From);
            var dt = (span / count);

            error += ((((6d * Math.Abs(value: (((double)stroke.RadiusEnd) - stroke.RadiusStart))) * dt) * dt) / ((8d * width) * width));
        }
        if (error > path.Tolerance) {
            if (depth >= 24) { throw new ArgumentException(message: "Path subdivision exceeded its depth budget."); }
            var middle = ((from + to) * 0.5d);

            Flatten(count: count, curve: curve, depth: (depth + 1), edges: edges, from: from, path: path, segment: segment, to: middle);
            Flatten(count: count, curve: curve, depth: (depth + 1), edges: edges, from: middle, path: path, segment: segment, to: to);
            return;
        }
        var a = Deform(point: curve.At(t: from), shear: path.Shear);
        var b = Deform(point: curve.At(t: to), shear: path.Shear);
        var ra = Radius(stroke: stroke, t: ((segment + from) / count));
        var rb = Radius(stroke: stroke, t: ((segment + to) / count));

        Add(edges, new(A: a, B: b, RadiusA: ra, RadiusB: rb));
    }
    private static Vector2 Deform(Vector2 point, SdfPathShear? shear) {
        if (shear is null) { return point; }
        var d = Math.Clamp(((shear.Target == 0) ? point.Y : point.X), shear.From, shear.To);
        var offset = (shear.Offset + (d * (shear.Linear + (d * (shear.Quadratic + (d * shear.Cubic))))));

        return ((shear.Target == 0) ? new(x: (point.X + offset), y: point.Y) : new(x: point.X, y: (point.Y + offset)));
    }
    private static float Radius(SdfPathStroke? stroke, double t) {
        if (stroke is null) { return 0f; }
        t = Math.Clamp(((t - stroke.From) / (stroke.To - stroke.From)), 0d, 1d);
        if (stroke.Smooth) { t = ((t * t) * (3d - (2d * t))); }
        return ((float)(stroke.RadiusStart + ((stroke.RadiusEnd - stroke.RadiusStart) * t)));
    }
    private static void Add(List<SdfPathEdge> edges, SdfPathEdge edge) {
        if (edges.Count >= SdfPathProfile.MaxEdges) { throw new ArgumentException(message: "Path exceeds its 128-edge budget at the requested tolerance."); }
        if (Vector2.DistanceSquared(value1: edge.A, value2: edge.B) < (SdfPathProfile.MinEdgeLength * SdfPathProfile.MinEdgeLength)) {
            throw new ArgumentException(message: "A path edge is shorter than 0.000001 profile units at the requested tolerance.");
        }
        if (((MathF.Abs(x: edge.A.X) + edge.RadiusA) > 1f) || ((MathF.Abs(x: edge.A.Y) + edge.RadiusA) > 1f) ||
            ((MathF.Abs(x: edge.B.X) + edge.RadiusB) > 1f) || ((MathF.Abs(x: edge.B.Y) + edge.RadiusB) > 1f)) {
            throw new ArgumentException(message: "A path, including its stroke, must fit [-1, 1] on both axes.");
        }
        edges.Add(item: edge);
    }
    private static void RequirePoint(Vector2 p) {
        if (!float.IsFinite(f: p.X) || !float.IsFinite(f: p.Y) || (MathF.Abs(x: p.X) > 1f) || (MathF.Abs(x: p.Y) > 1f)) {
            throw new ArgumentException(message: "Path points must be finite and in [-1, 1].");
        }
    }

    internal static void ValidateFill(IReadOnlyList<SdfPathEdge> edges) {
        if (edges.Count < 3) { throw new ArgumentException(message: "A filled path needs at least three edges."); }
        var contours = new int[edges.Count];

        for (var start = 0; (start < edges.Count);) {
            var end = start;
            var next = edges[start].A;
            var area = 0d;

            do {
                if ((end >= edges.Count) || (edges[end].A != next)) { throw new ArgumentException(message: "Filled path edges must form closed, continuous contours."); }
                var edge = edges[end];

                contours[end] = start;
                area += ((((double)edge.A.X) * edge.B.Y) - (((double)edge.A.Y) * edge.B.X));
                next = edge.B;
                end++;
            } while (next != edges[start].A);
            if (((end - start) < 3) || (area == 0d)) { throw new ArgumentException(message: "A filled contour must enclose nonzero area with at least three edges."); }
            start = end;
        }
        for (var i = 0; (i < edges.Count); i++) {
            for (var j = (i + 1); (j < edges.Count); j++) {
                var a = edges[i];
                var b = edges[j];
                var adjacent = ((contours[i] == contours[j]) && ((a.B == b.A) || (b.B == a.A)));

                if (Intersects(a: a, adjacent: adjacent, b: b)) { throw new ArgumentException(message: "Filled path boundaries cannot cross, overlap, or touch except at adjacent endpoints."); }
            }
        }
    }

    private static double Cross(Vector2 a, Vector2 b, Vector2 c) =>
        (((((double)b.X) - a.X) * (((double)c.Y) - a.Y)) - ((((double)b.Y) - a.Y) * (((double)c.X) - a.X)));
    private static bool Intersects(SdfPathEdge a, SdfPathEdge b, bool adjacent) {
        var c1 = Cross(a: a.A, b: a.B, c: b.A);
        var c2 = Cross(a: a.A, b: a.B, c: b.B);
        var c3 = Cross(a: b.A, b: b.B, c: a.A);
        var c4 = Cross(a: b.A, b: b.B, c: a.B);

        if (adjacent && ((c1 != 0d) || (c2 != 0d))) { return false; }
        if ((c1 == 0d) && (c2 == 0d) && (c3 == 0d) && (c4 == 0d)) {
            var useX = (a.A.X != a.B.X);
            var a0 = (useX ? a.A.X : a.A.Y);
            var a1 = (useX ? a.B.X : a.B.Y);
            var b0 = (useX ? b.A.X : b.A.Y);
            var b1 = (useX ? b.B.X : b.B.Y);
            var overlap = (Math.Min(val1: Math.Max(val1: a0, val2: a1), val2: Math.Max(val1: b0, val2: b1)) - Math.Max(val1: Math.Min(val1: a0, val2: a1), val2: Math.Min(val1: b0, val2: b1)));

            return (adjacent ? (overlap > 0d) : (overlap >= 0d));
        }
        return ((Math.Sign(value: c1) != Math.Sign(value: c2)) && (Math.Sign(value: c3) != Math.Sign(value: c4)));
    }

    private readonly struct Curve {
        private readonly Vector2 m_a;
        private readonly SdfPathSegment m_segment;
        private readonly double m_angle;
        private readonly double m_sweep;
        private readonly double m_radius;

        public double SecondDerivativeBound { get; }
        public double FirstDerivativeBound { get; }
        public double EndpointErrorBound { get; }

        public Curve(Vector2 a, SdfPathSegment segment) {
            m_a = a;
            m_segment = segment;
            m_angle = m_sweep = m_radius = 0d;
            if (segment.ArcCenter is { } center) {
                var start = (a - center);
                var end = (segment.End - center);

                m_radius = start.Length();
                EndpointErrorBound = Math.Abs(value: (end.Length() - m_radius));
                if ((m_radius <= 0d) || (Math.Abs(value: (end.Length() - m_radius)) > (0.000001d * Math.Max(val1: 1d, val2: m_radius)))) {
                    throw new ArgumentException(message: "An arc needs nonzero equal endpoint radii about its center.");
                }
                m_angle = Math.Atan2(x: start.X, y: start.Y);
                var sweep = (Math.Atan2(x: end.X, y: end.Y) - m_angle);

                if (segment.Clockwise) { if (sweep >= 0d) { sweep -= Math.Tau; } } else if (sweep <= 0d) { sweep += Math.Tau; }
                m_sweep = sweep;
                SecondDerivativeBound = ((m_radius * sweep) * sweep);
                FirstDerivativeBound = (m_radius * Math.Abs(value: sweep));
            } else if (segment.Control2 is { } c2) {
                var c1 = segment.Control!.Value;

                SecondDerivativeBound = (6d * Math.Max(val1: ((a - (2f * c1)) + c2).Length(), val2: ((c1 - (2f * c2)) + segment.End).Length()));
                FirstDerivativeBound = (3d * Math.Max(val1: (c1 - a).Length(), val2: Math.Max(val1: (c2 - c1).Length(), val2: (segment.End - c2).Length())));
            } else if (segment.Control is { } c) {
                SecondDerivativeBound = (2d * ((a - (2f * c)) + segment.End).Length());
                FirstDerivativeBound = (2d * Math.Max(val1: (c - a).Length(), val2: (segment.End - c).Length()));
            } else { SecondDerivativeBound = 0d; FirstDerivativeBound = (segment.End - a).Length(); }
        }

        public Vector2 At(double t) {
            if (t == 0d) { return m_a; }
            if (t == 1d) { return m_segment.End; }
            if (m_segment.ArcCenter is { } center) {
                var angle = (m_angle + (t * m_sweep));

                return (center + new Vector2(x: ((float)(m_radius * Math.Cos(d: angle))), y: ((float)(m_radius * Math.Sin(a: angle)))));
            }
            var u = ((float)t);

            if (m_segment.Control is not { } c1) { return Vector2.Lerp(m_a, m_segment.End, u); }
            var a = Vector2.Lerp(amount: u, value1: m_a, value2: c1);

            if (m_segment.Control2 is not { } c2) { return Vector2.Lerp(a, Vector2.Lerp(c1, m_segment.End, u), u); }
            var b = Vector2.Lerp(amount: u, value1: c1, value2: c2);
            var c = Vector2.Lerp(c2, m_segment.End, u);

            return Vector2.Lerp(Vector2.Lerp(amount: u, value1: a, value2: b), Vector2.Lerp(amount: u, value1: b, value2: c), u);
        }
    }
}
