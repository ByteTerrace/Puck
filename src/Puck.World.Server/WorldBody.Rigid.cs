using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The authored rigid-contact tunables (<see cref="WorldBodyContactPolicy"/>'s rigid-specific fields),
/// converted to fixed point/engine ticks once per document (re)compile — see
/// <see cref="Puck.World.Server.WorldPopulation"/>'s own compiled tables — instead of every rigid step.</summary>
/// <param name="RestSpeedThreshold">Below this speed of its fastest point (the centre's linear speed plus its angular
/// speed times its bounding radius), at scale one, a supported rigid body counts toward the resting hold window.</param>
/// <param name="RestHoldTicks">How long, in exact engine ticks, a rigid body must stay under the rest speed while
/// supported before the resting latch actually closes.</param>
/// <param name="SubstepTravelFraction">The fraction of a rigid body's own bounding radius its solved motion may depart
/// from the motion its contact probe certified before a guard sweep re-certifies it.</param>
/// <param name="SubstepMinimumTravel">The floor under that departure bound, independent of
/// <see cref="SubstepTravelFraction"/> — see <see cref="WorldBodyContactPolicy.RigidSubstepMinimumTravel"/>.</param>
/// <param name="SubstepCeiling">The rigid solver's substeps per tick — see
/// <see cref="WorldBodyContactPolicy.RigidSubstepCeiling"/>.</param>
/// <param name="PairRestitutionThreshold">Below this closing speed a rigid contact restitutes at zero rather than its
/// pair's coefficient — see <see cref="WorldBodyContactPolicy.RigidPairRestitutionSpeed"/>.</param>
public readonly record struct RigidContactPolicy(
    FixedQ4816 RestSpeedThreshold,
    ulong RestHoldTicks,
    FixedQ4816 SubstepTravelFraction,
    FixedQ4816 SubstepMinimumTravel,
    int SubstepCeiling,
    FixedQ4816 PairRestitutionThreshold
) {
    /// <summary>Compiles an authored <see cref="WorldBodyContactPolicy"/>'s rigid fields to this fixed-point form.</summary>
    /// <param name="policy">The authored policy.</param>
    /// <returns>The compiled policy.</returns>
    public static RigidContactPolicy FromAuthored(WorldBodyContactPolicy policy) => new(
        RestSpeedThreshold: FixedQ4816.FromDouble(value: policy.RigidRestSpeed),
        RestHoldTicks: FixedTickConversion.DurationEngineTicks(seconds: ((decimal)policy.RigidRestHoldSeconds)),
        SubstepTravelFraction: FixedQ4816.FromDouble(value: policy.RigidSubstepTravelFraction),
        SubstepMinimumTravel: FixedQ4816.FromDouble(value: policy.RigidSubstepMinimumTravel),
        SubstepCeiling: policy.RigidSubstepCeiling,
        PairRestitutionThreshold: FixedQ4816.FromDouble(value: policy.RigidPairRestitutionSpeed)
    );
}
/// <summary>One static surface a rigid body's contact generation found this step: its unit normal toward the body, the
/// gap of the body's deepest support point to it at the step's start, and a source id stable while the surface stays.</summary>
/// <param name="SourceId">The surface's source id: its collider for a per-surface query, the walkable or non-walkable
/// channel for a sweep probe.</param>
/// <param name="Normal">The unit surface normal, toward the body.</param>
/// <param name="Separation">The deepest support point's gap at the step's start; negative overlaps.</param>
internal readonly record struct RigidSurface(int SourceId, FixedVector3 Normal, FixedQ4816 Separation);
/// <summary>What one rigid body's contact generation found this step: the static surfaces within the step's reach, and
/// the root the step predicted (for a sweep probe, the root it certified the predicted motion to): a solved motion that
/// departs from it far enough is re-certified by a guard sweep.</summary>
/// <param name="Refused">Whether the probe's sweep was refused; a refused body is immovable this tick.</param>
/// <param name="Count">The number of surfaces found, at most <see cref="WorldBody.MaximumRigidSurfaces"/>.</param>
/// <param name="ResolvedRoot">The predicted root, resolved by the probe sweep when one ran.</param>
internal readonly record struct RigidSurfaceProbe(bool Refused, int Count, FixedVector3 ResolvedRoot);
public sealed partial class WorldBody {
    // The persistent solver view TwoBodyHandle() maintains — allocated once, mutated in place every call, so steady-state
    // rigid contact allocates nothing.
    private FixedRigidBody? m_rigidHandle;
    // The step the rigid solve is preparing: its engine ticks (zero when no step is prepared) and the body's state at its
    // start, which a refused sweep restores.
    private ulong m_rigidStepTicks;
    private FixedVector3 m_rigidStartPosition;
    private FixedQuaternion m_rigidStartOrientation;
    private FixedVector3 m_rigidStartVelocity;
    private FixedVector3 m_rigidStartAngularVelocity;
    private FixedVector3 m_rigidAcceleration;

    /// <summary>Gets whether this body's kit carries a <c>rigid</c> facet — a passive rigid entity the population's rigid
    /// solve advances instead of a locomotion motion program.</summary>
    public bool IsRigid => (m_rigid is not null);
    /// <summary>Gets the rigid body's current world-space linear velocity. Zero for a locomotion kit.</summary>
    public FixedVector3 RigidVelocity => m_rigidVelocity;
    /// <summary>Gets the rigid body's current world-space angular velocity. Zero for a locomotion kit.</summary>
    public FixedVector3 RigidAngularVelocity => m_angularVelocity;
    /// <summary>Gets whether the rigid solver has latched this body to rest. Always <see langword="false"/> for a
    /// locomotion kit.</summary>
    public bool Resting => m_resting;
    /// <summary>Gets the elapsed engine ticks this body has spent under the rest thresholds so far — zero the
    /// instant it wakes, latched at <see cref="Resting"/>'s own hold window once it closes.</summary>
    public ulong RigidRestingHoldTicks => m_restingHoldTicks;
    /// <summary>Gets whether a rigid step is prepared for this tick and not yet finished.</summary>
    internal bool RigidStepPrepared => (m_rigidStepTicks != 0UL);
    /// <summary>Gets the rigid facet's mass at this body's live <see cref="Scale"/> — the authored mass at scale 1,
    /// scaled by <c>Scale³</c> (see <see cref="ScaleRigid"/>). Zero for a locomotion kit.</summary>
    public FixedQ4816 RigidMass => ((m_rigid is { } rigid)
        ? ScaleRigid(rigid: rigid).Mass
        : FixedQ4816.Zero
    );
    /// <summary>Gets the rigid facet's authored restitution against another rigid body — a dimensionless coefficient,
    /// unaffected by <see cref="Scale"/>. Zero for a locomotion kit.</summary>
    public FixedQ4816 RigidRestitution => (m_rigid?.Restitution ?? FixedQ4816.Zero);
    /// <summary>Gets the rigid facet's authored friction against another rigid body — a dimensionless coefficient,
    /// unaffected by <see cref="Scale"/>. Zero for a locomotion kit.</summary>
    public FixedQ4816 RigidFriction => (m_rigid?.Friction ?? FixedQ4816.Zero);
    /// <summary>Gets the rigid facet's conservative bounding radius at this body's live <see cref="Scale"/> — the
    /// same radius the pair-contact anchor approximation and the static-contact substep bound both read, scaled
    /// linearly with <see cref="Scale"/> (see <see cref="ScaleRigid"/>). Zero for a locomotion kit.</summary>
    public FixedQ4816 RigidBoundingRadius => ((m_rigid is { } rigid)
        ? ScaleRigid(rigid: rigid).BoundingRadius
        : FixedQ4816.Zero
    );
    /// <summary>Gets this body's live-<see cref="Scale"/>-consistent rigid-facet centre-of-mass offset from its root,
    /// body-local axes — the same offset the rigid solve rotates and translates the body about, and what
    /// <see cref="FixedRigidWitness.Anchor"/> subtracts from a local support point. Zero for a locomotion kit.</summary>
    public FixedVector3 RigidCenterOffset => ((m_rigid is { } rigid)
        ? ScaleRigid(rigid: rigid).CenterOffset
        : FixedVector3.Zero
    );

    // Derives a scale-consistent copy of a compiled rigid facet for this body's live Scale: mass ∝ Scale³ against the
    // authored mass at scale 1 (a uniformly bigger body of the same material is heavier by its volume ratio), inertia
    // (mass·length²) ∝ Scale⁵ so inverse inertia ∝ Scale⁻⁵, and CenterOffset/BoundingRadius ∝ Scale — the same linear
    // rule ScaledColliderVolumes applies to the collider itself, so a rigid body's mass distribution and its collider
    // stay geometrically consistent. Restitution, friction, rolling friction, and both damping rates are dimensionless
    // per-second/coefficient quantities — unaffected by Scale. Scale == One (the overwhelming common case) returns
    // rigid unchanged, touching no arithmetic.
    //
    // InverseInertia's Scale⁻⁵ law reaches FixedRigidScales.RoomScale's 40-fraction-bit placement (23 integer bits,
    // magnitude ceiling ≈ 8.39e6) well before InverseMass's Scale⁻³ does; a garden billiard ball's unscaled 1/I ≈ 370
    // already exceeds it below Scale ≈ 0.135, inside the garden's own authored floor of 0.05. ScaleRaw below
    // saturates to RepresentableInverseCeiling on that overflow instead of reverting to the unscaled raw: reverting
    // would leave InverseInertia at the full-size body's magnitude while InverseMass (whose overflow threshold sits
    // far lower and is not reached in the validated envelope) already reflects the shrunk body, so friction couples
    // a light mass to a spin resistance the solver still reads as heavy and can drive the linear velocity through
    // zero. Both fields saturate the same way past their own overflow point, so every scaled component stays
    // consistent in direction (lighter, easier to spin) even once the exact Scale⁻³/Scale⁻⁵ magnitude no longer
    // fits.
    private FixedWorldRigid ScaleRigid(FixedWorldRigid rigid) {
        if (m_scale == FixedQ4816.One) {
            return rigid;
        }

        var scaleSquared = SaturatingNonnegativeProduct(
            left: m_scale,
            right: m_scale
        );
        var scaleCubed = SaturatingNonnegativeProduct(
            left: scaleSquared,
            right: m_scale
        );

        // inverseScale^3 / inverseScale^5 are built by INVERTING SCALE ITSELF ONCE and then multiplying that
        // reciprocal (>= 1 for every authored Scale <= 1) by itself — never by inverting the scale^3/scale^5
        // magnitude directly, which underflows to a zero raw well inside the authored envelope (Scale = 0.05 has
        // scale^5 ≈ 3.125e-7, below Q16's smallest representable positive value 2^-16 ≈ 1.526e-5) and divides by
        // that zero. TryScaledReciprocal refuses rather than dividing by a non-positive raw (Scale is always
        // validated positive; guarded here regardless), and every power below multiplies a value that only grows,
        // so it cannot divide by an underflowed zero. A reciprocal refusal falls back to One only for a corrupt
        // non-positive Scale; a power overflow saturates upward, preserving the inverse quantity's direction before
        // ScaleRaw applies its solver-safe ceiling.
        var inverseScale = (FusedArithmetic.TryScaledReciprocal(
            value: m_scale.Value,
            fractionBitsIn: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var inverseScaleRaw
        )
            ? FixedQ4816.FromRawBits(value: inverseScaleRaw)
            : FixedQ4816.One
        );
        // The powers are accumulated at the inverse quantities' own 40-bit placement, not Q16: at Q16 a grown body's
        // Scale⁻⁵ (1e-5 at Scale 10) is below the smallest positive value and would zero the inverse inertia, freezing
        // its orientation; at 40 bits the same power keeps 15 significant bits.
        var inverseScaleCubed = InversePower(
            inverseScaleRaw: inverseScale.Value,
            exponent: 3
        );
        var inverseScaleFifth = InversePower(
            inverseScaleRaw: inverseScale.Value,
            exponent: 5
        );

        static long ScaleRaw(long raw, int fractionBits, long factorRaw) => (FusedArithmetic.TryMixedScaleProduct(
            a: raw,
            b: factorRaw,
            fractionBitsA: fractionBits,
            fractionBitsB: InverseFractionBits,
            fractionBitsOut: fractionBits,
            result: out var scaled
        )
            ? scaled
            : RepresentableInverseCeiling
        );

        return rigid with {
            Mass = SaturatingNonnegativeProduct(
            left: rigid.Mass,
            right: scaleCubed
        ),
            InverseMassRaw = ScaleRaw(
            raw: rigid.InverseMassRaw,
            fractionBits: FixedWorldRigid.Scales.InverseMass,
            factorRaw: inverseScaleCubed
        ),
            InverseInertiaXX = ScaleRaw(
            raw: rigid.InverseInertiaXX,
            fractionBits: FixedWorldRigid.Scales.InverseInertia,
            factorRaw: inverseScaleFifth
        ),
            InverseInertiaYY = ScaleRaw(
            raw: rigid.InverseInertiaYY,
            fractionBits: FixedWorldRigid.Scales.InverseInertia,
            factorRaw: inverseScaleFifth
        ),
            InverseInertiaZZ = ScaleRaw(
            raw: rigid.InverseInertiaZZ,
            fractionBits: FixedWorldRigid.Scales.InverseInertia,
            factorRaw: inverseScaleFifth
        ),
            CenterOffset = (rigid.CenterOffset * m_scale),
            BoundingRadius = (rigid.BoundingRadius * m_scale),
        };
    }

    // The fraction bits the inverse-scale powers are carried at: the inverse mass and inverse inertia placements
    // share it, so a power lands on the raw it scales without a second rounding.
    private const int InverseFractionBits = 40;

    // inverseScale^exponent at InverseFractionBits by repeated single-rounding multiplication of a Q16 base,
    // saturating on overflow. An inverse scale only grows as a body shrinks, so reverting to One at the
    // representation edge would move inverse mass/inertia in the physically wrong direction (back to the full-size
    // value).
    private static long InversePower(long inverseScaleRaw, int exponent) {
        var accumulatorRaw = (1L << InverseFractionBits);

        for (var step = 0; (step < exponent); step++) {
            if (!FusedArithmetic.TryMixedScaleProduct(
                a: accumulatorRaw,
                b: inverseScaleRaw,
                fractionBitsA: InverseFractionBits,
                fractionBitsB: FixedQ4816.FractionBitCount,
                fractionBitsOut: InverseFractionBits,
                result: out var scaled
            )) {
                return long.MaxValue;
            }

            accumulatorRaw = scaled;
        }

        return accumulatorRaw;
    }

    /// <summary>Gets the rigid facet's world-space centre of mass — <c>root + orientation·CenterOffset</c>, the
    /// point the rigid solve actually rotates and translates about. For a rolling or
    /// tumbling body this orbits away from <see cref="WorldBody.FixedPosition"/> (the root); the pose <c>body.where</c>
    /// echoes stays the root, with <c>com=</c> the read-back for this. Equal to the root for a locomotion kit.</summary>
    public FixedVector3 RigidCenterOfMass => ((m_rigid is { } rigid)
        ? (m_position + m_orientation.Rotate(vector: ScaleRigid(rigid: rigid).CenterOffset))
        : m_position
    );

    /// <summary>Applies an instantaneous world-space impulse to this rigid body's linear velocity
    /// (<c>Δv = impulse / mass</c>) and wakes it from rest. A no-op for a locomotion kit.</summary>
    /// <param name="impulse">The impulse, in mass·length/time units.</param>
    /// <param name="velocityCeiling">The speed the resulting velocity may not exceed — typically
    /// <see cref="WorldPopulation.RigidVelocityCeiling"/>. Bounds every solver step downstream of this call (the
    /// static-contact sweep's own substep travel, in particular) to a magnitude the fixed-point representation
    /// already carries elsewhere in the document, so an oversized authored or client-submitted impulse is refused
    /// here rather than reaching the solver as an unrepresentable velocity.</param>
    /// <returns><see langword="true"/> when every axis's scaled velocity delta fit the fixed-point representation
    /// AND the resulting velocity stayed at or under <paramref name="velocityCeiling"/>, and was applied;
    /// <see langword="false"/> otherwise, leaving velocity entirely unchanged (no partial application) — the caller
    /// refuses the command by name in that case.</returns>
    public bool TryApplyRigidImpulse(FixedVector3 impulse, FixedQ4816 velocityCeiling) {
        if (m_rigid is null) {
            return true;
        }
        if (!TryProjectRigidImpulse(
            impulse: impulse,
            projected: out var candidate,
            velocity: m_rigidVelocity,
            velocityCeiling: velocityCeiling
        )) {
            return false;
        }

        m_rigidVelocity = candidate;
        NotifyPopulationWake();
        m_resting = false;
        m_restingHoldTicks = 0UL;
        return true;
    }
    /// <summary>Returns the velocity an impulse would leave a rigid body at, starting from
    /// <paramref name="velocity"/> rather than the body's own, and changes nothing: the admission
    /// <see cref="TryApplyRigidImpulse"/> decides by, asked ahead of the act.</summary>
    /// <param name="impulse">The world-space impulse.</param>
    /// <param name="velocityCeiling">The world's declared speed ceiling.</param>
    /// <param name="velocity">The velocity the impulse lands on.</param>
    /// <param name="projected">The resulting velocity, on success; <paramref name="velocity"/> for a body with no
    /// rigid facet.</param>
    /// <returns><see langword="true"/> when the impulse is representable and the result stays at or under the
    /// ceiling.</returns>
    public bool TryProjectRigidImpulse(FixedVector3 impulse, FixedQ4816 velocityCeiling, FixedVector3 velocity, out FixedVector3 projected) {
        projected = velocity;

        if (m_rigid is not { } rigid) {
            return true;
        }

        return (
            TryImpulseToVelocity(
                impulse: impulse,
                inverseMassRaw: ScaleRigid(rigid: rigid).InverseMassRaw,
                delta: out var delta
            ) &&
            FixedVector3.TryAdd(
                left: velocity,
                right: delta,
                sum: out projected
            ) &&
            (projected.Length <= velocityCeiling)
        );
    }

    // The tick-rate-independent decay factor an authored per-second rate applies over an elapsed duration:
    // (1 - rate*seconds), clamped to [0, 1] so an aggressive rate at a wide step never reverses the quantity it
    // damps. Shared by linear/angular damping and (contact) friction/rolling friction — the same rate authored once
    // means the same physical decay whatever the world's simulation rate.
    private static FixedQ4816 RateDecay(FixedQ4816 rate, FixedQ4816 seconds) {
        if (
            (rate <= FixedQ4816.Zero) ||
            (seconds <= FixedQ4816.Zero)
        ) {
            return FixedQ4816.One;
        }

        // A product past the carrier saturates to the maximum, which the clamp reads as a full decay.
        return FixedQ4816.Max(
            x: FixedQ4816.Zero,
            y: (FixedQ4816.One - FixedSaturate.Multiply(
                left: rate,
                right: seconds
            ))
        );
    }

    /// <summary>Multiplies non-negative Q48.16 quantities without wraparound, saturating only when the exact
    /// rounded product leaves the representation. Used for scale-derived mass/reach and Coulomb impulse ceilings,
    /// where saturation preserves the quantity's monotone physical direction.</summary>
    internal static FixedQ4816 SaturatingNonnegativeProduct(FixedQ4816 left, FixedQ4816 right) =>
        (((left <= FixedQ4816.Zero) || (right <= FixedQ4816.Zero))
            ? FixedQ4816.Zero
            : FixedSaturate.Multiply(
                left: left,
                right: right
            ));

    // impulse * inverseMass, both at their own declared scales, rounded once to FixedQ4816. Refuses (rather than
    // silently reading a zero raw) when any axis's correctly-rounded product overflows the signed 64-bit raw.
    private static bool TryImpulseToVelocity(FixedVector3 impulse, long inverseMassRaw, out FixedVector3 delta) {
        bool TryScale(FixedQ4816 component, out FixedQ4816 result) {
            if (!FusedArithmetic.TryMixedScaleProduct(
                a: component.Value,
                fractionBitsA: FixedQ4816.FractionBitCount,
                b: inverseMassRaw,
                fractionBitsB: FixedWorldRigid.Scales.InverseMass,
                fractionBitsOut: FixedQ4816.FractionBitCount,
                result: out var raw
            )) {
                result = FixedQ4816.Zero;
                return false;
            }

            result = FixedQ4816.FromRawBits(value: raw);
            return true;
        }

        if (
            !TryScale(
            component: impulse.X,
            result: out var x
        ) ||
            !TryScale(
            component: impulse.Y,
            result: out var y
        ) ||
            !TryScale(
            component: impulse.Z,
            result: out var z
        )
        ) {
            delta = FixedVector3.Zero;
            return false;
        }

        delta = new FixedVector3(
            X: x,
            Y: y,
            Z: z
        );
        return true;
    }

    /// <summary>Gets a best-effort world-space linear velocity for a KINEMATIC body — the tangent planar velocity
    /// plus the vertical channel along the body's own up axis. Used only so a kinematic body pushing a rigid one
    /// contributes its true closing speed to the impulse; a kinematic body never reads its own velocity from here.
    /// A rigid body's is its rigid velocity.</summary>
    /// <returns>The body's world-space linear velocity.</returns>
    public FixedVector3 ApproximateWorldVelocity() => (IsRigid
        ? m_rigidVelocity
        : ComposedVelocity()
    );

    /// <summary>Builds (or refreshes) this body's persistent <see cref="FixedRigidBody"/> handle — the vehicle
    /// <see cref="Puck.Physics.FixedTwoBodyKernel"/> reads/writes for dynamic-vs-dynamic contact
    /// (<see cref="WorldPopulation.ResolveDynamicContacts"/>). A rigid body's handle is dynamic (its own mass/inertia,
    /// contributing and receiving impulse); a locomotion body's handle is STATIC (zero inverse mass/inertia) but
    /// carries its live <see cref="ApproximateWorldVelocity"/> so it still contributes its own closing speed to the
    /// relative-velocity term without ever receiving an impulse back — the "a kinematic character contributes its
    /// velocity; it is never pushed by them" rule, enforced by construction (<see cref="FixedRigidBody.IsDynamic"/>
    /// gates every write in <see cref="Puck.Physics.FixedTwoBodyKernel.ApplyImpulse"/>). Allocated once per body and
    /// mutated in place every call, so steady-state contact resolution allocates nothing.</summary>
    internal FixedRigidBody TwoBodyHandle() {
        var handle = (m_rigidHandle ??= new FixedRigidBody());

        handle.Orientation = m_orientation;

        // A body whose sweep was refused this tick is immovable until the tick ends: its partner meets it as static.
        if (m_sweepRefusedThisTick) {
            handle.LinearVelocity = FixedVector3.Zero;
            handle.AngularVelocity = FixedVector3.Zero;
            handle.InverseMassRaw = 0L;
            handle.InverseInertiaXX = 0L;
            handle.InverseInertiaYY = 0L;
            handle.InverseInertiaZZ = 0L;
        } else if (m_rigid is { } rigid) {
            var scaledRigid = ScaleRigid(rigid: rigid);

            handle.LinearVelocity = m_rigidVelocity;
            handle.AngularVelocity = m_angularVelocity;
            handle.InverseMassRaw = scaledRigid.InverseMassRaw;
            handle.InverseInertiaXX = scaledRigid.InverseInertiaXX;
            handle.InverseInertiaYY = scaledRigid.InverseInertiaYY;
            handle.InverseInertiaZZ = scaledRigid.InverseInertiaZZ;
        } else {
            handle.LinearVelocity = ApproximateWorldVelocity();
            handle.AngularVelocity = FixedVector3.Zero;
            handle.InverseMassRaw = 0L;
            handle.InverseInertiaXX = 0L;
            handle.InverseInertiaYY = 0L;
            handle.InverseInertiaZZ = 0L;
        }

        handle.InverseInertiaXY = 0L;
        handle.InverseInertiaXZ = 0L;
        handle.InverseInertiaYZ = 0L;

        return handle;
    }

    // The saturation ceiling ScaleRigid's InverseMass/InverseInertia scaling falls back to when the correctly
    // rounded Scale⁻³/Scale⁻⁵ product overflows the signed 64-bit raw — long.MaxValue right-shifted 8 leaves two
    // such raws (the largest sum any solver path adds, InverseMassRaw plus one angular term) eight bits of headroom
    // under the 64-bit ceiling, so a saturated pair contact still resolves through FixedTwoBodyKernel's own
    // overflow-checked sum rather than wrapping past it.
    private const long RepresentableInverseCeiling = (long.MaxValue >> 8);

    /// <summary>Gets this rigid body's own collider volume, scaled for its live <see cref="Scale"/>, in body-root-local
    /// axes — the single volume a rigid kit's facet requires (<see cref="Puck.World.FixedWorldRigid.TryCompile"/>). A
    /// default (zero-sphere) volume for a locomotion kit — never read there, callers gate on <see cref="IsRigid"/>.</summary>
    internal FixedBodyColliderVolume RigidWitnessVolume() {
        if (m_collider is not { Volumes.Length: > 0 }) {
            return default;
        }

        return ScaledColliderVolumes()[0];
    }
    /// <summary>Gets this rigid body's pose and collider as the rigid contact generators read them.</summary>
    /// <param name="id">The id the candidates name the body by: its population index.</param>
    /// <returns>The contact shape.</returns>
    internal FixedRigidContactShape RigidContactShape(int id) => new(
        CenterOffset: RigidCenterOffset,
        Id: id,
        Orientation: m_orientation,
        Root: m_position,
        Volume: RigidWitnessVolume()
    );

    /// <summary>Applies a positional-only correction to a rigid body and wakes it: the carry path's tangibility push,
    /// where a carried body (posed by its carrier, outside the rigid solve) shoves the body it overlaps. A no-op for zero
    /// correction, and for a body whose sweep was refused this tick, which is immovable until the tick ends.</summary>
    /// <param name="correction">The world-space displacement.</param>
    internal void ApplyRigidPositionalCorrection(FixedVector3 correction) {
        if (
            (correction == FixedVector3.Zero) ||
            m_sweepRefusedThisTick
        ) {
            return;
        }

        m_position += correction;
        InvalidateRigidRest();
    }
    private static FixedQ4816 StepSeconds(ulong stepTicks) =>
        (FixedQ4816.FromInteger(value: unchecked((long)stepTicks)) / FixedQ4816.FromInteger(value: EngineTicksPerSecond));

    private void InvalidateRigidRest() {
        NotifyPopulationWake();
        m_resting = false;
        m_restingHoldTicks = 0UL;
    }
    // The fastest any point of the body moves: the centre's linear speed plus the rotational speed of a point at the
    // bounding radius. The probe's guard bound and the rest latch both read this one quantity, so a long piece turning
    // slowly about its centre neither tunnels nor latches while its ends still move.
    private FixedQ4816 RigidSurfaceSpeed(FixedQ4816 boundingRadius) =>
        (m_rigidVelocity.Length + SaturatingNonnegativeProduct(
            left: m_angularVelocity.Length,
            right: boundingRadius
        ));
    // A spatial rate, so the threshold scales with the body like a hold's own travel speed does: a shrunk body
    // settles at a proportionally smaller absolute wobble.
    private bool RigidUnderRestSpeed(FixedQ4816 boundingRadius, RigidContactPolicy policy) =>
        (RigidSurfaceSpeed(boundingRadius: boundingRadius) <= (policy.RestSpeedThreshold * m_scale));
    // Undoes everything the prepared step wrote: a refused sweep is a full block, and the body keeps the pose and
    // velocity it began the tick with (WorldBody.SweepRefusal.cs).
    private void RestoreRigidStart() {
        m_position = m_rigidStartPosition;
        m_orientation = m_rigidStartOrientation;
        m_rigidVelocity = m_rigidStartVelocity;
        m_angularVelocity = m_rigidStartAngularVelocity;
    }

    /// <summary>Prepares this tick's rigid step: records the start the step restores on a refused sweep, damps both
    /// velocities, takes the solved gravity as the step's acceleration, and runs the implicit gyroscopic step on the free
    /// angular velocity. Nothing moves here; the population's rigid solve integrates every prepared body together
    /// against its contacts. A body with no rigid facet or collider prepares nothing.</summary>
    /// <param name="entityIndex">The body's population index.</param>
    /// <param name="stepTicks">The exact engine ticks the step advances.</param>
    internal void PrepareRigidStep(int entityIndex, ulong stepTicks) {
        m_entityIndex = entityIndex;
        m_rigidStepTicks = 0UL;

        if (
            (m_rigid is not { } authored) ||
            (m_collider is null)
        ) {
            return;
        }

        var rigid = ScaleRigid(rigid: authored);
        var seconds = StepSeconds(stepTicks: stepTicks);

        m_rigidStepTicks = stepTicks;
        m_rigidStartPosition = m_position;
        m_rigidStartOrientation = m_orientation;
        m_rigidStartVelocity = m_rigidVelocity;
        m_rigidStartAngularVelocity = m_angularVelocity;
        m_sweepRefusal = ContactRefusal.None;
        // Damping is authored as a per-second rate, applied as (1 - rate·dt) clamped to [0, 1] — never a flat
        // per-tick multiplier, which would decay far faster on a high-rate world than a low-rate one.
        m_rigidVelocity *= RateDecay(
            rate: rigid.LinearDamping,
            seconds: seconds
        );
        m_angularVelocity *= RateDecay(
            rate: rigid.AngularDamping,
            seconds: seconds
        );
        m_rigidAcceleration = (TrySolvedGravity(acceleration: out var gravity)
            ? gravity
            : FixedVector3.Zero
        );

        // The gyroscopic term ω × I·ω, once per rigid step, on the free angular velocity before any contact solve:
        // without it a non-spherical body's world angular velocity would stay fixed while the body turns, and its
        // angular momentum would precess about the spin axis.
        switch (FixedGyroscopicStep.Advance(
            advanced: out var gyroscopicVelocity,
            angularVelocity: m_angularVelocity,
            inverseInertiaXX: rigid.InverseInertiaXX,
            inverseInertiaYY: rigid.InverseInertiaYY,
            inverseInertiaZZ: rigid.InverseInertiaZZ,
            orientation: m_orientation,
            stepSeconds: seconds
        )) {
            case GyroscopicStepOutcome.Applied:
                m_angularVelocity = gyroscopicVelocity;
                WorldBodyWork.Process.Add(amount: 1, kind: WorldBodyWork.GyroscopicSteps);
                break;
            case GyroscopicStepOutcome.Refused:
                WorldBodyWork.Process.Add(amount: 1, kind: WorldBodyWork.GyroscopicRefusals);
                break;
        }
    }
    /// <summary>The most static surfaces one body's contact generation reports a step.</summary>
    internal const int MaximumRigidSurfaces = 3;
    // Two surfaces whose normals agree past this cosine are one face: a support built of flush tiles is one floor.
    private static readonly FixedQ4816 RigidSurfaceAgreement = FixedQ4816.FromDouble(value: 0.999d);

    /// <summary>Finds the static surfaces within this step's reach: one exact query per surface the body's volume grown by
    /// the step's travel (its mean velocity under its prepared acceleration) and the contact reach meets, each
    /// surface reporting its gap at the start. Coplanar surfaces merge into the deepest of them, and the deepest few are
    /// kept. A field that cannot answer per surface is probed instead by one certified sweep of the predicted motion,
    /// whose walkable and non-walkable normals become the surfaces, and whose refusal blocks the body for the tick.
    /// Nothing moves here; the solve decides where the body goes.</summary>
    /// <param name="reach">The non-negative distance past the step's travel a surface still counts within.</param>
    /// <param name="surfaces">The destination for the surfaces; holds at least <see cref="MaximumRigidSurfaces"/>.</param>
    /// <returns>The probe.</returns>
    internal RigidSurfaceProbe ProbeRigidSurface(FixedQ4816 reach, Span<RigidSurface> surfaces) {
        var seconds = StepSeconds(stepTicks: m_rigidStepTicks);
        // The step's mean velocity: the exact displacement of a constant acceleration over the step is (v + a·dt/2)·dt.
        var velocity = (m_rigidVelocity + (m_rigidAcceleration * (seconds / FixedQ4816.FromInteger(value: 2L))));
        var root = m_position;
        var resolved = (root + (velocity * seconds));

        DiagnosticSupport = null;
        m_diagnosticManifoldPoint = null;

        if (m_contactField is not { } field) {
            return new RigidSurfaceProbe(Count: 0, Refused: false, ResolvedRoot: resolved);
        }

        Span<FixedSurfaceContact> found = stackalloc FixedSurfaceContact[16];
        var volume = RigidWitnessVolume();
        var travel = FixedQ4816.Max(x: velocity.Length, y: (velocity + (m_angularVelocity * RigidBoundingRadius)).Length);
        var collected = field.CollectContacts(
            destination: found,
            margin: ((travel * seconds) + reach),
            orientation: in m_orientation,
            position: in root,
            volume: in volume
        );

        if (collected >= 0) {
            var count = 0;

            foreach (var contact in found[..collected]) {
                var sourceId = ((contact.Source == FixedSurfaceColliderSource.Static) ? contact.ColliderIndex : ~contact.ColliderIndex);
                var merged = false;

                for (var index = 0; (index < count); index++) {
                    if (FixedVector3.Dot(left: surfaces[index].Normal, right: contact.Normal) >= RigidSurfaceAgreement) {
                        if (contact.Separation < surfaces[index].Separation) {
                            surfaces[index] = new RigidSurface(Normal: contact.Normal, Separation: contact.Separation, SourceId: sourceId);
                        }

                        merged = true;
                        break;
                    }
                }

                if (merged) {
                    continue;
                }

                if (count < MaximumRigidSurfaces) {
                    surfaces[count++] = new RigidSurface(Normal: contact.Normal, Separation: contact.Separation, SourceId: sourceId);

                    continue;
                }

                // Full: the deepest surfaces stay, the lowest source breaking a tie.
                var shallowest = 0;

                for (var index = 1; (index < count); index++) {
                    if (surfaces[index].Separation > surfaces[shallowest].Separation) {
                        shallowest = index;
                    }
                }

                if (contact.Separation < surfaces[shallowest].Separation) {
                    surfaces[shallowest] = new RigidSurface(Normal: contact.Normal, Separation: contact.Separation, SourceId: sourceId);
                }
            }

            return new RigidSurfaceProbe(Count: count, Refused: false, ResolvedRoot: resolved);
        }

        var resolution = field.ResolveSweep(
            orientation: in m_orientation,
            position: ref resolved,
            previousPosition: in root,
            up: FixedVector3.UnitY,
            velocity: ref velocity,
            volumes: ScaledColliderVolumes()
        );

        if (resolution.Refusal != ContactRefusal.None) {
            NoteSweepRefusal(refusal: resolution.Refusal);
            RestoreRigidStart();

            return new RigidSurfaceProbe(Count: 0, Refused: true, ResolvedRoot: root);
        }

        var written = 0;

        if (resolution.Grounded) {
            // A grounded resolution with no normal comes only from a cross-authority merge; +Y is the up the sweep assumed.
            var normal = ((resolution.GroundNormal != FixedVector3.Zero) ? resolution.GroundNormal : FixedVector3.UnitY);

            surfaces[written++] = new RigidSurface(Normal: normal, Separation: FixedVector3.Dot(left: (root - resolved), right: normal),
                SourceId: WorldRigidContacts.GroundSource);
        }

        if (resolution.ObstructionNormal != FixedVector3.Zero) {
            surfaces[written++] = new RigidSurface(Normal: resolution.ObstructionNormal,
                Separation: FixedVector3.Dot(left: (root - resolved), right: resolution.ObstructionNormal), SourceId: WorldRigidContacts.ObstructionSource);
        }

        DiagnosticSupport = resolution.Support;

        return new RigidSurfaceProbe(Count: written, Refused: false, ResolvedRoot: resolved);
    }
    /// <summary>Gets this body's solver view for the step: a dynamic view of a prepared, unrefused rigid body carrying the
    /// step's acceleration, and a static one (which pushes with its velocity and is never pushed) for a refused or
    /// locomotion body.</summary>
    /// <returns>The solver view, reused from step to step.</returns>
    internal FixedRigidBody RigidSolverHandle() {
        var handle = TwoBodyHandle();

        handle.Acceleration = (handle.IsDynamic ? m_rigidAcceleration : FixedVector3.Zero);

        return handle;
    }
    /// <summary>Commits the solve's displacement and velocities: the body rotates and translates about its centre of
    /// mass. When the solved root departs from the root the probe certified by more than a fraction of the body's
    /// bounding radius, a guard sweep from the start re-certifies the solved motion against the static field, and a
    /// refused guard sweep blocks the body for the tick.</summary>
    /// <param name="handle">The body's solver view after the step.</param>
    /// <param name="probedRoot">The root the step's probe resolved the predicted motion to.</param>
    /// <param name="policy">The compiled rigid policy.</param>
    /// <returns><see langword="true"/> when a guard sweep ran.</returns>
    internal bool CommitRigidStep(FixedRigidBody handle, FixedVector3 probedRoot, RigidContactPolicy policy) {
        var rigid = ScaleRigid(rigid: m_rigid!.Value);
        var startCenter = (m_position + m_orientation.Rotate(vector: rigid.CenterOffset));
        var orientation = (handle.DeltaRotation * m_orientation).Normalize();
        var root = ((startCenter + handle.DeltaPosition) - orientation.Rotate(vector: rigid.CenterOffset));
        var velocity = handle.LinearVelocity;
        var bound = FixedQ4816.Max(
            x: (rigid.BoundingRadius * policy.SubstepTravelFraction),
            y: policy.SubstepMinimumTravel
        );
        var guarded = false;

        if (
            (m_contactField is { } field) &&
            ((root - probedRoot).Length > bound)
        ) {
            guarded = true;

            var start = m_position;
            var resolution = field.ResolveSweep(
                orientation: in orientation,
                position: ref root,
                previousPosition: in start,
                up: FixedVector3.UnitY,
                velocity: ref velocity,
                volumes: ScaledColliderVolumes()
            );

            if (resolution.Refusal != ContactRefusal.None) {
                NoteSweepRefusal(refusal: resolution.Refusal);
                RestoreRigidStart();

                return true;
            }
        }

        m_position = root;
        m_orientation = orientation;
        m_rigidVelocity = velocity;
        m_angularVelocity = handle.AngularVelocity;
        m_continuity = EntityContinuity.Continuous;

        return guarded;
    }
    /// <summary>Finishes the step: publishes the measured support, applies rolling resistance while supported, and counts
    /// the rest hold window, latching rest (and zeroing both velocities) once the body has stayed supported under the
    /// rest speed for the authored hold.</summary>
    /// <param name="supported">Whether a contact under the walkable slope carried load this step.</param>
    /// <param name="loadedPoint">The world point of the most loaded surface contact, for the contact diagnostics.</param>
    /// <param name="policy">The compiled rigid policy.</param>
    internal void FinishRigidStep(bool supported, FixedVector3? loadedPoint, RigidContactPolicy policy) {
        var stepTicks = m_rigidStepTicks;

        m_rigidStepTicks = 0UL;

        if (
            (m_rigid is not { } authored) ||
            m_sweepRefusedThisTick
        ) {
            return;
        }

        var rigid = ScaleRigid(rigid: authored);

        m_grounded = supported;
        m_lastContactCount = (supported ? 1 : 0);
        m_diagnosticManifoldPoint = loadedPoint;

        if (
            (loadedPoint is { } point) &&
            (DiagnosticSupport is { } support)
        ) {
            DiagnosticSupport = support with { BodyPoint = point };
        }

        if (!supported) {
            m_resting = false;
            m_restingHoldTicks = 0UL;

            return;
        }

        // Rolling resistance: a pure angular-velocity decay while supported, distinct from the contact friction the
        // solve already ran.
        m_angularVelocity *= RateDecay(
            rate: rigid.RollingFriction,
            seconds: StepSeconds(stepTicks: stepTicks)
        );

        if (RigidUnderRestSpeed(boundingRadius: rigid.BoundingRadius, policy: policy)) {
            m_restingHoldTicks += stepTicks;

            if (m_restingHoldTicks >= policy.RestHoldTicks) {
                m_resting = true;
                m_rigidVelocity = FixedVector3.Zero;
                m_angularVelocity = FixedVector3.Zero;
            }
        } else {
            m_restingHoldTicks = 0UL;
            m_resting = false;
        }
    }
}
