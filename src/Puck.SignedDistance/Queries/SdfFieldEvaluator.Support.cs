namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    /// <summary>Returns whether the evaluator interprets <paramref name="op"/>; a program holding any other op is refused
    /// at construction.</summary>
    /// <param name="op">The op.</param>
    /// <returns><see langword="true"/> when the op is interpreted.</returns>
    public static bool IsSupportedOp(SdfOp op) {
        return op switch {
            SdfOp.ResetPoint or
            SdfOp.Translate or
            SdfOp.Rotate or
            SdfOp.Scale or
            SdfOp.Elongate or
            SdfOp.ShapeBlend or
            SdfOp.Repeat or
            SdfOp.RepeatLimited or
            SdfOp.Onion or
            SdfOp.Dilate or
            SdfOp.CellDisplace or
            SdfOp.SymmetryPlane or
            SdfOp.PushField or
            SdfOp.PopField => true,
            _ => false,
        };
    }
    /// <summary>Gets the warps the evaluator interprets under <see cref="SdfFieldReading.Render"/> alone, each with a point
    /// rule and an inclusion rule: <see cref="SdfOp.AxialProfile"/> and <see cref="SdfOp.RotatePlane"/>.</summary>
    public static IReadOnlyList<SdfOp> RenderWarpOps { get; } = [SdfOp.AxialProfile, SdfOp.RotatePlane];
    /// <summary>Returns whether the evaluator interprets <paramref name="op"/> under a reading; a program holding any other
    /// op is refused at construction.</summary>
    /// <param name="op">The op.</param>
    /// <param name="reading">The reading.</param>
    /// <returns><see langword="true"/> when the op is interpreted.</returns>
    public static bool IsSupportedOp(SdfOp op, SdfFieldReading reading) =>
        (IsSupportedOp(op: op) || ((reading == SdfFieldReading.Render) && (op is SdfOp.AxialProfile or SdfOp.RotatePlane)));
    /// <summary>Returns whether the evaluator interprets <paramref name="shape"/>; a program holding any other shape in its
    /// contact field is refused at construction.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns><see langword="true"/> when the shape is interpreted.</returns>
    public static bool IsSupportedShape(SdfShapeType shape) {
        return shape switch {
            SdfShapeType.Box or
            SdfShapeType.Capsule or
            SdfShapeType.Sphere or
            SdfShapeType.Torus or
            SdfShapeType.Cylinder or
            SdfShapeType.Plane or
            SdfShapeType.Vesica or
            SdfShapeType.RoundedRectangle or
            SdfShapeType.Ellipse or
            SdfShapeType.Trapezoid or
            SdfShapeType.ChamferedRectangle or
            SdfShapeType.RoundCone or
            SdfShapeType.ScreenSlab or
            SdfShapeType.Superellipsoid or
            SdfShapeType.ConvexPolygon or
            // strands > 1 is refused separately, at Compile time (see Compile's isSweep check) — this type-keyed
            // gate cannot see the instruction's own strand count.
            SdfShapeType.Sweep => true,
            _ => false,
        };
    }
}
