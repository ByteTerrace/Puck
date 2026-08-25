using System.Diagnostics;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// KEEP IN SYNC with mapCore's RIGID op/shape cases in Assets/Shaders/Sdf/sdf-vm.hlsli (the SYNC PAIR this file is
// half of — see the sdf-world skill's sync-pair table). This is a SECOND, INDEPENDENT interpreter of the same
// SdfInstruction stream mapCore walks, in FixedQ4816/FixedVector3 instead of shader float — a deliberate DUAL
// implementation (like SdfProgram's own host-side AnalyzeBounds/AnalyzeLipschitz passes), not a codegen of the
// shader. Touching mapCore's RESET/TRANSLATE/ROTATE/SCALE/REPEAT/REPEAT_LIMITED/SYMMETRY_PLANE/ELONGATE/ONION/
// DILATE/PUSH_FIELD/POP_FIELD/SHAPE cases, or blendShape/evaluateShape's Sphere/Box/ScreenSlab/Torus/Plane/
// RoundCone/Capsule/Cylinder/Ellipsoid/Vesica/RoundedRectangle/Trapezoid bodies, means updating this file's mirror in
// the SAME change (and vice versa) — a divergence is silent (both sides compile and run; only the ANSWER differs).
//
// THE EXCLUDED-OPS RULE (asserted once at construction, never per query): this evaluator is WARP-FREE — it rejects
// any program containing an op that needs runtime trigonometry not implemented in fixed point (BendX/BendY/BendZ/
// TwistY/LogSphere/CellJitter/RepeatPolar/Displace/DomainWarp/NoiseDisplace), the one op needing a per-frame dynamic-transform
// buffer this evaluator's signature has no seam for (TransformDynamic — see the constructor's remarks), and
// WallpaperFold, whose 17-group parity-keyed cell logic has no fixed-point implementation. It also rejects a
// NON-UNIFORM Scale: the renderer's min-axis correction is deliberately a safe sphere-tracing lower bound, not
// Euclidean surface distance, while this evaluator's query and contact consumers require physical clearance.
// Scalable primitives must bake anisotropy into a native shape spelling (CreationGeometry does this for boxes,
// spheres/ellipsoids, axially symmetric cylinders/cones, and planes). It
// similarly rejects
// three shapes whose EXACT cores need runtime trig (RegularPolygon/Star: atan2 in sdfStar2D; Ellipse: an analytic
// cubic solve with acos/pow), one needing texture sampling (Glyph), and SampledRegion, whose brick pool is an engine
// resource this evaluator cannot access — every other shape in <see cref="SdfShapeType"/> is supported. The constructor
// throws <see cref="ArgumentException"/> naming the FIRST
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
    // The central-difference probe offset for TryFieldGradient, in RAW world units. Two failure modes
    // bound it from both sides: too small and both TryDistance taps quantize to the SAME raw Q48.16 distance (the
    // format's resolution is 2^-16 ~ 0.0000153, and every supported shape/blend involves at least one Sqrt whose
    // rounding is coarser still near a smooth seam), collapsing the estimated gradient to zero; too large and the
    // central-difference TRUNCATION error grows (the estimate is only accurate where the field is locally near-linear
    // across the probe span, and any accumulated blend seam or Repeat cell wall inside that span corrupts the taps).
    // The 6-tap per-axis probe's reconstruction response is 2*step*g per axis, so both taps landing on the same raw
    // Q48.16 distance requires 2*step >= 2^-16, i.e. step >= ~7.6e-6. The default 0.01 world units still sits ~3
    // orders of magnitude above that floor and three orders below room-scale content. 0.01 is documented, not
    // derived. Consumers authoring much smaller or larger geometry may need a different probe — this is a tuning
    // constant, not a physical law.
    private static readonly FixedQ4816 GradientEpsilon = FixedQ4816.FromDouble(value: 0.01);
    // The march accept threshold (Raycast/SphereCast/TryGroundHeight/LineOfSight): a sample within this of the
    // surface counts as a hit rather than one more step. Matches the scale of GradientEpsilon (both are "close
    // enough" tolerances against the same fixed-point field) — tighten per-consumer by wrapping this provider, not by
    // editing the shared constant.
    private static readonly FixedQ4816 HitEpsilon = FixedQ4816.FromDouble(value: 0.001);
    // The skin distance LineOfSight shrinks its probe by, so a target sitting exactly on a surface (the common "is
    // there a clear line to that wall" query) never reads as self-obstructing.
    private static readonly FixedQ4816 LineOfSightSkin = FixedQ4816.FromDouble(value: 0.05);

    // The iteration budget at unit step scale. A non-accepted point advance is at least
    // max(floor(HitEpsilon * stepScale), one Q48.16 tick) — the divisor m_marchIterations rescales by — so this budget
    // times HitEpsilon is the distance a point march always covers before it may exhaust, at every step scale.
    private const int BaseMarchIterations = 512;

    // TryFieldGradient probes by 6-tap CENTRAL DIFFERENCE — one +/- pair per world axis. Central differences are
    // exact where the field is mirror-symmetric about the probe point ONLY when every op between the probe point and
    // the shape body is exactly affine in fixed point (ResetPoint/Translate plus a symmetric shape body) — the same
    // fixed-point subtraction on bitwise-mirrored taps. Through Rotate/Scale the rounding of r(c+d) and r(c-d) is
    // independent (a yaw-90 quaternion quantizes to |q|^2 = 1 + 2.1e-6), leaving a systematic tangential residual of
    // order 1e-3 in the normalized gradient — sub-perceptual at feel scale, and zero on unrotated geometry.
    private readonly CompiledInstruction[] m_instructions;
    // Whether the compiled stream declares any shape at all. A program with none has nothing to answer against, which
    // is a DIFFERENT answer from "this point cannot be expressed against the program's frame" — March must be able to
    // tell the two apart, since only the second is a non-convergence.
    private readonly bool m_hasShape;
    // The iteration budget March runs, derived from m_stepScale so that the distance a POINT march always covers
    // before it may exhaust is the SAME at every step scale: BaseMarchIterations * HitEpsilon.
    private readonly int m_marchIterations;
    // SdfProgram.StepScale (1/L, in (0, 1]) in fixed point — the factor that turns the interpreted field's value into
    // a lower bound on true Euclidean distance. The interpreted op subset is 1-Lipschitz, but the blend tail is not:
    // a chamfer's bevel arm and an eccentric Ellipsoid both make the field OVERESTIMATE, so a march advancing by the
    // raw value steps past thin geometry and tunnels.
    private readonly FixedQ4816 m_stepScale;

    /// <summary>Compiles <paramref name="program"/>'s instruction stream into this evaluator's fixed-point form.</summary>
    /// <param name="program">The program to wrap. Its <see cref="SdfProgram.Instructions"/> are walked ONCE here —
    /// every baked float (a Rotate's quaternion, a shape's dimensions, a blend's smooth radius, ...) converts to
    /// <see cref="FixedQ4816"/> exactly once and is cached, never re-converted per query.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="program"/> contains an op or shape this WARP-FREE
    /// evaluator cannot interpret, or contains a non-uniform <see cref="SdfOp.Scale"/> whose value is only a
    /// conservative march bound — see the type remarks' excluded-op rule. <see cref="SdfOp.TransformDynamic"/> is
    /// excluded not because a rigid dynamic transform is hard to interpret (it is the same cross/mul/add as
    /// <see cref="SdfOp.Rotate"/> plus a translate), but because THIS constructor takes only a program, never a
    /// per-frame dynamic-transform table against which to resolve a slot.</exception>
    public SdfFieldEvaluator(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(argument: program);

        m_instructions = Compile(instructions: program.Instructions);
        m_stepScale = ConservativeStepScale(value: program.StepScale);
        m_marchIterations = MarchIterationsFor(stepScale: m_stepScale);

        for (var index = 0; (index < m_instructions.Length); index++) {
            if (m_instructions[index].Op == SdfOp.ShapeBlend) {
                m_hasShape = true;

                break;
            }
        }
    }

    // StepScale is a lower-bound multiplier: rounding it upward would make a later advance larger than the program's
    // Lipschitz proof. Convert with a directed floor, not FixedQ4816.FromDouble's nearest-even policy. A positive
    // scale below one Q48.16 tick therefore becomes zero, authorizing no scaled advance at all: a radius cast reports
    // Bounded at its origin, a point cast reports Bounded after March's one-tick reach, and Overlap reports occupied,
    // rather than inventing a representable scale larger than the proof permits.
    private static FixedQ4816 ConservativeStepScale(float value) {
        const double RawOne = (1L << FixedQ4816.FractionBitCount);

        var raw = ((long)Math.Floor(((double)value) * RawOne));

        return FixedQ4816.FromRawBits(value: Math.Clamp(
            value: raw,
            min: 0L,
            max: ((long)RawOne)
        ));
    }

    /// <inheritdoc/>
    QueryCapabilities IWorldQuery.Capabilities =>
        // Every verb below marches the EXACT field (no baked/quantized layer), so "occupancy" reads as fully present —
        // richer than the grid QueryCapabilities.HasOccupancy's own doc describes, but the same true statement: raycast
        // and LineOfSight see real 3D geometry, never degrading to a flat heightfield.
        new(
            HasBlocked: true,
            HasHeightfield: true,
            HasOccupancy: true
        );

    /// <inheritdoc/>
    public FieldEvaluatorCapabilities Capabilities => new(WarpFree: true);

    private static FixedVector3 Abs(FixedVector3 value) =>
        new(
            X: FixedQ4816.Abs(value: value.X),
            Y: FixedQ4816.Abs(value: value.Y),
            Z: FixedQ4816.Abs(value: value.Z)
        );
    private static FixedQ4816 BlendShape(FixedQ4816 current, FixedQ4816 candidate, uint blend, FixedQ4816 smoothRadius) {
        var smoothK = FixedQ4816.Max(
            x: smoothRadius,
            y: SmoothRadiusMin
        );
        var chamfer = FixedQ4816.Max(
            x: smoothRadius,
            y: FixedQ4816.Zero
        );

        return blend switch {
            ((uint)SdfBlendOp.SmoothUnion) => BlendSmoothUnion(
            a: current,
            b: candidate,
            k: smoothK
        ),
            ((uint)SdfBlendOp.Subtraction) => FixedQ4816.Max(
            x: current,
            y: -candidate
        ),
            ((uint)SdfBlendOp.Intersection) => FixedQ4816.Max(
            x: current,
            y: candidate
        ),
            ((uint)SdfBlendOp.Xor) => FixedQ4816.Max(
            x: FixedQ4816.Min(
                x: current,
                y: candidate
            ),
            y: -FixedQ4816.Max(
                x: current,
                y: candidate
            )
        ),
            ((uint)SdfBlendOp.SmoothIntersection) => -BlendSmoothUnion(
            a: -current,
            b: -candidate,
            k: smoothK
        ),
            ((uint)SdfBlendOp.SmoothSubtraction) => -BlendSmoothUnion(
            a: candidate,
            b: -current,
            k: smoothK
        ),
            ((uint)SdfBlendOp.ChamferUnion) => FixedQ4816.Min(
            x: FixedQ4816.Min(
                x: current,
                y: candidate
            ),
            y: (((current + candidate) - chamfer) * SqrtHalf)
        ),
            ((uint)SdfBlendOp.ChamferIntersection) => FixedQ4816.Max(
            x: FixedQ4816.Max(
                x: current,
                y: candidate
            ),
            y: (((current + candidate) + chamfer) * SqrtHalf)
        ),
            ((uint)SdfBlendOp.ChamferSubtraction) => FixedQ4816.Max(
            x: FixedQ4816.Max(
                x: current,
                y: -candidate
            ),
            y: (((current - candidate) + chamfer) * SqrtHalf)
        ),
            _ => FixedQ4816.Min(
            x: current,
            y: candidate
        ), // SDF_BLEND_UNION, the default
        };
    }
    // Both saturated endpoints return their input to the bit — see blendSmoothUnion's remarks in sdf-vm.hlsli for
    // why the `h <= 0` select matters (an unselected far-shape's SDF_FAR_DISTANCE accumulator would otherwise poison
    // the result).
    private static FixedQ4816 BlendSmoothUnion(FixedQ4816 a, FixedQ4816 b, FixedQ4816 k) {
        var h = FixedQ4816.Clamp(
            value: (Half + ((Half * (b - a)) / k)),
            minimum: FixedQ4816.Zero,
            maximum: FixedQ4816.One
        );
        var blended = ((h <= FixedQ4816.Zero)
            ? b
            : FixedQ4816.Lerp(
                from: a,
                to: b,
                amount: (FixedQ4816.One - h)
            )
        );

        return (blended - ((k * h) * (FixedQ4816.One - h)));
    }
    private static FixedVector3 ClampComponents(FixedVector3 value, FixedVector3 minimum, FixedVector3 maximum) =>
        new(
            X: FixedQ4816.Clamp(
                value: value.X,
                minimum: minimum.X,
                maximum: maximum.X
            ),
            Y: FixedQ4816.Clamp(
                value: value.Y,
                minimum: minimum.Y,
                maximum: maximum.Y
            ),
            Z: FixedQ4816.Clamp(
                value: value.Z,
                minimum: minimum.Z,
                maximum: maximum.Z
            )
        );
    // Validates and converts a program's instruction stream ONCE — see the type remarks' excluded-ops rule for what
    // throws and why.
    private static CompiledInstruction[] Compile(IReadOnlyList<SdfInstruction> instructions) {
        var compiled = new CompiledInstruction[instructions.Count];

        for (var index = 0; (index < instructions.Count); index++) {
            var instruction = instructions[index];

            if (
                (instruction.Op == SdfOp.Scale) &&
                ((instruction.Data0.X != instruction.Data0.Y) || (instruction.Data0.Y != instruction.Data0.Z))
            ) {
                throw new ArgumentException(
                    message: $"SdfFieldEvaluator cannot interpret instruction {index}'s non-uniform Scale ({instruction.Data0.X}, {instruction.Data0.Y}, {instruction.Data0.Z}) as physical distance. The renderer's minimum-axis correction is a conservative march bound; bake anisotropy into an exact primitive spelling before constructing a query/contact field.",
                    paramName: nameof(instructions)
                );
            }

            if (!IsSupportedOp(op: instruction.Op)) {
                throw new ArgumentException(
                    message: $"SdfFieldEvaluator is warp-free this wave and cannot interpret instruction {index}'s op {instruction.Op}. See SdfFieldEvaluator.cs's KEEP-IN-SYNC header for the full excluded-op rule.",
                    paramName: nameof(instructions)
                );
            }

            if (
                (instruction.Op == SdfOp.ShapeBlend) &&
                !IsSupportedShape(shape: ((SdfShapeType)instruction.Shape))
            ) {
                throw new ArgumentException(
                    message: $"SdfFieldEvaluator cannot interpret instruction {index}'s shape {((SdfShapeType)instruction.Shape)} (its exact core needs runtime trig or texture sampling this wave does not implement). See SdfFieldEvaluator.cs's KEEP-IN-SYNC header.",
                    paramName: nameof(instructions)
                );
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
                Material: ((int)instruction.Material),
                Op: instruction.Op,
                Shape: instruction.Shape
            );
        }

        return compiled;
    }
    // === The blend accumulator (KEEP IN SYNC with mapCore's shared blend tail + blendShape/blendSmoothUnion) ===========
    // Mirrors the shader's semantics EXACTLY, including op order effects: the material winner is resolved from the
    // PRE-blend (current, candidate) pair using the SAME strict compares a SHAPE or a POP_FIELD candidate gets, then
    // the distance blends — never the reverse order.

    private static (FixedQ4816 Distance, int Material) Compose(FixedQ4816 current, int currentMaterial, FixedQ4816 candidate, int candidateMaterial, uint blend, FixedQ4816 smooth) {
        var candidateWins = ResolveWinner(
            blend: blend,
            candidate: candidate,
            current: current
        );
        var winnerMaterial = (candidateWins
            ? candidateMaterial
            : currentMaterial
        );
        var blended = BlendShape(
            blend: blend,
            candidate: candidate,
            current: current,
            smoothRadius: smooth
        );

        return (blended, winnerMaterial);
    }
    private static FixedVector3 DivideComponents(FixedVector3 left, FixedVector3 right) =>
        new(
            X: (left.X / right.X),
            Y: (left.Y / right.Y),
            Z: (left.Z / right.Z)
        );
    // === The shape distance functions (KEEP IN SYNC with the matching sdf* functions in Assets/Shaders/Sdf/sdf-vm.hlsli)
    // ======================================================================================================================

    private static FixedQ4816 EvaluateShape(CompiledInstruction instruction, FixedVector3 p) {
        return ((SdfShapeType)instruction.Shape) switch {
            SdfShapeType.Sphere => SdfSphere(
            p: p,
            radius: instruction.Data0X
        ),
            SdfShapeType.Box or SdfShapeType.ScreenSlab => SdfBox(
            p: p,
            halfExtents: Vector(instruction: instruction),
            cornerRadius: instruction.Data0W
        ),
            SdfShapeType.Torus => SdfTorus(
            p: p,
            major: instruction.Data0X,
            minor: instruction.Data0Y
        ),
            SdfShapeType.Plane => SdfPlane(
            p: p,
            normal: Vector(instruction: instruction),
            offset: instruction.Data0W
        ),
            SdfShapeType.RoundCone => SdfRoundCone(
            p: p,
            lowerRadius: instruction.Data0X,
            upperRadius: instruction.Data0Y,
            height: instruction.Data0Z,
            b: instruction.Data0W,
            a: instruction.Data1Y
        ),
            SdfShapeType.Capsule => SdfCapsule(
            p: p,
            endpoint: Vector(instruction: instruction),
            radius: instruction.Data0W,
            inverseLengthSquared: instruction.Data1Y
        ),
            SdfShapeType.Cylinder => SdfCylinder(
            p: p,
            radius: instruction.Data0X,
            halfHeight: instruction.Data0Y
        ),
            SdfShapeType.Ellipsoid => SdfEllipsoid(
            p: p,
            inverseRadii: new FixedVector3(
                X: instruction.Data1Y,
                Y: instruction.Data1Z,
                Z: instruction.Data1W
            )
        ),
            SdfShapeType.Vesica => SdfVesica(
            p: p,
            r: instruction.Data0X,
            d: instruction.Data0Y,
            b: instruction.Data0Z
        ),
            SdfShapeType.RoundedRectangle => SdfRoundedRectangle(
            p: p,
            halfWidth: instruction.Data0X,
            halfHeight: instruction.Data0Y,
            cornerRadius: instruction.Data0Z,
            liftAmount: instruction.Data0W,
            lift: instruction.Data1Y
        ),
            SdfShapeType.Trapezoid => SdfTrapezoidSolid(
            p: p,
            bottomHalfWidth: instruction.Data0X,
            topHalfWidth: instruction.Data0Y,
            halfHeight: instruction.Data0Z,
            liftAmount: instruction.Data0W,
            lift: instruction.Data1Y
        ),
            _ => throw new UnreachableException(message: $"The constructor validated every shape is supported; shape {((SdfShapeType)instruction.Shape)} reached EvaluateShape unvalidated."),
        };
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
            SdfShapeType.RoundedRectangle or
            SdfShapeType.Trapezoid or
            SdfShapeType.RoundCone or
            SdfShapeType.ScreenSlab => true,
            _ => false,
        };
    }
    // A single stepped sphere-trace march shared by Raycast (radius == 0) and SphereCast (radius > 0). Two asymmetric
    // uses of the field value, and they are NOT interchangeable:
    //   accept  — the RAW clearance (fieldDistance - radius) against HitEpsilon. The field is an OVERestimate of true
    //             distance, so a raw clearance inside the epsilon proves the surface is too.
    //   advance — the SCALED lower bound (fieldDistance * stepScale), floored at one fixed-point tick, minus the
    //             radius. Scaling the clearance instead ((f - r) * s) is anti-conservative for r > 0: it shrinks the
    //             radius by s as well, leaving f/L - r/L, which exceeds the true safe advance f/L - r whenever L > 1.
    //             A radius cast whose scaled bound cannot clear its radius resolves conservatively instead of
    //             advancing through an unproven gap; see the tick floor at the advance for why a point cast cannot
    //             reach that branch.
    // Mirrors BakedWorldQuery.March's shape so the two providers read as the same family of verb despite one walking a
    // baked grid and the other a live field.
    //
    // Returns THREE outcomes, not a Boolean, because the third is not a miss: see MarchOutcome. On Exhausted the hit is
    // filled at the last marched point, so a caller that treats non-convergence as an obstruction has the position and
    // travel it needs without re-marching.
    private MarchOutcome March(FixedPosition origin, FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 radius, out RayHit hit) {
        hit = default;

        var unit = direction.Normalize();

        if (
            (unit == FixedVector3.Zero) ||
            (maxDistance <= FixedQ4816.Zero)
        ) {
            return MarchOutcome.Miss;
        }

        var position = origin;
        var traveled = FixedQ4816.Zero;
        var lastMaterial = 0;

        for (var iteration = 0; (iteration < m_marchIterations); iteration++) {
            if (!TryDistance(
                distance: out var fieldDistance,
                material: out var material,
                position: position
            )) {
                // A shape-free program genuinely has nothing on the ray; anything else is a point the program's frame
                // cannot express, which proves neither hit nor miss.
                return (m_hasShape
                    ? Exhaust(
                    hit: out hit,
                    material: lastMaterial,
                    position: position,
                    traveled: traveled
                )
                    : MarchOutcome.Miss
                );
            }

            lastMaterial = material;

            var clearance = (fieldDistance - radius);

            if (clearance <= HitEpsilon) {
                // Normal is deliberately NOT computed here — see RayHit.Normal's remarks. Call TryFieldGradient at
                // hit.Point if a future consumer needs it.
                hit = new RayHit(
                    Confidence: WorldQueryConfidence.Exact,
                    Distance: traveled,
                    Material: material,
                    Normal: FixedVector3.Zero,
                    Point: position
                );

                return MarchOutcome.Hit;
            }

            if (traveled >= maxDistance) {
                return MarchOutcome.Miss;
            }

            // The tick floor sits on the field, before the radius comes off, and it is what makes this stop condition
            // subordinate to the accept arm for a point cast. The two are otherwise in different units — accept tests
            // the raw field against HitEpsilon (raw 66), this tests the scaled field against zero — and they cross at
            // stepScale 978/65536 (~0.0149): below it a descent stops one raw tick short of a surface the accept arm's
            // own premise places within HitEpsilon + 1 tick, and TryGroundHeight folds that to "no ground" over every
            // column of such a program. Floored, a point advance is at least one tick at every representable step
            // scale, so the only non-accepting end to a point march is the iteration budget MarchIterationsFor sizes.
            // Overstep bound: the floor bites only where the proof-backed advance is already under one tick, and one
            // tick (2^-16 world units) is both the smallest step the format expresses and 1/66 of the band the accept
            // arm already calls contact, so a point march passes the true surface by less than one tick and the next
            // iteration accepts on the negative field inside. Geometry thinner than one tick is under the format.
            // Radius casts are bit-identical: for radius >= one tick, max(floor(f*s), tick) - radius <= 0 exactly when
            // floor(f*s) - radius <= 0, so a sweep still stops before advancing into the contact envelope.
            var safeAdvance = FixedQ4816.Max(
                x: ScaleDistanceDown(
                    distance: fieldDistance,
                    scale: m_stepScale
                ),
                y: FixedQ4816.Epsilon
            ) - radius;

            if (safeAdvance <= FixedQ4816.Zero) {
                return Exhaust(
                    hit: out hit,
                    material: material,
                    position: position,
                    traveled: traveled
                );
            }

            var step = FixedQ4816.Min(
                x: safeAdvance,
                y: (maxDistance - traveled)
            );

            traveled += step;
            position += (unit * step);
        }

        return Exhaust(
            hit: out hit,
            material: lastMaterial,
            position: position,
            traveled: traveled
        );
    }
    // The iteration budget that keeps a point cast's guaranteed pre-exhaustion reach at BaseMarchIterations *
    // HitEpsilon however far the step scale clamps the advance. A non-accepting point iteration advances at least
    // max(floor(HitEpsilon * stepScale), one Q48.16 tick), so the budget is that reach divided by the same floor,
    // rounded up. The one-tick half bounds the budget on its own: no step scale can push it past 33,792 iterations
    // (raw HitEpsilon 66 * BaseMarchIterations, over a one-tick divisor), so a pathologically clamped program cannot
    // spin. Exactly BaseMarchIterations at stepScale 1, so a warp-free program's answers are unchanged to the bit.
    // A radius cast has no equivalent reach guarantee: it stops as soon as the scaled field cannot clear the unscaled
    // radius, which the tick floor deliberately does not lift, so it may report a bounded answer having travelled
    // nothing at all. Across both, an exhausted answer has exactly three causes: that vanished radius clearance, this
    // budget running out, and a marched point the program's frame cannot express.
    private static int MarchIterationsFor(FixedQ4816 stepScale) {
        var reach = (HitEpsilon * FixedQ4816.FromInteger(value: BaseMarchIterations));
        var floor = FixedQ4816.Max(
            x: ScaleDistanceDown(
                distance: HitEpsilon,
                scale: stepScale
            ),
            y: FixedQ4816.Epsilon
        );

        return ((int)(((reach.Value + floor.Value) - 1L) / floor.Value));
    }
    // Both operands are non-negative on every call. FixedQ4816 multiplication rounds to nearest, which can round a
    // Lipschitz lower bound UP by half a tick and thereby authorize an unproved advance. This directed product floors
    // the widened raw value so the fixed result remains a lower bound. Scale is in [0,1], so the narrowed quotient
    // cannot overflow long.
    private static FixedQ4816 ScaleDistanceDown(FixedQ4816 distance, FixedQ4816 scale) =>
        FixedQ4816.FromRawBits(value: ((long)((((Int128)distance.Value) * scale.Value) >> FixedQ4816.FractionBitCount)));
    // Fills the non-convergence hit: the last point the march reached, carrying WorldQueryConfidence.Bounded because
    // the answer is a conservative stand-in for a surface never proven, not a measured one.
    private static MarchOutcome Exhaust(FixedPosition position, FixedQ4816 traveled, int material, out RayHit hit) {
        hit = new RayHit(
            Confidence: WorldQueryConfidence.Bounded,
            Distance: traveled,
            Material: material,
            Normal: FixedVector3.Zero,
            Point: position
        );

        return MarchOutcome.Exhausted;
    }
    private static FixedQ4816 MaxComponent(FixedVector3 value) =>
        FixedQ4816.Max(
            x: value.X,
            y: FixedQ4816.Max(
                x: value.Y,
                y: value.Z
            )
        );
    private static FixedVector3 MaxComponents(FixedVector3 value, FixedQ4816 scalar) =>
        new(
            X: FixedQ4816.Max(
                x: value.X,
                y: scalar
            ),
            Y: FixedQ4816.Max(
                x: value.Y,
                y: scalar
            ),
            Z: FixedQ4816.Max(
                x: value.Z,
                y: scalar
            )
        );
    private static FixedVector3 MultiplyComponents(FixedVector3 left, FixedVector3 right) =>
        new(
            X: (left.X * right.X),
            Y: (left.Y * right.Y),
            Z: (left.Z * right.Z)
        );
    private static FixedVector3 Negate(FixedVector3 value) =>
        new(
            X: -value.X,
            Y: -value.Y,
            Z: -value.Z
        );
    private static FixedQ4816 RadialLength(FixedQ4816 x, FixedQ4816 z) =>
        new FixedVector2(
            X: x,
            Y: z
        ).Length;
    private static bool ResolveWinner(FixedQ4816 current, FixedQ4816 candidate, uint blend) {
        return blend switch {
            ((uint)SdfBlendOp.Intersection) or ((uint)SdfBlendOp.SmoothIntersection) or ((uint)SdfBlendOp.ChamferIntersection) => (candidate > current),
            ((uint)SdfBlendOp.Subtraction) or ((uint)SdfBlendOp.SmoothSubtraction) or ((uint)SdfBlendOp.ChamferSubtraction) => (-candidate > current),
            _ => (candidate < current),
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
    private static FixedVector3 RoundComponents(FixedVector3 value) =>
        new(
            X: FixedQ4816.Round(value: value.X),
            Y: FixedQ4816.Round(value: value.Y),
            Z: FixedQ4816.Round(value: value.Z)
        );
    private static FixedQ4816 SdfBox(FixedVector3 p, FixedVector3 halfExtents, FixedQ4816 cornerRadius) {
        var q = (Abs(value: p) - SubtractScalar(
            scalar: cornerRadius,
            value: halfExtents
        ));
        var outside = MaxComponents(
            value: q,
            scalar: FixedQ4816.Zero
        ).Length;
        var inside = FixedQ4816.Min(
            x: MaxComponent(value: q),
            y: FixedQ4816.Zero
        );

        return ((outside + inside) - cornerRadius);
    }
    private static FixedQ4816 SdfCapsule(FixedVector3 p, FixedVector3 endpoint, FixedQ4816 radius, FixedQ4816 inverseLengthSquared) {
        var h = FixedQ4816.Clamp(
            value: (FixedVector3.Dot(
                left: p,
                right: endpoint
            ) * inverseLengthSquared),
            minimum: FixedQ4816.Zero,
            maximum: FixedQ4816.One
        );

        return ((p - (endpoint * h)).Length - radius);
    }
    private static FixedQ4816 SdfCylinder(FixedVector3 p, FixedQ4816 radius, FixedQ4816 halfHeight) {
        var dx = (RadialLength(
            x: p.X,
            z: p.Z
        ) - radius);
        var dy = (FixedQ4816.Abs(value: p.Y) - halfHeight);
        var inside = FixedQ4816.Min(
            x: FixedQ4816.Max(
                x: dx,
                y: dy
            ),
            y: FixedQ4816.Zero
        );
        var outside = new FixedVector2(
            X: FixedQ4816.Max(
                x: dx,
                y: FixedQ4816.Zero
            ),
            Y: FixedQ4816.Max(
                x: dy,
                y: FixedQ4816.Zero
            )
        ).Length;

        return (inside + outside);
    }
    private static FixedQ4816 SdfEllipsoid(FixedVector3 p, FixedVector3 inverseRadii) {
        var q = MultiplyComponents(
            left: p,
            right: inverseRadii
        );
        var k0 = q.Length;
        var k1 = MultiplyComponents(
            left: q,
            right: inverseRadii
        ).Length;
        var denom = FixedQ4816.Max(
            x: k1,
            y: EllipsoidMinDenom
        );

        return ((k0 * (k0 - FixedQ4816.One)) / denom);
    }
    private static FixedQ4816 SdfExtrude2D(FixedQ4816 distance2D, FixedQ4816 z, FixedQ4816 halfDepth) {
        var w = new FixedVector2(
            X: distance2D,
            Y: (FixedQ4816.Abs(value: z) - halfDepth)
        );
        var inside = FixedQ4816.Min(
            x: FixedQ4816.Max(
                x: w.X,
                y: w.Y
            ),
            y: FixedQ4816.Zero
        );
        var outside = new FixedVector2(
            X: FixedQ4816.Max(
                x: w.X,
                y: FixedQ4816.Zero
            ),
            Y: FixedQ4816.Max(
                x: w.Y,
                y: FixedQ4816.Zero
            )
        ).Length;

        return (inside + outside);
    }
    private static FixedQ4816 SdfPlane(FixedVector3 p, FixedVector3 normal, FixedQ4816 offset) =>
        (FixedVector3.Dot(
            left: p,
            right: normal
        ) + offset);
    private static FixedQ4816 SdfRoundCone(FixedVector3 p, FixedQ4816 lowerRadius, FixedQ4816 upperRadius, FixedQ4816 height, FixedQ4816 b, FixedQ4816 a) {
        var qx = RadialLength(
            x: p.X,
            z: p.Z
        );
        var qy = p.Y;
        var k = ((qx * -b) + (qy * a));

        if (k < FixedQ4816.Zero) {
            return (new FixedVector2(
                X: qx,
                Y: qy
            ).Length - lowerRadius);
        }

        if (k > (a * height)) {
            return (new FixedVector2(
                X: qx,
                Y: (qy - height)
            ).Length - upperRadius);
        }

        return (((qx * a) + (qy * b)) - lowerRadius);
    }
    private static FixedQ4816 SdfRoundedRectangle(FixedVector3 p, FixedQ4816 halfWidth, FixedQ4816 halfHeight, FixedQ4816 cornerRadius, FixedQ4816 liftAmount, FixedQ4816 lift) {
        FixedVector2 point2D;

        if (lift > Half) {
            point2D = new FixedVector2(
                X: p.X,
                Y: p.Y
            );

            return SdfExtrude2D(
                distance2D: SdfRoundedRectangle2D(
                    cornerRadius: cornerRadius,
                    halfHeight: halfHeight,
                    halfWidth: halfWidth,
                    p: point2D
                ),
                z: p.Z,
                halfDepth: liftAmount
            );
        }

        point2D = new FixedVector2(
            X: (RadialLength(
                x: p.X,
                z: p.Z
            ) - liftAmount),
            Y: p.Y
        );

        return SdfRoundedRectangle2D(
            cornerRadius: cornerRadius,
            halfHeight: halfHeight,
            halfWidth: halfWidth,
            p: point2D
        );
    }
    private static FixedQ4816 SdfRoundedRectangle2D(FixedVector2 p, FixedQ4816 halfWidth, FixedQ4816 halfHeight, FixedQ4816 cornerRadius) {
        var q = new FixedVector2(
            X: ((FixedQ4816.Abs(value: p.X) - halfWidth) + cornerRadius),
            Y: ((FixedQ4816.Abs(value: p.Y) - halfHeight) + cornerRadius)
        );
        var outside = new FixedVector2(
            X: FixedQ4816.Max(
                x: q.X,
                y: FixedQ4816.Zero
            ),
            Y: FixedQ4816.Max(
                x: q.Y,
                y: FixedQ4816.Zero
            )
        ).Length;
        var inside = FixedQ4816.Min(
            x: FixedQ4816.Max(
                x: q.X,
                y: q.Y
            ),
            y: FixedQ4816.Zero
        );

        return ((inside + outside) - cornerRadius);
    }
    private static FixedQ4816 SdfSphere(FixedVector3 p, FixedQ4816 radius) =>
        (p.Length - radius);
    private static FixedQ4816 SdfTorus(FixedVector3 p, FixedQ4816 major, FixedQ4816 minor) {
        var q = new FixedVector2(
            X: (RadialLength(
                x: p.X,
                z: p.Z
            ) - major),
            Y: p.Y
        );

        return (q.Length - minor);
    }
    private static FixedQ4816 SdfTrapezoid2D(FixedVector2 p, FixedQ4816 r1, FixedQ4816 r2, FixedQ4816 halfHeight) {
        var k1 = new FixedVector2(
            X: r2,
            Y: halfHeight
        );
        var k2 = new FixedVector2(
            X: (r2 - r1),
            Y: (Two * halfHeight)
        );

        p = p with { X = FixedQ4816.Abs(value: p.X) };

        var ca = new FixedVector2(
            X: (p.X - FixedQ4816.Min(
                x: p.X,
                y: ((p.Y < FixedQ4816.Zero)
            ? r1
            : r2)
            )),
            Y: (FixedQ4816.Abs(value: p.Y) - halfHeight)
        );
        // k2 is the slanted side; the projection divides by its squared length. SdfProgramBuilder.MinTrapezoidProfileSlant
        // keeps every admitted profile clear of the rounding window where that length reads zero, so the zero arm is
        // unreachable through the builder — it is here because an integer divide has no NaN to propagate, and a
        // total function is the only shape this may take on a query path the authoritative server calls per tick.
        var slantLengthSquared = FixedVector2.Dot(
            left: k2,
            right: k2
        );
        var projection = ((slantLengthSquared == FixedQ4816.Zero)
            ? FixedQ4816.Zero
            : FixedQ4816.Clamp(
            value: (FixedVector2.Dot(
                left: (k1 - p),
                right: k2
            ) / slantLengthSquared),
            minimum: FixedQ4816.Zero,
            maximum: FixedQ4816.One
        ));
        var cb = ((p - k1) + (k2 * projection));
        var sign = (((cb.X < FixedQ4816.Zero) && (ca.Y < FixedQ4816.Zero))
            ? -FixedQ4816.One
            : FixedQ4816.One
        );

        return (sign * FixedQ4816.Sqrt(value: FixedQ4816.Min(
            x: FixedVector2.Dot(
                left: ca,
                right: ca
            ),
            y: FixedVector2.Dot(
                left: cb,
                right: cb
            )
        )));
    }
    private static FixedQ4816 SdfTrapezoidSolid(FixedVector3 p, FixedQ4816 bottomHalfWidth, FixedQ4816 topHalfWidth, FixedQ4816 halfHeight, FixedQ4816 liftAmount, FixedQ4816 lift) {
        if (lift > Half) {
            var distance2D = SdfTrapezoid2D(
                p: new FixedVector2(
                    X: p.X,
                    Y: p.Y
                ),
                r1: bottomHalfWidth,
                r2: topHalfWidth,
                halfHeight: halfHeight
            );

            return SdfExtrude2D(
                distance2D: distance2D,
                z: p.Z,
                halfDepth: liftAmount
            );
        }

        return SdfTrapezoid2D(
            p: new FixedVector2(
                X: (RadialLength(
                    x: p.X,
                    z: p.Z
                ) - liftAmount),
                Y: p.Y
            ),
            r1: bottomHalfWidth,
            r2: topHalfWidth,
            halfHeight: halfHeight
        );
    }
    private static FixedQ4816 SdfVesica(FixedVector3 p, FixedQ4816 r, FixedQ4816 d, FixedQ4816 b) {
        var qx = RadialLength(
            x: p.X,
            z: p.Z
        );
        var qy = FixedQ4816.Abs(value: p.Y);

        return ((((qy - b) * d) > (qx * b))
            ? new FixedVector2(
                X: qx,
                Y: (qy - b)
            ).Length
            : (new FixedVector2(
                X: (qx + d),
                Y: qy
            ).Length - r)
        );
    }
    private static FixedVector3 SubtractScalar(FixedVector3 value, FixedQ4816 scalar) =>
        new(
            X: (value.X - scalar),
            Y: (value.Y - scalar),
            Z: (value.Z - scalar)
        );
    // One central-difference pair: d(p + offset) - d(p - offset). Both taps must answer.
    private bool TryAxisDifference(FixedPosition position, FixedVector3 offset, out FixedQ4816 difference) {
        difference = FixedQ4816.Zero;

        if (
            !TryDistance(
            distance: out var positive,
            material: out _,
            position: (position + offset)
        ) ||
            !TryDistance(
            distance: out var negative,
            material: out _,
            position: (position + (-offset))
        )
        ) {
            return false;
        }

        difference = (positive - negative);

        return true;
    }
    // === Elementwise FixedVector3 helpers (System.Numerics.Vector3 offers these as instance methods; FixedVector3
    // does not, so this file supplies the handful the interpreted op set needs) ==========================================

    private static FixedVector3 Vector(CompiledInstruction instruction) =>
        new(
            X: instruction.Data0X,
            Y: instruction.Data0Y,
            Z: instruction.Data0Z
        );
    private static FixedVector3 Vector1(CompiledInstruction instruction) =>
        new(
            X: instruction.Data1X,
            Y: instruction.Data1Y,
            Z: instruction.Data1Z
        );

    /// <inheritdoc/>
    /// <remarks>A probe that cannot converge reports BLOCKED, because it rides
    /// <see cref="Raycast(FixedPosition, FixedVector3, FixedQ4816, out RayHit)"/>'s conservative non-convergence
    /// contract: "clear" is the assertion this verb makes, so it is the one an unfinished march may not make.</remarks>
    public bool LineOfSight(FixedPosition from, FixedPosition to) {
        var delta = (to - from);
        var distance = delta.Length;

        if (distance <= FixedQ4816.Zero) {
            return true;
        }

        var probeDistance = (distance - LineOfSightSkin);

        if (probeDistance <= FixedQ4816.Zero) {
            return true;
        }

        return !Raycast(
            dir: delta,
            hit: out _,
            maxDist: probeDistance,
            origin: from
        );
    }
    /// <inheritdoc/>
    /// <remarks>The test is the SCALED field value against the radius, so the sphere is effectively widened by the
    /// program's Lipschitz factor: occupancy may over-report by that factor and never under-reports, which is the
    /// direction a placement/spawn consumer can survive being wrong about. If the center cannot be rebased into the
    /// program's signed-Q48.16 frame, a shape-bearing program reports occupied: inability to evaluate must not become
    /// a false-clear placement result. A shape-free program still reports clear everywhere.</remarks>
    public bool Overlap(FixedPosition center, FixedQ4816 radius) {
        if (!TryDistance(
            distance: out var distance,
            material: out _,
            position: center
        )) {
            return m_hasShape;
        }

        return (ScaleDistanceDown(
            distance: distance,
            scale: m_stepScale
        ) <= FixedQ4816.Max(
            x: radius,
            y: FixedQ4816.Zero
        ));
    }
    /// <inheritdoc/>
    /// <remarks>A march that cannot safely complete its hit-or-clear proof reports a HIT at the last marched point,
    /// carrying <see cref="WorldQueryConfidence.Bounded"/>. This occurs when the scaled field can no longer clear the
    /// sweep radius (a radius cast only — a point march always advances by at least one fixed-point tick), when the
    /// iteration budget ends, or when the march reaches a point the program's frame
    /// cannot express. Reporting the miss instead would let a grazing ray
    /// that never reached its obstruction claim the line was clear — the one answer a contact, visibility, or sweep
    /// consumer cannot recover from.</remarks>
    public bool Raycast(FixedPosition origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit) =>
        (March(
            origin: origin,
            direction: dir,
            maxDistance: maxDist,
            radius: FixedQ4816.Zero,
            hit: out hit
        ) != MarchOutcome.Miss);
    /// <inheritdoc/>
    /// <remarks>Non-convergence resolves as a hit, exactly as in
    /// <see cref="Raycast(FixedPosition, FixedVector3, FixedQ4816, out RayHit)"/>.</remarks>
    public bool SphereCast(FixedPosition origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit) =>
        (March(
            direction: dir,
            hit: out hit,
            maxDistance: maxDist,
            origin: origin,
            radius: FixedQ4816.Max(
                x: radius,
                y: FixedQ4816.Zero
            )
        ) != MarchOutcome.Miss);
    /// <inheritdoc/>
    /// <remarks>The evaluated point is the exact world-space displacement from the world origin — <c>position</c>
    /// REBASED against <see cref="FixedPosition.Zero"/>, not its raw <see cref="FixedPosition.Local"/> offset. The
    /// wrapped <see cref="SdfProgram"/> bakes its geometry in world space around that origin, so reading <c>.Local</c>
    /// alone would alias the whole field with the 2^<see cref="FixedPosition.CellSizeLog2"/>-unit cell period and
    /// answer for the wrong copy; <see cref="FixedPosition.FromLocal"/> creates a nonzero cell on its own past half a
    /// cell, so no caller has to opt in to reach that. Rebasing is exact integer arithmetic and is the identity for a
    /// position already in cell <c>(0,0,0)</c>. Returns <see langword="false"/> when the program declares no shape, or
    /// when the displacement is outside signed Q48.16 (past ~1.4e14 units from the origin), which no authored program
    /// can hold geometry at.</remarks>
    public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
        distance = FixedQ4816.Zero;
        material = 0;

        if (
            !m_hasShape ||
            !position.TryDelta(
            delta: out var worldPosition,
            origin: FixedPosition.Zero
        )
        ) {
            return false;
        }

        var localPosition = worldPosition;
        var distanceScale = FixedQ4816.One;
        var resultDistance = FarDistance;
        var resultMaterial = 0;
        var savedFieldDistance = FarDistance;
        var savedFieldMaterial = 0;

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
                        localPosition = RotateByInverseQuaternion(
                            instruction: instruction,
                            p: localPosition
                        );
                        break;
                    }
                case SdfOp.Scale: {
                        localPosition = DivideComponents(
                            left: localPosition,
                            right: Vector(instruction: instruction)
                        );
                        distanceScale *= instruction.Data0W;
                        break;
                    }
                case SdfOp.Repeat: {
                        var spacing = Vector(instruction: instruction);
                        var inverseSpacing = Vector1(instruction: instruction);

                        localPosition -= MultiplyComponents(
                            left: spacing,
                            right: RoundComponents(value: MultiplyComponents(
                                left: localPosition,
                                right: inverseSpacing
                            ))
                        );
                        break;
                    }
                case SdfOp.RepeatLimited: {
                        var spacing = Vector(instruction: instruction);
                        var limit = Vector1(instruction: instruction);
                        var rounded = RoundComponents(value: DivideComponents(
                            left: localPosition,
                            right: spacing
                        ));

                        localPosition -= MultiplyComponents(
                            left: spacing,
                            right: ClampComponents(
                                value: rounded,
                                minimum: Negate(value: limit),
                                maximum: limit
                            )
                        );
                        break;
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

                        localPosition -= (normal * twiceMin);
                        break;
                    }
                case SdfOp.Elongate: {
                        var extents = Vector(instruction: instruction);

                        localPosition -= ClampComponents(
                            value: localPosition,
                            minimum: Negate(value: extents),
                            maximum: extents
                        );
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

                        // Data1.y is the scope's baked 1/L candidate scale (KEEP IN SYNC with mapCore's pop); zero =
                        // unpatched, no scale. The directed-floor multiply rounds a positive candidate down —
                        // conservative for the march, like every scaled advance in this evaluator.
                        if (instruction.Data1Y > FixedQ4816.Zero) {
                            candidateDistance *= instruction.Data1Y;
                        }

                        resultDistance = savedFieldDistance;
                        resultMaterial = savedFieldMaterial;
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
                case SdfOp.ShapeBlend: {
                        var candidateDistance = (EvaluateShape(
                            instruction: instruction,
                            p: localPosition
                        ) * distanceScale);

                        (resultDistance, resultMaterial) = Compose(
                            current: resultDistance,
                            currentMaterial: resultMaterial,
                            candidate: candidateDistance,
                            candidateMaterial: instruction.Material,
                            blend: instruction.Blend,
                            smooth: instruction.Data1X
                        );
                        break;
                    }
                default: {
                        throw new UnreachableException(message: $"The constructor validated every instruction's op is supported; op {instruction.Op} reached the interpreter unvalidated.");
                    }
            }
        }

        distance = resultDistance;
        material = resultMaterial;

        return true;
    }
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) =>
        TryFieldGradient(
            epsilon: GradientEpsilon,
            gradient: out gradient,
            position: position
        );
    /// <summary>Evaluates the field's GRADIENT at <paramref name="position"/> with a caller-chosen probe step — the
    /// per-call peer of <see cref="TryFieldGradient(FixedPosition, out FixedVector3)"/> for a consumer authoring geometry
    /// at a scale the baked default probe does not suit. A non-positive <paramref name="epsilon"/> takes the evaluator's
    /// documented default (0.01 world units); the interface method is exactly this overload at that default.</summary>
    /// <param name="position">The world-space point to evaluate.</param>
    /// <param name="epsilon">The finite-difference probe span in world units, or a non-positive value for the default.</param>
    /// <param name="gradient">The unit-length gradient, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when every probe <see cref="TryDistance"/> call succeeded and the raw gradient was
    /// non-zero.</returns>
    public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) {
        gradient = FixedVector3.Zero;

        var step = ((epsilon > FixedQ4816.Zero)
            ? epsilon
            : GradientEpsilon
        );

        if (
            !TryAxisDifference(
            position: position,
            offset: new FixedVector3(
                X: step,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            difference: out var differenceX
        ) ||
            !TryAxisDifference(
            position: position,
            offset: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: step,
                Z: FixedQ4816.Zero
            ),
            difference: out var differenceY
        ) ||
            !TryAxisDifference(
            position: position,
            offset: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.Zero,
                Z: step
            ),
            difference: out var differenceZ
        )
        ) {
            return false;
        }

        var normalized = new FixedVector3(
            X: differenceX,
            Y: differenceY,
            Z: differenceZ
        ).Normalize();

        if (normalized == FixedVector3.Zero) {
            return false;
        }

        gradient = normalized;

        return true;
    }
    /// <inheritdoc/>
    /// <remarks>Requires a CONVERGED downward march, unlike the cast and visibility verbs. This verb's return value is
    /// a surface, not an obstruction: it has no confidence channel to mark a stand-in with, and a caller that grounds a
    /// body onto a fabricated Y is moved to a place the world does not have. A descent that runs out of iterations, or
    /// reaches a point the program's frame cannot express, proves nothing about what is below, so it answers
    /// <see langword="false"/> — "no ground within the probe range", the same answer an empty column gives.</remarks>
    public bool TryGroundHeight(FixedPosition position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY) {
        groundY = FixedQ4816.Zero;

        var probeRange = (probeUp + probeDown);

        if (probeRange <= FixedQ4816.Zero) {
            return false;
        }

        var top = (position + new FixedVector3(
            X: FixedQ4816.Zero,
            Y: probeUp,
            Z: FixedQ4816.Zero
        ));

        if (March(
            origin: top,
            direction: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: -FixedQ4816.One,
                Z: FixedQ4816.Zero
            ),
            maxDistance: probeRange,
            radius: FixedQ4816.Zero,
            hit: out var hit
        ) != MarchOutcome.Hit) {
            return false;
        }

        // World Y, rebased against the world origin exactly as TryDistance rebases its query point — never the hit's
        // raw .Local, which is relative to whichever cell the descending march re-anchored into.
        if (!hit.Point.TryDelta(
            delta: out var world,
            origin: FixedPosition.Zero
        )) {
            return false;
        }

        groundY = world.Y;

        return true;
    }

    // What a March call proved. Miss and Hit are assertions about the ray; Exhausted is the absence of one — a radius
    // cast lost its scaled clearance, the iteration budget ran out, or the march reached a point the frame cannot express,
    // with the field neither accepted nor cleared, so it proves NEITHER. Every consumer folds it into whichever of the two its own contract can survive being wrong
    // about: Hit for an obstruction/contact question (Raycast/SphereCast/LineOfSight), Miss for TryGroundHeight, whose
    // true half asserts a SURFACE.
    private enum MarchOutcome {
        Miss = 0,
        Hit = 1,
        Exhausted = 2,
    }

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
