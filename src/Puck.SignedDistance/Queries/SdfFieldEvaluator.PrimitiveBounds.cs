using Puck.Maths;

namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    /// <summary>Encloses a primitive's exact mathematical field over a box using its packed float parameters.
    /// Parameter conversion rounds outward; it does not first quantize the instruction to fixed-point values.
    /// The rules are the same interval rules used by the fixed-point query evaluator. Float execution error,
    /// point transforms, distance scaling and composition belong to the caller's certificate.</summary>
    /// <param name="instruction">The decoded ShapeBlend instruction containing the primitive parameters.</param>
    /// <param name="x">The shape-local X interval.</param>
    /// <param name="y">The shape-local Y interval.</param>
    /// <param name="z">The shape-local Z interval.</param>
    /// <returns>The field enclosure, or the entire interval for an unsupported shape or an unbounded operation.</returns>
    public static FixedInterval EnclosePrimitive(SdfInstruction instruction, FixedInterval x, FixedInterval y, FixedInterval z) {
        if ((instruction.Op != SdfOp.ShapeBlend) || x.IsUnbounded || y.IsUnbounded || z.IsUnbounded) {
            return FixedInterval.Entire;
        }
        var p = new IntervalVector3(X: x, Y: y, Z: z);
        var a = new IntervalParameters(X: SdfTapeCertificate.EncloseFloat(value: instruction.Data0.X),
            Y: SdfTapeCertificate.EncloseFloat(value: instruction.Data0.Y), Z: SdfTapeCertificate.EncloseFloat(value: instruction.Data0.Z),
            W: SdfTapeCertificate.EncloseFloat(value: instruction.Data0.W));
        var b = new IntervalParameters(X: SdfTapeCertificate.EncloseFloat(value: instruction.Data1.X),
            Y: SdfTapeCertificate.EncloseFloat(value: instruction.Data1.Y), Z: SdfTapeCertificate.EncloseFloat(value: instruction.Data1.Z),
            W: SdfTapeCertificate.EncloseFloat(value: instruction.Data1.W));
        var shape = ((SdfShapeType)(instruction.Shape & SdfProgram.ShapeTypeMask));

        return shape switch {
            SdfShapeType.Sphere => SphereBounds(p: p, radius: a.X),
            SdfShapeType.Box or SdfShapeType.ScreenSlab => BoxBounds(p: p, halfExtents: a.Xyz, cornerRadius: a.W),
            SdfShapeType.Torus => TorusBounds(p: p, majorRadius: a.X, minorRadius: a.Y),
            SdfShapeType.Plane => PlaneBounds(p: p, normal: a.Xyz, offset: a.W),
            SdfShapeType.Capsule => CapsuleBounds(p: p, endpoint: a.Xyz, radius: a.W, inverseLengthSquared: b.Y),
            SdfShapeType.Cylinder => (Extrude2DBounds(distance2D: (FixedInterval.Magnitude(x: p.X, y: p.Z) - a.X),
                z: p.Y, halfDepth: a.Y) - b.W),
            SdfShapeType.Superellipsoid when (instruction.Data0.W == 2f) => SuperellipsoidSphereBounds(p: p, radii: a.Xyz,
                inverseRadii: new IntervalVector3(X: b.Y, Y: b.Z, Z: b.W)),
            SdfShapeType.Superellipsoid when ((instruction.Data0.W > 2f) && (instruction.Data0.W <= 3f)
                && (instruction.Data0.X > 0f) && (instruction.Data0.Y > 0f) && (instruction.Data0.Z > 0f)
                && (instruction.Data1.Y > 0f) && (instruction.Data1.Z > 0f) && (instruction.Data1.W > 0f)) =>
                SuperellipsoidNormBounds(p: p, radii: a.Xyz, inverseRadii: new IntervalVector3(X: b.Y, Y: b.Z, Z: b.W),
                    lowerFactor: SdfTapeCertificate.SuperellipsoidNormFactor(exponent: instruction.Data0.W)),
            SdfShapeType.RoundedRectangle or SdfShapeType.ChamferedRectangle or SdfShapeType.Trapezoid =>
                PackedLiftBounds(p: p, shape: shape, a: a, b: b, extrude: (instruction.Data1.Y > 0.5f)),
            _ => FixedInterval.Entire,
        };
    }

    private static FixedInterval SphereBounds(IntervalVector3 p, FixedInterval radius) => (p.Length - radius);
    private static FixedInterval TorusBounds(IntervalVector3 p, FixedInterval majorRadius, FixedInterval minorRadius) =>
        (FixedInterval.Magnitude(x: (FixedInterval.Magnitude(x: p.X, y: p.Z) - majorRadius), y: p.Y) - minorRadius);
    private static FixedInterval PlaneBounds(IntervalVector3 p, IntervalVector3 normal, FixedInterval offset) =>
        ((((p.X * normal.X) + (p.Y * normal.Y)) + (p.Z * normal.Z)) + offset);
    private static IntervalVector3 Interval(FixedVector3 value) => new(X: Point(value: value.X), Y: Point(value: value.Y), Z: Point(value: value.Z));
    private static FixedInterval PackedLiftBounds(IntervalVector3 p, SdfShapeType shape, IntervalParameters a,
        IntervalParameters b, bool extrude) {
        var sqrtHalf = SdfTapeCertificate.EncloseFloat(value: 0.70710678f);
        var profilePoint = new IntervalVector2(X: (extrude ? p.X : (FixedInterval.Magnitude(x: p.X, y: p.Z) - a.W)), Y: p.Y);
        var profile = shape switch {
            SdfShapeType.RoundedRectangle => RoundedRectangle2DBounds(p: profilePoint, halfWidth: a.X, halfHeight: a.Y, cornerRadius: a.Z),
            SdfShapeType.ChamferedRectangle => ChamferBox2DBounds(p: profilePoint, halfWidth: a.X, halfHeight: a.Y, chamfer: a.Z, sqrtHalf: sqrtHalf),
            SdfShapeType.Trapezoid => PackedTrapezoidBounds(p: profilePoint, r1: a.X, r2: a.Y, halfHeight: a.Z),
            _ => FixedInterval.Entire,
        };

        return ((extrude ? ExtrudeChamfer2DBounds(distance2D: profile, z: p.Z, halfDepth: a.W,
            c: ((shape == SdfShapeType.ChamferedRectangle) ? a.Z : b.Z), sqrtHalf: sqrtHalf) : profile) - b.W);
    }
    private static FixedInterval PackedTrapezoidBounds(IntervalVector2 p, FixedInterval r1, FixedInterval r2, FixedInterval halfHeight) {
        var slant = new IntervalVector2(X: (r2 - r1), Y: (Point(value: Two) * halfHeight));
        var squared = (FixedInterval.Square(value: slant.X) + FixedInterval.Square(value: slant.Y));

        if (squared.IsUnbounded || (squared.Lower <= FixedQ4816.Zero)) { return FixedInterval.Entire; }
        return Trapezoid2DBounds(halfHeight: halfHeight, p: p, r1: r1, r2: r2, slant: slant, slantLengthSquared: squared);
    }

    private readonly record struct IntervalParameters(FixedInterval X, FixedInterval Y, FixedInterval Z, FixedInterval W) {
        public IntervalVector3 Xyz => new(X: X, Y: Y, Z: Z);
    }
}
