using System.Diagnostics;
using Puck.Maths;

namespace Puck.SdfVm.Queries;

// KEEP IN SYNC with mapCore's RIGID op/shape cases in Assets/Shaders/Sdf/sdf-vm.hlsli (the SYNC PAIR this file is
// half of — see the sdf-world skill's sync-pair table). This is a SECOND, INDEPENDENT interpreter of the same
// SdfInstruction stream mapCore walks, in FixedQ4816/FixedVector3 instead of shader float — a deliberate DUAL
// implementation (like SdfProgram's own host-side AnalyzeBounds/AnalyzeLipschitz passes), not a codegen of the
// shader. Touching mapCore's RESET/TRANSLATE/ROTATE/SCALE/REPEAT/REPEAT_LIMITED/SYMMETRY_PLANE/ELONGATE/ONION/
// DILATE/PUSH_FIELD/POP_FIELD/SHAPE cases, or blendShape/evaluateShape's Sphere/Box/ScreenSlab/Torus/Plane/
// RoundCone/Capsule/Cylinder/Ellipsoid/Vesica bodies, means updating this file's mirror in the SAME change (and vice
// versa) — a divergence is silent (both sides compile and run; only the ANSWER differs).
//
// THE EXCLUDED-OPS RULE (asserted once at construction, never per query): this evaluator is WARP-FREE — it rejects
// any program containing an op that needs runtime trigonometry not implemented in fixed point (BendX/BendY/BendZ/
// TwistY/LogSphere/CellJitter/RepeatPolar/Displace/DomainWarp), the one op needing a per-frame dynamic-transform
// buffer this evaluator's signature has no seam for (TransformDynamic — see the constructor's remarks), and
// WallpaperFold, whose 17-group parity-keyed cell logic has no fixed-point implementation. It similarly rejects
// three shapes whose EXACT cores need runtime trig (RegularPolygon/Star: atan2 in sdfStar2D; Ellipse: an analytic
// cubic solve with acos/pow) and one needing texture sampling (Glyph) — every other shape in
// <see cref="SdfShapeType"/> is supported. The constructor throws <see cref="ArgumentException"/> naming the FIRST
// disqualifying instruction's op or shape, rather than silently constructing an evaluator that would answer wrong
// for part of the program.
public sealed class SdfFieldEvaluator : IWorldQuery, IFieldEvaluator {
    // SDF_FAR_DISTANCE (sdf-vm.hlsli): the accumulator's seed value — "nothing found yet," farther than any real
    // program's geometry, so the first SHAPE candidate always wins the initial compose.
    private static readonly FixedQ4816 FarDistance = FixedQ4816.FromInteger(value: 1_000_000_000L);
    // SDF_SMOOTH_RADIUS_MIN / SDF_SQRT_HALF / SDF_ELLIPSOID_MIN_DENOM (sdf-vm.hlsli) — the same epsilon floors the
    // shader's blend/shape math uses, transcribed to fixed point so a zero/degenerate radius behaves identically.
    private static readonly FixedQ4816 SmoothRadiusMin = FixedQ4816.FromDouble(value: 0.0001);
    private static readonly FixedQ4816 SqrtHalf = FixedQ4816.FromDouble(value: 0.70710678118654752440);
    private static readonly FixedQ4816 EllipsoidMinDenom = FixedQ4816.FromDouble(value: 0.0001);
    private static readonly FixedQ4816 Half = FixedQ4816.FromDouble(value: 0.5);
    private static readonly FixedQ4816 Two = FixedQ4816.FromInteger(value: 2L);
    // The tetrahedron central-difference probe offset for TryFieldGradient, in RAW world units. Two failure modes
    // bound it from both sides: too small and both TryDistance taps quantize to the SAME raw Q48.16 distance (the
    // format's resolution is 2^-16 ~ 0.0000153, and every supported shape/blend involves at least one Sqrt whose
    // rounding is coarser still near a smooth seam), collapsing the estimated gradient to zero; too large and the
    // central-difference TRUNCATION error grows (the estimate is only accurate where the field is locally near-linear
    // across the probe span, and any accumulated blend seam or Repeat cell wall inside that span corrupts the taps).
    // 0.01 world units is documented, not derived: it sits three orders of magnitude above the format's raw floor and
    // three orders below room-scale content. Consumers authoring much smaller or larger geometry may need a
    // different probe — this is a tuning constant, not a physical law.
    private static readonly FixedQ4816 GradientEpsilon = FixedQ4816.FromDouble(value: 0.01);
    // The march accept threshold (Raycast/SphereCast/TryGroundHeight/LineOfSight): a sample within this of the
    // surface counts as a hit rather than one more step. Matches the scale of GradientEpsilon (both are "close
    // enough" tolerances against the same fixed-point field) — tighten per-consumer by wrapping this provider, not by
    // editing the shared constant.
    private static readonly FixedQ4816 HitEpsilon = FixedQ4816.FromDouble(value: 0.001);
    // The march step floor: since every op this evaluator interprets is an isometry, a distance-preserving field op,
    // or Scale's exact min-axis correction (see the type remarks), the interpreted field is EXACTLY 1-Lipschitz, so
    // stepping by the raw field distance can never overstep a real surface — this floor exists only to keep a
    // pathological near-zero-but-not-accepted clearance from stalling the loop at MaxMarchIterations.
    private static readonly FixedQ4816 MinMarchStep = FixedQ4816.FromDouble(value: 0.0001);
    // The skin distance LineOfSight shrinks its probe by, so a target sitting exactly on a surface (the common "is
    // there a clear line to that wall" query) never reads as self-obstructing.
    private static readonly FixedQ4816 LineOfSightSkin = FixedQ4816.FromDouble(value: 0.05);
    // A generous ceiling for a well-conditioned field (see MinMarchStep): the loop always terminates and reports "no
    // hit" rather than spin — the standard non-convergence contract every sphere tracer carries, never a hang.
    private const int MaxMarchIterations = 512;

    // The tetrahedron normal-estimate offsets: four isotropic directions whose outer products sum to 4*I, so the
    // weighted sum TryFieldGradient computes reconstructs the same gradient a 6-tap central difference would, at 2/3
    // the TryDistance calls (see the sdf-world skill's analytic-normal gotcha — this evaluator uses the TAP form
    // deliberately, Decision B: direction dominates for gravity, and matching the renderer's own probe shape keeps
    // the two systems' "which way is down" answers comparable at a shared point).
    private static readonly FixedVector3[] TetrahedronOffsets = [
        new(X: FixedQ4816.One, Y: -FixedQ4816.One, Z: -FixedQ4816.One),
        new(X: -FixedQ4816.One, Y: -FixedQ4816.One, Z: FixedQ4816.One),
        new(X: -FixedQ4816.One, Y: FixedQ4816.One, Z: -FixedQ4816.One),
        new(X: FixedQ4816.One, Y: FixedQ4816.One, Z: FixedQ4816.One),
    ];
    private readonly CompiledInstruction[] m_instructions;

    /// <summary>Compiles <paramref name="program"/>'s instruction stream into this evaluator's fixed-point form.</summary>
    /// <param name="program">The program to wrap. Its <see cref="SdfProgram.Instructions"/> are walked ONCE here —
    /// every baked float (a Rotate's quaternion, a shape's dimensions, a blend's smooth radius, ...) converts to
    /// <see cref="FixedQ4816"/> exactly once and is cached, never re-converted per query.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="program"/> contains an op or shape this WARP-FREE
    /// evaluator cannot interpret — see the type remarks' excluded-ops rule. <see cref="SdfOp.TransformDynamic"/> is
    /// excluded not because a rigid dynamic transform is hard to interpret (it is the same cross/mul/add as
    /// <see cref="SdfOp.Rotate"/> plus a translate), but because THIS constructor takes only a program, never a
    /// per-frame dynamic-transform table against which to resolve a slot.</exception>
    public SdfFieldEvaluator(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(argument: program);

        m_instructions = Compile(instructions: program.Instructions);
    }

    /// <inheritdoc/>
    public FieldEvaluatorCapabilities Capabilities => new(WarpFree: true);

    /// <inheritdoc/>
    public bool TryDistance(WorldCoord3 position, out FixedQ4816 distance, out int material) {
        distance = FixedQ4816.Zero;
        material = 0;

        var worldPosition = position.Local;
        var localPosition = worldPosition;
        var distanceScale = FixedQ4816.One;
        var resultDistance = FarDistance;
        var resultMaterial = 0;
        var savedFieldDistance = FarDistance;
        var savedFieldMaterial = 0;
        var sawShape = false;

        for (var index = 0; (index < m_instructions.Length); index++) {
            var instruction = m_instructions[index];

            switch (instruction.Op) {
                case SdfOp.ResetPoint: {
                        localPosition = worldPosition;
                        distanceScale = FixedQ4816.One;
                        break;
                    }
                case SdfOp.Translate: {
                        localPosition -= Vector(instruction: instruction);
                        break;
                    }
                case SdfOp.Rotate: {
                        localPosition = RotateByInverseQuaternion(p: localPosition, instruction: instruction);
                        break;
                    }
                case SdfOp.Scale: {
                        localPosition = DivideComponents(left: localPosition, right: Vector(instruction: instruction));
                        distanceScale *= instruction.Data0W;
                        break;
                    }
                case SdfOp.Repeat: {
                        var spacing = Vector(instruction: instruction);
                        var inverseSpacing = Vector1(instruction: instruction);

                        localPosition -= MultiplyComponents(left: spacing, right: RoundComponents(value: MultiplyComponents(left: localPosition, right: inverseSpacing)));
                        break;
                    }
                case SdfOp.RepeatLimited: {
                        var spacing = Vector(instruction: instruction);
                        var limit = Vector1(instruction: instruction);
                        var rounded = RoundComponents(value: DivideComponents(left: localPosition, right: spacing));

                        localPosition -= MultiplyComponents(left: spacing, right: ClampComponents(value: rounded, minimum: Negate(value: limit), maximum: limit));
                        break;
                    }
                case SdfOp.SymmetryPlane: {
                        var normal = Vector(instruction: instruction);
                        var t = (FixedVector3.Dot(left: localPosition, right: normal) + instruction.Data0W);
                        var twiceMin = (FixedQ4816.Min(x: t, y: FixedQ4816.Zero) * Two);

                        localPosition -= (normal * twiceMin);
                        break;
                    }
                case SdfOp.Elongate: {
                        var extents = Vector(instruction: instruction);

                        localPosition -= ClampComponents(value: localPosition, minimum: Negate(value: extents), maximum: extents);
                        break;
                    }
                case SdfOp.Onion: {
                        resultDistance = (FixedQ4816.Abs(value: resultDistance) - instruction.Data0X);
                        break;
                    }
                case SdfOp.Dilate: {
                        resultDistance -= instruction.Data0X;
                        break;
                    }
                case SdfOp.PushField: {
                        savedFieldDistance = resultDistance;
                        savedFieldMaterial = resultMaterial;
                        resultDistance = FarDistance;
                        resultMaterial = 0;
                        break;
                    }
                case SdfOp.PopField: {
                        var candidateDistance = resultDistance;
                        var candidateMaterial = resultMaterial;

                        resultDistance = savedFieldDistance;
                        resultMaterial = savedFieldMaterial;
                        (resultDistance, resultMaterial) = Compose(current: resultDistance, currentMaterial: resultMaterial, candidate: candidateDistance, candidateMaterial: candidateMaterial, blend: instruction.Blend, smooth: instruction.Data1X);
                        break;
                    }
                case SdfOp.ShapeBlend: {
                        var candidateDistance = (EvaluateShape(instruction: instruction, p: localPosition) * distanceScale);

                        (resultDistance, resultMaterial) = Compose(current: resultDistance, currentMaterial: resultMaterial, candidate: candidateDistance, candidateMaterial: instruction.Material, blend: instruction.Blend, smooth: instruction.Data1X);
                        sawShape = true;
                        break;
                    }
                default: {
                        throw new UnreachableException(message: $"The constructor validated every instruction's op is supported; op {instruction.Op} reached the interpreter unvalidated.");
                    }
            }
        }

        if (!sawShape) {
            return false;
        }

        distance = resultDistance;
        material = resultMaterial;

        return true;
    }

    /// <inheritdoc/>
    public bool TryFieldGradient(WorldCoord3 position, out FixedVector3 gradient) {
        gradient = FixedVector3.Zero;

        var accumulator = FixedVector3.Zero;

        for (var index = 0; (index < TetrahedronOffsets.Length); index++) {
            var offset = TetrahedronOffsets[index];

            if (!TryDistance(position: (position + (offset * GradientEpsilon)), distance: out var probeDistance, material: out _)) {
                return false;
            }

            accumulator += (offset * probeDistance);
        }

        var normalized = accumulator.Normalize();

        if (normalized == FixedVector3.Zero) {
            return false;
        }

        gradient = normalized;

        return true;
    }

    /// <inheritdoc/>
    QueryCapabilities IWorldQuery.Capabilities =>
        // Every verb below marches the EXACT field (no baked/quantized layer), so "occupancy" reads as fully present —
        // richer than the grid QueryCapabilities.HasOccupancy's own doc describes, but the same true statement: raycast
        // and LineOfSight see real 3D geometry, never degrading to a flat heightfield.
        new(HasBlocked: true, HasHeightfield: true, HasOccupancy: true);

    /// <inheritdoc/>
    public bool Raycast(WorldCoord3 origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit) =>
        March(origin: origin, direction: dir, maxDistance: maxDist, radius: FixedQ4816.Zero, hit: out hit);

    /// <inheritdoc/>
    public bool SphereCast(WorldCoord3 origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit) =>
        March(origin: origin, direction: dir, maxDistance: maxDist, radius: radius, hit: out hit);

    /// <inheritdoc/>
    public bool Overlap(WorldCoord3 center, FixedQ4816 radius) =>
        (TryDistance(position: center, distance: out var distance, material: out _) && (distance <= radius));

    /// <inheritdoc/>
    public bool TryGroundHeight(WorldCoord3 position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY) {
        groundY = FixedQ4816.Zero;

        var probeRange = (probeUp + probeDown);

        if (probeRange <= FixedQ4816.Zero) {
            return false;
        }

        var top = (position + new FixedVector3(X: FixedQ4816.Zero, Y: probeUp, Z: FixedQ4816.Zero));

        if (!March(origin: top, direction: new FixedVector3(X: FixedQ4816.Zero, Y: -FixedQ4816.One, Z: FixedQ4816.Zero), maxDistance: probeRange, radius: FixedQ4816.Zero, hit: out var hit)) {
            return false;
        }

        // Same single-cell assumption BakedWorldQuery documents: the probe stays within the room/arena-scale span a
        // vertical ground search covers, so the hit's .Local (relative to its own, possibly re-anchored cell) reads
        // correctly against `position`'s own Y.
        groundY = hit.Point.Local.Y;

        return true;
    }

    /// <inheritdoc/>
    public bool LineOfSight(WorldCoord3 from, WorldCoord3 to) {
        var delta = (to - from);
        var distance = delta.Length;

        if (distance <= FixedQ4816.Zero) {
            return true;
        }

        var probeDistance = (distance - LineOfSightSkin);

        if (probeDistance <= FixedQ4816.Zero) {
            return true;
        }

        return !Raycast(origin: from, dir: delta, maxDist: probeDistance, hit: out _);
    }

    // A single stepped sphere-trace march shared by Raycast (radius == 0) and SphereCast (radius > 0): steps by the
    // field's own clearance (see MinMarchStep's remarks on why no extra Lipschitz clamp is needed), testing against
    // HitEpsilon each iteration. Mirrors BakedWorldQuery.March's shape so the two providers read as the same family
    // of verb despite one walking a baked grid and the other a live field.
    private bool March(WorldCoord3 origin, FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 radius, out RayHit hit) {
        hit = default;

        var unit = direction.Normalize();

        if ((unit == FixedVector3.Zero) || (maxDistance <= FixedQ4816.Zero)) {
            return false;
        }

        var position = origin;
        var traveled = FixedQ4816.Zero;

        for (var iteration = 0; (iteration < MaxMarchIterations); iteration++) {
            if (!TryDistance(position: position, distance: out var fieldDistance, material: out var material)) {
                return false;
            }

            var clearance = (fieldDistance - radius);

            if (clearance <= HitEpsilon) {
                _ = TryFieldGradient(position: position, gradient: out var normal); // best-effort; Zero on a degenerate field

                hit = new RayHit(Confidence: WorldQueryConfidence.Exact, Distance: traveled, Material: material, Normal: normal, Point: position);

                return true;
            }

            if (traveled >= maxDistance) {
                return false;
            }

            var step = FixedQ4816.Min(x: FixedQ4816.Max(x: clearance, y: MinMarchStep), y: (maxDistance - traveled));

            traveled += step;
            position += (unit * step);
        }

        return false;
    }

    // Validates and converts a program's instruction stream ONCE — see the type remarks' excluded-ops rule for what
    // throws and why.
    private static CompiledInstruction[] Compile(IReadOnlyList<SdfInstruction> instructions) {
        var compiled = new CompiledInstruction[instructions.Count];

        for (var index = 0; (index < instructions.Count); index++) {
            var instruction = instructions[index];

            if (!IsSupportedOp(op: instruction.Op)) {
                throw new ArgumentException(message: $"SdfFieldEvaluator is warp-free this wave and cannot interpret instruction {index}'s op {instruction.Op}. See SdfFieldEvaluator.cs's KEEP-IN-SYNC header for the full excluded-op rule.", paramName: nameof(instructions));
            }

            if ((instruction.Op == SdfOp.ShapeBlend) && !IsSupportedShape(shape: (SdfShapeType)instruction.Shape)) {
                throw new ArgumentException(message: $"SdfFieldEvaluator cannot interpret instruction {index}'s shape {(SdfShapeType)instruction.Shape} (its exact core needs runtime trig or texture sampling this wave does not implement). See SdfFieldEvaluator.cs's KEEP-IN-SYNC header.", paramName: nameof(instructions));
            }

            compiled[index] = new CompiledInstruction(
                Blend: instruction.Blend,
                Data0W: FixedQ4816.FromDouble(value: instruction.Data0.W),
                Data0X: FixedQ4816.FromDouble(value: instruction.Data0.X),
                Data0Y: FixedQ4816.FromDouble(value: instruction.Data0.Y),
                Data0Z: FixedQ4816.FromDouble(value: instruction.Data0.Z),
                Data1W: FixedQ4816.FromDouble(value: instruction.Data1.W),
                Data1X: FixedQ4816.FromDouble(value: instruction.Data1.X),
                Data1Y: FixedQ4816.FromDouble(value: instruction.Data1.Y),
                Data1Z: FixedQ4816.FromDouble(value: instruction.Data1.Z),
                Material: (int)instruction.Material,
                Op: instruction.Op,
                Shape: instruction.Shape
            );
        }

        return compiled;
    }
    private static bool IsSupportedOp(SdfOp op) {
        return op switch {
            SdfOp.ResetPoint or
            SdfOp.Translate or
            SdfOp.Rotate or
            SdfOp.Scale or
            SdfOp.Elongate or
            SdfOp.ShapeBlend or
            SdfOp.Repeat or
            SdfOp.RepeatLimited or
            SdfOp.Onion or
            SdfOp.Dilate or
            SdfOp.SymmetryPlane or
            SdfOp.PushField or
            SdfOp.PopField => true,
            _ => false,
        };
    }
    private static bool IsSupportedShape(SdfShapeType shape) {
        return shape switch {
            SdfShapeType.Box or
            SdfShapeType.Capsule or
            SdfShapeType.Sphere or
            SdfShapeType.Torus or
            SdfShapeType.Cylinder or
            SdfShapeType.Plane or
            SdfShapeType.Ellipsoid or
            SdfShapeType.Vesica or
            SdfShapeType.RoundCone or
            SdfShapeType.ScreenSlab => true,
            _ => false,
        };
    }

    // === The rigid transform ops (KEEP IN SYNC with mapCore's SDF_OP_TRANSLATE/ROTATE/SCALE/REPEAT/REPEAT_LIMITED/
    // SYMMETRY_PLANE/ELONGATE cases) ======================================================================================

    // The PRE-BAKED quaternion already ran sin/cos host-side. Route through the canonical fused fixed-point kernel so
    // CPU queries do not insert different intermediate rounding boundaries from every other quaternion consumer.
    private static FixedVector3 RotateByInverseQuaternion(FixedVector3 p, CompiledInstruction instruction) {
        var rotation = new FixedQuaternion(
            X: instruction.Data0X,
            Y: instruction.Data0Y,
            Z: instruction.Data0Z,
            W: instruction.Data0W
        );

        return rotation.RotateInverse(vector: p);
    }

    // === The shape distance functions (KEEP IN SYNC with the matching sdf* functions in Assets/Shaders/Sdf/sdf-vm.hlsli)
    // ======================================================================================================================

    private static FixedQ4816 EvaluateShape(CompiledInstruction instruction, FixedVector3 p) {
        return (SdfShapeType)instruction.Shape switch {
            SdfShapeType.Sphere => SdfSphere(p: p, radius: instruction.Data0X),
            SdfShapeType.Box or SdfShapeType.ScreenSlab => SdfBox(p: p, halfExtents: Vector(instruction: instruction), cornerRadius: instruction.Data0W),
            SdfShapeType.Torus => SdfTorus(p: p, major: instruction.Data0X, minor: instruction.Data0Y),
            SdfShapeType.Plane => SdfPlane(p: p, normal: Vector(instruction: instruction), offset: instruction.Data0W),
            SdfShapeType.RoundCone => SdfRoundCone(p: p, lowerRadius: instruction.Data0X, upperRadius: instruction.Data0Y, height: instruction.Data0Z, b: instruction.Data0W, a: instruction.Data1Y),
            SdfShapeType.Capsule => SdfCapsule(p: p, endpoint: Vector(instruction: instruction), radius: instruction.Data0W, inverseLengthSquared: instruction.Data1Y),
            SdfShapeType.Cylinder => SdfCylinder(p: p, radius: instruction.Data0X, halfHeight: instruction.Data0Y),
            SdfShapeType.Ellipsoid => SdfEllipsoid(p: p, inverseRadii: new FixedVector3(X: instruction.Data1Y, Y: instruction.Data1Z, Z: instruction.Data1W)),
            SdfShapeType.Vesica => SdfVesica(p: p, r: instruction.Data0X, d: instruction.Data0Y, b: instruction.Data0Z),
            _ => throw new UnreachableException(message: $"The constructor validated every shape is supported; shape {(SdfShapeType)instruction.Shape} reached EvaluateShape unvalidated."),
        };
    }
    private static FixedQ4816 SdfSphere(FixedVector3 p, FixedQ4816 radius) =>
        (p.Length - radius);
    private static FixedQ4816 SdfBox(FixedVector3 p, FixedVector3 halfExtents, FixedQ4816 cornerRadius) {
        var q = (Abs(value: p) - SubtractScalar(value: halfExtents, scalar: cornerRadius));
        var outside = MaxComponents(value: q, scalar: FixedQ4816.Zero).Length;
        var inside = FixedQ4816.Min(x: MaxComponent(value: q), y: FixedQ4816.Zero);

        return ((outside + inside) - cornerRadius);
    }
    private static FixedQ4816 SdfTorus(FixedVector3 p, FixedQ4816 major, FixedQ4816 minor) {
        var q = new FixedVector2(X: (RadialLength(x: p.X, z: p.Z) - major), Y: p.Y);

        return (q.Length - minor);
    }
    private static FixedQ4816 SdfPlane(FixedVector3 p, FixedVector3 normal, FixedQ4816 offset) =>
        (FixedVector3.Dot(left: p, right: normal) + offset);
    private static FixedQ4816 SdfCapsule(FixedVector3 p, FixedVector3 endpoint, FixedQ4816 radius, FixedQ4816 inverseLengthSquared) {
        var h = FixedQ4816.Clamp(value: (FixedVector3.Dot(left: p, right: endpoint) * inverseLengthSquared), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);

        return ((p - (endpoint * h)).Length - radius);
    }
    private static FixedQ4816 SdfCylinder(FixedVector3 p, FixedQ4816 radius, FixedQ4816 halfHeight) {
        var dx = (RadialLength(x: p.X, z: p.Z) - radius);
        var dy = (FixedQ4816.Abs(value: p.Y) - halfHeight);
        var inside = FixedQ4816.Min(x: FixedQ4816.Max(x: dx, y: dy), y: FixedQ4816.Zero);
        var outside = new FixedVector2(X: FixedQ4816.Max(x: dx, y: FixedQ4816.Zero), Y: FixedQ4816.Max(x: dy, y: FixedQ4816.Zero)).Length;

        return (inside + outside);
    }
    private static FixedQ4816 SdfEllipsoid(FixedVector3 p, FixedVector3 inverseRadii) {
        var q = MultiplyComponents(left: p, right: inverseRadii);
        var k0 = q.Length;
        var k1 = MultiplyComponents(left: q, right: inverseRadii).Length;
        var denom = FixedQ4816.Max(x: k1, y: EllipsoidMinDenom);

        return ((k0 * (k0 - FixedQ4816.One)) / denom);
    }
    private static FixedQ4816 SdfVesica(FixedVector3 p, FixedQ4816 r, FixedQ4816 d, FixedQ4816 b) {
        var qx = RadialLength(x: p.X, z: p.Z);
        var qy = FixedQ4816.Abs(value: p.Y);

        return ((((qy - b) * d) > (qx * b))
            ? new FixedVector2(X: qx, Y: (qy - b)).Length
            : (new FixedVector2(X: (qx + d), Y: qy).Length - r));
    }
    private static FixedQ4816 SdfRoundCone(FixedVector3 p, FixedQ4816 lowerRadius, FixedQ4816 upperRadius, FixedQ4816 height, FixedQ4816 b, FixedQ4816 a) {
        var qx = RadialLength(x: p.X, z: p.Z);
        var qy = p.Y;
        var k = ((qx * -b) + (qy * a));

        if (k < FixedQ4816.Zero) {
            return (new FixedVector2(X: qx, Y: qy).Length - lowerRadius);
        }

        if (k > (a * height)) {
            return (new FixedVector2(X: qx, Y: (qy - height)).Length - upperRadius);
        }

        return (((qx * a) + (qy * b)) - lowerRadius);
    }
    private static FixedQ4816 RadialLength(FixedQ4816 x, FixedQ4816 z) =>
        new FixedVector2(X: x, Y: z).Length;

    // === The blend accumulator (KEEP IN SYNC with mapCore's shared blend tail + blendShape/blendSmoothUnion) ===========
    // Mirrors the shader's semantics EXACTLY, including op order effects: the material winner is resolved from the
    // PRE-blend (current, candidate) pair using the SAME strict compares a SHAPE or a POP_FIELD candidate gets, then
    // the distance blends — never the reverse order.

    private static (FixedQ4816 Distance, int Material) Compose(FixedQ4816 current, int currentMaterial, FixedQ4816 candidate, int candidateMaterial, uint blend, FixedQ4816 smooth) {
        var candidateWins = ResolveWinner(current: current, candidate: candidate, blend: blend);
        var winnerMaterial = (candidateWins ? candidateMaterial : currentMaterial);
        var blended = BlendShape(current: current, candidate: candidate, blend: blend, smoothRadius: smooth);

        return (blended, winnerMaterial);
    }
    private static bool ResolveWinner(FixedQ4816 current, FixedQ4816 candidate, uint blend) {
        return blend switch {
            (uint)SdfBlendOp.Intersection or (uint)SdfBlendOp.SmoothIntersection or (uint)SdfBlendOp.ChamferIntersection => (candidate > current),
            (uint)SdfBlendOp.Subtraction or (uint)SdfBlendOp.SmoothSubtraction or (uint)SdfBlendOp.ChamferSubtraction => (-candidate > current),
            _ => (candidate < current),
        };
    }
    private static FixedQ4816 BlendShape(FixedQ4816 current, FixedQ4816 candidate, uint blend, FixedQ4816 smoothRadius) {
        var smoothK = FixedQ4816.Max(x: smoothRadius, y: SmoothRadiusMin);
        var chamfer = FixedQ4816.Max(x: smoothRadius, y: FixedQ4816.Zero);

        return blend switch {
            (uint)SdfBlendOp.SmoothUnion => BlendSmoothUnion(a: current, b: candidate, k: smoothK),
            (uint)SdfBlendOp.Subtraction => FixedQ4816.Max(x: current, y: -candidate),
            (uint)SdfBlendOp.Intersection => FixedQ4816.Max(x: current, y: candidate),
            (uint)SdfBlendOp.Xor => FixedQ4816.Max(x: FixedQ4816.Min(x: current, y: candidate), y: -FixedQ4816.Max(x: current, y: candidate)),
            (uint)SdfBlendOp.SmoothIntersection => -BlendSmoothUnion(a: -current, b: -candidate, k: smoothK),
            (uint)SdfBlendOp.SmoothSubtraction => -BlendSmoothUnion(a: candidate, b: -current, k: smoothK),
            (uint)SdfBlendOp.ChamferUnion => FixedQ4816.Min(x: FixedQ4816.Min(x: current, y: candidate), y: (((current + candidate) - chamfer) * SqrtHalf)),
            (uint)SdfBlendOp.ChamferIntersection => FixedQ4816.Max(x: FixedQ4816.Max(x: current, y: candidate), y: (((current + candidate) + chamfer) * SqrtHalf)),
            (uint)SdfBlendOp.ChamferSubtraction => FixedQ4816.Max(x: FixedQ4816.Max(x: current, y: -candidate), y: (((current - candidate) + chamfer) * SqrtHalf)),
            _ => FixedQ4816.Min(x: current, y: candidate), // SDF_BLEND_UNION, the default
        };
    }

    // Both saturated endpoints return their input to the bit — see blendSmoothUnion's remarks in sdf-vm.hlsli for
    // why the `h <= 0` select matters (an unselected far-shape's SDF_FAR_DISTANCE accumulator would otherwise poison
    // the result).
    private static FixedQ4816 BlendSmoothUnion(FixedQ4816 a, FixedQ4816 b, FixedQ4816 k) {
        var h = FixedQ4816.Clamp(value: (Half + ((Half * (b - a)) / k)), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);
        var blended = ((h <= FixedQ4816.Zero) ? b : Lerp(a: a, b: b, t: (FixedQ4816.One - h)));

        return (blended - ((k * h) * (FixedQ4816.One - h)));
    }
    private static FixedQ4816 Lerp(FixedQ4816 a, FixedQ4816 b, FixedQ4816 t) =>
        (a + ((b - a) * t));

    // === Elementwise FixedVector3 helpers (System.Numerics.Vector3 offers these as instance methods; FixedVector3
    // does not, so this file supplies the handful the interpreted op set needs) ==========================================

    private static FixedVector3 Vector(CompiledInstruction instruction) =>
        new(X: instruction.Data0X, Y: instruction.Data0Y, Z: instruction.Data0Z);
    private static FixedVector3 Vector1(CompiledInstruction instruction) =>
        new(X: instruction.Data1X, Y: instruction.Data1Y, Z: instruction.Data1Z);
    private static FixedVector3 Abs(FixedVector3 value) =>
        new(X: FixedQ4816.Abs(value: value.X), Y: FixedQ4816.Abs(value: value.Y), Z: FixedQ4816.Abs(value: value.Z));
    private static FixedVector3 Negate(FixedVector3 value) =>
        new(X: -value.X, Y: -value.Y, Z: -value.Z);
    private static FixedVector3 SubtractScalar(FixedVector3 value, FixedQ4816 scalar) =>
        new(X: (value.X - scalar), Y: (value.Y - scalar), Z: (value.Z - scalar));
    private static FixedVector3 MaxComponents(FixedVector3 value, FixedQ4816 scalar) =>
        new(X: FixedQ4816.Max(x: value.X, y: scalar), Y: FixedQ4816.Max(x: value.Y, y: scalar), Z: FixedQ4816.Max(x: value.Z, y: scalar));
    private static FixedQ4816 MaxComponent(FixedVector3 value) =>
        FixedQ4816.Max(x: value.X, y: FixedQ4816.Max(x: value.Y, y: value.Z));
    private static FixedVector3 MultiplyComponents(FixedVector3 left, FixedVector3 right) =>
        new(X: (left.X * right.X), Y: (left.Y * right.Y), Z: (left.Z * right.Z));
    private static FixedVector3 DivideComponents(FixedVector3 left, FixedVector3 right) =>
        new(X: (left.X / right.X), Y: (left.Y / right.Y), Z: (left.Z / right.Z));
    private static FixedVector3 RoundComponents(FixedVector3 value) =>
        new(X: FixedQ4816.Round(value: value.X), Y: FixedQ4816.Round(value: value.Y), Z: FixedQ4816.Round(value: value.Z));
    private static FixedVector3 ClampComponents(FixedVector3 value, FixedVector3 minimum, FixedVector3 maximum) =>
        new(
            X: FixedQ4816.Clamp(value: value.X, minimum: minimum.X, maximum: maximum.X),
            Y: FixedQ4816.Clamp(value: value.Y, minimum: minimum.Y, maximum: maximum.Y),
            Z: FixedQ4816.Clamp(value: value.Z, minimum: minimum.Z, maximum: maximum.Z)
        );

    // The compiled, fixed-point form of one SdfInstruction: every Data0/Data1 float lane converted to FixedQ4816
    // ONCE at construction (see Compile). Field names mirror the shader's data0.x/y/z/w and data1.x/y/z/w swizzles
    // directly so a shape/op body reads as a transcription of its mapCore counterpart, not a re-derivation.
    private readonly record struct CompiledInstruction(
        SdfOp Op,
        uint Shape,
        uint Blend,
        int Material,
        FixedQ4816 Data0X,
        FixedQ4816 Data0Y,
        FixedQ4816 Data0Z,
        FixedQ4816 Data0W,
        FixedQ4816 Data1X,
        FixedQ4816 Data1Y,
        FixedQ4816 Data1Z,
        FixedQ4816 Data1W
    );
}
