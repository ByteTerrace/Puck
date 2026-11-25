using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE GRADIENT READER: the stream read once at a point in forward mode, every value carried with its three partial
// derivatives along the world axes (Puck.Maths' FixedGradient), so a contact normal costs one walk where central
// differences cost six. It is the shared formulas' fixed-point dual lowering (SdfFormula's dual reader, the same lowering
// the bake's gradient kernel runs over floats). A regional evaluator walks the point's region tape end to end, as the wide
// reader does; any other walks the whole stream, skipping an instance whose reach clears the running answer as the point
// walk does (InstanceClears), since such an instance moves neither the answer nor, away from the measure-zero boundary of
// its certificate, the answer's slope. Neither keeps a root frame or a material. At a hard minimum or a clamp the active branch's derivative is taken, deciding the branch by the values as the
// point walk does, so a crease answers the face the point reads rather than a blend of the two faces a stencil straddles.
// Every operation is integer arithmetic on raws: deterministic on every machine, like the point walk.
//
// A walk that meets an op or a formula node the reader has no dual of (a warp, a masked relief, a shape whose reader is
// hand-written, a varying exponent, the ellipse's root, the cell noise) answers nothing, and TryFieldGradient takes its
// six taps.
public sealed partial class SdfFieldEvaluator {
    // Set by the dispatch's fallback when the walk reached a formula without a fixed-point dual.
    [ThreadStatic]
    private static bool DualMissed;

    private static FixedGradient DualUncovered() {
        DualMissed = true;
        return default;
    }
    private static FixedGradient3 DualUncoveredPoint() {
        DualMissed = true;
        return default;
    }

    // The superellipsoid's |p| · min(r) / r (SuperellipsoidScaledRatio), whose slope is the constant min(r) / r; the
    // shader-only reciprocal radius is unused, as the plain reader leaves it.
    private static FixedGradient SuperellipsoidScaledRatioGradient(FixedGradient magnitude, FixedQ4816 minRadius, FixedQ4816 radius) =>
        new(Value: SuperellipsoidScaledRatio(magnitude: magnitude.Value, minRadius: minRadius, radius: radius),
            Gradient: (magnitude.Gradient * FusedArithmetic.MultiplyDivide(divisor: radius, multiplicand: FixedQ4816.One, multiplier: minRadius)));
    // x^y at a constant exponent through the exact power memo, its slope y · x^y / x where x is positive and zero elsewhere,
    // as the kernels' sdfdPower takes it.
    private static FixedGradient PowerGradient(ExactPowerMemo memo, FixedGradient x, FixedQ4816 exponent, ref PowerWork powerWork) {
        var value = memo.Pow(x: x.Value, y: exponent, work: ref powerWork);

        return (x.Value > FixedQ4816.Zero)
            ? new FixedGradient(Value: value, Gradient: (x.Gradient * FusedArithmetic.MultiplyDivide(divisor: x.Value, multiplicand: exponent, multiplier: value)))
            : value;
    }

    // A candidate scaled by a scope's 2⁻³² fraction (ScaleScope), its gradient with it.
    private static FixedGradient ScaleScope(FixedGradient candidate, long scale) => new(
        Value: ScaleScope(candidate: candidate.Value, scale: scale),
        Gradient: new FixedVector3(
            X: ScaleScope(candidate: candidate.Gradient.X, scale: scale),
            Y: ScaleScope(candidate: candidate.Gradient.Y, scale: scale),
            Z: ScaleScope(candidate: candidate.Gradient.Z, scale: scale)
        )
    );

    // Walks a tape at one point in forward mode and answers the distance with its gradient, or refuses a tape that holds an
    // op or formula the reader has no dual of.
    private bool TryWalkGradient(ReadOnlySpan<int> tape, FixedVector3 world, ReadOnlySpan<FixedQ4816> reaches, out FixedGradient distance) {
        var instructions = m_instructions;
        var cullBounds = m_cullBounds;
        var cullIndex = (reaches.IsEmpty ? cullBounds.Length : 0);
        var test = new PointReachTest(point: world);
        var seeded = FixedGradient3.Seed(point: world);
        var result = (FixedGradient)FarDistance;
        var local = seeded;
        var distanceScale = FixedQ4816.One;
        var powerWork = new PowerWork();
        Span<FixedGradient> scopes = stackalloc FixedGradient[SdfProgramBuilder.MaxFieldScopeDepth];
        var scopeDepth = 0;

        distance = result;
        DualMissed = false;

        for (var range = 0; (range < tape.Length); range += 2) {
            for (var index = tape[range]; (index < tape[(range + 1)]); index++) {
                if ((cullIndex < cullBounds.Length) && (cullBounds[cullIndex].First == index)) {
                    var bound = cullIndex++;

                    if (InstanceClears(bound: in cullBounds[bound], cheapReach: reaches[bound], test: in test, threshold: result.Value)) {
                        index = (cullBounds[bound].End - 1);
                        continue;
                    }
                }

                ref readonly var instruction = ref instructions[index];

                switch (instruction.PointAction) {
                    case SdfPointAction.ResetPoint: {
                            local = seeded;
                            distanceScale = FixedQ4816.One;
                            break;
                        }
                    case SdfPointAction.Translate:
                    case SdfPointAction.Rotate:
                    case SdfPointAction.Fold: {
                            local = MapPoint(instruction: in instruction, p: local);
                            break;
                        }
                    case SdfPointAction.Scale: {
                            local = MapPoint(instruction: in instruction, p: local);
                            distanceScale = ScaleDistance(distanceScale: distanceScale, instruction: in instruction);
                            break;
                        }
                    case SdfPointAction.EditField: {
                            result = EditField(distance: result, instruction: in instruction, p: local);
                            break;
                        }
                    case SdfPointAction.WeightedRelief: {
                            // A masked relief's weight varies along the surface and has no dual yet.
                            if (instruction.ReliefMasks is not null) { return false; }
                            result = NoiseDisplaceField(distance: result, instruction: in instruction, p: local, weight: FixedQ4816.One);
                            break;
                        }
                    case SdfPointAction.Identity: {
                            break;
                        }
                    case SdfPointAction.PushField: {
                            scopes[scopeDepth++] = result;
                            result = FarDistance;
                            break;
                        }
                    case SdfPointAction.PopField: {
                            result = BlendShape(
                                blend: instruction.Blend,
                                candidate: ((instruction.ScopeScale > 0L) ? ScaleScope(candidate: result, scale: instruction.ScopeScale) : result),
                                current: scopes[--scopeDepth],
                                operand: PopOperand(instruction: in instruction),
                                smoothRadius: instruction.Data1X
                            );
                            break;
                        }
                    case SdfPointAction.ShapeBlend: {
                            if (instruction.Detail) {
                                break;
                            }

                            var candidate = FixedGradients.Multiply(a: EvaluateShape(instruction: in instruction, p: local, powerWork: ref powerWork), b: distanceScale);

                            if (instruction.ScopeScale > 0L) { candidate = ScaleScope(candidate: candidate, scale: instruction.ScopeScale); }
                            // A union is the point walk's strict less-than: an equal candidate leaves the running answer.
                            result = ((instruction.Blend == ((uint)SdfBlendOp.Union))
                                ? ((candidate.Value < result.Value) ? candidate : result)
                                : BlendShape(blend: instruction.Blend, candidate: candidate, current: result, operand: FixedQ4816.Zero, smoothRadius: instruction.Data1X));
                            break;
                        }
                    default: {
                            return false;
                        }
                }
                if (DualMissed) { return false; }
            }
        }

        powerWork.Publish();
        distance = result;

        return true;
    }

    // The field's gradient at a point from one forward-mode walk: of the point's region's tape where the evaluator records
    // one, else of the whole stream under the instance cull; false where the point leaves the frame or the walk meets what
    // the reader cannot read.
    private bool TryWalkedGradient(FixedPosition position, out FixedVector3 gradient) {
        gradient = FixedVector3.Zero;

        if (!TryWorldPoint(position: position, worldPosition: out var world)) {
            return false;
        }

        FixedGradient distance;

        if ((m_regions is not null) && TryRegionTape(point: world, tape: out var tape)) {
            if (!TryWalkGradient(distance: out distance, reaches: [], tape: tape, world: world)) { return false; }
        } else {
            _ = FillPointReaches(margin: 0L, reaches: out var reaches, worldPosition: world);

            if (!TryWalkGradient(distance: out distance, reaches: reaches, tape: [0, m_instructions.Length], world: world)) { return false; }
        }
        SdfQueryWork.ProcessWork.Count(kind: SdfQueryWork.GradientWalks);
        gradient = distance.Gradient.Normalize();
        return true;
    }
}
