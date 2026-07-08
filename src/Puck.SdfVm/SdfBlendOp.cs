namespace Puck.SdfVm;

/// <summary>How a shape composes with everything emitted before it.
/// <para>THE ACCUMULATOR RULE. A program is a flat instruction stream over ONE running nearest-surface distance.
/// <c>ResetPoint</c> resets the evaluation POINT, never that accumulator, so a blend never sees a "subtree" — it sees
/// the whole scene so far. That makes the union family (a <c>min</c>) and the subtraction family (a <c>max</c> against
/// the NEGATED candidate, which only bites inside the subtrahend) LOCAL: they may be emitted anywhere.</para>
/// <para>The INTERSECTION family is NOT local. <c>max(accumulator, candidate)</c> returns the candidate wherever the
/// candidate is farther — everywhere outside its own shape — so an intersection ANNIHILATES every earlier shape it does
/// not overlap, the ground plane included. To intersect exactly two shapes, emit them FIRST, against the empty
/// accumulator; emitting an intersection after unrelated geometry silently deletes that geometry.</para>
/// <para>The same asymmetry is why an instance carrying an intersection-family blend is UNMASKABLE: its influence region
/// is unbounded, so no cull bound can contain it (see <c>SdfProgram.UnmaskableBoundRadius</c>).</para></summary>
// Values must match Shaders/Sdf/sdf-vm.hlsli (SDF_BLEND_*).
public enum SdfBlendOp : uint {
    Union = 0,
    SmoothUnion = 1, // blend radius = instruction Data1.x
    Subtraction = 2,
    /// <summary>Intersection with everything accumulated so far — NOT with the preceding shape alone. See the accumulator
    /// rule on <see cref="SdfBlendOp"/>.</summary>
    Intersection = 3,
    /// <summary>Symmetric difference: solid where exactly one of the fields is solid (hollow where they overlap).</summary>
    Xor = 4,
    /// <summary>Intersection with a smooth seam (blend radius = instruction Data1.x). Subject to the accumulator rule on
    /// <see cref="SdfBlendOp"/>.</summary>
    SmoothIntersection = 5,
    /// <summary>Subtraction with a smooth (filleted) carve seam (blend radius = instruction Data1.x).</summary>
    SmoothSubtraction = 6,
    /// <summary>Union with a CHAMFERED (45° beveled) seam instead of a round fillet (bevel size = instruction Data1.x)
    /// — the mechanical/CAD look, distinct from <see cref="SmoothUnion"/>'s organic blob. Unlike smooth-min (which stays
    /// 1-Lipschitz), the bevel plane's gradient reaches √2 at a flat/near-parallel seam, so a chamfer blend contributes a
    /// conservative √2 factor to <c>AnalyzeLipschitz</c> (a step clamp, exactly 1 at a perpendicular seam and safe past
    /// it); a chamfer-free program is unaffected.</summary>
    ChamferUnion = 7,
    /// <summary>Intersection with a chamfered (45° beveled) seam (bevel size = instruction Data1.x). Carries the same √2
    /// Lipschitz factor as <see cref="ChamferUnion"/>.</summary>
    ChamferIntersection = 8,
    /// <summary>Subtraction with a chamfered (45° beveled) carve seam (bevel size = instruction Data1.x). Carries the
    /// same √2 Lipschitz factor as <see cref="ChamferUnion"/>.</summary>
    ChamferSubtraction = 9,
}
