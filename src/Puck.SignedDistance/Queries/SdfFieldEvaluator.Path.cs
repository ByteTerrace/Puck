using System.Diagnostics;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// The Path shape (SdfShapeType.Path) and the edge-list core it shares with ConvexPolygon. KEEP IN SYNC with sdfPathSolid,
// sdfPathStrokeEdge and sdfConvexPolygon2D in field/sdf-shapes.hlsli: a fill is the exact distance to a closed edge list
// signed by one crossing-parity flip per edge, a stroke the least distance to the convex hull of each edge's endpoint
// disks, and both extrude to the shape's half-depth through the plain join. The edges are the program's own table
// (SdfProgram.Paths), already placed at the shape's scale by SdfProgramBuilder.Path, so the instruction's Data0 x lane
// (a packed table offset) is never read.
public sealed partial class SdfFieldEvaluator {
    // A list of directed edges in the profile plane, each from A to B: a convex polygon's sides from each vertex back to
    // the one before it, as sdfConvexPolygon2D walks them, or a path's compiled edges in their own order.
    private interface IEdges {
        int Count { get; }
        (FixedVector2 A, FixedVector2 B) this[int index] { get; }
    }
    private readonly struct PolygonEdges(FixedVector2[] vertices) : IEdges {
        public int Count => vertices.Length;
        public (FixedVector2 A, FixedVector2 B) this[int index] => (vertices[index], vertices[((index == 0) ? (vertices.Length - 1) : (index - 1))]);
    }
    private readonly struct PathEdges(PathEdgeFixed[] edges) : IEdges {
        public int Count => edges.Length;
        public (FixedVector2 A, FixedVector2 B) this[int index] => (edges[index].A, edges[index].B);
    }
    // One compiled path edge, pre-converted once, with the stroke's radius at each end.
    private readonly record struct PathEdgeFixed(FixedVector2 A, FixedVector2 B, FixedQ4816 RadiusA, FixedQ4816 RadiusB);

    private static PathEdgeFixed[] CompilePathEdges(IReadOnlyList<SdfCompiledPath> paths, int instructionIndex) {
        foreach (var path in paths) {
            if (path.InstructionIndex != instructionIndex) {
                continue;
            }

            var edges = new PathEdgeFixed[path.Edges.Count];

            for (var index = 0; (index < edges.Length); index++) {
                var edge = path.Edges[index];

                edges[index] = new PathEdgeFixed(
                    A: new FixedVector2(X: FixedQ4816.FromDouble(value: edge.A.X), Y: FixedQ4816.FromDouble(value: edge.A.Y)),
                    B: new FixedVector2(X: FixedQ4816.FromDouble(value: edge.B.X), Y: FixedQ4816.FromDouble(value: edge.B.Y)),
                    RadiusA: FixedQ4816.FromDouble(value: edge.RadiusA),
                    RadiusB: FixedQ4816.FromDouble(value: edge.RadiusB)
                );
            }

            return edges;
        }

        throw new ArgumentException(
            message: $"Instruction {instructionIndex} is a Path shape with no matching edge table on the program.",
            paramName: nameof(paths)
        );
    }
    // The exact signed distance to the solid an edge list bounds: the least distance to any edge, negated inside by the
    // even/odd crossing parity (one flip per edge whose three tests agree). The kernel keeps the least squared distance and
    // roots it once; each candidate is rooted as a length here, because a squared millimetre distance is a few Q48.16 ulps
    // and its root would step by whole ulps across a centimetre of field, flattening the gradient the mesh bake reads near
    // every edge.
    private static FixedQ4816 SdfPolygon2D<TEdges>(FixedVector2 p, TEdges edges) where TEdges : struct, IEdges {
        var d = FarDistance;
        var negate = false;

        for (var index = 0; (index < edges.Count); index++) {
            var (a, b) = edges[index];
            var e = (b - a);
            var w = (p - a);
            var t = FixedQ4816.Clamp(
                value: (FixedVector2.Dot(left: w, right: e) / FixedVector2.Dot(left: e, right: e)),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One
            );

            d = FixedQ4816.Min(x: d, y: (w - (e * t)).Length);

            var c1 = (p.Y >= a.Y);
            var c2 = (p.Y < b.Y);
            var c3 = (((e.X * w.Y) - (e.Y * w.X)) > FixedQ4816.Zero);

            if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) {
                negate = !negate;
            }
        }

        return (negate ? -d : d);
    }
    // The signed distance to the convex hull of the disks of radius ra about a and rb about b: the larger disk alone when
    // it holds the other, else the taper's two tangents between the caps.
    private static FixedQ4816 SdfPathStrokeEdge(FixedVector2 p, FixedVector2 a, FixedVector2 b, FixedQ4816 ra, FixedQ4816 rb) {
        var ab = (b - a);
        var length = ab.Length;

        if (FixedQ4816.Abs(value: (ra - rb)) >= length) {
            return ((ra >= rb) ? ((p - a).Length - ra) : ((p - b).Length - rb));
        }

        var u = (ab / length);
        var v = (p - a);
        var x = FixedVector2.Dot(left: v, right: u);
        var y = FixedQ4816.Abs(value: ((v.X * u.Y) - (v.Y * u.X)));
        var k = ((ra - rb) / length);
        var c = FixedQ4816.Sqrt(value: FixedQ4816.Max(x: FixedQ4816.Zero, y: (FixedQ4816.One - (k * k))));
        var along = ((x * c) - (y * k));

        if (along < FixedQ4816.Zero) {
            return (v.Length - ra);
        }
        if (along > (length * c)) {
            return ((p - b).Length - rb);
        }

        return (((x * k) + (y * c)) - ra);
    }
    private static FixedQ4816 SdfPathStroke2D(FixedVector2 p, PathEdgeFixed[] edges) {
        var distance = FarDistance;

        foreach (var edge in edges) {
            distance = FixedQ4816.Min(x: distance, y: SdfPathStrokeEdge(a: edge.A, b: edge.B, p: p, ra: edge.RadiusA, rb: edge.RadiusB));
        }

        return distance;
    }
    // sdfPathSolid: the profile's distance extruded to halfDepth through the plain join (no cap chamfer).
    private static FixedQ4816 SdfPathSolid(FixedVector3 p, PathEdgeFixed[] edges, bool stroke, FixedQ4816 halfDepth) {
        var q = new FixedVector2(X: p.X, Y: p.Y);
        var distance2D = (stroke ? SdfPathStroke2D(edges: edges, p: q) : SdfPolygon2D(edges: new PathEdges(edges: edges), p: q));

        return SdfExtrudeChamfer2D(c: FixedQ4816.Zero, distance2D: distance2D, halfDepth: halfDepth, z: p.Z);
    }

    // The exact edge-list field over a box: the least distance enclosed edge by edge, and the crossing parity followed
    // while every edge's three tests keep one answer over the box; once one does not, the sign is either, and the bound is
    // the hull of both.
    private static FixedInterval Polygon2DBounds<TEdges>(IntervalVector2 p, TEdges edges) where TEdges : struct, IEdges {
        var d = FixedInterval.FromPoint(value: FarDistance);
        var negate = false;
        var parityKnown = true;
        var zero = FixedQ4816.Zero;

        for (var index = 0; (index < edges.Count); index++) {
            var (a, b) = edges[index];
            var e = (b - a);
            var wx = (p.X - Point(value: a.X));
            var wy = (p.Y - Point(value: a.Y));
            var t = FixedInterval.Clamp(
                value: (((wx * Point(value: e.X)) + (wy * Point(value: e.Y))) / Point(value: FixedVector2.Dot(left: e, right: e))),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One
            );
            var bx = (wx - (Point(value: e.X) * t));
            var by = (wy - (Point(value: e.Y) * t));

            d = FixedInterval.Min(first: d, second: FixedInterval.Magnitude(x: bx, y: by));

            var c1 = Decide(always: (p.Y.Lower >= a.Y), never: (p.Y.Upper < a.Y));
            var c2 = Decide(always: (p.Y.Upper < b.Y), never: (p.Y.Lower >= b.Y));
            var cross = ((Point(value: e.X) * wy) - (Point(value: e.Y) * wx));
            var c3 = Decide(always: (cross.Lower > zero), never: (cross.Upper <= zero));

            if ((c1 is null) || (c2 is null) || (c3 is null)) {
                parityKnown = false;
            } else if ((c1.Value && c2.Value && c3.Value) || (!c1.Value && !c2.Value && !c3.Value)) {
                negate = !negate;
            }
        }

        return (!parityKnown
            ? FixedInterval.Union(first: -d, second: d)
            : (negate ? -d : d));
    }
    // A stroke's field over a box: the exact answer at the box's centre widened by the box's half-diagonal, since the
    // least distance to a union of convex hulls is 1-Lipschitz. A box past the frame limit (frame discovery's) proves
    // nothing, as a point of it cannot be answered without overflow.
    private static FixedInterval PathStroke2DBounds(IntervalVector2 p, PathEdgeFixed[] edges) {
        if (p.X.IsUnbounded || p.Y.IsUnbounded || Wide(interval: p.X) || Wide(interval: p.Y)) {
            return FixedInterval.Entire;
        }

        var centre = new FixedVector2(X: FixedQ4816.Lerp(amount: Half, from: p.X.Lower, to: p.X.Upper), Y: FixedQ4816.Lerp(amount: Half, from: p.Y.Lower, to: p.Y.Upper));
        var reach = FixedInterval.Magnitude(x: (p.X - Point(value: centre.X)), y: (p.Y - Point(value: centre.Y)));

        if (reach.IsUnbounded) {
            return FixedInterval.Entire;
        }

        return (Point(value: SdfPathStroke2D(edges: edges, p: centre)) + new FixedInterval(lower: -reach.Upper, upper: reach.Upper));

        static bool Wide(FixedInterval interval) => ((FixedQ4816.Abs(value: interval.Lower) > StrokeFrameLimit) || (FixedQ4816.Abs(value: interval.Upper) > StrokeFrameLimit));
    }
    // The farthest coordinate a stroke's box rule answers at: a point of a wider box could not be read without overflow.
    private static readonly FixedQ4816 StrokeFrameLimit = FixedQ4816.FromInteger(value: (1L << 30));
    private static FixedInterval PathBounds(IntervalVector3 p, in CompiledInstruction instruction) {
        var edges = (instruction.PathEdges ?? throw new UnreachableException(message: "Compile always attaches the edge table to a Path instruction."));
        var profile = new IntervalVector2(X: p.X, Y: p.Y);
        var distance2D = ((instruction.Data1Y > Half) ? PathStroke2DBounds(edges: edges, p: profile) : Polygon2DBounds(edges: new PathEdges(edges: edges), p: profile));

        return ExtrudeChamfer2DBounds(c: FixedQ4816.Zero, distance2D: distance2D, halfDepth: instruction.Data0W, z: p.Z);
    }
}
