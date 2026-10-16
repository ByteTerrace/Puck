namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // The analysis keeps (gain, sag) per accumulator: the accumulated field is at least gain * d - sag, where d is the
    // distance from the query point to the instance's packed influence sphere's surface (d >= 0). An empty accumulator
    // holds the far sentinel and constrains nothing.
    private const float UnconstrainedGain = float.PositiveInfinity;
    // The relative evaluation error a gauge's computed norm is allowed before its certificate stops holding. The
    // influence contract already requires the evaluated zero set to stay inside the reach the stamper charges.
    private const float GaugeEvaluationMargin = (1f - (1f / 1024f));
    // The smooth blends' radius floor, SDF_SMOOTH_RADIUS_MIN in field/sdf-program.hlsli.
    private const float SmoothRadiusMin = 0.0001f;

    /// <summary>The part entry's distance-gain bits, below <see cref="PartDistanceGainShift"/>: <c>k</c> encodes
    /// <c>k * PartDistanceGainUnit</c>, and the full mask encodes exactly 1.</summary>
    public const uint PartDistanceGainMask = 0x3fffu;
    /// <summary>The part entry's <c>.z</c> flag naming an instance the shadow and ambient-occlusion walks read as
    /// bounded (<see cref="InstanceBoundsSecondaryField"/>).</summary>
    public const uint PartBoundedSecondaryFlag = 0x40000000u;
    /// <summary>The part entry's distance-gain field's bit position in its <c>.z</c> lane.</summary>
    public const int PartDistanceGainShift = 16;
    /// <summary>The value of one step of the packed distance gain.</summary>
    public const float PartDistanceGainUnit = (1f / PartDistanceGainMask);

    /// <summary>Encodes a distance gain into the part entry's field, rounding down: the decoded value never exceeds
    /// <paramref name="gain"/>.</summary>
    /// <param name="gain">The certified gain, 0 when none.</param>
    /// <returns>The field, unshifted.</returns>
    public static uint EncodePartDistanceGain(float gain) {
        if (gain >= 1f) { return PartDistanceGainMask; }
        if (!(gain > 0f)) { return 0u; }
        // One step below the floor keeps the decoding's rounded product at or under the certified gain.
        return ((uint)Math.Clamp(value: (((int)MathF.Floor(x: (gain * PartDistanceGainMask))) - 1), max: (((int)PartDistanceGainMask) - 1), min: 0));
    }
    /// <summary>Decodes a part entry's distance gain the way the kernels do (<c>sdfPartDistanceGain</c>).</summary>
    /// <param name="entryCountLane">The part entry's <c>.z</c> lane.</param>
    /// <returns>The gain, 0 when the instance is never rejected whole.</returns>
    public static float DecodePartDistanceGain(uint entryCountLane) {
        var field = (entryCountLane >> PartDistanceGainShift) & PartDistanceGainMask;

        return ((field == PartDistanceGainMask) ? 1f : (field * PartDistanceGainUnit));
    }
    /// <summary>Gets an instance's distance gain: the largest factor <c>g</c> in (0, 1] for which the instance's field,
    /// in every walk (camera, hit-only shading, shadow and ambient occlusion), is at least <c>g</c> times the distance
    /// from the query point to its packed influence sphere, wherever that point lies outside the sphere. Zero means no
    /// such factor is certified and the instance is never rejected whole.</summary>
    /// <param name="index">The instance index.</param>
    /// <returns>1 for a field of exact primitives, similarity transforms and the union, subtraction and intersection
    /// families; less for an underestimating gauge (<see cref="SdfShapeType.Superellipsoid"/>) or a non-uniform
    /// <see cref="SdfOp.Scale"/>, whose fields grow more slowly than distance; 0 for an approximate primitive, a
    /// non-similarity domain, a field modifier, an unknown composition, or a clipping operand only some walks
    /// apply.</returns>
    /// <remarks>Each certified primitive's field is at least its gain times the distance to its own solid, which the
    /// influence sphere contains. A union keeps the smaller gain, a subtraction keeps its subject's, and an
    /// intersection keeps the smaller of both operands' gains, since the composed bound may derive from either
    /// operand. A smooth union lowers the field by at most its radius; the packed sphere is widened until that sag is
    /// absorbed at the instance's gain. Scope rescales multiply the gains of the scope's operands.</remarks>
    public float InstanceDistanceGain(int index) => m_instanceDistanceBounds[index].Gain;
    /// <summary>Gets whether the shadow and ambient-occlusion walks read an instance's field as at least the distance
    /// from the query point to its packed influence sphere, max(field, |p - c| - r), and so reject it whole at a unit
    /// gain. The camera, surface, views and indirect walks read the field itself.</summary>
    /// <param name="index">The instance index.</param>
    /// <returns><see langword="true"/> when the program's root is a hard union of independent operands
    /// (<see cref="InstancesComposable"/>) and the instance is live, maskable, and drops no clipping operand in
    /// those walks, so the surface they see lies inside the sphere; <see langword="false"/> otherwise.</returns>
    /// <remarks>The sphere contains the instance's surface and its blends' influence, so outside it the distance to the
    /// sphere is at most the true distance to the surface, and inside it the reading is unchanged wherever the field is
    /// positive: the maximum keeps every surface and interior, never exceeds the true distance where the field does not,
    /// and only lifts an underestimating gauge, scale, warp or field op toward that distance away from the body. An
    /// operand only those walks drop (a detail or non-secondary shape) can widen the surface they see only through the
    /// intersection family, a xor or a morph, so such an instance, and one with a lane erosion that can skip a clipper,
    /// keeps its own field. Under any other root composition an instance's field feeds later operations of the shared
    /// accumulator, so none is bounded.</remarks>
    public bool InstanceBoundsSecondaryField(int index) {
        var instance = m_instances[index];

        if (!InstancesComposable || !instance.Active || IsUnmaskable(instance: instance) ||
            !float.IsFinite(f: m_instanceBinning[index].Radius) || (m_instanceBinning[index].Radius < 0f)) {
            return false;
        }
        for (var cursor = instance.First; (cursor < instance.End); cursor++) {
            var instruction = m_instructions[cursor];

            if (instruction.Op == SdfOp.LaneErode) { return false; }
            if ((instruction.Op == SdfOp.ShapeBlend) && (instruction.Detail || !instruction.Secondary) &&
                (((SdfBlendOp)instruction.Blend) is SdfBlendOp.Intersection or SdfBlendOp.SmoothIntersection
                    or SdfBlendOp.ChamferIntersection or SdfBlendOp.Xor or SdfBlendOp.Morph)) {
                return false;
            }
        }
        return true;
    }

    // The soft-blend coupling halo the packed radius adds, widened to cover the smooth-union sag at the instance's
    // distance gain: past the bound the field is at least gain * (distance to the geometry) - sag, so a sag of S needs
    // S / gain of clearance there.
    private float SmoothHalo(int index) {
        var instance = m_instances[index];
        var bound = m_instanceDistanceBounds[index];
        var halo = MaxSmoothBlendRadius(first: instance.First, end: instance.End);

        return (((bound.Gain > 0f) && (bound.Sag > (bound.Gain * halo))) ? (bound.Sag / bound.Gain) : halo);
    }
    private DistanceBound[] AnalyzeInstanceDistanceBounds() {
        var result = new DistanceBound[m_instances.Length];

        for (var index = 0; (index < m_instances.Length); index++) {
            result[index] = AnalyzeInstanceDistanceBound(instance: m_instances[index]);
        }
        return result;
    }
    private DistanceBound AnalyzeInstanceDistanceBound(SdfInstanceRange instance) {
        if (!instance.Active || IsUnmaskable(instance: instance)) { return default; }
        Span<DistanceBound> scopes = stackalloc DistanceBound[(SdfProgramBuilder.MaxFieldScopeDepth + 1)];
        var depth = 0;
        var chain = 1f;

        scopes[0] = new DistanceBound(Gain: UnconstrainedGain, Sag: 0f);
        for (var cursor = instance.First; (cursor < instance.End); cursor++) {
            var instruction = m_instructions[cursor];

            switch (instruction.Op) {
                case SdfOp.ResetPoint:
                    chain = 1f;
                    break;
                case SdfOp.Translate:
                case SdfOp.Rotate:
                case SdfOp.TransformDynamic:
                    break;
                case SdfOp.Scale:
                    // The candidate is min(s) f(S^-1 q), and S^-1 shortens no distance by more than 1/max(s), so the
                    // candidate is at least min(s)/max(s) times f's gain times the distance to the scaled solid.
                    var scale = instruction.Data0;
                    var largest = MathF.Max(x: scale.X, y: MathF.Max(x: scale.Y, y: scale.Z));

                    if (!(scale.X > 0f) || !(scale.Y > 0f) || !(scale.Z > 0f) || !(scale.W > 0f) || !float.IsFinite(f: largest)) {
                        return default;
                    }
                    chain *= ((scale.W == largest) ? 1f : (scale.W / largest));
                    break;
                case SdfOp.PushField:
                    scopes[++depth] = new DistanceBound(Gain: UnconstrainedGain, Sag: 0f);
                    break;
                case SdfOp.ShapeBlend:
                    var shapeGain = ShapeDistanceGain(instruction: instruction, instructionIndex: cursor);

                    if (!TryCompose(
                        accumulator: ref scopes[depth],
                        blend: ((SdfBlendOp)instruction.Blend),
                        candidate: new DistanceBound(Gain: (shapeGain * chain), Sag: 0f),
                        optional: (instruction.Detail || !instruction.Secondary),
                        smoothRadius: instruction.Data1.X
                    )) { return default; }
                    break;
                case SdfOp.PopField:
                    var child = scopes[depth--];
                    var candidateScale = ((instruction.Data1.Y > 0f) ? instruction.Data1.Y : 1f);

                    if (!TryCompose(
                        accumulator: ref scopes[depth],
                        blend: ((SdfBlendOp)instruction.Blend),
                        candidate: new DistanceBound(Gain: (child.Gain * candidateScale), Sag: (child.Sag * candidateScale)),
                        optional: false,
                        smoothRadius: instruction.Data1.X
                    )) { return default; }
                    break;
                default:
                    return default;
            }
        }

        var gain = scopes[0].Gain;

        // An instance with no union operand holds the far sentinel through its scope rescales, which the inverse
        // rescale already bounds; every gain is capped by it so the far guard at the query site stays conservative.
        gain = MathF.Min(x: MathF.Min(x: gain, y: 1f), y: (1f / FieldScopeExtent(first: instance.First, end: instance.End).Rescale));
        return (((gain > 0f) && float.IsFinite(f: gain))
            ? new DistanceBound(Gain: gain, Sag: scopes[0].Sag)
            : default);
    }
    private static bool TryCompose(ref DistanceBound accumulator, SdfBlendOp blend, DistanceBound candidate, bool optional, float smoothRadius) {
        switch (blend) {
            case SdfBlendOp.Union:
                accumulator = new DistanceBound(
                    Gain: MathF.Min(x: accumulator.Gain, y: candidate.Gain),
                    Sag: MathF.Max(x: accumulator.Sag, y: candidate.Sag)
                );
                return true;
            case SdfBlendOp.SmoothUnion:
                // blendSmoothUnion is nondecreasing in both operands and at least min(a, b) - k/4. With one operand at
                // least M and the other at least M - S, it is at least M - max(S, k); with both sagging, at least
                // M - max(S) - k/4. The kernel floors the radius at SDF_SMOOTH_RADIUS_MIN.
                var radius = MathF.Max(x: MathF.Abs(x: smoothRadius), y: SmoothRadiusMin);
                var sag = (((accumulator.Sag == 0f) || (candidate.Sag == 0f))
                    ? MathF.Max(x: MathF.Max(x: accumulator.Sag, y: candidate.Sag), y: radius)
                    : (MathF.Max(x: accumulator.Sag, y: candidate.Sag) + (0.25f * radius)));

                accumulator = new DistanceBound(Gain: MathF.Min(x: accumulator.Gain, y: candidate.Gain), Sag: sag);
                return true;
            case SdfBlendOp.Subtraction:
            case SdfBlendOp.SmoothSubtraction:
                // max(a, -b) and its smooth form never fall below the subject.
                return true;
            case SdfBlendOp.Intersection:
            case SdfBlendOp.SmoothIntersection:
                // The composed bound is the smaller operand's, so the gain must hold for either. A walk that drops an
                // optional clipper leaves the subject, whose solid that bound need not contain.
                if (optional) { return false; }
                accumulator = new DistanceBound(
                    Gain: MathF.Min(x: accumulator.Gain, y: candidate.Gain),
                    Sag: MathF.Max(x: accumulator.Sag, y: candidate.Sag)
                );
                return true;
            default:
                return false;
        }
    }
    private float ShapeDistanceGain(SdfInstruction instruction, int instructionIndex) {
        switch ((SdfShapeType)instruction.Shape) {
            case SdfShapeType.Superellipsoid:
                return SuperellipsoidDistanceGain(instruction: instruction);
            // Their fields are bounded against their own spheres, not exact distances to their solids, and are capped
            // at the far sentinel in local units.
            case SdfShapeType.Sweep:
            case SdfShapeType.Path:
                return 0f;
            default:
                return (TryGetLocalBound(
                    center: out _,
                    convexPolygonProfiles: m_convexPolygonProfiles,
                    instruction: instruction,
                    instructionIndex: instructionIndex,
                    radius: out _,
                    sweepCurves: m_sweepCurves
                ) ? 1f : 0f);
        }
    }
    // The gauge (sdfSuperellipsoid in field/sdf-shapes.hlsli) is minR (N(q) - 1) with q = |p| * inverseRadii and N the
    // exponent's norm, homogeneous of degree one. Outside the solid, p / N(q) lies on its surface, so the distance to
    // the solid is at most |p| (1 - 1/N) and the gauge is at least minR (N / |p|) times that distance.
    // N(q) >= c |q| >= c min(inverseRadii) |p|, where c is 1 at e = 2 and 3^(1/e - 1/2) past it (the Euclidean norm
    // reaches the e-norm's at the cube diagonal).
    private static float SuperellipsoidDistanceGain(SdfInstruction instruction) {
        var radii = instruction.Data0;
        var exponent = radii.W;
        var smallest = MathF.Min(x: radii.X, y: MathF.Min(x: radii.Y, y: radii.Z));
        var inverse = MathF.Min(x: instruction.Data1.Y, y: MathF.Min(x: instruction.Data1.Z, y: instruction.Data1.W));

        if (!(exponent >= SdfProgramBuilder.MinSuperellipsoidExponent) || !(exponent <= SdfProgramBuilder.MaxSuperellipsoidExponent) ||
            !(smallest > 0f) || !(inverse > 0f) || !float.IsFinite(f: inverse)) {
            return 0f;
        }

        var norm = ((exponent == 2f) ? 1f : MathF.Pow(x: 3f, y: ((1f / exponent) - 0.5f)));
        var gain = (((norm * smallest) * inverse) * GaugeEvaluationMargin);

        return (((gain > 0f) && float.IsFinite(f: gain)) ? MathF.Min(x: gain, y: 1f) : 0f);
    }

    private readonly record struct DistanceBound(float Gain, float Sag);
}
