using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The authored rigid-contact tunables (<see cref="WorldBodyContactPolicy"/>'s rigid-specific fields),
/// converted to fixed point/engine ticks once per document (re)compile — see
/// <see cref="Puck.World.Server.WorldPopulation"/>'s own compiled tables — instead of every
/// <see cref="WorldBody.AdvanceRigid"/> call.</summary>
/// <param name="RestLinearThreshold">Below this linear speed a grounded rigid body counts toward the resting hold
/// window.</param>
/// <param name="RestAngularThreshold">Below this angular speed a grounded rigid body counts toward the resting hold
/// window.</param>
/// <param name="RestHoldTicks">How long, in exact engine ticks, a rigid body must stay under both rest thresholds
/// while grounded before the resting latch actually closes.</param>
/// <param name="SubstepTravelFraction">The fraction of a rigid body's own bounding radius one continuous-collision
/// substep may travel.</param>
/// <param name="SubstepMinimumTravel">The floor under one substep's travel bound, independent of
/// <see cref="SubstepTravelFraction"/> — see <see cref="WorldBodyContactPolicy.RigidSubstepMinimumTravel"/>.</param>
/// <param name="SubstepCeiling">The most substeps one rigid body's static-contact integration may take in a tick —
/// see <see cref="WorldBodyContactPolicy.RigidSubstepCeiling"/>.</param>
/// <param name="PairRestitutionThreshold">Below this closing speed, a rigid-vs-rigid pair contact restitutes at
/// zero rather than the authored coefficient — see <see cref="WorldBodyContactPolicy.RigidPairRestitutionSpeed"/>.</param>
/// <param name="ManifoldIterations">The sequential-impulse pass count a box or capsule body's own ground support
/// manifold resolves over — see <see cref="WorldBodyContactPolicy.RigidManifoldIterations"/>.</param>
public readonly record struct RigidContactPolicy(
    FixedQ4816 RestLinearThreshold,
    FixedQ4816 RestAngularThreshold,
    ulong RestHoldTicks,
    FixedQ4816 SubstepTravelFraction,
    FixedQ4816 SubstepMinimumTravel,
    int SubstepCeiling,
    FixedQ4816 PairRestitutionThreshold,
    int ManifoldIterations
) {
    /// <summary>Compiles an authored <see cref="WorldBodyContactPolicy"/>'s rigid fields to this fixed-point form.</summary>
    public static RigidContactPolicy FromAuthored(WorldBodyContactPolicy policy) => new(
        RestLinearThreshold: FixedQ4816.FromDouble(value: policy.RigidRestLinearSpeed),
        RestAngularThreshold: FixedQ4816.FromDouble(value: policy.RigidRestAngularSpeed),
        RestHoldTicks: FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: policy.RigidRestHoldSeconds)),
        SubstepTravelFraction: FixedQ4816.FromDouble(value: policy.RigidSubstepTravelFraction),
        SubstepMinimumTravel: FixedQ4816.FromDouble(value: policy.RigidSubstepMinimumTravel),
        SubstepCeiling: policy.RigidSubstepCeiling,
        PairRestitutionThreshold: FixedQ4816.FromDouble(value: policy.RigidPairRestitutionSpeed),
        ManifoldIterations: policy.RigidManifoldIterations
    );
}

public sealed partial class WorldBody {
    // The persistent FixedTwoBodyKernel vehicle RigidHandle() maintains — allocated once, mutated in place every
    // call, so steady-state dynamic-vs-dynamic contact resolution allocates nothing.
    private FixedRigidBody? m_rigidHandle;
    // The persistent infinite-mass phantom AdvanceRigid's own static-contact friction solve reads as the "other
    // body" in a two-body kernel call — the world/ground never moves and never receives an impulse (zero inverse
    // mass/inertia), so this is allocated once and its velocity fields are never touched after construction.
    private FixedRigidBody? m_groundPhantomHandle;
    // Whether the previous substep already had a walkable (ground) / non-walkable (obstruction) contact — two
    // independent restitution edge latches (see AdvanceRigid): a rising edge (false -> true) on EITHER channel is a
    // genuine impact on THAT surface, continuous contact on it is not. Separate latches so a ball resting on the
    // floor (continuous ground contact) still bounces the first time it clips a wall, rather than reading the wall
    // hit as a continuation of the unrelated floor contact.
    private bool m_rigidGroundContacting;
    private bool m_rigidObstructionContacting;
    // Consecutive substeps a latched contact has gone unconfirmed by a fresh push — a body sitting depenetrated
    // EXACTLY at its contact skin's minimum distance reads no push (distance >= minimum) for a run of substeps even
    // while still touching, only re-closing the gap once gravity's own per-substep pull exceeds the fixed-point
    // rounding left at the surface; a smaller collider (AdvanceRigid's own derivedSubsteps grows as BoundingRadius
    // shrinks) takes proportionally more such substeps to do it. AdvanceRigid tolerates a miss streak up to that
    // same tick's own derivedSubsteps — one full engine tick's worth of grace, however finely this tick happened to
    // be substepped — without dropping the latch; a departure spanning MORE than one tick's substeps still clears
    // it, so a real landing still restitutes.
    private int m_rigidGroundMissStreak;
    private int m_rigidObstructionMissStreak;

    /// <summary>Gets the number of substeps the most recent <see cref="AdvanceRigid"/> call took — the
    /// derived-count read-back <c>world.budget</c> echoes. Zero for a locomotion kit or a body never yet advanced.</summary>
    public int RigidStaticSubstepsThisTick { get; private set; }

    private static readonly FixedQ4816 RigidHalf = FixedQ4816.FromDouble(value: 0.5d);
    // The saturation ceiling ScaleRigid's InverseMass/InverseInertia scaling falls back to when the correctly
    // rounded Scale⁻³/Scale⁻⁵ product overflows the signed 64-bit raw — long.MaxValue right-shifted 8 leaves two
    // such raws (the largest sum any solver path adds, InverseMassRaw plus one angular term) eight bits of headroom
    // under the 64-bit ceiling, so a saturated pair contact still resolves through FixedTwoBodyKernel's own
    // overflow-checked sum rather than wrapping past it.
    private const long RepresentableInverseCeiling = (long.MaxValue >> 8);

    /// <summary>Gets whether this body's kit carries a <c>rigid</c> facet — a passive rigid entity advanced by
    /// <see cref="AdvanceRigid"/> instead of a locomotion motion program.</summary>
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
    /// <summary>Gets whether the previous substep already had a walkable (ground) contact — the ground-channel
    /// restitution edge latch <see cref="AdvanceRigid"/> reads.</summary>
    public bool RigidGroundContacting => m_rigidGroundContacting;
    /// <summary>Gets whether the previous substep already had a non-walkable (obstruction) contact — the
    /// obstruction-channel restitution edge latch <see cref="AdvanceRigid"/> reads.</summary>
    public bool RigidObstructionContacting => m_rigidObstructionContacting;
    /// <summary>Gets the ground-channel contact's current run of consecutive substeps missed without dropping its
    /// latch — a miss streak that reaches the current tick's own derived substep count drops the latch (see
    /// <see cref="AdvanceRigid"/>).</summary>
    public int RigidGroundMissStreak => m_rigidGroundMissStreak;
    /// <summary>Gets the obstruction-channel contact's current miss streak, on the same terms as
    /// <see cref="RigidGroundMissStreak"/>.</summary>
    public int RigidObstructionMissStreak => m_rigidObstructionMissStreak;
    /// <summary>Gets the rigid facet's mass at this body's live <see cref="Scale"/> — the authored mass at scale 1,
    /// scaled by <c>Scale³</c> (see <see cref="ScaleRigid"/>). Zero for a locomotion kit.</summary>
    public FixedQ4816 RigidMass => (m_rigid is { } rigid ? ScaleRigid(rigid: rigid).Mass : FixedQ4816.Zero);
    /// <summary>Gets the rigid facet's authored restitution against another rigid body — a dimensionless coefficient,
    /// unaffected by <see cref="Scale"/>. Zero for a locomotion kit.</summary>
    public FixedQ4816 RigidRestitution => (m_rigid?.Restitution ?? FixedQ4816.Zero);
    /// <summary>Gets the rigid facet's authored friction against another rigid body — a dimensionless coefficient,
    /// unaffected by <see cref="Scale"/>. Zero for a locomotion kit.</summary>
    public FixedQ4816 RigidFriction => (m_rigid?.Friction ?? FixedQ4816.Zero);
    /// <summary>Gets the rigid facet's conservative bounding radius at this body's live <see cref="Scale"/> — the
    /// same radius the pair-contact anchor approximation and the static-contact substep bound both read, scaled
    /// linearly with <see cref="Scale"/> (see <see cref="ScaleRigid"/>). Zero for a locomotion kit.</summary>
    public FixedQ4816 RigidBoundingRadius => (m_rigid is { } rigid ? ScaleRigid(rigid: rigid).BoundingRadius : FixedQ4816.Zero);
    /// <summary>Gets this body's live-<see cref="Scale"/>-consistent rigid-facet centre-of-mass offset from its root,
    /// body-local axes — the same offset <see cref="AdvanceRigid"/> rotates and translates the body about, and what
    /// <see cref="FixedRigidWitness.Anchor"/> subtracts from a local support point. Zero for a locomotion kit.</summary>
    public FixedVector3 RigidCenterOffset => (m_rigid is { } rigid ? ScaleRigid(rigid: rigid).CenterOffset : FixedVector3.Zero);
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

        var scaleSquared = SaturatingNonnegativeProduct(left: m_scale, right: m_scale);
        var scaleCubed = SaturatingNonnegativeProduct(left: scaleSquared, right: m_scale);

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
        ) ? FixedQ4816.FromRawBits(value: inverseScaleRaw) : FixedQ4816.One);
        // The powers are accumulated at the inverse quantities' own 40-bit placement, not Q16: at Q16 a grown body's
        // Scale⁻⁵ (1e-5 at Scale 10) is below the smallest positive value and would zero the inverse inertia, freezing
        // its orientation; at 40 bits the same power keeps 15 significant bits.
        var inverseScaleCubed = InversePower(inverseScaleRaw: inverseScale.Value, exponent: 3);
        var inverseScaleFifth = InversePower(inverseScaleRaw: inverseScale.Value, exponent: 5);

        static long ScaleRaw(long raw, int fractionBits, long factorRaw) => (FusedArithmetic.TryMixedScaleProduct(
            a: raw,
            fractionBitsA: fractionBits,
            b: factorRaw,
            fractionBitsB: InverseFractionBits,
            fractionBitsOut: fractionBits,
            result: out var scaled
        ) ? scaled : RepresentableInverseCeiling);

        return rigid with {
            Mass = SaturatingNonnegativeProduct(left: rigid.Mass, right: scaleCubed),
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
                fractionBitsA: InverseFractionBits,
                b: inverseScaleRaw,
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

    // baseValue^exponent by repeated single-rounding multiplication, saturating on overflow.
    private static FixedQ4816 PositiveIntegerPower(FixedQ4816 baseValue, int exponent) {
        var accumulatorRaw = FixedQ4816.One.Value;

        for (var step = 0; (step < exponent); step++) {
            if (!FusedArithmetic.TryMixedScaleProduct(
                a: accumulatorRaw,
                fractionBitsA: FixedQ4816.FractionBitCount,
                b: baseValue.Value,
                fractionBitsB: FixedQ4816.FractionBitCount,
                fractionBitsOut: FixedQ4816.FractionBitCount,
                result: out var scaled
            )) {
                return FixedQ4816.MaxValue;
            }

            accumulatorRaw = scaled;
        }

        return FixedQ4816.FromRawBits(value: accumulatorRaw);
    }
    /// <summary>Gets the rigid facet's world-space centre of mass — <c>root + orientation·CenterOffset</c>, the
    /// point every substep actually rotates and translates about (see <see cref="AdvanceRigid"/>). For a rolling or
    /// tumbling body this orbits away from <see cref="WorldBody.FixedPosition"/> (the root); the pose <c>body.where</c>
    /// echoes stays the root, with <c>com=</c> the read-back for this. Equal to the root for a locomotion kit.</summary>
    public FixedVector3 RigidCenterOfMass => (m_rigid is { } rigid
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
        if (m_rigid is not { } rigid) {
            return true;
        }

        if (!TryImpulseToVelocity(
            impulse: impulse,
            inverseMassRaw: ScaleRigid(rigid: rigid).InverseMassRaw,
            delta: out var delta
        )) {
            return false;
        }

        if (!TryAdd(left: m_rigidVelocity, right: delta, sum: out var candidate)) {
            return false;
        }

        if (candidate.Length > velocityCeiling) {
            return false;
        }

        m_rigidVelocity = candidate;
        m_resting = false;
        m_restingHoldTicks = 0UL;
        return true;
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

        if (!FusedArithmetic.TryMixedScaleProduct(
            a: rate.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: seconds.Value,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var decayRaw
        )) {
            return FixedQ4816.Zero;
        }

        return FixedQ4816.Max(
            x: FixedQ4816.Zero,
            y: (FixedQ4816.One - FixedQ4816.FromRawBits(value: decayRaw))
        );
    }
    /// <summary>Multiplies non-negative Q48.16 quantities without wraparound, saturating only when the exact
    /// rounded product leaves the representation. Used for scale-derived mass/reach and Coulomb impulse ceilings,
    /// where saturation preserves the quantity's monotone physical direction.</summary>
    internal static FixedQ4816 SaturatingNonnegativeProduct(FixedQ4816 left, FixedQ4816 right) {
        if (
            (left <= FixedQ4816.Zero) ||
            (right <= FixedQ4816.Zero)
        ) {
            return FixedQ4816.Zero;
        }

        return (FusedArithmetic.TryMixedScaleProduct(
            a: left.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: right.Value,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var product
        ) ? FixedQ4816.FromRawBits(value: product) : FixedQ4816.MaxValue);
    }
    private static bool TryAdd(FixedVector3 left, FixedVector3 right, out FixedVector3 sum) {
        static bool TryAddRaw(long left, long right, out long result) {
            result = unchecked((left + right));
            return (((left ^ result) & (right ^ result)) >= 0L);
        }

        if (
            !TryAddRaw(left: left.X.Value, right: right.X.Value, result: out var x) ||
            !TryAddRaw(left: left.Y.Value, right: right.Y.Value, result: out var y) ||
            !TryAddRaw(left: left.Z.Value, right: right.Z.Value, result: out var z)
        ) {
            sum = FixedVector3.Zero;
            return false;
        }

        sum = new FixedVector3(
            X: FixedQ4816.FromRawBits(value: x),
            Y: FixedQ4816.FromRawBits(value: y),
            Z: FixedQ4816.FromRawBits(value: z)
        );
        return true;
    }
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
            !TryScale(component: impulse.X, result: out var x) ||
            !TryScale(component: impulse.Y, result: out var y) ||
            !TryScale(component: impulse.Z, result: out var z)
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
    /// contributes its true closing speed to the impulse; a kinematic body never reads its own velocity from here.</summary>
    internal FixedVector3 ApproximateWorldVelocity() => (IsRigid
        ? m_rigidVelocity
        : (m_planarVelocity + (m_up * m_verticalVelocity))
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

        if (m_rigid is { } rigid) {
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
    /// <summary>Gets this rigid body's own collider volume, scaled for its live <see cref="Scale"/>, in body-root-
    /// local axes — the single volume a rigid kit's facet requires (<see cref="Puck.World.FixedWorldRigid.TryCompile"/>),
    /// read by <see cref="FixedRigidWitness.Anchor"/> to place a contact anchor on the shape's true surface. A
    /// default (zero-sphere) volume for a locomotion kit — never read there, callers gate on <see cref="IsRigid"/>.</summary>
    internal FixedBodyColliderVolume RigidWitnessVolume() {
        if (m_collider is not { Volumes.Length: > 0 } collider) {
            return default;
        }

        Span<FixedBodyColliderVolume> scratch = stackalloc FixedBodyColliderVolume[1];
        var volumes = ScaledColliderVolumes(
            scratch: scratch,
            volumes: collider.Volumes
        );

        return volumes[0];
    }
    /// <summary>Builds (or refreshes) the persistent static ground phantom <see cref="AdvanceRigid"/>'s static-
    /// contact friction solve plays as the kernel's "other" body: zero velocity, zero inverse mass/inertia, so it
    /// never moves and never receives an impulse, whatever anchor or normal a call names.</summary>
    private FixedRigidBody GroundPhantomHandle() {
        var handle = (m_groundPhantomHandle ??= new FixedRigidBody());

        handle.LinearVelocity = FixedVector3.Zero;
        handle.AngularVelocity = FixedVector3.Zero;
        handle.InverseMassRaw = 0L;
        handle.InverseInertiaXX = 0L;
        handle.InverseInertiaYY = 0L;
        handle.InverseInertiaZZ = 0L;
        handle.InverseInertiaXY = 0L;
        handle.InverseInertiaXZ = 0L;
        handle.InverseInertiaYZ = 0L;

        return handle;
    }
    /// <summary>Writes a rigid body's own <see cref="TwoBodyHandle"/>, after the kernel has applied an impulse to it,
    /// back onto this body's velocity state and wakes it. A no-op for a locomotion kit (its handle is a static
    /// phantom — see <see cref="TwoBodyHandle"/> — and never receives a written impulse to commit).</summary>
    internal void CommitRigidHandle(FixedRigidBody handle) {
        if (m_rigid is null) {
            return;
        }

        m_rigidVelocity = handle.LinearVelocity;
        m_angularVelocity = handle.AngularVelocity;
        m_resting = false;
        m_restingHoldTicks = 0UL;
    }
    /// <summary>Applies a positional-only depenetration correction to a rigid body — no velocity or impulse change,
    /// that is the separate rigid-pair impulse path's job (<see cref="WorldPopulation.ResolveRigidPairContact"/>) —
    /// and wakes it: a body another body's overlap has just physically displaced is no longer at rest, whatever its
    /// latched velocity said a moment ago. A no-op for zero correction, so a body already merely touching (never
    /// re-entering overlap once actually settled — see <see cref="Puck.Physics.FixedDynamicBodyContacts"/>) is never
    /// woken by a call that does nothing.</summary>
    internal void ApplyRigidPositionalCorrection(FixedVector3 correction) {
        if (correction == FixedVector3.Zero) {
            return;
        }

        m_position += correction;
        m_resting = false;
        m_restingHoldTicks = 0UL;
    }
    /// <summary>Advances a rigid-kit body by one exact simulation step: damping, gravity, a swept, substepped
    /// integration against the world's static contact field with restitution/friction/rolling response, and the
    /// resting latch. Dynamic-vs-dynamic contact is a separate pass
    /// (<see cref="WorldPopulation.ResolveDynamicContacts"/>), run after every body has advanced.</summary>
    /// <param name="entityIndex">This body's population index — the same index gravity/checkpoint reads key on.</param>
    /// <param name="stepTicks">The exact engine ticks this call advances.</param>
    /// <param name="policy">The authored, once-compiled rigid-contact tunables (rest thresholds/hold window, the
    /// substep travel fraction, and the substep ceiling <see cref="WorldBodyContactPolicy.RigidSubstepCeiling"/>
    /// bounds) — <see cref="RigidContactPolicy"/>.</param>
    private void AdvanceRigid(int entityIndex, ulong stepTicks, RigidContactPolicy policy) {
        m_entityIndex = entityIndex;

        if (
            (m_rigid is not { } rigid) ||
            (m_collider is not { } collider)
        ) {
            return;
        }

        // A body the rest latch has closed stays FROZEN — position, orientation, and both contact latches held
        // bit-identical — until something wakes it: TryApplyRigidImpulse, CommitRigidHandle (a dynamic-pair
        // impulse), ApplyRigidPositionalCorrection (a dynamic-pair depenetration), Pose (a hard teleport), or
        // SetContactField (a live solid edit replacing the static world it rested against — both in
        // WorldBody.Lifecycle.cs), each of which clears m_resting itself. `$physics:quiescent`/`world.rigid`/
        // `body.where`'s own "resting" fact all read this latch directly, so this IS what those facts mean: a body
        // actually at rest has nothing left to solve, and re-deriving gravity/contact against it every tick (the
        // bounded-iteration ground manifold below converges close to, never exactly, zero net impulse) is exactly
        // what would make them lie.
        if (m_resting) {
            RigidStaticSubstepsThisTick = 0;
            return;
        }

        // Every subsequent read of `rigid` in this call — including the reference ResolveRigidContact receives below
        // — is this body's live-Scale-consistent copy (see ScaleRigid's own remarks), never the kit-shared authored
        // facet: a shrunk body's mass, inertia, substep bound, and contact-point lever arm all shrink with it.
        rigid = ScaleRigid(rigid: rigid);

        var tickSeconds = (FixedQ4816.FromInteger(value: unchecked((long)stepTicks)) / FixedQ4816.FromInteger(value: EngineTicksPerSecond));

        // Every one of the four rigid coefficients (damping x2, friction, rolling friction) is authored as a
        // per-second RATE, applied here as (1 - rate·dt) clamped to [0, 1] — never a flat per-tick multiplier, which
        // would make the same authored value decay velocity far faster on a high-rate world than a low-rate one.
        m_rigidVelocity *= RateDecay(
            rate: rigid.LinearDamping,
            seconds: tickSeconds
        );
        m_angularVelocity *= RateDecay(
            rate: rigid.AngularDamping,
            seconds: tickSeconds
        );

        if (TrySolvedGravity(acceleration: out var gravity)) {
            m_rigidVelocity += (gravity * tickSeconds);
        }

        // Continuous collision, by derived substep count: a fast ball must not tunnel through a thin wall at the
        // authority rate, so the travel this tick is bounded against an authored fraction of the collider's own
        // bounding radius rather than a fixed knob. "Travel" is the fastest a point on the collider's own SURFACE
        // moves, not just the centre: angularVelocity·BoundingRadius is a spinning body's own surface speed from
        // rotation alone, added to the centre's own linear speed — a light, thin body pivoting in place can carry
        // tens of radians/second from a single ground-manifold impulse while its centre barely translates, and
        // without this term its ground contact would re-resolve only once a whole tick's rotation late.
        var travel = ((m_rigidVelocity.Length + SaturatingNonnegativeProduct(left: m_angularVelocity.Length, right: rigid.BoundingRadius)) * tickSeconds);
        var perSubstepBound = FixedQ4816.Max(
            x: (rigid.BoundingRadius * policy.SubstepTravelFraction),
            y: policy.SubstepMinimumTravel
        );
        var derivedSubsteps = 1;

        while (
            (derivedSubsteps < Math.Max(val1: 1, val2: policy.SubstepCeiling)) &&
            ((perSubstepBound * FixedQ4816.FromInteger(value: derivedSubsteps)) < travel)
        ) {
            derivedSubsteps++;
        }

        RigidStaticSubstepsThisTick = derivedSubsteps;

        var subTicks = (stepTicks / unchecked((ulong)derivedSubsteps));
        var leftoverTicks = (stepTicks - (subTicks * unchecked((ulong)derivedSubsteps)));
        var grounded = false;
        // Hoisted out of the substep loop: the local-frame collider volumes are constant for the whole tick (only
        // position/orientation move per substep), so this scales once rather than up to SubstepCeiling times.
        Span<FixedBodyColliderVolume> staticContactScratch = stackalloc FixedBodyColliderVolume[WorldCollider.MaxVolumes];
        var scaledColliderVolumes = ScaledColliderVolumes(
            volumes: collider.Volumes,
            scratch: staticContactScratch
        );

        for (var sub = 0; (sub < derivedSubsteps); sub++) {
            // The remainder rides the first substep so the sum of every substep's ticks is exactly stepTicks.
            var thisSubTicks = (subTicks + ((sub == 0) ? leftoverTicks : 0UL));

            if (thisSubTicks == 0UL) {
                continue;
            }

            var subSeconds = (FixedQ4816.FromInteger(value: unchecked((long)thisSubTicks)) / FixedQ4816.FromInteger(value: EngineTicksPerSecond));
            var preVelocity = m_rigidVelocity;

            // Rotate and translate about the body's own centre of mass, never its root: capture the CoM under the
            // OLD orientation first, displace THAT by the substep's linear motion, then re-derive the root from the
            // NEW orientation's own offset. Updating the orientation first and re-deriving the root from it (root =
            // CoM_old + v·dt - R_new·offset) would translate the root at v while the true CoM — root + R·offset —
            // additionally jumps by (R_new - R_old)·offset every substep from the rotation alone.
            var previousCenter = (m_position + m_orientation.Rotate(vector: rigid.CenterOffset));
            var deltaRotation = FixedQuaternion.Exp(bivector: (m_angularVelocity * (subSeconds * RigidHalf)));

            m_orientation = (deltaRotation * m_orientation).Normalize();

            var center = (previousCenter + (m_rigidVelocity * subSeconds));

            var previousBodyOrigin = m_position;
            var bodyOrigin = (center - m_orientation.Rotate(vector: rigid.CenterOffset));
            var velocity = m_rigidVelocity;
            var resolution = default(ContactResolution);

            if (m_contactField is { } field) {
                resolution = field.ResolveSweep(
                    orientation: in m_orientation,
                    position: ref bodyOrigin,
                    previousPosition: in previousBodyOrigin,
                    up: in UnitY,
                    velocity: ref velocity,
                    volumes: scaledColliderVolumes
                );
            }

            m_position = bodyOrigin;
            m_rigidVelocity = velocity;
            // The sweep above is a SINGLE-POINT CCD pass (no lever): for a box or capsule it already consumes this
            // substep's own gravity-driven closing velocity at the body's centre, before the manifold below ever
            // sees it. Captured here, BEFORE any obstruction-contact impulse runs, so ResolveRigidGroundManifold can
            // reconstruct what the corner manifold needs — the RAW pre-sweep velocity (gravity's contribution intact,
            // for the torque only an off-centre corner impulse can supply) PLUS whatever an obstruction contact this
            // same substep goes on to add — without also re-consuming what the sweep already resolved.
            var postSweepVelocity = velocity;

            // Ground and obstruction are independent contacts that may both hold in the same substep (a ball
            // resting on the floor that also clips a wall) — each carries its own rising-edge restitution latch and
            // its own coupled slip-friction solve, so a wall hit bounces on its own first contact even while the
            // floor contact underneath has been continuous for many ticks (see the two latch fields above).
            if (resolution.ObstructionNormal != FixedVector3.Zero) {
                m_rigidObstructionMissStreak = 0;

                ResolveRigidContact(
                    normal: resolution.ObstructionNormal,
                    preVelocity: preVelocity,
                    contacting: ref m_rigidObstructionContacting,
                    rigid: in rigid,
                    volume: scaledColliderVolumes[0]
                );
            } else if (m_rigidObstructionMissStreak < derivedSubsteps) {
                m_rigidObstructionMissStreak++;
            } else {
                m_rigidObstructionContacting = false;
            }

            if (resolution.Grounded) {
                // GroundNormal reads Zero on a genuinely grounded substep only through WorldAdjacencyContactField's
                // own cross-authority merge (its `return new ContactResolution(Grounded: ..., ObstructionNormal: ...)`
                // omits GroundNormal, defaulting it) — the local-only FixedFieldContactSolver always sets a walkable
                // GroundNormal together with Grounded. UnitY is the same up ResolveSweep already assumes, so it
                // stands in for the surface normal on that path, where friction/rolling-resistance still apply
                // against the still-true contact.
                var groundNormal = ((resolution.GroundNormal != FixedVector3.Zero) ? resolution.GroundNormal : UnitY);

                m_rigidGroundMissStreak = 0;

                // A box or capsule rests on a MANIFOLD (up to four corners, or two cap points lying on a side), never
                // a single witness point straight below its centre — that single point carries no lever against a
                // tip in the direction perpendicular to it, which is exactly the tip a body standing on its own
                // support polygon must resist. A sphere's own witness point is already its unique ground contact.
                var groundVolume = scaledColliderVolumes[0];

                if (
                    (groundVolume.Kind == FixedBodyColliderKind.Box) ||
                    (groundVolume.Kind == FixedBodyColliderKind.Capsule)
                ) {
                    ResolveRigidGroundManifold(
                        contacting: ref m_rigidGroundContacting,
                        iterations: policy.ManifoldIterations,
                        normal: groundNormal,
                        postSweepVelocity: postSweepVelocity,
                        preVelocity: preVelocity,
                        rigid: in rigid,
                        volume: groundVolume
                    );
                } else {
                    ResolveRigidContact(
                        contacting: ref m_rigidGroundContacting,
                        normal: groundNormal,
                        preVelocity: preVelocity,
                        rigid: in rigid,
                        volume: groundVolume
                    );
                }
                // Rolling resistance: a pure angular-velocity decay while actually grounded, distinct from — and
                // applied after — the coupled slip-friction solve above (which already moved translational energy
                // into rotational and back through the shared inertia, not this decay).
                m_angularVelocity *= RateDecay(
                    rate: rigid.RollingFriction,
                    seconds: subSeconds
                );
            } else if (m_rigidGroundMissStreak < derivedSubsteps) {
                m_rigidGroundMissStreak++;
            } else {
                m_rigidGroundContacting = false;
            }

            grounded = (grounded || resolution.Grounded);
        }

        var linearSpeed = m_rigidVelocity.Length;
        var angularSpeed = m_angularVelocity.Length;
        // Linear speed is a spatial rate (u/s), so its rest threshold scales with the body exactly like a hold's own
        // travel speed does (Server.WorldBody.Scale's remarks) — a shrunk body settles at a proportionally smaller
        // absolute wobble. Angular speed (rad/s) carries no length dimension, so its threshold is unaffected by Scale.
        var restLinearThreshold = (policy.RestLinearThreshold * m_scale);

        if (
            grounded &&
            (linearSpeed <= restLinearThreshold) &&
            (angularSpeed <= policy.RestAngularThreshold)
        ) {
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

        m_continuity = EntityContinuity.Continuous;
    }
    /// <summary>Resolves one contact normal (ground OR obstruction — see <see cref="AdvanceRigid"/>) for the current
    /// substep: restitution on a rising edge, then a coupled slip-friction impulse at the contact point that moves
    /// translational and rotational velocity together through the body's own inertia (the two-body kernel, with the
    /// world modeled as an infinite-mass static phantom — see <see cref="GroundPhantomHandle"/>) rather than an
    /// ad-hoc lever with no way back into linear motion.</summary>
    private void ResolveRigidContact(FixedVector3 normal, FixedVector3 preVelocity, ref bool contacting, in FixedWorldRigid rigid, FixedBodyColliderVolume volume) {
        var postSweepVelocity = m_rigidVelocity;
        var incoming = FixedVector3.Dot(
            left: preVelocity,
            right: normal
        );

        if (
            !contacting &&
            (incoming < FixedQ4816.Zero)
        ) {
            m_rigidVelocity += (normal * (rigid.Restitution * -incoming));
        }

        contacting = true;

        if (rigid.BoundingRadius <= FixedQ4816.Zero) {
            return;
        }

        // Coulomb friction, on the SAME terms `rigid.friction` carries against another rigid body
        // (WorldPopulation.ResolveRigidPairContact): the tangential impulse is clamped to friction times the normal
        // impulse magnitude, never a bare speed-independent decay rate — one authored coefficient, one meaning. The
        // sweep above already removed this substep's inward velocity component; the impulse that took
        // (postSweepVelocity - preVelocity)·normal off the body IS this contact's normal force for the substep, so
        // it stands in for the explicit normal impulse a two-body pair contact computes directly.
        var normalSpeedRemoved = FixedQ4816.Max(
            x: FixedQ4816.Zero,
            y: (FixedVector3.Dot(left: postSweepVelocity, right: normal) - incoming)
        );

        if (normalSpeedRemoved <= FixedQ4816.Zero) {
            return;
        }

        // The contact point, relative to the body's own centre of mass: the true witness point of the shape along
        // -normal (the point a sphere/capsule/box actually touches the surface at), not a point on the conservative
        // bounding sphere — see FixedRigidWitness. Carries a real lever arm off-centre, so the friction impulse below
        // imparts torque exactly where the shape actually contacts.
        var contactAnchor = FixedRigidWitness.Anchor(centerOffset: rigid.CenterOffset, orientation: m_orientation, volume: volume, worldDirection: -normal);
        var contactVelocity = (m_rigidVelocity + FixedVector3.Cross(
            left: m_angularVelocity,
            right: contactAnchor
        ));
        var tangential = (contactVelocity - (normal * FixedVector3.Dot(
            left: contactVelocity,
            right: normal
        )));

        if (tangential == FixedVector3.Zero) {
            return;
        }

        var tangentSpeed = tangential.Length;
        var tangentDirection = (tangential / tangentSpeed);

        var ballHandle = TwoBodyHandle();
        var groundHandle = GroundPhantomHandle();
        var refusals = 0;

        if (
            !FixedTwoBodyKernel.TryEffectiveMass(
            bodyA: groundHandle,
            anchorA: FixedVector3.Zero,
            bodyB: ballHandle,
            anchorB: contactAnchor,
            normal: tangentDirection,
            scales: FixedWorldRigid.Scales,
            normalMassRaw: out var tangentMassRaw,
            refusals: ref refusals
        )
        ) {
            return;
        }

        if (
            !FusedArithmetic.TryMixedScaleProduct(
            a: (-tangentSpeed).Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: tangentMassRaw,
            fractionBitsB: FixedWorldRigid.Scales.EffectiveMass,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var stickImpulseRaw
        )
        ) {
            return;
        }

        var maxTangentImpulseRaw = SaturatingNonnegativeProduct(
            left: SaturatingNonnegativeProduct(left: rigid.Mass, right: normalSpeedRemoved),
            right: rigid.Friction
        ).Value;
        var clampedImpulseRaw = Math.Clamp(
            value: stickImpulseRaw,
            min: -maxTangentImpulseRaw,
            max: maxTangentImpulseRaw
        );

        if (clampedImpulseRaw == 0L) {
            return;
        }

        FixedTwoBodyKernel.ApplyImpulse(
            bodyA: groundHandle,
            anchorA: FixedVector3.Zero,
            bodyB: ballHandle,
            anchorB: contactAnchor,
            normal: tangentDirection,
            impulseRaw: clampedImpulseRaw,
            scales: FixedWorldRigid.Scales,
            refusals: ref refusals
        );
        m_rigidVelocity = ballHandle.LinearVelocity;
        m_angularVelocity = ballHandle.AngularVelocity;
    }
    /// <summary>Resolves a box or capsule rigid body's ground contact over its own support manifold
    /// (<see cref="FixedRigidWitness.SupportManifold"/> — up to four box corners, or two capsule cap points lying on
    /// a side, down to a single true corner once the body is tilted enough) instead of one CoM-anchored witness
    /// point: <paramref name="iterations"/> sequential-impulse passes distribute a real NORMAL impulse — reflected by
    /// restitution on a rising edge, targeting zero closing speed otherwise, on the same terms
    /// <see cref="ResolveRigidContact"/>'s own restitution branch uses — over every manifold point still closing,
    /// each carrying its own lever arm off the body's centre of mass, then a per-point Coulomb friction impulse
    /// clamped to THAT point's own accumulated normal impulse — never the manifold's total, so one heavily loaded
    /// corner cannot license slip at a corner carrying none. A normal impulse applied through an off-centre corner is
    /// what produces real torque: an upright body's weight, reacting only against the corner(s) still under it, is
    /// what keeps its centre of mass over its support polygon (or topples it once that polygon no longer reaches
    /// under the centre) without an artificial extra damping term.</summary>
    private void ResolveRigidGroundManifold(FixedVector3 normal, FixedVector3 preVelocity, FixedVector3 postSweepVelocity, ref bool contacting, in FixedWorldRigid rigid, FixedBodyColliderVolume volume, int iterations) {
        var incoming = FixedVector3.Dot(
            left: preVelocity,
            right: normal
        );
        var wasContacting = contacting;

        contacting = true;

        if (rigid.BoundingRadius <= FixedQ4816.Zero) {
            return;
        }

        Span<FixedVector3> anchors = stackalloc FixedVector3[4];
        var pointCount = FixedRigidWitness.SupportManifold(
            anchors: anchors,
            centerOffset: rigid.CenterOffset,
            normal: normal,
            orientation: m_orientation,
            volume: volume
        );

        // AdvanceRigid's earlier field.ResolveSweep call is a SINGLE-POINT CCD pass (no lever): for a box or capsule
        // it already consumes this substep's own gravity-driven closing velocity at the body's CENTRE, before this
        // manifold ever runs — reading the post-sweep m_rigidVelocity here would hand every corner an already-zeroed
        // deficit and this loop would apply no impulse at all, freezing a tilted body exactly where it was posed
        // rather than letting gravity's torque about the still-loaded corner(s) carry it the rest of the way over.
        // The manifold instead reconstructs what SHOULD reach it: the RAW pre-sweep velocity (gravity intact, for the
        // corner impulses to redistribute with real lever arms) plus whatever an obstruction contact THIS SAME
        // substep already added on top of the sweep's own result (`m_rigidVelocity - postSweepVelocity`) — so that
        // contact's effect is preserved rather than discarded. A genuine first impact (the rising edge) still
        // reflects by restitution, exactly like ResolveRigidContact's own single-point path. Angular velocity is
        // untouched by the sweep, so it carries over as-is (any obstruction impulse's spin included).
        var obstructionDelta = (m_rigidVelocity - postSweepVelocity);
        var manifoldBaseline = (preVelocity + obstructionDelta);

        if (
            (!wasContacting) &&
            (incoming < FixedQ4816.Zero)
        ) {
            manifoldBaseline += (normal * (rigid.Restitution * -incoming));
        }

        var ballHandle = TwoBodyHandle();

        ballHandle.LinearVelocity = manifoldBaseline;

        var groundHandle = GroundPhantomHandle();
        var refusals = 0;
        Span<long> accumulatedNormalImpulseRaw = stackalloc long[4];
        var passes = Math.Max(val1: 1, val2: iterations);

        // Sequential impulse with a per-point ACCUMULATED normal impulse, clamped to non-negative and applied by
        // DELTA (never a bare positive-only add): every corner shares the same body's mass/inertia, so one corner's
        // correction shifts the closing speed every other corner reads. Clamping the running total — rather than
        // only ever adding more whenever a point still reads closing<0 — lets a later pass back a point OFF an
        // earlier pass's over-application when the coupling has since satisfied it, so passes converge toward the
        // manifold's true equilibrium (zero net closing at every point still under load) instead of only ever
        // injecting energy. A body already resting under gravity converges to zero delta, not a residual climb.
        for (var pass = 0; (pass < passes); pass++) {
            for (var point = 0; (point < pointCount); point++) {
                var anchor = anchors[point];
                var contactVelocity = (ballHandle.LinearVelocity + FixedVector3.Cross(
                    left: ballHandle.AngularVelocity,
                    right: anchor
                ));
                var closing = FixedVector3.Dot(
                    left: contactVelocity,
                    right: normal
                );

                if (!FixedTwoBodyKernel.TryEffectiveMass(
                    bodyA: groundHandle,
                    anchorA: FixedVector3.Zero,
                    bodyB: ballHandle,
                    anchorB: anchor,
                    normal: normal,
                    scales: FixedWorldRigid.Scales,
                    normalMassRaw: out var normalMassRaw,
                    refusals: ref refusals
                )) {
                    continue;
                }

                if (!FusedArithmetic.TryMixedScaleProduct(
                    a: (-closing).Value,
                    fractionBitsA: FixedQ4816.FractionBitCount,
                    b: normalMassRaw,
                    fractionBitsB: FixedWorldRigid.Scales.EffectiveMass,
                    fractionBitsOut: FixedQ4816.FractionBitCount,
                    result: out var lambdaRaw
                )) {
                    continue;
                }

                var previousAccumulated = accumulatedNormalImpulseRaw[point];
                var newAccumulated = Math.Max(val1: 0L, val2: unchecked((previousAccumulated + lambdaRaw)));
                var deltaRaw = unchecked((newAccumulated - previousAccumulated));

                if (deltaRaw == 0L) {
                    continue;
                }

                accumulatedNormalImpulseRaw[point] = newAccumulated;

                FixedTwoBodyKernel.ApplyImpulse(
                    bodyA: groundHandle,
                    anchorA: FixedVector3.Zero,
                    bodyB: ballHandle,
                    anchorB: anchor,
                    normal: normal,
                    impulseRaw: deltaRaw,
                    scales: FixedWorldRigid.Scales,
                    refusals: ref refusals
                );
            }
        }

        for (var point = 0; (point < pointCount); point++) {
            if (accumulatedNormalImpulseRaw[point] <= 0L) {
                continue;
            }

            var anchor = anchors[point];
            var contactVelocity = (ballHandle.LinearVelocity + FixedVector3.Cross(
                left: ballHandle.AngularVelocity,
                right: anchor
            ));
            var tangential = (contactVelocity - (normal * FixedVector3.Dot(
                left: contactVelocity,
                right: normal
            )));

            if (tangential == FixedVector3.Zero) {
                continue;
            }

            var tangentSpeed = tangential.Length;
            var tangentDirection = (tangential / tangentSpeed);

            if (!FixedTwoBodyKernel.TryEffectiveMass(
                bodyA: groundHandle,
                anchorA: FixedVector3.Zero,
                bodyB: ballHandle,
                anchorB: anchor,
                normal: tangentDirection,
                scales: FixedWorldRigid.Scales,
                normalMassRaw: out var tangentMassRaw,
                refusals: ref refusals
            )) {
                continue;
            }

            if (!FusedArithmetic.TryMixedScaleProduct(
                a: (-tangentSpeed).Value,
                fractionBitsA: FixedQ4816.FractionBitCount,
                b: tangentMassRaw,
                fractionBitsB: FixedWorldRigid.Scales.EffectiveMass,
                fractionBitsOut: FixedQ4816.FractionBitCount,
                result: out var stickImpulseRaw
            )) {
                continue;
            }

            var maxTangentImpulseRaw = SaturatingNonnegativeProduct(
                left: FixedQ4816.FromRawBits(value: accumulatedNormalImpulseRaw[point]),
                right: rigid.Friction
            ).Value;
            var clampedImpulseRaw = Math.Clamp(
                value: stickImpulseRaw,
                min: -maxTangentImpulseRaw,
                max: maxTangentImpulseRaw
            );

            if (clampedImpulseRaw == 0L) {
                continue;
            }

            FixedTwoBodyKernel.ApplyImpulse(
                bodyA: groundHandle,
                anchorA: FixedVector3.Zero,
                bodyB: ballHandle,
                anchorB: anchor,
                normal: tangentDirection,
                impulseRaw: clampedImpulseRaw,
                scales: FixedWorldRigid.Scales,
                refusals: ref refusals
            );
        }

        m_rigidVelocity = ballHandle.LinearVelocity;
        m_angularVelocity = ballHandle.AngularVelocity;
    }
}
