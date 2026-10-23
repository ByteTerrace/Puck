namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    /// <summary>Returns whether the evaluator interprets <paramref name="op"/> as physical clearance
    /// (<see cref="SdfFieldReading.Contact"/>); a program holding any other op is refused at construction.</summary>
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
    /// <summary>Gets the ops the evaluator interprets under <see cref="SdfFieldReading.Render"/> alone, each with a point
    /// rule mirroring <c>mapCore</c>'s case (<c>SdfFieldEvaluator.RenderOps.cs</c>) and an inclusion rule: every warp,
    /// fold and relief the renderer draws but no contact field reads. <see cref="SdfOp.TransformDynamic"/> stays out of
    /// both readings: the evaluator carries no per-frame transform table to resolve a slot against, and the mesh audit
    /// poses each slot as a translate and rotate before it reads a group (<c>SdfMeshAudit.Field</c>).</summary>
    public static IReadOnlyList<SdfOp> RenderWarpOps { get; } = [
        SdfOp.AxialProfile,
        SdfOp.RotatePlane,
        SdfOp.Shear,
        SdfOp.GaussianPush,
        SdfOp.DomainWarp,
        SdfOp.RepeatPolar,
        SdfOp.WallpaperFold,
        SdfOp.LogSphere,
        SdfOp.CellJitter,
        SdfOp.LaneErode,
        SdfOp.Displace,
        SdfOp.NoiseDisplace,
    ];
    /// <summary>Returns whether the evaluator interprets <paramref name="op"/> under a reading; a program holding any other
    /// op is refused at construction.</summary>
    /// <param name="op">The op.</param>
    /// <param name="reading">The reading.</param>
    /// <returns><see langword="true"/> when the op is interpreted.</returns>
    public static bool IsSupportedOp(SdfOp op, SdfFieldReading reading) =>
        (IsSupportedOp(op: op) || ((reading == SdfFieldReading.Render) && RenderWarpOps.Contains(value: op)));
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
            // strands > 1 is refused separately for contact, at Compile time (see Compile's isSweep check) — this
            // type-keyed gate cannot see the instruction's own strand count. The render reading takes every strand.
            SdfShapeType.Sweep or
            // A fill is the exact distance to its edge list and a stroke the exact distance to its hulls of disks, both
            // 1-Lipschitz (SdfFieldEvaluator.Path.cs).
            SdfShapeType.Path => true,
            _ => false,
        };
    }
}
