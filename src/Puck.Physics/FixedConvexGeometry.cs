using Puck.Maths;

namespace Puck.Physics;

// Support of the actual convex zero set, rather than of a surrounding bound. All iteration intervals live on
// the fixed parameter grid and retain endpoints; equal scores choose the first witness deterministically.
internal static class FixedConvexGeometry {
    private static readonly FixedQ4816 Two = FixedQ4816.FromInteger(value: 2);
    private static readonly FixedQ4816 Three = FixedQ4816.FromInteger(value: 3);
    private static readonly FixedQ4816 RootTwo = FixedQ4816.Sqrt(value: Two);

    internal static FixedVector3 Support(in FixedStaticCollider collider, FixedVector3 direction) {
        if (collider.Kind == FixedStaticColliderKind.Superellipsoid) {
            var weighted = FixedVector3.Multiply(left: FixedVector3.Abs(value: direction), right: collider.Extent);
            var largest = FixedQ4816.Max(x: weighted.X, y: FixedQ4816.Max(x: weighted.Y, y: weighted.Z));

            if (largest == FixedQ4816.Zero) { return FixedVector3.Zero; }
            // Rescaling the dual direction before exponentiation avoids losing small authored radii to Q16.
            weighted /= largest;
            var inverse = (FixedQ4816.One / (collider.Exponent - FixedQ4816.One));
            var powers = new FixedVector3(X: Power(weighted.X, inverse), Y: Power(weighted.Y, inverse), Z: Power(weighted.Z, inverse));
            var sum = FixedVector3.Dot(left: weighted, right: powers);
            var divisor = Power(sum, (FixedQ4816.One / collider.Exponent));

            return (Signed(FixedVector3.Multiply(left: (powers / divisor), right: collider.Extent), direction) + (direction.Normalize() * collider.Radius));
        }
        var d = FixedVector3.Abs(value: direction);
        var half = collider.Extent;
        var r = collider.Radius;
        var c = collider.Chamfer;
        // At a fixed z the renderer requires its 2D profile distance to be <= t. The plain extrusion
        // contributes the circular join and the cap chamfer contributes the diagonal constraint.
        var minimum = FixedQ4816.Max(x: -FixedQ4816.Min(x: half.X, y: half.Y), y: (((c - half.X) - half.Y) / RootTwo));
        var high = FixedQ4816.Min(x: (half.Z + r), y: (((half.Z + (RootTwo * r)) - c) - minimum));

        high = FixedQ4816.Max(x: FixedQ4816.Zero, y: high);
        var low = FixedQ4816.Zero;
        var best = Slice(c: c, d: d, half: half, r: r, z: low);

        Consider(Slice(c: c, d: d, half: half, r: r, z: high), d, ref best);
        if ((d.Z != FixedQ4816.Zero) && ((d.X != FixedQ4816.Zero) || (d.Y != FixedQ4816.Zero))) {
            // A convex body's support over its parallel slices is concave in the slice coordinate.
            for (var i = 0; ((i < 32) && ((high - low) > FixedQ4816.Epsilon)); i++) {
                var third = ((high - low) / Three);

                if (third == FixedQ4816.Zero) { break; }
                var a = (low + third);
                var b = (high - third);
                var pa = Slice(c: c, d: d, half: half, r: r, z: a);
                var pb = Slice(c: c, d: d, half: half, r: r, z: b);

                Consider(best: ref best, direction: d, point: pa);
                Consider(best: ref best, direction: d, point: pb);
                if (FixedVector3.Dot(left: pa, right: d) >= FixedVector3.Dot(left: pb, right: d)) { high = b; } else { low = a; }
            }
        }
        return Signed(direction: direction, point: best);
    }

    private static FixedQ4816 Power(FixedQ4816 value, FixedQ4816 exponent) =>
        ((value == FixedQ4816.Zero) ? FixedQ4816.Zero : FixedQ4816.Pow(x: value, y: exponent));

    // The drawn field and its active outward gradient. The field is a signed bound outside a bevel vertex;
    // distance queries still use GJK. This recognizes a boundary probe before a vanishing EPA tetrahedron is built.
    internal static (FixedQ4816 Distance, FixedVector3 Normal) Field(in FixedStaticCollider collider, FixedVector3 point) {
        var p = FixedVector3.Abs(value: point);
        var e = collider.Extent;

        if (collider.Kind == FixedStaticColliderKind.Superellipsoid) {
            var q = new FixedVector3(X: (p.X / e.X), Y: (p.Y / e.Y), Z: (p.Z / e.Z));
            var exponent = collider.Exponent;
            var sum = ((Power(q.X, exponent) + Power(q.Y, exponent)) + Power(q.Z, exponent));
            var distance = ((Power(sum, (FixedQ4816.One / exponent)) - FixedQ4816.One) * FixedQ4816.Min(x: e.X, y: FixedQ4816.Min(x: e.Y, y: e.Z)));
            var normal = new FixedVector3(X: (Power(q.X, (exponent - FixedQ4816.One)) / e.X),
                Y: (Power(q.Y, (exponent - FixedQ4816.One)) / e.Y), Z: (Power(q.Z, (exponent - FixedQ4816.One)) / e.Z)).Normalize();

            return ((distance - collider.Radius), Signed(direction: point, point: normal));
        }
        var relative = (p - e);
        var positive = new FixedVector3(X: FixedQ4816.Max(x: relative.X, y: FixedQ4816.Zero), Y: FixedQ4816.Max(x: relative.Y, y: FixedQ4816.Zero), Z: FixedQ4816.Zero);
        var length = positive.Length;
        var profile = (length + FixedQ4816.Min(x: FixedQ4816.Max(x: relative.X, y: relative.Y), y: FixedQ4816.Zero));
        var gradient = ((length > FixedQ4816.Zero) ? positive.Normalize() : ((relative.X >= relative.Y) ? FixedVector3.UnitX : FixedVector3.UnitY));
        var bevel = (((relative.X + relative.Y) + collider.Chamfer) / RootTwo);

        if (bevel > profile) {
            profile = bevel;
            gradient = new FixedVector3(X: (FixedQ4816.One / RootTwo), Y: (FixedQ4816.One / RootTwo), Z: FixedQ4816.Zero);
        }
        var cap = relative.Z;
        var join = new FixedVector2(X: FixedQ4816.Max(x: profile, y: FixedQ4816.Zero), Y: FixedQ4816.Max(x: cap, y: FixedQ4816.Zero));
        var plain = (join.Length + FixedQ4816.Min(x: FixedQ4816.Max(x: profile, y: cap), y: FixedQ4816.Zero));
        var outward = ((join.Length > FixedQ4816.Zero) ? ((gradient * join.X) + (FixedVector3.UnitZ * join.Y)).Normalize()
            : ((profile >= cap) ? gradient : FixedVector3.UnitZ));
        var capBevel = (((profile + cap) + collider.Chamfer) / RootTwo);

        if (capBevel > plain) { plain = capBevel; outward = (gradient + FixedVector3.UnitZ).Normalize(); }
        return ((plain - collider.Radius), Signed(direction: point, point: outward));
    }

    private static FixedVector3 Signed(FixedVector3 point, FixedVector3 direction) => new(
        X: FixedQ4816.CopySign(point.X, direction.X), Y: FixedQ4816.CopySign(point.Y, direction.Y), Z: FixedQ4816.CopySign(point.Z, direction.Z));
    private static void Consider(FixedVector3 point, FixedVector3 direction, ref FixedVector3 best) {
        if (FixedVector3.Dot(left: point, right: direction) > FixedVector3.Dot(left: best, right: direction)) { best = point; }
    }
    private static FixedVector3 Slice(FixedVector3 half, FixedQ4816 c, FixedQ4816 r, FixedVector3 d, FixedQ4816 z) {
        var cap = (z - half.Z);
        var ratio = ((r > FixedQ4816.Zero) ? (cap / r) : FixedQ4816.One);
        var t = ((cap <= FixedQ4816.Zero) ? r : (r * FixedQ4816.Sqrt(value: FixedQ4816.Max(x: FixedQ4816.Zero, y: (FixedQ4816.One - (ratio * ratio))))));

        t = FixedQ4816.Min(x: t, y: (((RootTwo * r) - c) - cap));
        var core = new FixedVector2(X: half.X, Y: half.Y);
        var round = FixedQ4816.Max(x: FixedQ4816.Zero, y: t);

        if (t < FixedQ4816.Zero) { core += new FixedVector2(X: t, Y: t); }
        var planar = new FixedVector2(X: d.X, Y: d.Y);
        var length = planar.Length;
        var point = (core + (((length > FixedQ4816.Zero) ? (planar / length) : FixedVector2.Zero) * round));
        // The profile is a rounded rectangle intersected with x+y<=bound. If the unconstrained support
        // violates that plane, the support lies on its clipped segment, whose endpoints are analytic.
        var bound = (((half.X + half.Y) - c) + (RootTwo * t));

        if ((point.X + point.Y) > bound) {
            var lower = FixedQ4816.Max(x: FixedQ4816.Zero, y: ((bound - core.Y) - round));
            var upper = FixedQ4816.Min(x: (core.X + round), y: bound);
            var excess = ((bound - core.X) - core.Y);

            if (excess > round) {
                var middle = (((bound + core.X) - core.Y) / Two);
                var relative = (excess / round);
                var width = ((round * FixedQ4816.Sqrt(value: FixedQ4816.Max(x: FixedQ4816.Zero, y: (Two - (relative * relative))))) / Two);

                lower = FixedQ4816.Max(x: lower, y: (middle - width));
                upper = FixedQ4816.Min(x: upper, y: (middle + width));
            }
            var x = ((d.X == d.Y) ? ((lower + upper) / Two) : ((d.X > d.Y) ? upper : lower));

            point = new FixedVector2(X: x, Y: (bound - x));
        }
        return new FixedVector3(X: point.X, Y: point.Y, Z: z);
    }
}
