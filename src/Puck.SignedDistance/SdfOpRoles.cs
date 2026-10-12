namespace Puck.SignedDistance;

/// <summary>What an <see cref="SdfOp"/> does to the evaluation state, for the analyses that walk a stream: the point
/// (the local position, the distance scale, the wallpaper material delta), the field accumulator, or neither.</summary>
public enum SdfOpRole {
    /// <summary>Reads or restructures the stream and moves no point: <see cref="SdfOp.ShapeBlend"/>,
    /// <see cref="SdfOp.ResetPoint"/>, <see cref="SdfOp.PushField"/>, <see cref="SdfOp.PopField"/>.</summary>
    Structure,
    /// <summary>Changes the accumulated field, never the point: <see cref="SdfOp.Onion"/>, <see cref="SdfOp.Dilate"/>,
    /// <see cref="SdfOp.Displace"/>, <see cref="SdfOp.NoiseDisplace"/>, <see cref="SdfOp.CellDisplace"/>.</summary>
    Field,
    /// <summary>Moves the point, and every copy it makes of what follows is bounded: a rigid move, a warp, a finite
    /// fold.</summary>
    Point,
    /// <summary>Moves the point through a lattice that has no edge unless its limit gives it one.</summary>
    Lattice,
}
/// <summary>The operand lanes of an instruction header (<see cref="SdfInstruction.Shape"/>,
/// <see cref="SdfInstruction.Blend"/>, <see cref="SdfInstruction.Material"/>), as a set.</summary>
[Flags]
public enum SdfInstructionLanes {
    /// <summary>No lane.</summary>
    None = 0,
    /// <summary>The shape lane (<see cref="SdfInstruction.Shape"/>).</summary>
    Shape = 1,
    /// <summary>The blend lane (<see cref="SdfInstruction.Blend"/>).</summary>
    Blend = 2,
    /// <summary>The material lane (<see cref="SdfInstruction.Material"/>).</summary>
    Material = 4,
}
/// <summary>The one table of <see cref="SdfOpRole"/>s, and of the header lanes whose value selects the code an op runs
/// (<see cref="CodeLanesOf"/>). An op with no row refuses by name rather than reading as harmless, so a new instruction is
/// classified before it can reach a program.</summary>
public static class SdfOpRoles {
    /// <summary>Returns the role of <paramref name="op"/>.</summary>
    /// <param name="op">A defined op.</param>
    /// <returns>The op's role.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="op"/> has no role.</exception>
    public static SdfOpRole Of(SdfOp op) => op switch {
        SdfOp.ShapeBlend or SdfOp.ResetPoint or SdfOp.PushField or SdfOp.PopField => SdfOpRole.Structure,
        SdfOp.Onion or SdfOp.Dilate or SdfOp.Displace or SdfOp.NoiseDisplace or SdfOp.CellDisplace => SdfOpRole.Field,
        SdfOp.Translate or SdfOp.Rotate or SdfOp.Scale or SdfOp.TransformDynamic or SdfOp.RotatePlane or SdfOp.Elongate or
        SdfOp.RepeatPolar or SdfOp.DomainWarp or SdfOp.SymmetryPlane or SdfOp.AxialProfile or SdfOp.Shear or
        SdfOp.GaussianPush or SdfOp.LaneErode => SdfOpRole.Point,
        SdfOp.Repeat or SdfOp.RepeatLimited or SdfOp.WallpaperFold or SdfOp.CellJitter or SdfOp.LogSphere => SdfOpRole.Lattice,
        _ => throw new InvalidOperationException(message: $"SDF op {op} has no point-state role; classify it in SdfOpRoles.Of."),
    };
    /// <summary>Returns the header lanes whose value selects the code <paramref name="op"/> runs, rather than feeding it
    /// arithmetic: a shape's type and blend, a pop's compose form, the plane and axis selectors of
    /// <see cref="SdfOp.RotatePlane"/>, <see cref="SdfOp.AxialProfile"/>, <see cref="SdfOp.Shear"/>,
    /// <see cref="SdfOp.RepeatPolar"/> and <see cref="SdfOp.WallpaperFold"/>, a wallpaper fold's group, a polar repeat's
    /// mirror flag, a cell jitter's noise flavor and a cell displacement's mode. Every other lane is data: a seed, an
    /// octave count (a loop bound), <see cref="SdfOp.GaussianPush"/>'s float bits, a material or a palette stride. A
    /// chain's key (<see cref="SdfChain"/>) reads exactly these lanes, so an edit to a data lane never changes a program's
    /// <see cref="SdfSpecializationSet"/>.</summary>
    /// <param name="op">A defined op.</param>
    /// <returns>The op's code-selecting lanes.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="op"/> has no lane classification.</exception>
    public static SdfInstructionLanes CodeLanesOf(SdfOp op) => op switch {
        SdfOp.ResetPoint or SdfOp.Translate or SdfOp.Rotate or SdfOp.Scale or SdfOp.TransformDynamic or SdfOp.Elongate or
        SdfOp.Repeat or SdfOp.RepeatLimited or SdfOp.Onion or SdfOp.Dilate or SdfOp.LogSphere or SdfOp.Displace or
        SdfOp.DomainWarp or SdfOp.SymmetryPlane or SdfOp.PushField or SdfOp.NoiseDisplace or SdfOp.GaussianPush or
        SdfOp.LaneErode => SdfInstructionLanes.None,
        SdfOp.ShapeBlend or SdfOp.RotatePlane or SdfOp.WallpaperFold or SdfOp.RepeatPolar or
        SdfOp.Shear => (SdfInstructionLanes.Shape | SdfInstructionLanes.Blend),
        SdfOp.AxialProfile => SdfInstructionLanes.Shape,
        SdfOp.CellJitter or SdfOp.PopField or SdfOp.CellDisplace => SdfInstructionLanes.Blend,
        _ => throw new InvalidOperationException(message: $"SDF op {op} has no code-selecting lane classification; classify it in SdfOpRoles.CodeLanesOf."),
    };
}
