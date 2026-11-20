namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    /// <summary>Returns whether the evaluator interprets <paramref name="op"/> as physical clearance
    /// (<see cref="SdfFieldReading.Contact"/>); a program holding any other op is refused at construction.</summary>
    /// <param name="op">The op.</param>
    /// <returns><see langword="true"/> when the op is interpreted.</returns>
    public static bool IsSupportedOp(SdfOp op) {
        return (SdfFieldOps.Contains(value: op) && SdfFieldOps.ContactOf(value: op));
    }

    /// <summary>Gets the ops the evaluator interprets under <see cref="SdfFieldReading.Render"/> alone, each with a point
    /// rule mirroring <c>mapCore</c>'s case (<c>SdfFieldEvaluator.RenderOps.cs</c>) and an inclusion rule: every warp,
    /// fold and per-shape relief the renderer draws but no contact field reads. <see cref="SdfOp.TransformDynamic"/> stays out of
    /// both readings: the evaluator carries no per-frame transform table to resolve a slot against, and the mesh audit
    /// poses each slot as a translate and rotate before it reads a group (<c>SdfMeshAudit.Field</c>).</summary>
    public static IReadOnlyList<SdfOp> RenderWarpOps { get; } = Array.AsReadOnly(Enum.GetValues<SdfOp>().Where(static op => SdfFieldOps.RenderOnlyOf(value: op)).ToArray());

    /// <summary>Returns whether the evaluator interprets <paramref name="op"/> under a reading; a program holding any other
    /// op is refused at construction.</summary>
    /// <param name="op">The op.</param>
    /// <param name="reading">The reading.</param>
    /// <returns><see langword="true"/> when the op is interpreted.</returns>
    public static bool IsSupportedOp(SdfOp op, SdfFieldReading reading) =>
        (IsSupportedOp(op: op) || ((reading == SdfFieldReading.Render) && SdfFieldOps.Contains(value: op) && SdfFieldOps.RenderOnlyOf(value: op)));
    /// <summary>Returns whether the evaluator interprets <paramref name="shape"/>; a program holding any other shape in its
    /// contact field is refused at construction.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns><see langword="true"/> when the shape is interpreted.</returns>
    public static bool IsSupportedShape(SdfShapeType shape) {
        return (SdfFieldShapes.Contains(value: shape) && SdfFieldShapes.ContactOf(value: shape));
    }
}
