using Puck.Maths;

namespace Puck.Physics.Tests.TwoBody;

/// <summary>
/// Test-only measurement scaffolding, not production code. The generalization of <see cref="FixedRigidSolver"/>'s single-dynamic-body
/// effective-mass and impulse application to TWO dynamic bodies sharing one contact — a minimal two-dynamic-body rig,
/// because a static wall's zero inverse mass and inverse inertia are exact at any placement and hide exactly the
/// mass-ratio pathology a two-body precision floor needs to see.
/// </summary>
/// <remarks>
/// <para>Every kernel call below is the same one <see cref="FixedRigidSolver"/> makes — <see cref="FixedSymmetricSolve.TryApplySymmetric3"/>,
/// <see cref="FusedArithmetic.TryMixedScaleDotProduct(long, long, long, int, long, long, long, int, int, out long)"/>,
/// <see cref="FusedArithmetic.TryScaledReciprocal"/>,
/// <see cref="FusedArithmetic.TryMixedScaleProduct(long, int, long, int, int, out long)"/> — called twice, once per
/// body, with the contributions summed before the one reciprocal. A STATIC body
/// (<see cref="FixedRigidBody.InverseMassRaw"/> zero, inverse inertia all zero) contributes exactly zero to every sum,
/// so this reduces to the single-body formula without a special case — the two-body code path is exercised even by a
/// dynamic-vs-ground contact.</para>
/// <para>Kept independent of any production two-body kernel: this type exists to justify a production shape, not to
/// share code with one.</para>
/// </remarks>
internal static class TwoBodyDynamics {
    /// <summary>Applies a signed normal impulse to both bodies: <c>-impulse·normal</c> to A, <c>+impulse·normal</c> to
    /// B, each scaled by its own inverse mass and inverse inertia. The impulse is computed ONCE and only its sign
    /// flips between the two applications — never two independently rounded impulses.</summary>
    internal static void ApplyImpulse(
        FixedRigidBody bodyA,
        FixedVector3 anchorA,
        FixedRigidBody bodyB,
        FixedVector3 anchorB,
        FixedVector3 normal,
        long impulseRaw,
        FixedRigidScales scales,
        ref int refusals
    ) {
        if (impulseRaw == 0L) {
            return;
        }

        var impulse = (normal * FixedQ4816.FromRawBits(value: impulseRaw));

        ApplyToOneBody(
            anchor: anchorA,
            body: bodyA,
            impulse: -impulse,
            refusals: ref refusals,
            scales: scales
        );
        ApplyToOneBody(
            anchor: anchorB,
            body: bodyB,
            impulse: impulse,
            refusals: ref refusals,
            scales: scales
        );
    }
    /// <summary>Computes the relative normal velocity of B with respect to A at the contact.</summary>
    internal static FixedQ4816 RelativeNormalVelocity(FixedRigidBody bodyA, FixedVector3 anchorA, FixedRigidBody bodyB, FixedVector3 anchorB, FixedVector3 normal) {
        var velocityAtA = (bodyA.LinearVelocity + FixedVector3.Cross(
            left: bodyA.AngularVelocity,
            right: anchorA
        ));
        var velocityAtB = (bodyB.LinearVelocity + FixedVector3.Cross(
            left: bodyB.AngularVelocity,
            right: anchorB
        ));

        return FixedVector3.Dot(
            left: (velocityAtB - velocityAtA),
            right: normal
        );
    }
    /// <summary>Computes the combined effective mass of a contact between two bodies (either may be static).</summary>
    /// <param name="bodyA">The body the normal points away from.</param>
    /// <param name="anchorA">The contact point relative to <paramref name="bodyA"/>'s centre of mass, world axes.</param>
    /// <param name="bodyB">The body the normal points toward.</param>
    /// <param name="anchorB">The contact point relative to <paramref name="bodyB"/>'s centre of mass, world axes.</param>
    /// <param name="normal">The unit contact normal, world axes, pointing from A toward B.</param>
    /// <param name="scales">Where inverse mass, inverse inertia and effective mass are placed.</param>
    /// <param name="normalMassRaw">The effective mass raw, at <see cref="FixedRigidScales.EffectiveMass"/>, on success;
    /// zero on refusal.</param>
    /// <param name="refusals">Incremented once per declining kernel call.</param>
    /// <returns><see langword="false"/> when the combined inverse mass is non-positive (both bodies static — no
    /// constraint is possible), the sum leaves its raw carrier, or a kernel declined.</returns>
    internal static bool TryEffectiveMass(
        FixedRigidBody bodyA,
        FixedVector3 anchorA,
        FixedRigidBody bodyB,
        FixedVector3 anchorB,
        FixedVector3 normal,
        FixedRigidScales scales,
        out long normalMassRaw,
        ref int refusals
    ) {
        var angularA = AngularTerm(
            anchor: anchorA,
            body: bodyA,
            normal: normal,
            refusals: ref refusals,
            scales: scales
        );
        var angularB = AngularTerm(
            anchor: anchorB,
            body: bodyB,
            normal: normal,
            refusals: ref refusals,
            scales: scales
        );

        // A checked sum: four addends instead of one body's two doubles the exposure to a wrap landing on a small
        // positive value, which would slip past the "kNormal <= 0" guard undetected.
        long kNormal;

        try {
            kNormal = checked(((bodyA.InverseMassRaw + bodyB.InverseMassRaw) + (angularA + angularB)));
        } catch (OverflowException) {
            ++refusals;
            normalMassRaw = 0L;
            return false;
        }

        if (kNormal <= 0L) {
            normalMassRaw = 0L;
            return false;
        }

        if (!FusedArithmetic.TryScaledReciprocal(
            value: kNormal,
            fractionBitsIn: scales.InverseMass,
            fractionBitsOut: scales.EffectiveMass,
            result: out normalMassRaw
        )) {
            ++refusals;
            return false;
        }

        return true;
    }

    private static long AngularTerm(FixedRigidBody body, FixedVector3 anchor, FixedVector3 normal, FixedRigidScales scales, ref int refusals) {
        var lever = FixedVector3.Cross(
            left: anchor,
            right: normal
        );
        var localLever = body.Orientation.RotateInverse(vector: lever);

        if (!FixedSymmetricSolve.TryApplySymmetric3(
            a: body.InverseInertiaXX,
            b: body.InverseInertiaXY,
            c: body.InverseInertiaXZ,
            d: body.InverseInertiaYY,
            e: body.InverseInertiaYZ,
            f: body.InverseInertiaZZ,
            vX: localLever.X.Value,
            vY: localLever.Y.Value,
            vZ: localLever.Z.Value,
            fractionBitsMatrix: scales.InverseInertia,
            fractionBitsVector: FixedQ4816.FractionBitCount,
            fractionBitsOut: scales.InverseInertia,
            x: out var wx,
            y: out var wy,
            z: out var wz
        )) {
            ++refusals;
            return 0L;
        }

        if (!FusedArithmetic.TryMixedScaleDotProduct(
            ax: localLever.X.Value,
            ay: localLever.Y.Value,
            az: localLever.Z.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            bx: wx,
            by: wy,
            bz: wz,
            fractionBitsB: scales.InverseInertia,
            fractionBitsOut: scales.InverseMass,
            result: out var angular
        )) {
            ++refusals;
            return 0L;
        }

        return angular;
    }
    private static void ApplyToOneBody(FixedRigidBody body, FixedVector3 anchor, FixedVector3 impulse, FixedRigidScales scales, ref int refusals) {
        if (!body.IsDynamic) {
            return;
        }

        if (
            !FusedArithmetic.TryMixedScaleProduct(
            a: body.InverseMassRaw,
            fractionBitsA: scales.InverseMass,
            b: impulse.X.Value,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var dvx
        ) ||
            !FusedArithmetic.TryMixedScaleProduct(
            a: body.InverseMassRaw,
            fractionBitsA: scales.InverseMass,
            b: impulse.Y.Value,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var dvy
        ) ||
            !FusedArithmetic.TryMixedScaleProduct(
            a: body.InverseMassRaw,
            fractionBitsA: scales.InverseMass,
            b: impulse.Z.Value,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var dvz
        )
        ) {
            ++refusals;
            return;
        }

        body.LinearVelocity += new FixedVector3(
            X: FixedQ4816.FromRawBits(value: dvx),
            Y: FixedQ4816.FromRawBits(value: dvy),
            Z: FixedQ4816.FromRawBits(value: dvz)
        );

        var orientation = body.CurrentOrientation;
        var torque = orientation.RotateInverse(vector: FixedVector3.Cross(
            left: anchor,
            right: impulse
        ));

        if (!FixedSymmetricSolve.TryApplySymmetric3(
            a: body.InverseInertiaXX,
            b: body.InverseInertiaXY,
            c: body.InverseInertiaXZ,
            d: body.InverseInertiaYY,
            e: body.InverseInertiaYZ,
            f: body.InverseInertiaZZ,
            vX: torque.X.Value,
            vY: torque.Y.Value,
            vZ: torque.Z.Value,
            fractionBitsMatrix: scales.InverseInertia,
            fractionBitsVector: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            x: out var wx,
            y: out var wy,
            z: out var wz
        )) {
            ++refusals;
            return;
        }

        body.AngularVelocity += orientation.Rotate(vector: new FixedVector3(
            X: FixedQ4816.FromRawBits(value: wx),
            Y: FixedQ4816.FromRawBits(value: wy),
            Z: FixedQ4816.FromRawBits(value: wz)
        ));
    }
}
