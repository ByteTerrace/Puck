namespace Puck.SignedDistance;

/// <summary>What an <see cref="SdfOp"/> does to the evaluation state, for the analyses that walk a stream: the point
/// (the local position, the distance scale, the wallpaper material delta), the field accumulator, or neither.</summary>
public enum SdfOpRole {
    /// <summary>Reads or restructures the stream and moves no point: <see cref="SdfOp.ShapeBlend"/>,
    /// <see cref="SdfOp.ResetPoint"/>, <see cref="SdfOp.PushField"/>, <see cref="SdfOp.PopField"/>.</summary>
    Structure,
    /// <summary>Changes the accumulated field, never the point: <see cref="SdfOp.Onion"/>, <see cref="SdfOp.Dilate"/>,
    /// <see cref="SdfOp.Displace"/>, <see cref="SdfOp.NoiseDisplace"/>, <see cref="SdfOp.CellDisplace"/>,
    /// and a relief's data row, <see cref="SdfOp.ReliefRow"/>, which travels with the relief that reads it.</summary>
    Field,
    /// <summary>Moves the point, and every copy it makes of what follows is bounded: a rigid move, a warp, a finite
    /// fold.</summary>
    Point,
    /// <summary>Moves the point through a lattice that has no edge unless its limit gives it one.</summary>
    Lattice,
}
/// <summary>The one table of <see cref="SdfOpRole"/>s. An op with no row refuses by name rather than reading as harmless,
/// so a new instruction is classified before it can reach a program.</summary>
public static class SdfOpRoles {
    /// <summary>Returns the role of <paramref name="op"/>.</summary>
    /// <param name="op">A defined op.</param>
    /// <returns>The op's role.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="op"/> has no role.</exception>
    public static SdfOpRole Of(SdfOp op) => SdfFieldOps.RoleOf(value: op);
}
