namespace Puck.SignedDistance;

/// <summary>Field blends and their CPU and shader facets.</summary>
[SdfFieldCatalogue("SdfBlendOp", unsigned: true)]
public static partial class SdfFieldBlends {
    private static SdfShaderAction FartherMaterial => new(Code: "candidateWins = (candidate > result.distance); handsSlot = false; break;", Operands: SdfShaderOperands.None);
    private static SdfShaderAction CutMaterial => new(Code: "candidateWins = (-candidate > result.distance); handsSlot = false; break;", Operands: SdfShaderOperands.None);
    private static SdfShaderAction NearerMaterial => new(Code: "candidateWins = (candidate < result.distance); break;", Operands: SdfShaderOperands.None);

    private abstract class Entry {
        public abstract SdfBlendHaloAction Halo { get; }
        public abstract SdfBlendLipschitzAction Lipschitz { get; }
        // The shared formula both distance readers lower (SdfFieldFormulas); Shader is SdfShaderAction.Of it.
        public abstract SdfFormula Formula { get; }
        public abstract SdfShaderAction Shader { get; }
        public abstract SdfShaderAction MaterialShader { get; }
        public abstract SdfCullBoundAction CullBound { get; }
        public abstract SdfCullGuardAction CullGuard { get; }
        public abstract SdfPopAdmissionAction PopAdmission { get; }
        public abstract SdfBlendDistanceGainAction BlendDistanceGain { get; }
        public abstract SdfMaterialWinnerAction MaterialWinner { get; }
        public abstract SdfIntervalBlendAction IntervalBlend { get; }
        public abstract SdfBlendGrowthAction BlendGrowth { get; }
        public abstract bool IsIntersection { get; }
        public abstract bool IsSubtraction { get; }
        public abstract SdfFieldResource Resources { get; }
        public abstract bool ScopeOnly { get; }
        public abstract bool Hard { get; }
    }

    /// <summary>Returns the external field inputs this entry needs.</summary>
    public static SdfFieldResource ResourcesOf(SdfBlendOp value) => GetResourcesOf(value: value);

    [SdfFieldEntry(0)]
    private sealed class Union : Entry {
        public override bool Hard => true;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.None;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Maximum;
        public override SdfFormula Formula => SdfFieldFormulas.Union;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => NearerMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Union;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Union;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Union;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Nearer;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.Union;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    // blend radius = instruction Data1.x
    [SdfFieldEntry(1)]
    private sealed class SmoothUnion : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.Radius;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Maximum;
        public override SdfFormula Formula => SdfFieldFormulas.SmoothUnion;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => NearerMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.SmoothUnion;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.SmoothUnion;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.SmoothUnion;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Nearer;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.SmoothUnion;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.QuarterRadiusFloor;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Carves the shape out of everything accumulated so far: <c>max(a, −b)</c>.
    /// <para>The result is an exact distance only where the subject is the nearest surface. Inside the carved void
    /// <c>max(a, −b)</c> returns the subject's carved-away face or the carve's own boundary rather than the true clearance,
    /// so contact queries see phantom surfaces there: <c>world.collision.probe</c> reads a positive distance with an upward
    /// gradient in open air. Extend a carve past every point a body can reach, or build the void from union geometry.</para></summary>
    [SdfFieldEntry(2)]
    private sealed class Subtraction : Entry {
        public override bool Hard => true;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.None;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Maximum;
        public override SdfFormula Formula => SdfFieldFormulas.Subtraction;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => CutMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Local;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Subtraction;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Subject;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Cut;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.Subtraction;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Intersection with everything accumulated so far — not with the preceding shape alone. See the accumulator
    /// rule on <see cref="SdfBlendOp"/>.</summary>
    [SdfFieldEntry(3)]
    private sealed class Intersection : Entry {
        public override bool Hard => true;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.None;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Maximum;
        public override SdfFormula Formula => SdfFieldFormulas.Intersection;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => FartherMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Local;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Smaller;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Farther;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.Intersection;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => true;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Symmetric difference: solid where exactly one of the fields is solid (hollow where they overlap).</summary>
    [SdfFieldEntry(4)]
    private sealed class Xor : Entry {
        public override bool Hard => true;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.None;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Maximum;
        public override SdfFormula Formula => SdfFieldFormulas.Xor;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => NearerMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Disable;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Uncertified;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Nearer;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.Xor;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Intersection with a smooth seam (blend radius = instruction Data1.x). Subject to the accumulator rule on
    /// <see cref="SdfBlendOp"/>.</summary>
    [SdfFieldEntry(5)]
    private sealed class SmoothIntersection : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.None;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Maximum;
        public override SdfFormula Formula => SdfFieldFormulas.SmoothIntersection;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => FartherMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.LocalWithRadius;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Smaller;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Farther;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.SmoothIntersection;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => true;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Subtraction with a smooth (filleted) carve seam (blend radius = instruction Data1.x).</summary>
    [SdfFieldEntry(6)]
    private sealed class SmoothSubtraction : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.Radius;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Maximum;
        public override SdfFormula Formula => SdfFieldFormulas.SmoothSubtraction;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => CutMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.LocalWithRadius;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Subject;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Cut;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.SmoothSubtraction;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Union with a chamfered (45° beveled) seam instead of a round fillet (bevel size = instruction Data1.x)
    /// — the mechanical/CAD look, distinct from <see cref="SmoothUnion"/>'s organic blob.
    /// <para>The chamfer family's Lipschitz bound can
    /// exceed BOTH of its operands: the bevel plane's gradient is <c>(∇a ± ∇b)/√2</c>, so composing fields bounded by
    /// <c>La</c> and <c>Lb</c> yields <c>max(La, Lb, (La + Lb)/√2)</c>. <c>AnalyzeLipschitz</c> therefore folds it once
    /// per COMPOSITION, not once per program: the first chamfer against an empty accumulator is the identity, two reach
    /// √2, and a longer chain approaches the recurrence's fixed point <c>1 + √2</c>. Other composition families
    /// retain their own bounds.</para></summary>
    [SdfFieldEntry(7)]
    private sealed class ChamferUnion : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.ChamferUnion;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Chamfer;
        public override SdfFormula Formula => SdfFieldFormulas.ChamferUnion;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => NearerMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Disable;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Uncertified;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Nearer;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.ChamferUnion;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.HalfRadius;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Intersection with a chamfered (45° beveled) seam (bevel size = instruction Data1.x). Composes through
    /// the same Lipschitz recurrence as <see cref="ChamferUnion"/>.</summary>
    [SdfFieldEntry(8)]
    private sealed class ChamferIntersection : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.None;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Chamfer;
        public override SdfFormula Formula => SdfFieldFormulas.ChamferIntersection;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => FartherMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Disable;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Uncertified;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Farther;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.ChamferIntersection;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => true;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Subtraction with a chamfered (45° beveled) carve seam (bevel size = instruction Data1.x). Composes
    /// through the same Lipschitz recurrence as <see cref="ChamferUnion"/>.</summary>
    [SdfFieldEntry(9)]
    private sealed class ChamferSubtraction : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.Radius;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Chamfer;
        public override SdfFormula Formula => SdfFieldFormulas.ChamferSubtraction;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => CutMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Disable;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Uncertified;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Cut;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.ChamferSubtraction;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Carves a round seam from the union: max(min(a,b), r - sqrt(a²+b²)). Radius is Data1.x.
    /// The tube is measured in field coordinates; its spatial width depends on the surfaces' angle.</summary>
    [SdfFieldEntry(10)]
    private sealed class GrooveUnion : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.Radius;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.RoundSeam;
        public override SdfFormula Formula => SdfFieldFormulas.GrooveUnion;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => NearerMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Disable;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Uncertified;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Nearer;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.GrooveUnion;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Adds a round seam to the union: min(a,b,sqrt(a²+b²)-r). Radius is Data1.x.
    /// Both round seam blends compose derivative bounds as sqrt(La²+Lb²).</summary>
    [SdfFieldEntry(11)]
    private sealed class PipeUnion : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.Pipe;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.RoundSeam;
        public override SdfFormula Formula => SdfFieldFormulas.PipeUnion;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => NearerMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Disable;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Uncertified;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Nearer;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.PipeUnion;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.Radius;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Continuous linear field interpolation between parent accumulator and field scope: (1 - t)·a + t·b.
    /// t is derived from an instance render lane: t = saturate((lane[Data0.x] - Data0.y) / (Data0.z - Data0.y)).
    /// Strictly 1-Lipschitz. PopField compose only.</summary>
    [SdfFieldEntry(12)]
    private sealed class Morph : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.None;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Maximum;
        public override SdfFormula Formula => SdfFieldFormulas.Union;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => NearerMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Disable;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Morph;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Uncertified;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Nearer;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.Union;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.SlotLanes;
        public override bool ScopeOnly => true;
    }

    /// <summary>Carves a round seam while subtracting: max(a, -b, r - sqrt(a²+b²)). Radius is Data1.x.
    /// Composes derivative bounds as sqrt(La²+Lb²).</summary>
    [SdfFieldEntry(13)]
    private sealed class GrooveSubtraction : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.Radius;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.RoundSeam;
        public override SdfFormula Formula => SdfFieldFormulas.GrooveSubtraction;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => CutMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Disable;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Uncertified;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Cut;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.GrooveSubtraction;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Leaves a round bead while subtracting: min(max(a, -b), sqrt(a²+b²) - r). Radius is Data1.x.
    /// Composes derivative bounds as sqrt(La²+Lb²).</summary>
    [SdfFieldEntry(14)]
    private sealed class PipeSubtraction : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.Pipe;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.RoundSeam;
        public override SdfFormula Formula => SdfFieldFormulas.PipeSubtraction;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => CutMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Disable;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Radius;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Uncertified;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Cut;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.PipeSubtraction;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.Radius;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => false;
    }

    /// <summary>Adds continuous steps of radius r (Data1.x) and step count n (Data1.z) to the union.
    /// Strictly 1-Lipschitz. PopField compose only.</summary>
    [SdfFieldEntry(15)]
    private sealed class StairsUnion : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.Radius;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Maximum;
        public override SdfFormula Formula => SdfFieldFormulas.Union;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => NearerMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Disable;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Stairs;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Uncertified;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Nearer;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.Union;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.Radius;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => true;
    }

    /// <summary>Carves continuous steps of radius r (Data1.x) and step count n (Data1.z) from the subtraction.
    /// Strictly 1-Lipschitz. PopField compose only.</summary>
    [SdfFieldEntry(16)]
    private sealed class StairsSubtraction : Entry {
        public override bool Hard => false;
        public override SdfBlendHaloAction Halo => SdfBlendHaloAction.Radius;
        public override SdfBlendLipschitzAction Lipschitz => SdfBlendLipschitzAction.Maximum;
        public override SdfFormula Formula => SdfFieldFormulas.Subtraction;
        public override SdfShaderAction Shader => SdfShaderAction.Of(Formula);
        public override SdfShaderAction MaterialShader => NearerMaterial;
        public override SdfCullBoundAction CullBound => SdfCullBoundAction.Disable;
        public override SdfCullGuardAction CullGuard => SdfCullGuardAction.Disable;
        public override SdfPopAdmissionAction PopAdmission => SdfPopAdmissionAction.Stairs;
        public override SdfBlendDistanceGainAction BlendDistanceGain => SdfBlendDistanceGainAction.Uncertified;
        public override SdfMaterialWinnerAction MaterialWinner => SdfMaterialWinnerAction.Cut;
        public override SdfIntervalBlendAction IntervalBlend => SdfIntervalBlendAction.Union;
        public override SdfBlendGrowthAction BlendGrowth => SdfBlendGrowthAction.None;
        public override bool IsIntersection => false;
        public override bool IsSubtraction => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
        public override bool ScopeOnly => true;
    }
}
