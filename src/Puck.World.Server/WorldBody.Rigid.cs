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
public readonly record struct RigidContactPolicy(
    FixedQ4816 RestLinearThreshold,
    FixedQ4816 RestAngularThreshold,
    ulong RestHoldTicks,
    FixedQ4816 SubstepTravelFraction,
    FixedQ4816 SubstepMinimumTravel,
    int SubstepCeiling,
    FixedQ4816 PairRestitutionThreshold
) {
    /// <summary>Compiles an authored <see cref="WorldBodyContactPolicy"/>'s rigid fields to this fixed-point form.</summary>
    public static RigidContactPolicy FromAuthored(WorldBodyContactPolicy policy) => new(
        RestLinearThreshold: FixedQ4816.FromDouble(value: policy.RigidRestLinearSpeed),
        RestAngularThreshold: FixedQ4816.FromDouble(value: policy.RigidRestAngularSpeed),
        RestHoldTicks: FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: policy.RigidRestHoldSeconds)),
        SubstepTravelFraction: FixedQ4816.FromDouble(value: policy.RigidSubstepTravelFraction),
        SubstepMinimumTravel: FixedQ4816.FromDouble(value: policy.RigidSubstepMinimumTravel),
        SubstepCeiling: policy.RigidSubstepCeiling,
        PairRestitutionThreshold: FixedQ4816.FromDouble(value: policy.RigidPairRestitutionSpeed)
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

    /// <summary>Gets the number of substeps the most recent <see cref="AdvanceRigid"/> call took — the
    /// derived-count read-back <c>world.budget</c> echoes. Zero for a locomotion kit or a body never yet advanced.</summary>
    public int RigidStaticSubstepsThisTick { get; private set; }

    private static readonly FixedQ4816 RigidHalf = FixedQ4816.FromDouble(value: 0.5d);

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
    /// <summary>Gets the rigid facet's authored mass, or zero for a locomotion kit.</summary>
    public FixedQ4816 RigidMass => (m_rigid?.Mass ?? FixedQ4816.Zero);
    /// <summary>Gets the rigid facet's authored restitution against another rigid body, or zero for a locomotion kit.</summary>
    public FixedQ4816 RigidRestitution => (m_rigid?.Restitution ?? FixedQ4816.Zero);
    /// <summary>Gets the rigid facet's authored friction against another rigid body, or zero for a locomotion kit.</summary>
    public FixedQ4816 RigidFriction => (m_rigid?.Friction ?? FixedQ4816.Zero);
    /// <summary>Gets the rigid facet's derived conservative bounding radius — the same radius the pair-contact
    /// anchor approximation and the static-contact substep bound both read. Zero for a locomotion kit.</summary>
    public FixedQ4816 RigidBoundingRadius => (m_rigid?.BoundingRadius ?? FixedQ4816.Zero);

    /// <summary>Applies an instantaneous world-space impulse to this rigid body's linear velocity
    /// (<c>Δv = impulse / mass</c>) and wakes it from rest. A no-op for a locomotion kit.</summary>
    /// <param name="impulse">The impulse, in mass·length/time units.</param>
    public void ApplyRigidImpulse(FixedVector3 impulse) {
        if (m_rigid is not { } rigid) {
            return;
        }

        m_rigidVelocity += ImpulseToVelocity(
            impulse: impulse,
            inverseMassRaw: rigid.InverseMassRaw
        );
        m_resting = false;
        m_restingHoldTicks = 0UL;
    }
    // The tick-rate-independent decay factor an authored per-second rate applies over an elapsed duration:
    // (1 - rate*seconds), clamped to [0, 1] so an aggressive rate at a wide step never reverses the quantity it
    // damps. Shared by linear/angular damping and (contact) friction/rolling friction — the same rate authored once
    // means the same physical decay whatever the world's simulation rate.
    private static FixedQ4816 RateDecay(FixedQ4816 rate, FixedQ4816 seconds) => FixedQ4816.Max(
        x: FixedQ4816.Zero,
        y: (FixedQ4816.One - (rate * seconds))
    );
    // impulse * inverseMass, both at their own declared scales, rounded once to FixedQ4816.
    private static FixedVector3 ImpulseToVelocity(FixedVector3 impulse, long inverseMassRaw) {
        FixedQ4816 Scale(FixedQ4816 component) {
            _ = FusedArithmetic.TryMixedScaleProduct(
                a: component.Value,
                fractionBitsA: FixedQ4816.FractionBitCount,
                b: inverseMassRaw,
                fractionBitsB: FixedWorldRigid.Scales.InverseMass,
                fractionBitsOut: FixedQ4816.FractionBitCount,
                result: out var raw
            );

            return FixedQ4816.FromRawBits(value: raw);
        }

        return new FixedVector3(
            X: Scale(component: impulse.X),
            Y: Scale(component: impulse.Y),
            Z: Scale(component: impulse.Z)
        );
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
            handle.LinearVelocity = m_rigidVelocity;
            handle.AngularVelocity = m_angularVelocity;
            handle.InverseMassRaw = rigid.InverseMassRaw;
            handle.InverseInertiaXX = rigid.InverseInertiaXX;
            handle.InverseInertiaYY = rigid.InverseInertiaYY;
            handle.InverseInertiaZZ = rigid.InverseInertiaZZ;
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
        // bounding radius rather than a fixed knob.
        var travel = (m_rigidVelocity.Length * tickSeconds);
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
                    volumes: collider.Volumes
                );
            }

            m_position = bodyOrigin;
            m_rigidVelocity = velocity;

            // Ground and obstruction are independent contacts that may both hold in the same substep (a ball
            // resting on the floor that also clips a wall) — each carries its own rising-edge restitution latch and
            // its own coupled slip-friction solve, so a wall hit bounces on its own first contact even while the
            // floor contact underneath has been continuous for many ticks (see the two latch fields above).
            if (resolution.ObstructionNormal != FixedVector3.Zero) {
                ResolveRigidContact(
                    normal: resolution.ObstructionNormal,
                    preVelocity: preVelocity,
                    contacting: ref m_rigidObstructionContacting,
                    rigid: in rigid
                );
            } else {
                m_rigidObstructionContacting = false;
            }

            if (resolution.GroundNormal != FixedVector3.Zero) {
                ResolveRigidContact(
                    normal: resolution.GroundNormal,
                    preVelocity: preVelocity,
                    contacting: ref m_rigidGroundContacting,
                    rigid: in rigid
                );
                // Rolling resistance: a pure angular-velocity decay while actually grounded, distinct from — and
                // applied after — the coupled slip-friction solve above (which already moved translational energy
                // into rotational and back through the shared inertia, not this decay).
                m_angularVelocity *= RateDecay(
                    rate: rigid.RollingFriction,
                    seconds: subSeconds
                );
            } else {
                m_rigidGroundContacting = false;
            }

            grounded = (grounded || resolution.Grounded);
        }

        var linearSpeed = m_rigidVelocity.Length;
        var angularSpeed = m_angularVelocity.Length;

        if (
            grounded &&
            (linearSpeed <= policy.RestLinearThreshold) &&
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
    private void ResolveRigidContact(FixedVector3 normal, FixedVector3 preVelocity, ref bool contacting, in FixedWorldRigid rigid) {
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

        // The contact point, relative to the body's own center: straight out from the center opposite the contact
        // normal, at the collider's conservative bounding radius — the same single-point approximation the
        // rigid-vs-rigid pair anchors use.
        var contactAnchor = (-normal * rigid.BoundingRadius);
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

        var maxTangentImpulseRaw = (rigid.Mass * normalSpeedRemoved * rigid.Friction).Value;
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
}
