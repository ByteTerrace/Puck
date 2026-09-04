using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // The persistent FixedTwoBodyKernel vehicle RigidHandle() maintains — allocated once, mutated in place every
    // call, so steady-state dynamic-vs-dynamic contact resolution allocates nothing.
    private FixedRigidBody? m_rigidHandle;
    // Whether the previous substep was already in static contact — the restitution edge latch (see AdvanceRigid):
    // a rising edge (false -> true) is a genuine impact, continuous contact is not.
    private bool m_rigidContacting;

    /// <summary>Gets the number of substeps the most recent <see cref="AdvanceRigid"/> call took — the
    /// derived-count read-back <c>world.budget</c> echoes. Zero for a locomotion kit or a body never yet advanced.</summary>
    public int RigidStaticSubstepsThisTick { get; private set; }

    // Below these magnitudes a rigid body counts as "not moving" toward the resting latch — small enough that a
    // ball settled in a tray floor's own contact skin never reads as still drifting, large enough that ordinary
    // fixed-point contact noise never re-arms it.
    private static readonly FixedQ4816 RigidRestLinearThreshold = FixedQ4816.FromDouble(value: 0.05d);
    private static readonly FixedQ4816 RigidRestAngularThreshold = FixedQ4816.FromDouble(value: 0.1d);
    // How long a grounded, slow rigid body must stay that way before the resting latch actually closes — long enough
    // that a ball crossing a contact skin's noise band for one tick never freezes mid-roll.
    private static readonly ulong RigidRestHoldTicks = FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: 0.25d));
    private static readonly FixedQ4816 RigidHalf = FixedQ4816.FromDouble(value: 0.5d);
    private static readonly FixedQ4816 RigidTwo = FixedQ4816.FromInteger(value: 2L);

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
    /// <summary>Gets the rigid facet's authored mass, or zero for a locomotion kit.</summary>
    public FixedQ4816 RigidMass => (m_rigid?.Mass ?? FixedQ4816.Zero);
    /// <summary>Gets the rigid facet's authored restitution against another rigid body, or zero for a locomotion kit.</summary>
    public FixedQ4816 RigidRestitution => (m_rigid?.Restitution ?? FixedQ4816.Zero);
    /// <summary>Gets the rigid facet's authored friction against another rigid body, or zero for a locomotion kit.</summary>
    public FixedQ4816 RigidFriction => (m_rigid?.Friction ?? FixedQ4816.Zero);

    /// <summary>Applies an instantaneous world-space impulse to this rigid body's linear velocity
    /// (<c>Δv = impulse / mass</c>) and wakes it from rest. A no-op for a locomotion kit.</summary>
    /// <param name="impulse">The impulse, in mass·length/time units.</param>
    internal void ApplyRigidImpulse(FixedVector3 impulse) {
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
    /// <summary>Advances a rigid-kit body by one exact simulation step: damping, gravity, a swept, substepped
    /// integration against the world's static contact field with restitution/friction/rolling response, and the
    /// resting latch. Dynamic-vs-dynamic contact is a separate pass
    /// (<see cref="WorldPopulation.ResolveDynamicContacts"/>), run after every body has advanced.</summary>
    /// <param name="entityIndex">This body's population index — the same index gravity/checkpoint reads key on.</param>
    /// <param name="stepTicks">The exact engine ticks this call advances.</param>
    /// <param name="maxSubsteps">The authored ceiling (<see cref="WorldBodyContactPolicy.RigidSubstepCeiling"/>) on
    /// how many substeps this call may take; the actual count is derived from this tick's speed and the collider's
    /// bounding radius, never authored directly.</param>
    private void AdvanceRigid(int entityIndex, ulong stepTicks, int maxSubsteps) {
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
        // authority rate, so the travel this tick is bounded against a fraction of the collider's own bounding
        // radius rather than authored as a free knob.
        var travel = (m_rigidVelocity.Length * tickSeconds);
        var perSubstepBound = FixedQ4816.Max(
            x: (rigid.BoundingRadius * RigidHalf),
            y: FixedQ4816.FromDouble(value: 0.001d)
        );
        var derivedSubsteps = 1;

        while (
            (derivedSubsteps < Math.Max(val1: 1, val2: maxSubsteps)) &&
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

            var deltaRotation = FixedQuaternion.Exp(bivector: (m_angularVelocity * (subSeconds * RigidHalf)));

            m_orientation = (deltaRotation * m_orientation).Normalize();

            var center = (m_position + m_orientation.Rotate(vector: rigid.CenterOffset));

            center += (m_rigidVelocity * subSeconds);

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

            var normal = (resolution.Grounded
                ? resolution.GroundNormal
                : resolution.ObstructionNormal
            );

            if (normal != FixedVector3.Zero) {
                // Restitution fires only on the RISING edge of contact (m_rigidContacting false -> true this
                // substep) — a genuine impact, never every tick of continuous rest: applying it every contacting
                // tick would read gravity's own per-tick pull as a fresh impact and bounce the body back up by
                // restitution·g·dt every tick forever. While resting, ResolveSweep's own inward-component removal is
                // what holds the body still; nothing here adds velocity back.
                var incoming = FixedVector3.Dot(
                    left: preVelocity,
                    right: normal
                );

                if (
                    !m_rigidContacting &&
                    (incoming < FixedQ4816.Zero)
                ) {
                    velocity += (normal * (rigid.Restitution * -incoming));
                }

                m_rigidContacting = true;

                var normalComponent = (normal * FixedVector3.Dot(
                    left: velocity,
                    right: normal
                ));
                var tangential = (velocity - normalComponent);
                var keptTangential = (tangential * RateDecay(
                    rate: rigid.Friction,
                    seconds: subSeconds
                ));
                var removedTangential = (tangential - keptTangential);

                if (
                    (rigid.BoundingRadius > FixedQ4816.Zero) &&
                    (removedTangential != FixedVector3.Zero)
                ) {
                    // A removed tangential (slip) velocity becomes rolling spin about the contact-plane axis
                    // perpendicular to it: ω += (n × v_removed) / r — the same lever a contact-point friction
                    // impulse would apply, folded to angular velocity directly rather than through a torque impulse.
                    m_angularVelocity += (FixedVector3.Cross(
                        left: normal,
                        right: removedTangential
                    ) / rigid.BoundingRadius);
                }

                m_angularVelocity *= RateDecay(
                    rate: rigid.RollingFriction,
                    seconds: subSeconds
                );
                velocity = (normalComponent + keptTangential);
                grounded = true;
            } else {
                m_rigidContacting = false;
            }

            m_rigidVelocity = velocity;
        }

        var linearSpeed = m_rigidVelocity.Length;
        var angularSpeed = m_angularVelocity.Length;

        if (
            grounded &&
            (linearSpeed <= RigidRestLinearThreshold) &&
            (angularSpeed <= RigidRestAngularThreshold)
        ) {
            m_restingHoldTicks += stepTicks;

            if (m_restingHoldTicks >= RigidRestHoldTicks) {
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
}
