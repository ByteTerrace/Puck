using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // The per-program Lipschitz factor L, returned as the STEP SCALE 1/L in (0, 1]. A SEPARATE static pass over the
    // instruction stream — deliberately NOT grafted into AnalyzeSegment/AnalyzeBounds (they sit at their CA150x
    // complexity ceilings). It answers the one question sphere tracing needs: by how much can this program's packed
    // distance field OVERESTIMATE true distance? A field that overestimates by factor L lets the marcher step L times
    // too far and tunnel through thin/twisted surfaces, so mapCore scales its final distance by 1/L to keep every step
    // conservative (KEEP IN SYNC with the stepScale read + final multiply in Assets/Shaders/Sdf/sdf-vm.hlsli's mapCore).
    //
    // TWO passes, because a chain's factor bounds one CANDIDATE while the program's L bounds the ACCUMULATOR the
    // candidates compose into, and one composition family (chamfer) can exceed both of its inputs:
    //
    //   Pass 1 — AnalyzeChainLipschitz: the per-chain candidate factor (see its own remarks).
    //   Pass 2 — here: walk the stream in mapCore's own COMPOSITION order, folding each candidate into a running
    //            accumulator bound through ComposeLipschitz. mapCore seeds result.distance with the CONSTANT
    //            SDF_FAR_DISTANCE (gradient 0, so L = 0) and carries that one accumulator across every ResetPoint (the
    //            accumulator rule, sdf-vm.hlsli); PushField saves it and reseeds the same constant, PopField composes
    //            the scope back in. This walk mirrors that state machine exactly, so segment splitting is NOT a
    //            protection and is not treated as one.
    //
    // The invariant this pass exists for: every non-chamfer arm of blendShape is a min/max/lerp of its two operands and
    // so carries L = max(La, Lb), which is idempotent and order-free — a per-chain maximum computes it. A chamfer arm
    // adds the bevel plane (a ± b ± r)·√½, whose gradient is (∇a ± ∇b)/√2 and therefore reaches (La + Lb)/√2:
    //
    //     L = max(La, Lb, (La + Lb)/√2)
    //
    // That recurrence is NOT idempotent — it grows with every chamfer composition, and only a per-composition fold
    // counts them. Its fixed point is L = (L + 1)/√2 ⟹ L = 1 + √2 ≈ 2.41421, so a chamfer chain over unit-gradient
    // operands can reach 1.70711× the value any one-shot √2 factor reports. Two consequences of the recurrence to hold
    // onto before "simplifying" it back to a per-chain factor:
    //
    //   * The first chamfer composition is the identity, because its accumulator is still the SDF_FAR_DISTANCE
    //     constant: max(0, Lb, Lb/√2) = Lb. One chamfer is factor 1; TWO are exactly √2 (2 · 0.70710678f ==
    //     1.41421356f in float32), so shallow-chamfer content keeps the step scale it had. Growth starts at the third.
    //   * A chamfer PopField is this same composition, not a program-wide multiply. For one pop against an accumulator
    //     dominating both operands the recurrence gives max(L, L, 2L/√2) = √2·L; it additionally counts repeated pops,
    //     which MaxFieldScopeDepth = 1 forbids nesting but not sequencing.
    // The depth-0 chain with the largest factor: the chain that binds the global step scale below 1 (a scoped chain's
    // factor is clamped to 1 at its pop, see AnalyzeLipschitz). Null when no unscoped chain carries a factor above 1,
    // which is every isometric program. A diagnostic, never an input to the packed words.
    private static SdfStepScaleBinder? AnalyzeStepScaleBinder(SdfInstruction[] instructions, SdfInstanceRange[] instances) {
        var chainFactors = AnalyzeChainLipschitz(
            chainHasShape: out _,
            instructions: instructions
        );
        SdfStepScaleBinder? best = null;
        var chainIndex = 0;
        var depth = 0;

        for (var index = 0; (index < instructions.Length); index++) {
            var instruction = instructions[index];

            if (
                (instruction.Op == SdfOp.ResetPoint) &&
                (index != 0)
            ) {
                chainIndex++;
            }

            switch (instruction.Op) {
                case SdfOp.PushField: {
                        depth++;
                        break;
                    }
                case SdfOp.PopField: {
                        depth--;
                        break;
                    }
                case SdfOp.ShapeBlend: {
                        var factor = chainFactors[chainIndex];

                        if (
                            (depth == 0) &&
                            (factor > 1.0f) &&
                            ((best is not { } current) || (factor > current.Factor))
                        ) {
                            best = new SdfStepScaleBinder(
                                Factor: factor,
                                InstanceIndex: InstanceOwning(
                                    instances: instances,
                                    instructionIndex: index
                                ),
                                InstructionIndex: index,
                                Shape: ((SdfShapeType)instruction.Shape)
                            );
                        }

                        break;
                    }
                default: {
                        break;
                    }
            }
        }

        return best;
    }
    private static int InstanceOwning(SdfInstanceRange[] instances, int instructionIndex) {
        for (var index = 0; (index < instances.Length); index++) {
            if (
                (instructionIndex >= instances[index].First) &&
                (instructionIndex < instances[index].End)
            ) {
                return index;
            }
        }

        return -1;
    }
    private static float AnalyzeLipschitz(SdfInstruction[] instructions) {
        var chainFactors = AnalyzeChainLipschitz(
            chainHasShape: out var chainHasShape,
            instructions: instructions
        );
        // mapCore's seed is the SDF_FAR_DISTANCE CONSTANT — a zero-gradient function, hence L = 0, which is what makes
        // the first composition the identity. A shape-free program never leaves 0 and clamps to 1 below, exactly as it
        // did when this pass seeded programLipschitz at 1.
        var accumulator = 0.0f;
        // The one-deep PushField save (SdfProgramBuilder.MaxFieldScopeDepth == 1), mirroring mapCore's
        // savedFieldDistance slot. A PUSH reseeds the accumulator to the same constant the program started from.
        var savedAccumulator = 0.0f;
        var chainIndex = 0;

        for (var index = 0; (index < instructions.Length); index++) {
            var instruction = instructions[index];

            // The SAME chain delimiter pass 1 folded on, so chainIndex selects that chain's recorded factor.
            if (
                (instruction.Op == SdfOp.ResetPoint) &&
                (index != 0)
            ) {
                chainIndex++;
            }

            switch (instruction.Op) {
                case SdfOp.ShapeBlend: {
                        accumulator = ComposeLipschitz(
                            blend: instruction.Blend,
                            candidate: chainFactors[chainIndex],
                            current: accumulator
                        );
                        break;
                    }
                case SdfOp.PushField: {
                        savedAccumulator = accumulator;
                        accumulator = 0.0f;
                        break;
                    }
                case SdfOp.Displace:
                case SdfOp.NoiseDisplace: {
                        // A field op in a SHAPE-FREE chain never reaches a ShapeBlend compose (pass 1's chain factor is
                        // composed only there), yet at runtime it still displaced the accumulated field exactly where it
                        // executes — fold its own factor in additively HERE (|∇(f + g)| ≤ L_f + L_g), inside the scope
                        // the op acts on, so a Union pop keeps it instance-local instead of summing across instances.
                        // A field op in a shape-bearing chain stays on the pass-1 chain-product path, byte-identically.
                        if (!chainHasShape[chainIndex]) {
                            accumulator += (((instruction.Op == SdfOp.Displace)
                                ? DisplaceWarpLipschitz(instruction: instruction)
                                : NoiseDisplaceLipschitz(instruction: instruction)) - 1.0f);
                        }

                        break;
                    }
                case SdfOp.PopField: {
                        // The scope's own bound becomes a BAKED per-candidate scale: Data1.y = 1/L_scope, applied by
                        // mapCore/mapGradCore (and the CPU evaluator) to the scope's field at the pop — a positively
                        // scaled distance keeps its zero set, and (1/L)·f of an L-Lipschitz f is exactly 1-Lipschitz,
                        // so the scope's warps/relief/eccentricity stop taxing the GLOBAL step scale. A factor-1 scope
                        // stays unpatched (Data1.y = 0 reads as no scale), keeping existing programs byte-identical.
                        var scopeLipschitz = MathF.Max(
                            x: accumulator,
                            y: 1.0f
                        );

                        if (
                            (scopeLipschitz > 1.0f) &&
                            float.IsFinite(f: scopeLipschitz)
                        ) {
                            instructions[index] = (instruction with {
                                Data1 = new Vector4(
                                    x: instruction.Data1.X,
                                    y: (1.0f / scopeLipschitz),
                                    z: instruction.Data1.Z,
                                    w: instruction.Data1.W
                                ),
                            });
                        }

                        accumulator = ComposeLipschitz(
                            blend: instruction.Blend,
                            candidate: MathF.Min(
                                x: accumulator,
                                y: 1.0f
                            ),
                            current: savedAccumulator
                        );
                        savedAccumulator = 0.0f;
                        break;
                    }
                default: {
                        // Every other op acts on the POINT (or is a field op whose factor pass 1 already folded into the
                        // chain), so it changes no accumulator BOUND here.
                        break;
                    }
            }
        }

        // stepScale = 1 / max(L, 1), clamped to (0, 1]. A warp-free, eccentricity-free, chamfer-free program composes
        // nothing but factor-1 candidates through max-only arms, so L == 1 exactly and this returns 1.0f to the bit
        // (max(1,1) = 1, 1/1 = 1). The finite guard keeps an extreme authored warp from producing a non-finite scale,
        // which the shader's `> 0` guard would wrongly read as "no clamp".
        var lipschitz = MathF.Max(
            x: accumulator,
            y: 1.0f
        );

        return (float.IsFinite(f: lipschitz)
            ? (1.0f / lipschitz)
            : 0.0001f
        );
    }
    // Pass 1 of the Lipschitz analysis (see AnalyzeLipschitz): the per-CHAIN candidate factor, one entry per chain in
    // stream order. A chain closes at each ResetPoint past instruction 0; the last (or only) chain closes at the end.
    //
    // Per chain: domain ops that are isometries / non-expansive projections / field ops (Translate/Rotate/
    // TransformDynamic/Symmetry/Repeat/RepeatLimited/WallpaperFold/Elongate/Onion/Dilate; Scale is handled
    // conservatively by the runtime distanceScale) contribute factor 1. A coordinate-keyed plane rotation
    // (BendX/BendY/BendZ/TwistY) contributes the EXACT operator norm of its Jacobian over the chain's reach rho; an
    // ellipsoid (whose SDF can underestimate) contributes its eccentricity. A chain's factor is the product of its
    // domain-op factors times the max shape-approx factor in it (a twisted ellipsoid compounds both errors). A
    // warp-free, eccentricity-free chain yields exactly 1.
    //
    // A warp's reach rho depends on shapes that can appear AFTER it in the chain (the usual Translate/warp/Shape
    // order), so the chain's warp rates and its reach accumulate as the walk proceeds and fold together at chain end.
    //
    // A blend's own factor is deliberately ABSENT here: composition is not a property of the chain a candidate was
    // built in, and folding chamfer in at this level is precisely the latch AnalyzeLipschitz's remarks retire.
    private static List<float> AnalyzeChainLipschitz(IReadOnlyList<SdfInstruction> instructions, out List<bool> chainHasShape) {
        var chainFactors = new List<float>();
        var hasShapeByChain = new List<bool>();
        var chainHasShapeBlend = false;
        // Each warp's |rate| plus whether its keyed coordinate lies inside the plane it rotates (see BendOperatorNorm).
        var chainWarpRates = new List<(float Rate, bool KeyInRotatedPlane)>();
        var chainShapeApproxMax = 1.0f;   // max ellipsoid eccentricity among the chain's shapes (1 = none / perfectly round)
        var chainShapeReach = 0.0f;       // max local bounding radius among the chain's shapes
        var chainTranslateReach = 0.0f;   // sum of |translate offset| accumulated on the chain
        var chainLogSphereProduct = 1.0f; // product of the chain's log-spherical shell-fold factors exp(w/2) (1 = none)
        var chainDisplaceWarpProduct = 1.0f; // product of the chain's Displace/DomainWarp metric-stretch factors (1 + amp*max|freq_i|); reach-independent, like the log-sphere product (1 = none)
        var chainCellJitters = new List<(float MinSpacing, float Jitter)>(); // each CellJitter's (min spacing, jitter), folded at chain-close against the FINAL chainShapeReach

        for (var index = 0; (index < instructions.Count); index++) {
            var instruction = instructions[index];

            // Segments split BEFORE each ResetPoint, so a ResetPoint past the first instruction closes the chain that
            // preceded it: fold that chain, then begin a fresh one (the ResetPoint itself contributes nothing).
            if (
                (instruction.Op == SdfOp.ResetPoint) &&
                (index != 0)
            ) {
                chainFactors.Add(item: (((FoldChainLipschitz(
                    reach: (chainTranslateReach + chainShapeReach),
                    shapeApproxMax: chainShapeApproxMax,
                    warpRates: chainWarpRates
                ) * chainLogSphereProduct) * chainDisplaceWarpProduct) * FoldCellJitterProduct(
                    cellJitters: chainCellJitters,
                    shapeReach: chainShapeReach
                )));
                hasShapeByChain.Add(item: chainHasShapeBlend);
                chainHasShapeBlend = false;
                chainWarpRates.Clear();
                chainShapeApproxMax = 1.0f;
                chainShapeReach = 0.0f;
                chainTranslateReach = 0.0f;
                chainLogSphereProduct = 1.0f;
                chainDisplaceWarpProduct = 1.0f;
                chainCellJitters.Clear();
            }

            switch (instruction.Op) {
                case SdfOp.Translate: {
                        chainTranslateReach += new Vector3(
                            x: instruction.Data0.X,
                            y: instruction.Data0.Y,
                            z: instruction.Data0.Z
                        ).Length();
                        break;
                    }
                case SdfOp.BendX:
                case SdfOp.BendY:
                case SdfOp.BendZ: {
                        // Data0.x is the warp rate (radians of rotation per unit of the keyed coordinate). Every Bend keys
                        // on a coordinate INSIDE the plane it rotates, so its operator norm is the larger 1 + a form.
                        chainWarpRates.Add(item: (MathF.Abs(x: instruction.Data0.X), true));
                        break;
                    }
                case SdfOp.TwistY: {
                        // TwistY keys on y and rotates XZ — the key axis is orthogonal to the rotated plane.
                        chainWarpRates.Add(item: (MathF.Abs(x: instruction.Data0.X), false));
                        break;
                    }
                case SdfOp.ShapeBlend: {
                        chainHasShapeBlend = true;
                        chainShapeReach = MathF.Max(
                            x: chainShapeReach,
                            y: ShapeReachRadius(instruction: instruction)
                        );

                        if (((SdfShapeType)instruction.Shape) == SdfShapeType.Ellipsoid) {
                            chainShapeApproxMax = MathF.Max(
                                x: chainShapeApproxMax,
                                y: EllipsoidEccentricity(instruction: instruction)
                            );
                        }

                        break;
                    }
                case SdfOp.LogSphere: {
                        // The log-spherical shell fold's metric-distortion factor compounds over nested folds (a product,
                        // not a max — like a twisted ellipsoid compounding both its errors). Reach-INDEPENDENT, so it does
                        // not join chainWarpRates (which fold over the chain reach); it multiplies the whole chain's factor.
                        chainLogSphereProduct *= LogSphereLipschitz(instruction: instruction);
                        break;
                    }
                case SdfOp.CellJitter: {
                        // TWO orthogonal Lipschitz contributions, both kept:
                        //
                        // (1) REACH under a downstream warp. The per-cell displacement is INDEPENDENT on each axis
                        // ((r0 - 0.5) * Data0.w, r0 a float3), so a corner cell moves up to (sqrt(3)/2) * |Data0.w| in
                        // Euclidean distance toward a downstream warp, extending that warp's reach — treat it like a Translate
                        // of that magnitude. chainTranslateReach is a Euclidean-length sum (Translate adds Vector3(...).Length()),
                        // so the per-axis half-amplitude must be combined as a VECTOR (sqrt(3)/2), not summed as a scalar (0.5),
                        // or a jitter-under-a-warp chain would under-count reach and let the over-relaxed march overstep. The
                        // tumble is a rotation about the cell center (already inside chainShapeReach) and the fold is an
                        // isometry — NEITHER adds anything more.
                        chainTranslateReach += (0.8660254f * MathF.Abs(x: instruction.Data0.W));
                        // (2) The STANDALONE boundary-discontinuity step factor (the LogSphere-shaped fix). Stash this op's
                        // (min spacing, jitter) so the chain-close fold can compute a REACH-INDEPENDENT factor against the
                        // chain's FINAL max shapeReach (the shapes follow the fold, like chainLogSphereProduct's shells).
                        // See FoldCellJitterProduct / CellJitterLipschitz.
                        var cellSpacing = new Vector3(
                            x: instruction.Data0.X,
                            y: instruction.Data0.Y,
                            z: instruction.Data0.Z
                        );

                        chainCellJitters.Add(item: (MathF.Min(
                            x: cellSpacing.X,
                            y: MathF.Min(
                                x: cellSpacing.Y,
                                y: cellSpacing.Z
                            )
                        ), instruction.Data0.W));
                        break;
                    }
                case SdfOp.Displace: {
                        // The sinusoidal relief's gradient is bounded by amp*max|freq_i| (a global, reach-INDEPENDENT bound on the
                        // sin-product basis; see DisplaceWarpLipschitz), so the field can overestimate by that. It multiplies the whole
                        // chain like the log-sphere product. A FIELD op, so it adds no reach (the point is untouched).
                        chainDisplaceWarpProduct *= DisplaceWarpLipschitz(instruction: instruction);
                        break;
                    }
                case SdfOp.DomainWarp: {
                        // Same reach-independent metric-stretch factor (1 + amp*max|freq_i|) as Displace. As a POINT op it also
                        // moves the point by up to amp*sqrt(3), extending a downstream twist/bend's reach like a Translate.
                        chainDisplaceWarpProduct *= DisplaceWarpLipschitz(instruction: instruction);
                        chainTranslateReach += (1.7320508f * MathF.Abs(x: instruction.Data0.W));
                        break;
                    }
                case SdfOp.NoiseDisplace: {
                        // The fBm value-noise relief's gradient is bounded reach-independently (see NoiseDisplaceLipschitz),
                        // so it multiplies the whole chain like Displace's factor. A FIELD op, so it adds no reach.
                        chainDisplaceWarpProduct *= NoiseDisplaceLipschitz(instruction: instruction);
                        break;
                    }
                default: {
                        // ResetPoint/Rotate/Scale/TransformDynamic/SymmetryPlane/Repeat/RepeatLimited/WallpaperFold/RepeatPolar/
                        // Elongate/Onion/Dilate/PushField/PopField: factor 1 (isometry, non-expansive projection, field op,
                        // the runtime distanceScale-handled Scale, or a COMPOSITION AnalyzeLipschitz's pass folds rather
                        // than this one) — nothing accumulates on the chain. (RepeatPolar is a rotation/reflection fold,
                        // exactly like Repeat; CellJitter is handled above: its jitter half-amplitude joins the chain reach,
                        // its tumble/fold are isometries.)
                        break;
                    }
            }
        }

        // Fold the final (or only) chain.
        chainFactors.Add(item: (((FoldChainLipschitz(
            reach: (chainTranslateReach + chainShapeReach),
            shapeApproxMax: chainShapeApproxMax,
            warpRates: chainWarpRates
        ) * chainLogSphereProduct) * chainDisplaceWarpProduct) * FoldCellJitterProduct(
            cellJitters: chainCellJitters,
            shapeReach: chainShapeReach
        )));
        hasShapeByChain.Add(item: chainHasShapeBlend);
        chainHasShape = hasShapeByChain;

        return chainFactors;
    }
}
