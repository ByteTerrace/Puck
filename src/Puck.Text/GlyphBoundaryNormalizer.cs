using System.Numerics;

namespace Puck.Text;

/// <summary>Resolves nonzero-filled outlines into boundary-only polygon contours before edge coloring.</summary>
/// <remarks>Quadratics are subdivided to a 0.01 atlas-pixel chord tolerance. This is a sampled
/// representation, not an exact curve or a promise about the Lipschitz bound of a filtered texture.
/// Intersection and classification work is capped independently of font-program execution.</remarks>
internal static class GlyphBoundaryNormalizer {
    private const double Tolerance = 0.01;
    private const int MaximumEdges = 4096;
    private const int MaximumWork = 8_000_000;
    private const int MaximumCuts = 65_536;
    private const double Epsilon = 1e-7;

    private readonly record struct Point(double X, double Y) {
        public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
        public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
        public static Point operator *(Point a, double b) => new(a.X * b, a.Y * b);
        public double Length => Math.Sqrt(X * X + Y * Y);
        public Vector2 Vector => new((float)X, (float)Y);
    }
    private readonly record struct Edge(Point Start, Point End);
    private readonly record struct Cut(double Parameter, Point Position);

    private static double Cross(Point a, Point b) => a.X * b.Y - a.Y * b.X;
    private static double Dot(Point a, Point b) => a.X * b.X + a.Y * b.Y;
    private static Point Convert(Vector2 point) {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || Math.Abs(point.X) > 32768 || Math.Abs(point.Y) > 32768) {
            throw new InvalidDataException("A glyph outline exceeds the finite 32768-pixel coordinate limit.");
        }
        return new(point.X, point.Y);
    }
    private static void Charge(ref int work, FontGenerationBudget? budget) {
        budget?.Work();
        if (++work > MaximumWork) {
            throw new InvalidDataException("A glyph exceeds the boundary-normalization work limit.");
        }
    }
    private static void Add(List<Edge> edges, Point start, Point end) {
        if ((end - start).Length <= Epsilon) {
            return;
        }
        if (edges.Count >= MaximumEdges) {
            throw new InvalidDataException("A glyph exceeds the 4096-edge boundary limit.");
        }
        edges.Add(new(start, end));
    }
    private static void Flatten(List<Edge> edges, Point start, Point control, Point end, int depth = 0) {
        // The maximum separation from the parameter-matched chord is |2C-S-E|/4.
        if ((control * 2 - start - end).Length <= 4 * Tolerance) {
            Add(edges, start, end);
            return;
        }
        if (depth >= 24) {
            throw new InvalidDataException("A glyph exceeds the quadratic subdivision limit.");
        }
        var a = (start + control) * 0.5;
        var b = (control + end) * 0.5;
        var middle = (a + b) * 0.5;
        Flatten(edges, start, a, middle, depth + 1);
        Flatten(edges, middle, b, end, depth + 1);
    }
    private static void AddCut(List<Cut> cuts, Edge edge, double t, Point position, ref int cutCount) {
        // Epsilon is a distance, not a fraction of an arbitrarily long edge.
        var endpointTolerance = Epsilon / (edge.End - edge.Start).Length;
        if (t > endpointTolerance && t < 1 - endpointTolerance) {
            if (++cutCount > MaximumCuts) {
                throw new InvalidDataException("A glyph exceeds the 65536-intersection-cut storage limit.");
            }
            cuts.Add(new(t, position));
        }
    }
    private static void Intersect(Edge a, Edge b, List<Cut> cutsA, List<Cut> cutsB, ref int cutCount) {
        var r = a.End - a.Start;
        var s = b.End - b.Start;
        var q = b.Start - a.Start;
        var denominator = Cross(r, s);
        if (Math.Abs(denominator) > 1e-12 * r.Length * s.Length) {
            var t = Cross(q, s) / denominator;
            var u = Cross(q, r) / denominator;
            var toleranceA = Epsilon / r.Length;
            var toleranceB = Epsilon / s.Length;
            if (t >= -toleranceA && t <= 1 + toleranceA && u >= -toleranceB && u <= 1 + toleranceB) {
                // Both edges retain ONE intersection position; reconstructing it independently can open a loop.
                var point = t <= toleranceA ? a.Start : t >= 1 - toleranceA ? a.End
                    : u <= toleranceB ? b.Start : u >= 1 - toleranceB ? b.End : a.Start + r * t;
                AddCut(cutsA, a, t, point, ref cutCount);
                AddCut(cutsB, b, u, point, ref cutCount);
            }
            return;
        }
        if (Math.Abs(Cross(q, r)) > Epsilon * r.Length) {
            return;
        }
        // Collinear overlaps need both endpoints split, including reversed and identical contours.
        AddCut(cutsA, a, Dot(q, r) / Dot(r, r), b.Start, ref cutCount);
        AddCut(cutsA, a, Dot(b.End - a.Start, r) / Dot(r, r), b.End, ref cutCount);
        AddCut(cutsB, b, Dot(a.Start - b.Start, s) / Dot(s, s), a.Start, ref cutCount);
        AddCut(cutsB, b, Dot(a.End - b.Start, s) / Dot(s, s), a.End, ref cutCount);
    }
    private static int Winding(List<Edge> edges, Point point, ref int work, FontGenerationBudget? budget) {
        var winding = 0;
        foreach (var edge in edges) {
            Charge(ref work, budget);
            if (edge.Start.Y <= point.Y) {
                if (edge.End.Y > point.Y && Cross(edge.End - edge.Start, point - edge.Start) > 0) {
                    winding++;
                }
            } else if (edge.End.Y <= point.Y && Cross(edge.End - edge.Start, point - edge.Start) < 0) {
                winding--;
            }
        }
        return winding;
    }
    private static double Distance(Point p, Edge edge) {
        var d = edge.End - edge.Start;
        var t = Math.Clamp(Dot(p - edge.Start, d) / Dot(d, d), 0, 1);
        return (p - (edge.Start + d * t)).Length;
    }
    // Intersection endpoints are snapped only for connectivity, at a much finer scale than the curve tolerance.
    private static (long X, long Y) Key(Point p) => ((long)Math.Round(p.X / Epsilon), (long)Math.Round(p.Y / Epsilon));

    public static FontGlyphGeometry Normalize(FontGlyphGeometry geometry, FontGenerationBudget? budget = null) {
        var edges = new List<Edge>();
        foreach (var contour in geometry.Contours) {
            foreach (var segment in contour) {
                var start = Convert(segment.Start);
                var end = Convert(segment.End);
                if (segment.IsCurve) {
                    Flatten(edges, start, Convert(segment.Control), end);
                } else {
                    Add(edges, start, end);
                }
            }
        }
        var work = 0;
        var cutCount = edges.Count * 2;
        var cuts = edges.Select(static edge => new List<Cut> { new(0, edge.Start), new(1, edge.End) }).ToArray();
        for (var i = 0; i < edges.Count; i++) {
            for (var j = i + 1; j < edges.Count; j++) {
                Charge(ref work, budget);
                Intersect(edges[i], edges[j], cuts[i], cuts[j], ref cutCount);
            }
        }
        var boundary = new List<Edge>();
        var seen = new HashSet<((long X, long Y) Start, (long X, long Y) End)>();
        for (var i = 0; i < edges.Count; i++) {
            cuts[i].Sort(static (a, b) => a.Parameter.CompareTo(b.Parameter));
            for (var j = 1; j < cuts[i].Count; j++) {
                var low = cuts[i][j - 1];
                var high = cuts[i][j];
                var start = low.Position;
                var end = high.Position;
                if ((end - start).Length <= Epsilon) {
                    continue;
                }
                var direction = end - start;
                var middle = (start + end) * 0.5;
                // Stay inside this arrangement face even for very thin strokes or nearby parallel edges.
                var clearance = Math.Min(1e-4, direction.Length * 0.01);
                foreach (var edge in edges) {
                    Charge(ref work, budget);
                    var distance = Distance(middle, edge);
                    if (distance > Epsilon) {
                        clearance = Math.Min(clearance, distance * 0.25);
                    }
                }
                var normal = new Point(-direction.Y, direction.X) * (clearance / direction.Length);
                var leftFilled = Winding(edges, middle + normal, ref work, budget) != 0;
                var rightFilled = Winding(edges, middle - normal, ref work, budget) != 0;
                if (leftFilled == rightFilled) {
                    continue;
                }
                var resolved = leftFilled ? new Edge(start, end) : new Edge(end, start);
                if (seen.Add((Key(resolved.Start), Key(resolved.End)))) {
                    Add(boundary, resolved.Start, resolved.End);
                }
            }
        }
        var outgoing = new Dictionary<(long X, long Y), List<int>>();
        for (var i = 0; i < boundary.Count; i++) {
            var key = Key(boundary[i].Start);
            if (!outgoing.TryGetValue(key, out var indices)) {
                outgoing.Add(key, indices = []);
            }
            indices.Add(i);
        }
        var used = new bool[boundary.Count];
        var contours = new List<IReadOnlyList<FontOutlineSegment>>();
        for (var first = 0; first < boundary.Count; first++) {
            if (used[first]) {
                continue;
            }
            var contour = new List<FontOutlineSegment>();
            var current = first;
            while (true) {
                Charge(ref work, budget);
                var edge = boundary[current];
                used[current] = true;
                contour.Add(new(edge.Start.Vector, default, edge.End.Vector, false));
                if (Key(edge.End) == Key(boundary[first].Start)) {
                    break;
                }
                if (!outgoing.TryGetValue(Key(edge.End), out var candidates)) {
                    throw new InvalidDataException("A glyph boundary could not be closed at the supported precision.");
                }
                current = -1;
                var bestTurn = double.PositiveInfinity;
                var incoming = edge.End - edge.Start;
                foreach (var candidate in candidates) {
                    if (used[candidate]) {
                        continue;
                    }
                    var next = boundary[candidate].End - boundary[candidate].Start;
                    // Keep the filled face on the left at a point where more than two edges meet.
                    var turn = Math.Atan2(Cross(next, incoming * -1), Dot(next, incoming * -1));
                    if (turn < 0) {
                        turn += 2 * Math.PI;
                    }
                    if (turn < bestTurn) {
                        bestTurn = turn;
                        current = candidate;
                    }
                }
                if (current < 0) {
                    throw new InvalidDataException("A glyph boundary contains an unresolved junction.");
                }
            }
            contours.Add(contour);
        }
        budget?.Geometry(boundary.Count);
        return geometry with { Contours = contours };
    }
}
