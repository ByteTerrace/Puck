using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    private static readonly FixedQ4816 RigidPairHalf = FixedQ4816.FromDouble(value: 0.5d);

    /// <summary>Gets the number of active dynamic-body pairs the most recent <see cref="ResolveDynamicContacts"/>
    /// resolved through the rigid impulse path (at least one side a rigid kit) rather than plain positional
    /// depenetration.</summary>
    public int RigidPairResolvedCount { get; private set; }

    /// <summary>Gets the document-derived speed a <c>body.impulse</c> command's resulting velocity may not exceed —
    /// <see cref="WorldFacePortalPolicy.SpeedCeiling"/>, the same fastest travel the document already declares for
    /// crossing a face. <see cref="WorldBody.TryApplyRigidImpulse"/> refuses by name rather than applying a delta
    /// that would carry a rigid body past it, so a degenerate impulse magnitude never reaches the solver as an
    /// unrepresentable velocity.</summary>
    public FixedQ4816 RigidVelocityCeiling => m_rigidVelocityCeiling;

    /// <summary>Describes the rigid solver's current census and last-tick work — the <c>world.budget</c> cost-sheet
    /// segment: active rigid body count, resting count, dynamic-pair resolutions, the highest per-body substep count
    /// any active rigid body took its most recent step (the derived cost <see cref="WorldBodyContactPolicy.RigidSubstepCeiling"/>
    /// bounds), and the compiled rest/substep policy every rigid body's <see cref="WorldBody.AdvanceRigid"/> call
    /// reads this tick.</summary>
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

        return $"rigid {rigidCount} body/bodies ({restingCount} resting), pairsResolved={RigidPairResolvedCount}, pairPasses={RigidPairPassesThisTick}/{m_bodyContactPolicy.RigidPairIterationCeiling} pairBudget={m_bodyContactPolicy.RigidPairIterationBudget}, manifoldIterations={m_bodyContactPolicy.RigidManifoldIterations}, worstSubsteps={worstSubsteps}/{m_bodyContactPolicy.RigidSubstepCeiling}, restLinear<={m_bodyContactPolicy.RigidRestLinearSpeed:0.###} restAngular<={m_bodyContactPolicy.RigidRestAngularSpeed:0.###} restHold={m_bodyContactPolicy.RigidRestHoldSeconds:0.###}s substepFraction={m_bodyContactPolicy.RigidSubstepTravelFraction:0.###} substepMinTravel={m_bodyContactPolicy.RigidSubstepMinimumTravel:0.####} pairRestitutionSpeed={m_bodyContactPolicy.RigidPairRestitutionSpeed:0.###} impulseVelocityCeiling={(double)m_rigidVelocityCeiling:0.###}";
    }

    /// <summary>Resolves one already-detected overlapping pair where at least one side is a rigid kit: an
    /// impulse-based restitution/friction response through <see cref="FixedTwoBodyKernel"/> (real angular coupling
    /// when both sides are rigid — the contact anchor is each rigid side's own true witness point
    /// (<see cref="FixedRigidWitness.Anchor"/>) facing the other body, never the body-center anchor a torque-free
    /// response would imply and never a point on the conservative bounding sphere either; a kinematic side
    /// contributes its own velocity but is never itself pushed — see <see cref="WorldBody.TwoBodyHandle"/>), plus the
    /// SAME positional split <see cref="ResolveDynamicContacts"/> already applies to a kinematic-vs-kinematic pair,
    /// restricted to the rigid side(s) only: a kinematic body is never repositioned by a rigid body it did not choose
    /// to contact physically. May be replayed more than once in the same tick — see
    /// <see cref="ResolveDynamicContacts"/>'s own extra-pass replay.</summary>
    /// <param name="left">The first body in the pair.</param>
    /// <param name="right">The second body in the pair.</param>
    /// <param name="correction">The pair's already-computed overlap correction — its direction points from
    /// <paramref name="right"/> toward <paramref name="left"/> (<see cref="Puck.Physics.FixedDynamicBodyContacts"/>'s
    /// own convention: added to <paramref name="left"/>'s position, subtracted from <paramref name="right"/>'s), its
    /// magnitude the penetration depth.</param>
    private void ResolveRigidPairContact(WorldBody left, WorldBody right, FixedVector3 correction) {
        if (correction == FixedVector3.Zero) {
            return;
        }

        // The contact normal points away from `right` toward `left` (the correction's own direction).
        // FixedTwoBodyKernel's contract names its "A" side the body the normal points AWAY FROM — so `right` plays
        // A and `left` plays B here. Naming them the other way around (as this pair once did) reads an APPROACHING
        // pair as a POSITIVE closing speed, so the impulse gate below never opens until the two bodies' centers have
        // already crossed.
        var normal = correction.Normalize();
        var aHandle = right.TwoBodyHandle();
        var bHandle = left.TwoBodyHandle();
        // Each rigid side's own contact-point anchor: the true witness point of ITS shape facing the other body —
        // never the body-center (zero) anchor, which would carry no lever and so no torque, no matter how far
        // off-center a strike actually lands, and never a point on the conservative bounding sphere either (a struck
        // capsule or box needs the lever arm its own true surface carries, not its enclosing sphere's). A kinematic
        // side's anchor is irrelevant (its handle carries zero inverse inertia, so its own angular term is always
        // zero) and left at zero.
        var anchorA = (right.IsRigid
            ? FixedRigidWitness.Anchor(centerOffset: right.RigidCenterOffset, orientation: right.FixedOrientation, volume: right.RigidWitnessVolume(), worldDirection: normal)
            : FixedVector3.Zero
        );
        var anchorB = (left.IsRigid
            ? FixedRigidWitness.Anchor(centerOffset: left.RigidCenterOffset, orientation: left.FixedOrientation, volume: left.RigidWitnessVolume(), worldDirection: -normal)
            : FixedVector3.Zero
        );
        var refusals = 0;
        var closingSpeed = FixedTwoBodyKernel.RelativeNormalVelocity(
            bodyA: aHandle,
            anchorA: anchorA,
            bodyB: bHandle,
            anchorB: anchorB,
            normal: normal
        );

        if (
            (closingSpeed < FixedQ4816.Zero) &&
            FixedTwoBodyKernel.TryEffectiveMass(
            bodyA: aHandle,
            anchorA: anchorA,
            bodyB: bHandle,
            anchorB: anchorB,
            normal: normal,
            scales: FixedWorldRigid.Scales,
            normalMassRaw: out var normalMassRaw,
            refusals: ref refusals
        )
        ) {
            var restitution = ((closingSpeed < -m_rigidContactPolicy.PairRestitutionThreshold)
                ? ((left.RigidRestitution + right.RigidRestitution) * RigidPairHalf)
                : FixedQ4816.Zero
            );
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
                    bodyA: aHandle,
                    anchorA: anchorA,
                    bodyB: bHandle,
                    anchorB: anchorB,
                    normal: normal,
                    impulseRaw: impulseRaw,
                    scales: FixedWorldRigid.Scales,
                    refusals: ref refusals
                );
                ApplyRigidPairFriction(
                    aHandle: aHandle,
                    anchorA: anchorA,
                    bHandle: bHandle,
                    anchorB: anchorB,
                    normal: normal,
                    normalImpulseRaw: impulseRaw,
                    frictionCoefficient: AverageNonnegative(
                        left: left.RigidFriction,
                        right: right.RigidFriction
                    ),
                    refusals: ref refusals
                );
                left.CommitRigidHandle(handle: bHandle);
                right.CommitRigidHandle(handle: aHandle);
                RigidPairResolvedCount++;
            }
        }

        // Positional depenetration: split between two rigid bodies exactly as the kinematic-vs-kinematic path does;
        // restricted to the rigid side alone against a kinematic partner, which never moves for a body it did not
        // choose to contact. Routed through the rigid-aware correction (never the locomotion one, whose planar/
        // vertical-velocity channels a rigid body does not use) so a body this displaces wakes rather than keeping a
        // stale resting latch while it is visibly being pushed.
        if (left.IsRigid && right.IsRigid) {
            var shared = (correction / FixedQ4816.FromInteger(value: 2L));

            left.ApplyRigidPositionalCorrection(correction: shared);
            right.ApplyRigidPositionalCorrection(correction: -shared);
        } else if (left.IsRigid) {
            left.ApplyRigidPositionalCorrection(correction: correction);
        } else if (right.IsRigid) {
            right.ApplyRigidPositionalCorrection(correction: -correction);
        }
    }
    /// <summary>Applies a Coulomb-style tangential impulse at a just-resolved rigid pair's contact point: the
    /// impulse that would fully cancel the post-normal-impulse relative tangential velocity, clamped to the pair's
    /// average friction coefficient times the normal impulse just applied, and applied through the SAME two-body
    /// kernel the normal impulse used — so it moves exactly as much momentum off one body as it moves onto the
    /// other, rather than independently rescaling either body's whole velocity vector (which would burn or invent
    /// momentum along the normal too).</summary>
    private static void ApplyRigidPairFriction(FixedRigidBody aHandle, FixedVector3 anchorA, FixedRigidBody bHandle, FixedVector3 anchorB, FixedVector3 normal, long normalImpulseRaw, FixedQ4816 frictionCoefficient, ref int refusals) {
        var relativeVelocity = ((bHandle.LinearVelocity + FixedVector3.Cross(
            left: bHandle.AngularVelocity,
            right: anchorB
        )) - (aHandle.LinearVelocity + FixedVector3.Cross(
            left: aHandle.AngularVelocity,
            right: anchorA
        )));
        var tangential = (relativeVelocity - (normal * FixedVector3.Dot(
            left: relativeVelocity,
            right: normal
        )));

        if (tangential == FixedVector3.Zero) {
            return;
        }

        var tangentDirection = tangential.Normalize();

        if (
            !FixedTwoBodyKernel.TryEffectiveMass(
            bodyA: aHandle,
            anchorA: anchorA,
            bodyB: bHandle,
            anchorB: anchorB,
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
            a: (-tangential.Length).Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: tangentMassRaw,
            fractionBitsB: FixedWorldRigid.Scales.EffectiveMass,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var stickImpulseRaw
        )
        ) {
            return;
        }

        var maxTangentImpulseRaw = WorldBody.SaturatingNonnegativeProduct(
            left: FixedQ4816.FromRawBits(value: normalImpulseRaw),
            right: frictionCoefficient
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
            bodyA: aHandle,
            anchorA: anchorA,
            bodyB: bHandle,
            anchorB: anchorB,
            normal: tangentDirection,
            impulseRaw: clampedImpulseRaw,
            scales: FixedWorldRigid.Scales,
            refusals: ref refusals
        );
    }
    // (left + right) / 2 rounded to nearest, ties to even — the same value (left + right) * 0.5 yields through the
    // fixed-point multiply — computed on the halves so the intermediate sum can never leave the raw.
    private static FixedQ4816 AverageNonnegative(FixedQ4816 left, FixedQ4816 right) {
        var leftRaw = Math.Max(val1: 0L, val2: left.Value);
        var rightRaw = Math.Max(val1: 0L, val2: right.Value);
        var halvesRaw = ((leftRaw >> 1) + (rightRaw >> 1));
        var lowBits = ((leftRaw & 1L) + (rightRaw & 1L));
        var averageRaw = (halvesRaw + ((lowBits == 2L) ? 1L : ((lowBits == 1L) ? (halvesRaw & 1L) : 0L)));

        return FixedQ4816.FromRawBits(value: averageRaw);
    }
}
