using Puck.SignedDistance.Queries;

namespace Puck.SignedDistance;

/// <summary>Static helper-closed work. Inputs are admitted through the existing solid/paint compiler.</summary>
public static class SdfShadeCost {
    /// <summary>Prices a shape including bounded internal helper loops, never only the dispatch opcode.</summary>
    public static uint Shape(SdfShadeShape descriptor) => Shape(shape: descriptor.Capture());

    internal static uint Shape(SdfPaintShape shape) => checked(40u + (shape.Type switch {
        SdfShapeType.ConvexPolygon => checked(((64u * ((uint)SdfPrismProfile.MaxConvexVertices)) + 128u)),
        SdfShapeType.Ellipse => checked(((32u * SdfFieldEvaluator.EllipseRootSteps) + 128u)),
        // Samples includes both endpoints, two initial refinement probes, and at most two gaps per round.
        // Each gap includes the strand frame, trigonometry, Bezier evaluation, length and radius.
        SdfShapeType.Sweep => checked((((((uint)shape.Data0.Y) *
            ((uint)((SdfFieldEvaluator.SweepSamples + 3) + (2 * SdfFieldEvaluator.SweepRounds)))) * 256u) + 256u)),
        SdfShapeType.Sphere => 64u,
        _ => 512u,
    }));
}
