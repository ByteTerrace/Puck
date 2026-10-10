namespace Puck.SignedDistance;

/// <summary>How the bounds of a field's operands compose through its set operations: the one algebra both the
/// program, deciding whether an instance's authored bound can cull it (<see cref="SdfProgram.HasUnmaskableInfluence"/>),
/// and the authoring stamper, sizing that bound (<c>CreationStampEmitter.RenderReach</c>), read.
/// <para>A bound contains an operand's influence. Only operands admitted by <see cref="SdfProgram.InstanceHasDistanceLowerBound"/>
/// also certify that their field is at least the distance to that sphere, divided by their scope's
/// <see cref="SdfInstanceCost.FieldRescale"/>. Containment alone cannot justify distance rejection for an anisotropic
/// gauge or an optional clipper. <see cref="Unbounded"/> stands for no bound: an operand
/// whose influence has no edge (a lattice fold with an unbounded limit, an infinite repeat). Unbounded is a state, carried
/// as IEEE positive infinity so that composing, adding a finite margin and scaling by a positive factor keep it, and
/// only the program's packing (<see cref="SdfProgram.UnmaskableBoundRadius"/>) turns it into a number.</para>
/// <para>Each operation bounds its result by what survives it. An intersection, <c>max(a, b)</c>, is at least either
/// operand, so the smaller bound is a bound of it, and an unbounded operand imposes nothing: unbounded and finite is
/// finite. A subtraction, <c>max(a, -b)</c>, is at least <c>a</c>, so <c>a</c>'s bound is its bound whatever <c>b</c> is. A
/// union, <c>min(a, b)</c>, is at least the distance to the nearer operand, so the larger bound covers it, and one
/// unbounded operand makes it unbounded. A smooth or chamfered variant composes the same way; the blend radius it adds
/// is the instance's halo, which the program adds once for the whole instance.</para></summary>
public static class SdfBoundAlgebra {
    /// <summary>The bound of an influence nothing contains: a state, never a radius. Composition, a finite margin and a
    /// positive scale all leave it unbounded; <see cref="SdfProgramBuilder.BeginInstance"/> accepts it as an instance's
    /// authored radius, and the packed bound is then <see cref="SdfProgram.UnmaskableBoundRadius"/>.</summary>
    public const float Unbounded = float.PositiveInfinity;

    /// <summary>Whether <paramref name="bound"/> is <see cref="Unbounded"/>.</summary>
    /// <param name="bound">The bound.</param>
    /// <returns><see langword="true"/> for the unbounded state.</returns>
    public static bool IsUnbounded(float bound) => float.IsPositiveInfinity(f: bound);
    /// <summary>Whether <paramref name="blend"/> is an intersection, whose result is bounded by its smaller operand.</summary>
    /// <param name="blend">The blend.</param>
    /// <returns><see langword="true"/> for <see cref="SdfBlendOp.Intersection"/>, <see cref="SdfBlendOp.SmoothIntersection"/>
    /// and <see cref="SdfBlendOp.ChamferIntersection"/>.</returns>
    public static bool IsIntersection(SdfBlendOp blend) => (blend is (SdfBlendOp.Intersection or SdfBlendOp.SmoothIntersection or SdfBlendOp.ChamferIntersection));
    /// <summary>Whether <paramref name="blend"/> is a subtraction, whose result is bounded by its accumulated operand.</summary>
    /// <param name="blend">The blend.</param>
    /// <returns><see langword="true"/> for every subtraction family member.</returns>
    public static bool IsSubtraction(SdfBlendOp blend) => (blend is (
        SdfBlendOp.Subtraction or SdfBlendOp.SmoothSubtraction or SdfBlendOp.ChamferSubtraction or
        SdfBlendOp.GrooveSubtraction or SdfBlendOp.PipeSubtraction or SdfBlendOp.StairsSubtraction
    ));
    /// <summary>Composes a bound with the next operand's: the accumulated field <paramref name="blend"/> the operand.</summary>
    /// <param name="accumulated">The bound of the field so far.</param>
    /// <param name="operand">The bound of the operand joining it.</param>
    /// <param name="blend">The blend that joins them.</param>
    /// <returns>The bound of the result: the smaller of the two for an intersection, <paramref name="accumulated"/> for a
    /// subtraction, and the larger for every other blend, so that an unbounded operand bounds only what it must.</returns>
    public static float Compose(float accumulated, float operand, SdfBlendOp blend) =>
        (IsIntersection(blend: blend)
            ? MathF.Min(x: accumulated, y: operand)
            : (IsSubtraction(blend: blend)
                ? accumulated
                : MathF.Max(x: accumulated, y: operand))
        );
}
