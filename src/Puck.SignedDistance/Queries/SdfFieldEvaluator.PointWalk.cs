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

        // Outside the frame some step of the evaluation can leave the carrier, so the position is refused, never wrapped.
        if (
            !m_hasShape ||
            !position.TryDelta(
            delta: out var worldPosition,
            origin: FixedPosition.Zero
        ) ||
            !IsInFrame(point: worldPosition)
        ) {
            return false;
        }

        var cullBounds = m_cullBounds;
        var reaches = (((PointReachScratch is { } scratch) && (scratch.Length >= cullBounds.Length))
            ? scratch
            : (PointReachScratch = new FixedQ4816[cullBounds.Length]));
        var primer = PointPrimer.None;
        var walked = 0L;

        for (var bound = 0; (bound < cullBounds.Length); bound++) {
            reaches[bound] = FixedQ4816.FromRawBits(value: cullBounds[bound].Reach.CheapPointReach(point: worldPosition));

            if (m_primeable && ((primer.Candidate < 0) || (reaches[bound] < reaches[primer.Candidate]))) {
                primer.Candidate = bound;
            }
        }

        var (resultDistance, resultMaterial) = WalkPoints(
            end: m_instructions.Length,
            first: 0,
            primer: ref primer,
            reaches: reaches.AsSpan(start: 0, length: cullBounds.Length),
            walked: ref walked,
            worldPosition: worldPosition
        );

        distance = resultDistance;
        material = resultMaterial;
        instructionsWalked = ((int)walked);
        PointWork.Count(kind: PointEvaluations);
        PointWork.Add(amount: walked, kind: PointInstructions);

        return true;
    }

    // Walks [first, end) at one point from the far seed and answers the distance and material. With reaches, it tests
    // each cullable instance as it meets it: an instance whose reach clears the running answer, or exceeds the primer's
    // answer, is skipped. The first instance the running answer cannot skip first walks the nearest instance on its own,
    // the primer, whose answer then joins where that instance stands in the stream. Without reaches it walks every
    // instance in the range, as the primer's own walk does.
    private (FixedQ4816 Distance, int Material) WalkPoints(int first, int end, FixedVector3 worldPosition, ReadOnlySpan<FixedQ4816> reaches, ref PointPrimer primer, ref long walked) {
        var resultDistance = FarDistance;
        var resultMaterial = 0;
        var visited = 0L;
        var instructions = m_instructions;
        var cullBounds = m_cullBounds;
        var segmentSlots = m_segmentSlots;
        var segmentBounds = m_segmentBounds;
        var localPosition = worldPosition;
        var distanceScale = FixedQ4816.One;
        // The render reading's chain state beside the point: a fold's material stride (mapCore's parityMaterialDelta)
        // and a LaneErode's effect on the next shape (laneErodeSkipShape, laneErodeAmount), each cleared by ResetPoint.
        var materialDelta = 0;
        var laneErodeSkip = false;
        var laneErodeAmount = FixedQ4816.Zero;
        Span<(FixedQ4816 Distance, int Material)> scopes = stackalloc (FixedQ4816, int)[SdfProgramBuilder.MaxFieldScopeDepth];
        var scopeDepth = 0;
        var cullIndex = (reaches.IsEmpty ? cullBounds.Length : 0);

        for (var index = first; (index < end); index++) {
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

                if (InstanceClears(bound: in cullBounds[bound], cheapReach: reaches[bound], threshold: primer.Threshold(resultDistance: resultDistance), worldPosition: worldPosition)) {
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
                        reaches: [],
                        walked: ref visited,
                        worldPosition: worldPosition
                    );

                    if (InstanceClears(bound: in cullBounds[bound], cheapReach: reaches[bound], threshold: primer.Threshold(resultDistance: resultDistance), worldPosition: worldPosition)) {
                        index = (cullBounds[bound].End - 1);

                        continue;
                    }
                }

                // Walked in order, this instance's answer joins the running one, which then bounds what a primer would.
                if (bound == primer.Candidate) {
                    primer.Candidate = -1;
                }
            }

            ref readonly var instruction = ref instructions[index];

            ++visited;
            switch (instruction.Op) {
                case SdfOp.ResetPoint: {
                        localPosition = worldPosition;
                        distanceScale = FixedQ4816.One;
                        materialDelta = 0;
                        laneErodeSkip = false;
                        laneErodeAmount = FixedQ4816.Zero;

                        var segment = segmentSlots[index];

                        if ((segment >= 0) && segmentBounds[segment].Reach.PointClears(point: worldPosition, threshold: SegmentThreshold(above: resultDistance, below: resultDistance, bound: in segmentBounds[segment]))) {
                            index = (segmentBounds[segment].End - 1);
                        }

                        break;
                    }
                case SdfOp.Translate: {
                        localPosition = new FixedVector3(
                            X: FixedQ4816.FromRawBits(value: unchecked((localPosition.X.Value - instruction.Data0X.Value))),
                            Y: FixedQ4816.FromRawBits(value: unchecked((localPosition.Y.Value - instruction.Data0Y.Value))),
                            Z: FixedQ4816.FromRawBits(value: unchecked((localPosition.Z.Value - instruction.Data0Z.Value)))
                        );
                        break;
                    }
                case SdfOp.Rotate: {
                        // The identity, either sign of it, rotates through the fused kernel to the point itself: t is
                        // ±w·v rounded from an exact multiple of one, and d is zero.
                        if (IsIdentityRotation(instruction: in instruction)) {
                            break;
                        }

                        localPosition = RotateByInverseQuaternion(
                            instruction: instruction,
                            p: localPosition
                        );
                        break;
                    }
                case SdfOp.Scale: {
                        // A unit scale divides and multiplies by one, which is exact: skipping it changes no bit.
                        if (IsUnitScale(instruction: in instruction)) {
                            break;
                        }

                        localPosition = FixedVector3.Divide(
                            left: localPosition,
                            right: Vector(instruction: instruction)
                        );
                        distanceScale *= instruction.Data0W;
                        break;
                    }
                case SdfOp.Repeat:
                case SdfOp.RepeatLimited:
                case SdfOp.SymmetryPlane:
                case SdfOp.Elongate: {
                        localPosition = FoldPoint(instruction: in instruction, localPosition: localPosition);
                        break;
                    }
                case SdfOp.AxialProfile:
                case SdfOp.RotatePlane: {
                        localPosition = WarpPoint(distanceScale: ref distanceScale, instruction: in instruction, localPosition: localPosition);
                        break;
                    }
                case SdfOp.Shear:
                case SdfOp.GaussianPush:
                case SdfOp.DomainWarp:
                case SdfOp.RepeatPolar:
                case SdfOp.WallpaperFold:
                case SdfOp.LogSphere:
                case SdfOp.CellJitter: {
                        localPosition = RenderWarpPoint(distanceScale: ref distanceScale, instruction: in instruction, localPosition: localPosition, materialDelta: ref materialDelta);
                        break;
                    }
                case SdfOp.LaneErode: {
                        var amount = LaneErodeAmount(instruction: in instruction, localPosition: localPosition);

                        laneErodeSkip = (amount is null);
                        laneErodeAmount = (amount ?? FixedQ4816.Zero);
                        break;
                    }
                case SdfOp.Onion:
                case SdfOp.CellDisplace:
                case SdfOp.Dilate: {
                        resultDistance = EditField(instruction: in instruction, localPosition: localPosition, resultDistance: resultDistance);
                        break;
                    }
                case SdfOp.Displace:
                case SdfOp.NoiseDisplace: {
                        resultDistance = RenderEditField(instruction: in instruction, localPosition: localPosition, resultDistance: resultDistance);
                        break;
                    }
                case SdfOp.PushField: {
                        scopes[scopeDepth++] = (resultDistance, resultMaterial);
                        resultDistance = FarDistance;
                        resultMaterial = 0;
                        break;
                    }
                case SdfOp.PopField: {
                        var (savedFieldDistance, savedFieldMaterial) = scopes[--scopeDepth];

                        (resultDistance, resultMaterial) = PopScope(
                            candidateDistance: resultDistance,
                            candidateMaterial: resultMaterial,
                            instruction: in instruction,
                            savedFieldDistance: savedFieldDistance,
                            savedFieldMaterial: savedFieldMaterial
                        );
                        break;
                    }
                case SdfOp.ShapeBlend: {
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
                            p: localPosition
                        ) * distanceScale) + erosion);
                        // A fold's stride recolours the shape below the screen sentinels, as mapCore applies it.
                        var candidateMaterial = (((materialDelta != 0) && (instruction.Material < SdfProgramBuilder.ScreenMaterialId))
                            ? (instruction.Material + materialDelta)
                            : instruction.Material);

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
                default: {
                        ThrowUnvalidated(op: instruction.Op);
                        break;
                    }
            }
        }

        walked += visited;

        return (resultDistance, resultMaterial);
    }
    // The point folds no shipped contact field reaches, kept out of WalkPoints so its hot cases stay small enough to
    // inline their helpers.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FixedVector3 FoldPoint(in CompiledInstruction instruction, FixedVector3 localPosition) {
        switch (instruction.Op) {
            case SdfOp.Repeat: {
                    var spacing = Vector(instruction: instruction);
                    var inverseSpacing = Vector1(instruction: instruction);

                    return (localPosition - FixedVector3.Multiply(
                        left: spacing,
                        right: FixedVector3.Round(value: FixedVector3.Multiply(
                            left: localPosition,
                            right: inverseSpacing
                        ))
                    ));
                }
            case SdfOp.RepeatLimited: {
                    var spacing = Vector(instruction: instruction);
                    var limit = Vector1(instruction: instruction);
                    var rounded = FixedVector3.Round(value: FixedVector3.Divide(
                        left: localPosition,
                        right: spacing
                    ));

                    return (localPosition - FixedVector3.Multiply(
                        left: spacing,
                        right: FixedVector3.Clamp(
                            maximum: limit,
                            minimum: -limit,
                            value: rounded
                        )
                    ));
                }
            case SdfOp.SymmetryPlane: {
                    var normal = Vector(instruction: instruction);
                    var t = (FixedVector3.Dot(
                        left: localPosition,
                        right: normal
                    ) + instruction.Data0W);
                    var twiceMin = (FixedQ4816.Min(
                        x: t,
                        y: FixedQ4816.Zero
                    ) * Two);

                    return (localPosition - (normal * twiceMin));
                }
            default: {
                    var extents = Vector(instruction: instruction);

                    return (localPosition - FixedVector3.Clamp(
                        maximum: extents,
                        minimum: -extents,
                        value: localPosition
                    ));
                }
        }
    }
    // The render reading's warps (SdfFieldReading.Render), kept out of WalkPoints with the folds. KEEP IN SYNC with
    // mapCore's SDF_OP_AXIAL_PROFILE and SDF_OP_ROTATE_PLANE (field/sdf-map.hlsli). AxialProfile: t = saturate((top - p_axis)
    // / span) (Data0.z, Data0.w = 1/span), s = max(Data1.y + amount·t + bulge·sin(πt), FlareMinScale), the other two axes
    // divided by s and the distance scale times the host-baked correction Data1.x. RotatePlane: the plane's (u, v) turned
    // by rate·(p_driver - offset) (Data0.x, Data0.y), u and v as mapCore picks them from the plane.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FixedVector3 WarpPoint(in CompiledInstruction instruction, FixedVector3 localPosition, ref FixedQ4816 distanceScale) {
        if (instruction.Op == SdfOp.AxialProfile) {
            var axis = (int)instruction.Shape;
            var t = FixedQ4816.Clamp(maximum: FixedQ4816.One, minimum: FixedQ4816.Zero, value: ((instruction.Data0Z - Component(axis: axis, point: localPosition)) * instruction.Data0W));
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
        var (sin, cos) = FixedQ4816.SinCos(angle: (instruction.Data0X * (Component(axis: (int)instruction.Blend, point: localPosition) - instruction.Data0Y)));
        var pu = Component(axis: u, point: localPosition);
        var pv = Component(axis: v, point: localPosition);

        return WithComponents(point: localPosition, u: u, uValue: ((cos * pu) + (sin * pv)), v: v, vValue: ((cos * pv) - (sin * pu)));
    }
    // SDF_FLARE_MIN_SCALE in fixed point.
    private static readonly FixedQ4816 FlareMinScale = FixedQ4816.FromDouble(value: SdfProgramBuilder.FlareMinScale);
    // The axes a RotatePlane turns, as mapCore picks them: u is Y in the YZ plane and X otherwise, v is Y in the XY plane
    // and Z otherwise.
    private static (int U, int V) PlaneAxes(uint plane) => (((plane == ((uint)Puck.SignedDistance.SdfPlane.YZ)) ? 1 : 0), ((plane == ((uint)Puck.SignedDistance.SdfPlane.XY)) ? 1 : 2));
    private static FixedQ4816 Component(FixedVector3 point, int axis) => axis switch {
        0 => point.X,
        1 => point.Y,
        _ => point.Z,
    };
    private static FixedVector3 WithComponents(FixedVector3 point, int u, FixedQ4816 uValue, int v, FixedQ4816 vValue) => new(
        X: ((u == 0) ? uValue : ((v == 0) ? vValue : point.X)),
        Y: ((u == 1) ? uValue : ((v == 1) ? vValue : point.Y)),
        Z: ((u == 2) ? uValue : ((v == 2) ? vValue : point.Z))
    );
    // The field edits (Onion, CellDisplace, Dilate), kept out of WalkPoints with the folds.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FixedQ4816 EditField(in CompiledInstruction instruction, FixedVector3 localPosition, FixedQ4816 resultDistance) => instruction.Op switch {
        SdfOp.Onion => (FixedQ4816.Abs(value: resultDistance) - instruction.Data0X),
        SdfOp.CellDisplace => (resultDistance + (instruction.Data0Y * (SampleCells(
            (localPosition * instruction.Data0X),
            instruction.Shape,
            ((SdfCellMode)instruction.Blend),
            instruction.Data0Z
        ) - Half))),
        _ => (resultDistance - instruction.Data0X),
    };
    // A scope's pop: its candidate, scaled by the scope's baked 1/L, joins the field the scope saved. Data1.y is that
    // candidate scale on every pop, and a stairs pop carries its step count in Data1.z, as mapCore's pop and
    // AnalyzeLipschitz read them.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (FixedQ4816 Distance, int Material) PopScope(in CompiledInstruction instruction, FixedQ4816 savedFieldDistance, int savedFieldMaterial, FixedQ4816 candidateDistance, int candidateMaterial) {
        var isStairs = (instruction.Blend is ((uint)SdfBlendOp.StairsUnion) or ((uint)SdfBlendOp.StairsSubtraction));
        var candidateScale = instruction.Data1Y;

        if (candidateScale > FixedQ4816.Zero) {
            candidateDistance *= candidateScale;
        }

        if (instruction.Blend == ((uint)SdfBlendOp.Morph)) {
            var laneVal = FixedQ4816.Zero;
            var from = instruction.Data0Y;
            var to = instruction.Data0Z;
            var t = FixedQ4816.Clamp(
                value: ((laneVal - from) / (to - from)),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One
            );

            return ((((FixedQ4816.One - t) * savedFieldDistance) + (t * candidateDistance)), ((t >= Half)
                ? candidateMaterial
                : savedFieldMaterial
            ));
        }

        if (isStairs) {
            var r = instruction.Data1X;
            var n = instruction.Data1Z;

            if (
                (n >= FixedQ4816.One) &&
                (r > FixedQ4816.Zero)
            ) {
                var s = (r / n);
                var twoS = (s * Two);
                var isSub = (instruction.Blend == ((uint)SdfBlendOp.StairsSubtraction));
                var a = savedFieldDistance;
                var b = candidateDistance;
                var u = (isSub
                    ? ((-b) - r)
                    : (b - r)
                );
                var arg = ((u - a) + s);
                var m = (arg - (twoS * FixedQ4816.Floor(value: (arg / twoS))));
                var w = (m - s);
                var dStairs = (Half * ((u + a) + FixedQ4816.Abs(value: w)));

                return (isSub
                    ? (FixedQ4816.Max(
                        x: FixedQ4816.Max(
                            x: a,
                            y: -b
                        ),
                        y: -dStairs
                    ), (((-b) > a)
                        ? candidateMaterial
                        : savedFieldMaterial
                    ))
                    : (FixedQ4816.Min(
                        x: FixedQ4816.Min(
                            x: a,
                            y: b
                        ),
                        y: dStairs
                    ), ((b < a)
                        ? candidateMaterial
                        : savedFieldMaterial
                    )));
            }
        }

        return Compose(
            current: savedFieldDistance,
            currentMaterial: savedFieldMaterial,
            candidate: candidateDistance,
            candidateMaterial: candidateMaterial,
            blend: instruction.Blend,
            smooth: instruction.Data1X
        );
    }
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

    // Whether an instance's candidates clear the threshold: its certificate at the largest axis distance first, then at
    // the exact distance, then each lowering member's.
    private bool InstanceClears(in CullBound bound, FixedQ4816 cheapReach, FixedQ4816 threshold, FixedVector3 worldPosition) =>
        ((cheapReach >= threshold) ||
            bound.Reach.PointClears(point: worldPosition, threshold: threshold.Value) ||
            ((bound.MemberCount > 1) && MembersClear(bound: in bound, threshold: threshold.Value, worldPosition: worldPosition)));
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
