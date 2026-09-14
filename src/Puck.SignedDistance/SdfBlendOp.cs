namespace Puck.SignedDistance;

/// <summary>How a shape composes with everything emitted before it.
/// <para><b>The accumulator rule.</b> A program is a flat instruction stream over one running nearest-surface distance.
/// <c>ResetPoint</c> resets the evaluation point, never that accumulator, so a blend never sees a "subtree" — it sees
/// the whole scene so far. That makes the union family (a <c>min</c>) and the subtraction family (a <c>max</c> against
/// the negated candidate, which only bites inside the subtrahend) local: they may be emitted anywhere.</para>
/// <para><b>The intersection family is not local.</b> <c>max(accumulator, candidate)</c> returns the candidate wherever the
/// candidate is farther — everywhere outside its own shape — so an intersection annihilates every earlier shape it does
/// not overlap, the ground plane included. To intersect exactly two shapes, emit them first, against the empty
/// accumulator; emitting an intersection after unrelated geometry silently deletes that geometry.</para>
/// <para>The same asymmetry is why an instance carrying an intersection-family blend is unmaskable: its influence region
/// is unbounded, so no cull bound can contain it (see <c>SdfProgram.UnmaskableBoundRadius</c>).</para></summary>
// Values must match Shaders/Sdf/sdf-vm.hlsli (SDF_BLEND_*).
public enum SdfBlendOp : uint {
    Union = 0,
    SmoothUnion = 1, // blend radius = instruction Data1.x
    Subtraction = 2,
    /// <summary>Intersection with everything accumulated so far — not with the preceding shape alone. See the accumulator
    /// rule on <see cref="SdfBlendOp"/>.</summary>
    Intersection = 3,
    /// <summary>Symmetric difference: solid where exactly one of the fields is solid (hollow where they overlap).</summary>
    Xor = 4,
    /// <summary>Intersection with a smooth seam (blend radius = instruction Data1.x). Subject to the accumulator rule on
    /// <see cref="SdfBlendOp"/>.</summary>
    SmoothIntersection = 5,
    /// <summary>Subtraction with a smooth (filleted) carve seam (blend radius = instruction Data1.x).</summary>
    SmoothSubtraction = 6,
    /// <summary>Union with a chamfered (45° beveled) seam instead of a round fillet (bevel size = instruction Data1.x)
    /// — the mechanical/CAD look, distinct from <see cref="SmoothUnion"/>'s organic blob.
    /// <para>The chamfer family's Lipschitz bound can
    /// exceed BOTH of its operands: the bevel plane's gradient is <c>(∇a ± ∇b)/√2</c>, so composing fields bounded by
    /// <c>La</c> and <c>Lb</c> yields <c>max(La, Lb, (La + Lb)/√2)</c>. <c>AnalyzeLipschitz</c> therefore folds it once
    /// per COMPOSITION, not once per program: the first chamfer against an empty accumulator is the identity, two reach
    /// √2, and a longer chain approaches the recurrence's fixed point <c>1 + √2</c>. Other composition families
    /// retain their own bounds.</para></summary>
    ChamferUnion = 7,
    /// <summary>Intersection with a chamfered (45° beveled) seam (bevel size = instruction Data1.x). Composes through
    /// the same Lipschitz recurrence as <see cref="ChamferUnion"/>.</summary>
    ChamferIntersection = 8,
    /// <summary>Subtraction with a chamfered (45° beveled) carve seam (bevel size = instruction Data1.x). Composes
    /// through the same Lipschitz recurrence as <see cref="ChamferUnion"/>.</summary>
    ChamferSubtraction = 9,
    /// <summary>Carves a round seam from the union: max(min(a,b), r - sqrt(a²+b²)). Radius is Data1.x.
    /// The tube is measured in field coordinates; its spatial width depends on the surfaces' angle.</summary>
    GrooveUnion = 10,
    /// <summary>Adds a round seam to the union: min(a,b,sqrt(a²+b²)-r). Radius is Data1.x.
    /// Both round seam blends compose derivative bounds as sqrt(La²+Lb²).</summary>
    PipeUnion = 11,
    /// <summary>Continuous linear field interpolation between parent accumulator and field scope: (1 - t)·a + t·b.
    /// t is derived from an instance render lane: t = saturate((lane[Data0.x] - Data0.y) / (Data0.z - Data0.y)).
    /// Strictly 1-Lipschitz. PopField compose only.</summary>
    Morph = 12,
    /// <summary>Carves a round seam while subtracting: max(a, -b, r - sqrt(a²+b²)). Radius is Data1.x.
    /// Composes derivative bounds as sqrt(La²+Lb²).</summary>
    GrooveSubtraction = 13,
    /// <summary>Leaves a round bead while subtracting: min(max(a, -b), sqrt(a²+b²) - r). Radius is Data1.x.
    /// Composes derivative bounds as sqrt(La²+Lb²).</summary>
    PipeSubtraction = 14,
    /// <summary>Adds continuous steps of radius r (Data1.x) and step count n (Data1.y) to the union.
    /// Strictly 1-Lipschitz. PopField compose only.</summary>
    StairsUnion = 15,
    /// <summary>Carves continuous steps of radius r (Data1.x) and step count n (Data1.y) from the subtraction.
    /// Strictly 1-Lipschitz. PopField compose only.</summary>
    StairsSubtraction = 16,
}
