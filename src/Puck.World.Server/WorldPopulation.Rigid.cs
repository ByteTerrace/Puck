using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    private static readonly FixedQ4816 RigidPairFrictionHalf = FixedQ4816.FromDouble(value: 0.5d);

    /// <summary>Gets the number of active dynamic-body pairs the most recent <see cref="ResolveDynamicContacts"/>
    /// resolved through the rigid impulse path (at least one side a rigid kit) rather than plain positional
    /// depenetration.</summary>
    public int RigidPairResolvedCount { get; private set; }

    /// <summary>Describes the rigid solver's current census and last-tick work — the <c>world.budget</c> cost-sheet
    /// segment: active rigid body count, resting count, dynamic-pair resolutions, and the highest per-body substep
    /// count any active rigid body took its most recent step (the derived cost <see cref="WorldBodyContactPolicy.RigidSubstepCeiling"/>
    /// bounds).</summary>
    public string DescribeRigidWork() {
        var rigidCount = 0;
        var restingCount = 0;
        var worstSubsteps = 0;

        for (var index = 0; (index < m_entries.Length); index++) {
            if (m_entries[index] is not { Active: true, Body: { IsRigid: true } body }) {
                continue;
            }

            rigidCount++;

            if (body.Resting) {
                restingCount++;
            }

            worstSubsteps = Math.Max(val1: worstSubsteps, val2: body.RigidStaticSubstepsThisTick);
        }

        return $"rigid {rigidCount} body/bodies ({restingCount} resting), pairsResolved={RigidPairResolvedCount}, worstSubsteps={worstSubsteps}/{m_bodyContactPolicy.RigidSubstepCeiling}";
    }

    /// <summary>Resolves one already-detected overlapping pair where at least one side is a rigid kit: an
    /// impulse-based restitution/friction response through <see cref="FixedTwoBodyKernel"/> (real angular coupling
    /// when both sides are rigid; a kinematic side contributes its own velocity but is never itself pushed — see
    /// <see cref="WorldBody.TwoBodyHandle"/>), plus the SAME positional split <see cref="ResolveDynamicContacts"/>
    /// already applies to a kinematic-vs-kinematic pair, restricted to the rigid side(s) only: a kinematic body is
    /// never repositioned by a rigid body it did not choose to contact physically.</summary>
    /// <param name="left">The first body in the pair.</param>
    /// <param name="right">The second body in the pair.</param>
    /// <param name="correction">The pair's already-computed overlap correction — its direction is the contact normal
    /// (pointing from <paramref name="right"/> toward <paramref name="left"/>), its magnitude the penetration depth.</param>
    private void ResolveRigidPairContact(WorldBody left, WorldBody right, FixedVector3 correction) {
        if (correction == FixedVector3.Zero) {
            return;
        }

        var normal = correction.Normalize();
        var leftHandle = left.TwoBodyHandle();
        var rightHandle = right.TwoBodyHandle();
        var zero = FixedVector3.Zero;
        var refusals = 0;
        var closingSpeed = FixedTwoBodyKernel.RelativeNormalVelocity(
            bodyA: leftHandle,
            anchorA: zero,
            bodyB: rightHandle,
            anchorB: zero,
            normal: normal
        );

        if (
            (closingSpeed < FixedQ4816.Zero) &&
            FixedTwoBodyKernel.TryEffectiveMass(
            bodyA: leftHandle,
            anchorA: zero,
            bodyB: rightHandle,
            anchorB: zero,
            normal: normal,
            scales: FixedWorldRigid.Scales,
            normalMassRaw: out var normalMassRaw,
            refusals: ref refusals
        )
        ) {
            var restitution = ((left.RigidRestitution + right.RigidRestitution) * RigidPairFrictionHalf);
            var impulseScalar = ((FixedQ4816.One + restitution) * -closingSpeed);

            if (
                FusedArithmetic.TryMixedScaleProduct(
                a: impulseScalar.Value,
                fractionBitsA: FixedQ4816.FractionBitCount,
                b: normalMassRaw,
                fractionBitsB: FixedWorldRigid.Scales.EffectiveMass,
                fractionBitsOut: FixedQ4816.FractionBitCount,
                result: out var impulseRaw
            ) &&
                (impulseRaw > 0L)
            ) {
                FixedTwoBodyKernel.ApplyImpulse(
                    bodyA: leftHandle,
                    anchorA: zero,
                    bodyB: rightHandle,
                    anchorB: zero,
                    normal: normal,
                    impulseRaw: impulseRaw,
                    scales: FixedWorldRigid.Scales,
                    refusals: ref refusals
                );

                // A coarse Coulomb-style tangential damp on the post-impulse relative velocity — a single-tick
                // approximation (no persistent contact/friction-cone state), disclosed as a simplification of
                // per-manifold friction: real billiard/pin contacts are dominated by the normal impulse this
                // reproduces exactly, and this keeps two touching rigid bodies from sliding past each other forever.
                var friction = FixedQ4816.Max(
                    x: FixedQ4816.Zero,
                    y: (FixedQ4816.One - ((left.RigidFriction + right.RigidFriction) * RigidPairFrictionHalf))
                );

                leftHandle.LinearVelocity *= friction;
                rightHandle.LinearVelocity *= friction;

                left.CommitRigidHandle(handle: leftHandle);
                right.CommitRigidHandle(handle: rightHandle);
                RigidPairResolvedCount++;
            }
        }

        // Positional depenetration: split between two rigid bodies exactly as the kinematic-vs-kinematic path does;
        // restricted to the rigid side alone against a kinematic partner, which never moves for a body it did not
        // choose to contact.
        if (left.IsRigid && right.IsRigid) {
            var shared = (correction / FixedQ4816.FromInteger(value: 2L));

            left.ApplyDynamicContact(correction: shared);
            right.ApplyDynamicContact(correction: -shared);
        } else if (left.IsRigid) {
            left.ApplyDynamicContact(correction: correction);
        } else if (right.IsRigid) {
            right.ApplyDynamicContact(correction: -correction);
        }
    }
}
