using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE POINT INTERPRETER's walk: TryDistance reads the compiled stream at one point, skipping what the cull tables
// (SdfFieldEvaluator.Cull.cs) prove cannot move its answer.
public sealed partial class SdfFieldEvaluator {
    // Per thread, the reach of every cullable instance from the point being walked, filled once per walk.
    [ThreadStatic]
    private static FixedQ4816[]? PointReachScratch;

    /// <inheritdoc/>
    /// <remarks>The evaluated point is the exact world-space displacement from the world origin — <c>position</c>
    /// REBASED against <see cref="FixedPosition.Zero"/>, not its raw <see cref="FixedPosition.Local"/> offset. The
    /// wrapped <see cref="SdfProgram"/> bakes its geometry in world space around that origin, so reading <c>.Local</c>
    /// alone would alias the whole field with the 2^<see cref="FixedPosition.CellSizeLog2"/>-unit cell period and
    /// answer for the wrong copy; <see cref="FixedPosition.FromLocal"/> creates a nonzero cell on its own past half a
    /// cell, so no caller has to opt in to reach that. Rebasing is exact integer arithmetic and is the identity for a
    /// position already in cell <c>(0,0,0)</c>. Returns <see langword="false"/> when the program declares no shape, when
    /// the displacement is outside signed Q48.16 (past ~1.4e14 units from the origin), or when it lies outside
    /// <see cref="Frame"/>, where some step of the evaluation could leave the carrier and wrap.</remarks>
    public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) =>
        TryDistance(
            distance: out distance,
            instructionsWalked: out _,
            material: out material,
            position: position
        );
    /// <summary>Reads the field at a point, as <see cref="TryDistance(FixedPosition, out FixedQ4816, out int)"/> does,
    /// and counts the work: the instructions the walk visited after the cull skipped every instance and chain of shapes
    /// that could not move the answer.</summary>
    /// <param name="position">The world-space point to evaluate.</param>
    /// <param name="distance">The signed distance on success; zero on failure.</param>
    /// <param name="material">The winning material on success; zero on failure.</param>
    /// <param name="instructionsWalked">The instructions the walk visited; zero on failure.</param>
    /// <returns><see langword="false"/> when the program declares no shape or the point leaves the frame.</returns>
    public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material, out int instructionsWalked) {
        distance = FixedQ4816.Zero;
        material = 0;
        instructionsWalked = 0;

        if (!TryWorldPoint(position: position, worldPosition: out var worldPosition)) {
            return false;
        }

        if ((m_regions is not null) && TryRegionTape(point: worldPosition, tape: out var tape)) {
            (distance, material, instructionsWalked) = WalkPoint(candidate: -1, reaches: [], tape: tape, worldPosition: worldPosition);

            return true;
        }

        var candidate = FillPointReaches(margin: 0L, reaches: out var reaches, worldPosition: worldPosition);

        (distance, material, instructionsWalked) = WalkPoint(candidate: candidate, reaches: reaches, worldPosition: worldPosition);

        return true;
    }

    // The world point a position reads at, refused when the program declares no shape or the point leaves the frame:
    // outside the frame some step of the evaluation can leave the carrier, so the position is refused, never wrapped.
    private bool TryWorldPoint(FixedPosition position, out FixedVector3 worldPosition) {
        if (!m_hasShape) {
            worldPosition = FixedVector3.Zero;

            return false;
        }

        return (position.TryDelta(delta: out worldPosition, origin: FixedPosition.Zero) && IsInFrame(point: worldPosition));
    }
    // Fills the thread's reach scratch with each cullable instance's cheap reach from a point, lowered by a margin in raws,
    // and answers the primer's candidate, the instance of least reach (minus one when the program is not primeable).
    // A reach lowered by m raws is still a lower bound at every point whose cheap reach is at most m raws less, which
    // lets one prepass serve every point of a neighbourhood: the cull only ever skips on a reach at or above its
    // threshold, and a smaller reach only skips less, which leaves the walk's answer as it is.
    private int FillPointReaches(FixedVector3 worldPosition, long margin, out ReadOnlySpan<FixedQ4816> reaches) {
        var cullBounds = m_cullBounds;
        var scratch = (((PointReachScratch is { } held) && (held.Length >= cullBounds.Length))
            ? held
            : (PointReachScratch = CreateReachScratch(length: cullBounds.Length, work: PointWork, arrays: PointScratchArraysCreated, bytes: PointScratchPayloadCreated)));
        var candidate = -1;

        for (var bound = 0; (bound < cullBounds.Length); bound++) {
            scratch[bound] = FixedQ4816.FromRawBits(value: (cullBounds[bound].Reach.CheapPointReach(point: worldPosition) - margin));

            if (m_primeable && ((candidate < 0) || (scratch[bound] < scratch[candidate]))) {
                candidate = bound;
            }
        }

        PointWork.Add(kind: (m_marksRegions ? PointMarkedPrepassBounds : PointPrepassBounds), amount: cullBounds.Length);
        reaches = scratch.AsSpan(start: 0, length: cullBounds.Length);

        return candidate;
    }
    // Walks the whole program at one point over reaches FillPointReaches filled, or the tape of the point's region with
    // no reaches (SdfFieldEvaluator.Regions.cs), and counts the evaluation.
    private (FixedQ4816 Distance, int Material, int InstructionsWalked) WalkPoint(FixedVector3 worldPosition, ReadOnlySpan<FixedQ4816> reaches, int candidate, ReadOnlySpan<int> tape = default) {
        var primer = PointPrimer.None;
        var walked = 0L;
        var powerWork = new PowerWork();

        primer.Candidate = candidate;

        var (resultDistance, resultMaterial) = WalkPoints(
            end: m_instructions.Length,
            first: 0,
            primer: ref primer,
            powerWork: ref powerWork,
            reaches: reaches,
            tape: tape,
            walked: ref walked,
            worldPosition: worldPosition
        );

        PointWork.Count(kind: PointEvaluations);
        PointWork.Add(amount: walked, kind: (m_marksRegions ? PointMarkedInstructions : PointInstructions));
        powerWork.Publish();

        return (resultDistance, resultMaterial, ((int)walked));
    }
    // Walks [first, end) at one point from the far seed and answers the distance and material. With reaches, it tests
    // each cullable instance as it meets it: an instance whose reach clears the running answer, or exceeds the primer's
    // answer, is skipped. The first instance the running answer cannot skip first walks the nearest instance on its own,
    // the primer, whose answer then joins where that instance stands in the stream. Without reaches it walks every
    // instance in the range, as the primer's own walk does. With a tape it walks the tape's ranges in stream order in
    // place of [first, end), one accumulator across them all.
    private (FixedQ4816 Distance, int Material) WalkPoints(int first, int end, FixedVector3 worldPosition, ReadOnlySpan<FixedQ4816> reaches, ref PointPrimer primer, ref long walked, ref PowerWork powerWork, InfluenceWalk? influence = null, ReadOnlySpan<int> tape = default) {
        var resultDistance = FarDistance;
        var resultMaterial = 0;
        var visited = 0L;
        var instructions = m_instructions;
        var cullBounds = m_cullBounds;
        var segmentSlots = m_segmentSlots;
        var localPosition = worldPosition;
        var frames = m_rootFrames.Span;
        var root = new PointFrame<FixedVector3, FixedQ4816>();
        var distanceScale = FixedQ4816.One;
        // The render reading's chain state beside the point: a fold's material stride (mapCore's parityMaterialDelta)
        // and a LaneErode's effect on the next shape (laneErodeSkipShape, laneErodeAmount), each cleared by ResetPoint.
        var materialDelta = 0;
        var laneErodeSkip = false;
        var laneErodeAmount = FixedQ4816.Zero;
        Span<(FixedQ4816 Distance, int Material)> scopes = stackalloc (FixedQ4816, int)[SdfProgramBuilder.MaxFieldScopeDepth];
        var scopeDepth = 0;
        var cullIndex = (reaches.IsEmpty ? cullBounds.Length : 0);
        var test = new PointReachTest(point: worldPosition);
        var tapeAt = 0;

        if (!tape.IsEmpty) {
            (first, end, tapeAt) = (tape[0], tape[1], 2);
        }

        for (var index = first; NextLive(at: ref tapeAt, end: ref end, index: ref index, tape: tape); index++) {
            if (
                (cullIndex < cullBounds.Length) &&
                (cullBounds[cullIndex].First == index)
            ) {
                var bound = cullIndex++;

                if (bound == primer.Bound) {
                    (resultDistance, resultMaterial) = Compose(
                        blend: ((uint)SdfBlendOp.Union),
                        candidate: primer.Distance,
                        candidateMaterial: primer.Material,
                        current: resultDistance,
                        currentMaterial: resultMaterial,
                        smooth: FixedQ4816.Zero
                    );
                    index = (cullBounds[bound].End - 1);

                    continue;
                }

                if (InstanceClears(bound: in cullBounds[bound], cheapReach: reaches[bound], test: in test, threshold: primer.Threshold(resultDistance: resultDistance))) {
                    index = (cullBounds[bound].End - 1);

                    continue;
                }

                if ((primer.Bound < 0) && (primer.Candidate >= 0) && (primer.Candidate != bound)) {
                    var none = PointPrimer.None;

                    primer.Bound = primer.Candidate;
                    (primer.Distance, primer.Material) = WalkPoints(
                        end: cullBounds[primer.Bound].End,
                        first: cullBounds[primer.Bound].First,
                        primer: ref none,
                        powerWork: ref powerWork,
                        reaches: [],
                        walked: ref visited,
                        worldPosition: worldPosition
                    );

                    if (InstanceClears(bound: in cullBounds[bound], cheapReach: reaches[bound], test: in test, threshold: primer.Threshold(resultDistance: resultDistance))) {
                        index = (cullBounds[bound].End - 1);

                        continue;
                    }
                }

                // Walked in order, this instance's answer joins the running one, which then bounds what a primer would.
                if (bound == primer.Candidate) {
                    primer.Candidate = -1;
                }
            }

            root.Capture(index: index, point: localPosition, scale: distanceScale);
            ref readonly var instruction = ref instructions[index];

            ++visited;
            switch (instruction.PointAction) {
                case SdfPointAction.ResetPoint: {
                        localPosition = worldPosition;
                        distanceScale = FixedQ4816.One;
                        materialDelta = 0;
                        laneErodeSkip = false;
                        laneErodeAmount = FixedQ4816.Zero;

                        var segment = segmentSlots[index];

                        if ((influence is null) && (segment >= 0) && (SkippedChainsEnd(above: resultDistance, below: resultDistance, segment: segment, test: in test) is var skipped and >= 0)) {
                            index = (skipped - 1);
                        } else if (!frames.IsEmpty) {
                            root.Reset(frame: frames[index], index: ref index, point: ref localPosition, scale: ref distanceScale);
                        }

                        break;
                    }
                case SdfPointAction.Translate: {
                        localPosition = TranslatePoint(instruction: in instruction, p: localPosition);
                        break;
                    }
                case SdfPointAction.Rotate: {
                        // The identity, either sign of it, rotates through the fused kernel to the point itself: t is
                        // ±w·v rounded from an exact multiple of one, and d is zero.
                        if (IsIdentityRotation(instruction: in instruction)) {
                            break;
                        }

                        localPosition = RotatePoint(instruction: in instruction, p: localPosition);
                        break;
                    }
                case SdfPointAction.Scale: {
                        // A unit scale divides and multiplies by one, which is exact: skipping it changes no bit.
                        if (IsUnitScale(instruction: in instruction)) {
                            break;
                        }

                        localPosition = ScalePoint(instruction: in instruction, p: localPosition);
                        distanceScale = ScaleDistance(distanceScale: distanceScale, instruction: in instruction);
                        break;
                    }
                case SdfPointAction.Fold: {
                        localPosition = MapPoint(instruction: in instruction, p: localPosition);
                        break;
                    }
                case SdfPointAction.Warp: {
                        localPosition = WarpPoint(distanceScale: ref distanceScale, instruction: in instruction, localPosition: localPosition);
                        break;
                    }
                case SdfPointAction.RenderWarp: {
                        localPosition = RenderWarpPoint(distanceScale: ref distanceScale, instruction: in instruction, localPosition: localPosition, materialDelta: ref materialDelta);
                        break;
                    }
                case SdfPointAction.LaneErode: {
                        var amount = LaneErodeAmount(instruction: in instruction, localPosition: localPosition);

                        laneErodeSkip = (amount is null);
                        laneErodeAmount = (amount ?? FixedQ4816.Zero);
                        break;
                    }
                case SdfPointAction.EditField: {
                        resultDistance = EditField(distance: resultDistance, instruction: in instruction, p: localPosition);
                        break;
                    }
                case SdfPointAction.WeightedRelief: {
                        resultDistance = WeightedRelief(instruction: in instruction, localPosition: localPosition, powerWork: ref powerWork, resultDistance: resultDistance);
                        break;
                    }
                case SdfPointAction.Identity: {
                        // Data for the following relief.
                        break;
                    }
                case SdfPointAction.RenderEditField: {
                        resultDistance = RenderEditField(instruction: in instruction, localPosition: localPosition, resultDistance: resultDistance);
                        break;
                    }
                case SdfPointAction.PushField: {
                        influence?.Push();
                        scopes[scopeDepth++] = (resultDistance, resultMaterial);
                        resultDistance = FarDistance;
                        resultMaterial = 0;
                        break;
                    }
                case SdfPointAction.PopField: {
                        var (savedFieldDistance, savedFieldMaterial) = scopes[--scopeDepth];
                        influence?.Pop(candidate: resultDistance, current: savedFieldDistance, instruction: instruction);

                        (resultDistance, resultMaterial) = PopScope(
                            candidateDistance: resultDistance,
                            candidateMaterial: resultMaterial,
                            instruction: in instruction,
                            savedFieldDistance: savedFieldDistance,
                            savedFieldMaterial: savedFieldMaterial
                        );
                        break;
                    }
                case SdfPointAction.ShapeBlend: {
                        var leaf = (influence?.NextLeaf() ?? -1);
                        // A pending LaneErode is consumed first, so it never reaches a later shape: a skipped shape is
                        // absent, and an eroded one's candidate grows by the erosion after its distance scale.
                        var erosion = laneErodeAmount;
                        var skipped = laneErodeSkip;

                        laneErodeSkip = false;
                        laneErodeAmount = FixedQ4816.Zero;

                        // A detail shape (SdfInstruction.Detail) never reaches contact/gravity/collision — this
                        // evaluator has no shade-mode counterpart, so it is simply absent from every field it walks.
                        if (instruction.Detail || skipped) {
                            break;
                        }

                        var candidateDistance = ((EvaluateShape(
                            instruction: instruction,
                            p: localPosition,
                            powerWork: ref powerWork
                        ) * distanceScale) + erosion);
                        if (instruction.ScopeScale > 0L) { candidateDistance = ScaleScope(candidate: candidateDistance, scale: instruction.ScopeScale); }
                        // A fold's stride recolours the shape below the screen sentinels, as mapCore applies it.
                        var candidateMaterial = (((materialDelta != 0) && (instruction.Material < SdfProgramBuilder.ScreenMaterialId))
                            ? (instruction.Material + materialDelta)
                            : instruction.Material);

                        influence?.Leaf(candidate: candidateDistance, current: resultDistance, instruction: instruction, owner: leaf);

                        // A union is Compose's strict less-than and its minimum, taken here without the blend switch.
                        if (instruction.Blend == ((uint)SdfBlendOp.Union)) {
                            if (candidateDistance < resultDistance) {
                                resultDistance = candidateDistance;
                                resultMaterial = candidateMaterial;
                            }

                            break;
                        }

                        (resultDistance, resultMaterial) = Compose(
                            current: resultDistance,
                            currentMaterial: resultMaterial,
                            candidate: candidateDistance,
                            candidateMaterial: candidateMaterial,
                            blend: instruction.Blend,
                            smooth: instruction.Data1X
                        );
                        break;
                    }
                case SdfPointAction.Unsupported: {
                        ThrowUnvalidated(op: instruction.Op);
                        break;
                    }
                default:
                    throw new InvalidOperationException(message: "Invalid Point action.");
            }
        }

        walked += visited;

        return (resultDistance, resultMaterial);
    }
    // The render reading's warps (SdfFieldReading.Render), kept out of WalkPoints with the folds. KEEP IN SYNC with
    // mapCore's SDF_OP_AXIAL_PROFILE and SDF_OP_ROTATE_PLANE (field/sdf-map.hlsli). AxialProfile: t = saturate((top - p_axis)
    // / span) (Data0.z, Data0.w = 1/span), s = max(Data1.y + amount·t + bulge·sin(πt), FlareMinScale), the other two axes
    // divided by s and the distance scale times the host-baked correction Data1.x. RotatePlane: the plane's (u, v) turned
    // by rate·(p_driver - offset) (Data0.x, Data0.y), u and v as mapCore picks them from the plane.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FixedVector3 WarpPoint(in CompiledInstruction instruction, FixedVector3 localPosition, ref FixedQ4816 distanceScale) {
        if (instruction.Op == SdfOp.AxialProfile) {
            var axis = ((int)instruction.Shape);
            var t = FixedQ4816.Clamp(maximum: FixedQ4816.One, minimum: FixedQ4816.Zero, value: ((instruction.Data0Z - localPosition[axis]) * instruction.Data0W));
            // sin(πt) is the sine of t half turns: t's raw Q48.16 bits shifted to a 2^64 fraction of a turn.
            var (sine, _) = FixedQ4816.SinCosTurns(fractionalTurns: (((ulong)t.Value) << 47));
            var scale = FixedQ4816.Max(x: ((instruction.Data1Y + (instruction.Data0X * t)) + (instruction.Data0Y * sine)), y: FlareMinScale);

            distanceScale *= instruction.Data1X;

            return new FixedVector3(
                X: ((axis == 0) ? localPosition.X : (localPosition.X / scale)),
                Y: ((axis == 1) ? localPosition.Y : (localPosition.Y / scale)),
                Z: ((axis == 2) ? localPosition.Z : (localPosition.Z / scale))
            );
        }

        var (u, v) = PlaneAxes(plane: instruction.Shape);
        var (sin, cos) = FixedQ4816.SinCos(angle: (instruction.Data0X * (localPosition[((int)instruction.Blend)] - instruction.Data0Y)));
        var pu = localPosition[u];
        var pv = localPosition[v];

        return localPosition.With(axis: u, value: ((cos * pu) + (sin * pv))).With(axis: v, value: ((cos * pv) - (sin * pu)));
    }

    // SDF_FLARE_MIN_SCALE in fixed point.
    private static readonly FixedQ4816 FlareMinScale = FixedQ4816.FromDouble(value: SdfProgramBuilder.FlareMinScale);

    // The axes a RotatePlane turns, as mapCore picks them: u is Y in the YZ plane and X otherwise, v is Y in the XY plane
    // and Z otherwise.
    private static (int U, int V) PlaneAxes(uint plane) => (((plane == ((uint)Puck.SignedDistance.SdfPlane.YZ)) ? 1 : 0), ((plane == ((uint)Puck.SignedDistance.SdfPlane.XY)) ? 1 : 2));
    // A relief at its masks' weight (MaskProduct), one for an unmasked relief. The shared formula adds nothing, and
    // reads no noise, at a weight of exactly zero.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FixedQ4816 WeightedRelief(in CompiledInstruction instruction, FixedVector3 localPosition, FixedQ4816 resultDistance, ref PowerWork powerWork) =>
        NoiseDisplaceField(
            distance: resultDistance,
            instruction: in instruction,
            p: localPosition,
            weight: ((instruction.ReliefMasks is { } masks) ? MaskProduct(masks: masks, powerWork: ref powerWork, q: localPosition) : FixedQ4816.One)
        );
    // The product of a masked relief's weights at its point q: per mask, the least distance of its regions, each its
    // primitive (EvaluateShape) at u = turn_back(q - origin), read against the band. Once a mask's distance is at or below
    // its start its weight is its inside whatever its remaining regions read, so they are not evaluated; once the product
    // is exactly zero, neither are the remaining masks. The kernels' row walk is sdfMaskProduct (field/sdf-map.hlsli); the band's weight is the shared formula.
    private static FixedQ4816 MaskProduct(ReliefMaskFixed[] masks, FixedVector3 q, ref PowerWork powerWork) {
        var weight = FixedQ4816.One;

        foreach (var mask in masks) {
            var distance = FixedQ4816.MaxValue;

            foreach (var region in mask.Regions) {
                var u = new FixedVector3(
                    X: FixedQ4816.FromRawBits(value: unchecked((q.X.Value - region.Origin.X.Value))),
                    Y: FixedQ4816.FromRawBits(value: unchecked((q.Y.Value - region.Origin.Y.Value))),
                    Z: FixedQ4816.FromRawBits(value: unchecked((q.Z.Value - region.Origin.Z.Value)))
                );

                if (!region.Identity) {
                    u = region.Rotation.RotateInverse(vector: u);
                }

                distance = FixedQ4816.Min(x: distance, y: EvaluateShape(instruction: region.Primitive, p: u, powerWork: ref powerWork));

                if (distance <= mask.Start) { break; }
            }

            weight *= BandWeight(distance: distance, mask: mask);

            if (weight == FixedQ4816.Zero) { break; }
        }

        return weight;
    }
    private static FixedQ4816 BandWeight(ReliefMaskFixed mask, FixedQ4816 distance) =>
        SdfReliefBandWeight(distance: distance, end: mask.End, inside: mask.Inside, outside: mask.Outside, start: mask.Start);
    // A scope's pop: its candidate, scaled by the scope's baked 1/L (Data1.y, read on its finer compiled grid,
    // ScopeScale), joins the field the scope saved, with the pop's own operand (PopOperand).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (FixedQ4816 Distance, int Material) PopScope(in CompiledInstruction instruction, FixedQ4816 savedFieldDistance, int savedFieldMaterial, FixedQ4816 candidateDistance, int candidateMaterial) =>
        Compose(
            blend: instruction.Blend,
            candidate: ((instruction.ScopeScale > 0L) ? ScaleScope(candidate: candidateDistance, scale: instruction.ScopeScale) : candidateDistance),
            candidateMaterial: candidateMaterial,
            current: savedFieldDistance,
            currentMaterial: savedFieldMaterial,
            operand: PopOperand(instruction: in instruction),
            smooth: instruction.Data1X
        );
    [DoesNotReturn]
    private static void ThrowUnvalidated(SdfOp op) =>
        throw new UnreachableException(message: $"The constructor validated every instruction's op is supported; op {op} reached the interpreter unvalidated.");

    // The nearest cullable instance a point walk may evaluate ahead of its place, and its answer once walked.
    private struct PointPrimer {
        public static PointPrimer None => new() { Bound = -1, Candidate = -1, Distance = FarDistance };

        // The instance chosen to walk first, or -1 once the walk no longer needs one.
        public int Candidate;
        // The instance walked, or -1 before it is.
        public int Bound;
        public FixedQ4816 Distance;
        public int Material;

        // The least reach that proves an instance's candidates cannot move the answer: the running answer, or one raw
        // past the primer's when that is less, since only a candidate strictly past it keeps an earlier equal one's
        // material.
        public readonly FixedQ4816 Threshold(FixedQ4816 resultDistance) =>
            (((Bound >= 0) && (Distance < resultDistance)) ? (Distance + FixedQ4816.Epsilon) : resultDistance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentityRotation(in CompiledInstruction instruction) =>
        ((instruction.Data0X == FixedQ4816.Zero) && (instruction.Data0Y == FixedQ4816.Zero) && (instruction.Data0Z == FixedQ4816.Zero) &&
            ((instruction.Data0W == FixedQ4816.One) || (instruction.Data0W == -FixedQ4816.One)));
    // Whether a Scale op divides the point by one and multiplies the distance by one: both exact, so the op is the
    // identity in either walk.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUnitScale(in CompiledInstruction instruction) =>
        ((instruction.Data0X == FixedQ4816.One) && (instruction.Data0Y == FixedQ4816.One) && (instruction.Data0Z == FixedQ4816.One) && (instruction.Data0W == FixedQ4816.One));
}
