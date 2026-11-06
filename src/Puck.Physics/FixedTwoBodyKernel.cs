using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// The scalar kernels of a two-body contact that <see cref="FixedRigidWorld"/> solves: the combined effective mass,
/// relative velocity, and impulse application across two named bodies, either of which may be static, for a contact
/// point's linear rows and for an angular row about an axis. A static body's zero inverse mass and inverse inertia
/// contribute exactly zero to every sum, so a dynamic-vs-static pair degenerates to the single-body formula without a
/// special case. Every impulse raw is placed at <see cref="FixedRigidScales.Impulse"/>.
/// </summary>
public static class FixedTwoBodyKernel {
    // leverᵀ·I⁻¹·lever for one body, the lever in world axes and the tensor in the body's step-start frame, at the
    // inverse mass placement so it sums with an inverse mass.
    private static long AngularTerm(FixedRigidBody body, FixedVector3 lever, FixedRigidScales scales, ref int refusals) {
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
    // The angular velocity change I⁻¹·lever·impulse of one dynamic body, in its current frame. The inverse inertia meets
    // the geometric lever before the impulse magnitude: rounding lever·impulse to Q16 first quantizes a centimetre
    // body's torque into whole radians a second, or zero.
    private static void ApplyAngular(FixedRigidBody body, FixedVector3 lever, long impulseRaw, FixedRigidScales scales, ref int refusals) {
        var orientation = body.CurrentOrientation;
        var localLever = orientation.RotateInverse(vector: lever);

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
            x: out var responseX,
            y: out var responseY,
            z: out var responseZ
        ) || !FusedArithmetic.TryMixedScaleProduct(
            a: responseX, fractionBitsA: scales.InverseInertia, b: impulseRaw, fractionBitsB: scales.Impulse,
            fractionBitsOut: FixedQ4816.FractionBitCount, result: out var wx
        ) || !FusedArithmetic.TryMixedScaleProduct(
            a: responseY, fractionBitsA: scales.InverseInertia, b: impulseRaw, fractionBitsB: scales.Impulse,
            fractionBitsOut: FixedQ4816.FractionBitCount, result: out var wy
        ) || !FusedArithmetic.TryMixedScaleProduct(
            a: responseZ, fractionBitsA: scales.InverseInertia, b: impulseRaw, fractionBitsB: scales.Impulse,
            fractionBitsOut: FixedQ4816.FractionBitCount, result: out var wz
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
    private static void ApplyToOneBody(FixedRigidBody body, FixedVector3 anchor, FixedVector3 normal, long impulseRaw, FixedRigidScales scales, ref int refusals) {
        if (!body.IsDynamic) {
            return;
        }

        if (
            !FusedArithmetic.TryMixedScaleProduct(
            a: body.InverseMassRaw,
            fractionBitsA: scales.InverseMass,
            b: impulseRaw,
            fractionBitsB: scales.Impulse,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var speedChange
        )
        ) {
            ++refusals;
            return;
        }

        body.LinearVelocity += (normal * FixedQ4816.FromRawBits(value: speedChange));
        ApplyAngular(
            body: body,
            impulseRaw: impulseRaw,
            lever: FixedVector3.Cross(left: anchor, right: normal),
            refusals: ref refusals,
            scales: scales
        );
    }
    private static bool TryReciprocal(long inverse, FixedRigidScales scales, out long massRaw, ref int refusals) {
        if (inverse <= 0L) {
            massRaw = 0L;
            return false;
        }

        if (!FusedArithmetic.TryScaledReciprocal(
            value: inverse,
            fractionBitsIn: scales.InverseMass,
            fractionBitsOut: scales.EffectiveMass,
            result: out massRaw
        )) {
            ++refusals;
            return false;
        }

        return true;
    }

    /// <summary>Applies a signed angular impulse about an axis to both bodies: <c>-impulse·axis</c> to A and
    /// <c>+impulse·axis</c> to B, each through its own inverse inertia. The impulse is one value whose sign flips between
    /// the two applications.</summary>
    /// <param name="bodyA">The first body.</param>
    /// <param name="bodyB">The second body.</param>
    /// <param name="axis">The unit axis, world axes.</param>
    /// <param name="impulseRaw">The signed angular impulse raw: metres times an impulse raw at
    /// <see cref="FixedRigidScales.Impulse"/>.</param>
    /// <param name="scales">Where inverse inertia and impulse are placed.</param>
    /// <param name="refusals">Incremented once per declining kernel call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bodyA"/> or <paramref name="bodyB"/> is
    /// <see langword="null"/>.</exception>
    public static void ApplyAngularImpulse(FixedRigidBody bodyA, FixedRigidBody bodyB, FixedVector3 axis, long impulseRaw, FixedRigidScales scales, ref int refusals) {
        ArgumentNullException.ThrowIfNull(argument: bodyA);
        ArgumentNullException.ThrowIfNull(argument: bodyB);

        if (impulseRaw == 0L) {
            return;
        }

        if (bodyA.IsDynamic) {
            ApplyAngular(body: bodyA, impulseRaw: impulseRaw, lever: -axis, refusals: ref refusals, scales: scales);
        }

        if (bodyB.IsDynamic) {
            ApplyAngular(body: bodyB, impulseRaw: impulseRaw, lever: axis, refusals: ref refusals, scales: scales);
        }
    }
    /// <summary>Applies a signed normal impulse to both bodies: <c>-impulse·normal</c> to A, <c>+impulse·normal</c> to
    /// B, each scaled by its own inverse mass and inverse inertia. The impulse is one value whose sign flips between the
    /// two applications, never two independently rounded impulses.</summary>
    /// <param name="bodyA">The body the normal points away from.</param>
    /// <param name="anchorA">The contact point relative to <paramref name="bodyA"/>'s centre of mass, world axes.</param>
    /// <param name="bodyB">The body the normal points toward.</param>
    /// <param name="anchorB">The contact point relative to <paramref name="bodyB"/>'s centre of mass, world axes.</param>
    /// <param name="normal">The unit contact normal, world axes.</param>
    /// <param name="impulseRaw">The signed normal impulse raw, at <see cref="FixedRigidScales.Impulse"/>.</param>
    /// <param name="scales">Where inverse mass, inverse inertia and impulse are placed.</param>
    /// <param name="refusals">Incremented once per declining kernel call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bodyA"/> or <paramref name="bodyB"/> is
    /// <see langword="null"/>.</exception>
    public static void ApplyImpulse(
        FixedRigidBody bodyA,
        FixedVector3 anchorA,
        FixedRigidBody bodyB,
        FixedVector3 anchorB,
        FixedVector3 normal,
        long impulseRaw,
        FixedRigidScales scales,
        ref int refusals
    ) {
        ArgumentNullException.ThrowIfNull(argument: bodyA);
        ArgumentNullException.ThrowIfNull(argument: bodyB);

        if (impulseRaw == 0L) {
            return;
        }

        ApplyToOneBody(
            anchor: anchorA,
            body: bodyA,
            impulseRaw: impulseRaw,
            normal: -normal,
            refusals: ref refusals,
            scales: scales
        );
        ApplyToOneBody(
            anchor: anchorB,
            body: bodyB,
            impulseRaw: impulseRaw,
            normal: normal,
            refusals: ref refusals,
            scales: scales
        );
    }
    /// <summary>Computes the relative angular velocity of B with respect to A about an axis.</summary>
    /// <param name="bodyA">The first body.</param>
    /// <param name="bodyB">The second body.</param>
    /// <param name="axis">The unit axis, world axes.</param>
    /// <returns>The relative angular velocity about <paramref name="axis"/>, in radians per second.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bodyA"/> or <paramref name="bodyB"/> is
    /// <see langword="null"/>.</exception>
    public static FixedQ4816 RelativeAngularVelocity(FixedRigidBody bodyA, FixedRigidBody bodyB, FixedVector3 axis) {
        ArgumentNullException.ThrowIfNull(argument: bodyA);
        ArgumentNullException.ThrowIfNull(argument: bodyB);

        return FixedVector3.Dot(
            left: (bodyB.AngularVelocity - bodyA.AngularVelocity),
            right: axis
        );
    }
    /// <summary>Computes the relative normal velocity of B with respect to A at the contact.</summary>
    /// <param name="bodyA">The body the normal points away from.</param>
    /// <param name="anchorA">The contact point relative to <paramref name="bodyA"/>'s centre of mass, world axes.</param>
    /// <param name="bodyB">The body the normal points toward.</param>
    /// <param name="anchorB">The contact point relative to <paramref name="bodyB"/>'s centre of mass, world axes.</param>
    /// <param name="normal">The unit contact normal, world axes.</param>
    /// <returns>The relative normal velocity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bodyA"/> or <paramref name="bodyB"/> is
    /// <see langword="null"/>.</exception>
    public static FixedQ4816 RelativeNormalVelocity(FixedRigidBody bodyA, FixedVector3 anchorA, FixedRigidBody bodyB, FixedVector3 anchorB, FixedVector3 normal) {
        ArgumentNullException.ThrowIfNull(argument: bodyA);
        ArgumentNullException.ThrowIfNull(argument: bodyB);

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
    /// <summary>Computes the combined effective angular mass of two bodies (either may be static) about an axis: the
    /// reciprocal of <c>axisᵀ·(I_A⁻¹ + I_B⁻¹)·axis</c>.</summary>
    /// <param name="bodyA">The first body.</param>
    /// <param name="bodyB">The second body.</param>
    /// <param name="axis">The unit axis, world axes.</param>
    /// <param name="scales">Where inverse inertia and effective mass are placed.</param>
    /// <param name="angularMassRaw">The effective angular mass raw, at <see cref="FixedRigidScales.EffectiveMass"/>, on
    /// success; zero otherwise.</param>
    /// <param name="refusals">Incremented once per declining kernel call.</param>
    /// <returns><see langword="false"/> when neither body can turn about the axis, the raw sum leaves its carrier, or a
    /// kernel declined.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bodyA"/> or <paramref name="bodyB"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryAngularMass(FixedRigidBody bodyA, FixedRigidBody bodyB, FixedVector3 axis, FixedRigidScales scales, out long angularMassRaw, ref int refusals) {
        ArgumentNullException.ThrowIfNull(argument: bodyA);
        ArgumentNullException.ThrowIfNull(argument: bodyB);

        var angularA = AngularTerm(body: bodyA, lever: axis, refusals: ref refusals, scales: scales);
        var angularB = AngularTerm(body: bodyB, lever: axis, refusals: ref refusals, scales: scales);
        long inverse;

        try {
            inverse = checked((angularA + angularB));
        } catch (OverflowException) {
            ++refusals;
            angularMassRaw = 0L;
            return false;
        }

        return TryReciprocal(inverse: inverse, massRaw: out angularMassRaw, refusals: ref refusals, scales: scales);
    }
    /// <summary>Computes the combined effective mass of a contact between two bodies (either may be static).</summary>
    /// <param name="bodyA">The body the normal points away from.</param>
    /// <param name="anchorA">The contact point relative to <paramref name="bodyA"/>'s centre of mass, world axes.</param>
    /// <param name="bodyB">The body the normal points toward.</param>
    /// <param name="anchorB">The contact point relative to <paramref name="bodyB"/>'s centre of mass, world axes.</param>
    /// <param name="normal">The unit contact normal, world axes, pointing from A toward B.</param>
    /// <param name="scales">Where inverse mass, inverse inertia and effective mass are placed.</param>
    /// <param name="normalMassRaw">The effective mass raw, at <see cref="FixedRigidScales.EffectiveMass"/>, on
    /// success; zero on refusal.</param>
    /// <param name="refusals">Incremented once per declining kernel call.</param>
    /// <returns><see langword="false"/> when the combined inverse mass is non-positive (both bodies static), the raw
    /// sum leaves its 64-bit carrier, or a kernel declined.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bodyA"/> or <paramref name="bodyB"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryEffectiveMass(
        FixedRigidBody bodyA,
        FixedVector3 anchorA,
        FixedRigidBody bodyB,
        FixedVector3 anchorB,
        FixedVector3 normal,
        FixedRigidScales scales,
        out long normalMassRaw,
        ref int refusals
    ) {
        ArgumentNullException.ThrowIfNull(argument: bodyA);
        ArgumentNullException.ThrowIfNull(argument: bodyB);

        var angularA = AngularTerm(
            body: bodyA,
            lever: FixedVector3.Cross(left: anchorA, right: normal),
            refusals: ref refusals,
            scales: scales
        );
        var angularB = AngularTerm(
            body: bodyB,
            lever: FixedVector3.Cross(left: anchorB, right: normal),
            refusals: ref refusals,
            scales: scales
        );
        long kNormal;

        // A checked sum: four addends instead of one body's two doubles the exposure to a wrap landing on a small
        // positive value, which would slip past the "kNormal <= 0" refusal undetected.
        try {
            kNormal = checked(((bodyA.InverseMassRaw + bodyB.InverseMassRaw) + (angularA + angularB)));
        } catch (OverflowException) {
            ++refusals;
            normalMassRaw = 0L;
            return false;
        }

        return TryReciprocal(inverse: kNormal, massRaw: out normalMassRaw, refusals: ref refusals, scales: scales);
    }
}
