namespace Puck.SignedDistance;

/// <summary>How the bounds of a field's operands compose through its set operations: the one algebra both the
/// program, deciding whether an instance's authored bound can cull it (<see cref="SdfProgram.HasUnmaskableInfluence"/>),
/// and the authoring stamper, sizing that bound (<c>CreationStampEmitter.RenderReach</c>), read.
/// <para>A bound contains an operand's influence. An instance's field is at least the distance to that sphere times its
/// <see cref="SdfProgram.InstanceDistanceGain"/>, which accounts for its scopes' rescales, its gauges' and non-uniform
/// scales' slower growth and its smooth unions' sag; containment alone cannot justify distance rejection, and an optional
/// clipper or an approximate primitive certifies no gain. <see cref="Unbounded"/> stands for no bound: an operand
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
    /// <summary>Whether <paramref name="blend"/> is a subtraction whose result is bounded by its accumulated operand: one
    /// that reads at least <c>max(a, -b)</c>. A pipe subtraction is not one, since its bead,
    /// <c>hypot(a, b) - radius</c>, lays solid outside its subject (<see cref="Growth"/>).</summary>
    /// <param name="blend">The blend.</param>
    /// <returns><see langword="true"/> for every subtraction family member but <see cref="SdfBlendOp.PipeSubtraction"/>.</returns>
    public static bool IsSubtraction(SdfBlendOp blend) => (blend is (
        SdfBlendOp.Subtraction or SdfBlendOp.SmoothSubtraction or SdfBlendOp.ChamferSubtraction or
        SdfBlendOp.GrooveSubtraction or SdfBlendOp.StairsSubtraction
    ));
    /// <summary>Whether <paramref name="blend"/> only removes from what it blends into: an intersection or a subtraction
    /// (<see cref="IsIntersection"/>, <see cref="IsSubtraction"/>), so the shape it blends has no surface of its own and
    /// shapes only the surfaces accumulated before it.</summary>
    /// <param name="blend">The blend.</param>
    /// <returns><see langword="true"/> for a carving blend.</returns>
    public static bool Carves(SdfBlendOp blend) => (IsIntersection(blend: blend) || IsSubtraction(blend: blend));
    /// <summary>Returns how far below the lesser of its operands' readings a blend's result may read, so that wherever
    /// the result reads at most a level <c>λ ≥ 0</c>, the lesser operand reads at most <c>λ</c> plus this: the outward
    /// growth a composed solid can have past its operands. A smooth union sags by at most a quarter of its radius, a
    /// chamfered union's bevel by at most half its radius, a pipe (union or subtraction) or stairs union by at most its
    /// radius; every blend that reads at least the lesser operand (the hard set operations, xor, a morph, the
    /// intersections, the other subtractions, the grooves) grows nothing. A blend this algebra does not know grows by its
    /// radius.</summary>
    /// <param name="blend">The blend.</param>
    /// <param name="radius">The blend's radius, in field units, as authored; a smooth blend's floors at
    /// <see cref="SdfProgram.SmoothRadiusMin"/>.</param>
    /// <returns>The growth, in field units, at least zero.</returns>
    public static float Growth(SdfBlendOp blend, float radius) {
        var magnitude = MathF.Abs(x: radius);

        return blend switch {
            SdfBlendOp.Union or SdfBlendOp.Subtraction or SdfBlendOp.Intersection or SdfBlendOp.Xor or SdfBlendOp.Morph or
            SdfBlendOp.SmoothIntersection or SdfBlendOp.SmoothSubtraction or SdfBlendOp.ChamferIntersection or
            SdfBlendOp.ChamferSubtraction or SdfBlendOp.GrooveUnion or SdfBlendOp.GrooveSubtraction or
            SdfBlendOp.StairsSubtraction => 0f,
            SdfBlendOp.SmoothUnion => (MathF.Max(x: magnitude, y: SdfProgram.SmoothRadiusMin) / 4f),
            SdfBlendOp.ChamferUnion => (magnitude / 2f),
            _ => magnitude,
        };
    }
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
