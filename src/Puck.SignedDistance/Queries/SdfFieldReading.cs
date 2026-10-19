namespace Puck.SignedDistance.Queries;

/// <summary>What an <see cref="SdfFieldEvaluator"/> reads a program as.</summary>
public enum SdfFieldReading {
    /// <summary>Physical clearance, which every query and contact consumer reads: a non-uniform
    /// <see cref="SdfOp.Scale"/> and every warp are refused at construction.</summary>
    Contact,
    /// <summary>The field the renderer's interpreter samples (<c>mapCore</c>), which a baked mesh is measured against: a
    /// non-uniform <see cref="SdfOp.Scale"/> reads as the renderer's minimum-axis lower bound, and the
    /// <see cref="SdfOp.AxialProfile"/> and <see cref="SdfOp.RotatePlane"/> warps are interpreted
    /// (<see cref="SdfFieldEvaluator.RenderWarpOps"/>). Its distances are what the renderer marches, not clearance, so no
    /// contact consumer reads it.</summary>
    Render,
}
