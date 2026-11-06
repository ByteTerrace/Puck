using Puck.Maths;

namespace Puck.Physics;

/// <summary>What <see cref="FixedGyroscopicStep.Advance"/> did with one body's angular velocity.</summary>
public enum GyroscopicStepOutcome {
    /// <summary>The gyroscopic term is exactly zero (no spin, or spherical inertia), so the velocity is unchanged.</summary>
    Unneeded,
    /// <summary>The implicit Newton step ran, its correction was applied, and the spin it dissipated was restored as
    /// far as both invariants allow.</summary>
    Applied,
    /// <summary>The implicit Newton step ran and its correction was applied, but the restoring rescale was skipped:
    /// the corrected spin is zero, or its momentum, energy or rescaled components leave the rescale's representable
    /// range. The velocity is the Newton step's alone.</summary>
    AppliedUnrescaled,
    /// <summary>The inertia is degenerate (a non-positive inverse moment), the spin leaves the step's representable
    /// range, or the Jacobian is singular, so the velocity is left unchanged rather than wrapped.</summary>
    Refused,
}

/// <summary>
/// The implicit gyroscopic term of Euler's rigid-body equation <c>I·ω̇ + ω × I·ω = τ</c>, advanced by one
/// Newton-Raphson step in body coordinates, where the inertia tensor is constant: with residual
/// <c>f = h·ω_b × I_b·ω_b</c> and Jacobian <c>J = I_b + h·(skew(ω_b)·I_b − skew(I_b·ω_b))</c>, the body-frame angular
/// velocity becomes <c>ω_b − J⁻¹·f</c> (Catto, "Numerical Methods", GDC 2015; Bullet's
/// <c>BT_ENABLE_GYROSCOPIC_FORCE_IMPLICIT_BODY</c>). Holding world angular velocity constant instead lets a
/// non-spherical body's angular momentum precess about its spin axis; the explicit form gains energy. The implicit
/// form's error is dissipative: the exact implicit solution never raises the momentum magnitude <c>|I_b·ω_b|</c> or
/// the rotational energy <c>½·ω_b·I_b·ω_b</c>, and at 60 Hz it lowers both on every step. The corrected velocity is
/// then scaled by the one factor <c>s ≥ 1</c> that restores as much of that loss as neither invariant forbids.
/// </summary>
/// <remarks>
/// <para><b>Scale.</b> <c>J⁻¹·f</c> is invariant under scaling the inertia by any constant, so the step reads only
/// the ratios of the three body-local inverse moments: each principal moment is <c>min(a, b, c) / a_i</c> at
/// <c>InertiaFractionBits</c> (24) fraction bits, one rounding each, with the largest moment exactly
/// <c>2^InertiaFractionBits</c>. That makes the result independent of the inverse inertia's own placement and of the
/// body's live scale.</para>
/// <para><b>Rounding.</b> <c>h·ω_i</c> is an exact product at 32 fraction bits; each off-diagonal Jacobian entry and
/// each residual component is one rounding of an exact product (<see cref="FusedArithmetic.TryMixedScaleProduct(long, int, long, int, int, out long)"/>
/// and its three-factor sibling); the solve rounds each component once
/// (<see cref="FixedLinearSolve.TrySolve3"/>), and the correction is rotated back to world axes and subtracted, so a
/// correction that rounds to zero leaves the world angular velocity bit-identical, rescale included.</para>
/// <para><b>Rescale.</b> Free flight has no torque, so the momentum magnitude and the energy before the step are both
/// invariants the step should have kept. With <c>ω₀</c> the body-frame velocity before the step and <c>ω₁ = ω₀ − J⁻¹·f</c>
/// after it, the factor is <c>s = √min(|I·ω₀|²/|I·ω₁|², E₀/E₁)</c>, applied to the corrected world velocity: scaling
/// ω by <c>s</c> scales <c>|L|</c> by <c>s</c> and <c>E</c> by <c>s²</c>, so the smaller factor restores one invariant
/// exactly and leaves the other at or below its value before the step. Energy therefore never rises, and the scheme
/// keeps implicit Euler's stability while losing far less spin. A ratio at or below one (rounding made the step gain)
/// leaves the Newton result alone; the factor is never below one. Both sums are exact integers over the normalized
/// moments (<c>Σ (I_i·ω_i)²</c> and <c>Σ I_i·ω_i²</c>, each term an exact product), so the ratios are independent of
/// the inertia's placement too. One rounding rule holds throughout: every quantity the rescale derives rounds toward
/// zero. Each ratio is a floor at 64 fraction bits, the factor the floor square root of the smaller one
/// (<see cref="UnsignedNumberFunctions.SquareRoot{T}(T)"/>, at 32 fraction bits), and each scaled component the floor
/// of its magnitude, so neither the factor nor any component's magnitude exceeds its exact value. A zero spin after the
/// step, a weighted component at or past <c>2^63</c>, or a scaled component outside the raw carrier skips the rescale
/// (<see cref="GyroscopicStepOutcome.AppliedUnrescaled"/>) rather than saturating.</para>
/// </remarks>
public static class FixedGyroscopicStep {
    // The fraction bits the normalized principal moments are carried at. The largest moment is exactly
    // 2^InertiaFractionBits, which puts the Jacobian's diagonal at the 3×3 solve's lossless preconditioning target
    // (bit 40) once it is placed at FixedQ4816's sixteen fraction bits.
    private const int InertiaFractionBits = 24;

    // h·ω, the step's rotation in radians, carried exactly at the sum of its factors' fraction bits.
    private const int StepRotationFractionBits = 32;

    // The momentum and energy ratios' fraction bits; the rescale factor, their square root, lands at half as many.
    private const int RatioFractionBits = 64;
    private const int FactorFractionBits = (RatioFractionBits / 2);

    // The bound a weighted component I_i·|ω_i| stays under: its square and its product with |ω_i| (no larger than it,
    // since every normalized moment is at least one) then stay under 2^126, so three terms sum inside UInt128.
    private static readonly UInt128 WeightedComponentLimit = (UInt128.One << 63);

    /// <summary>Advances a rigid body's world angular velocity by one implicit gyroscopic step.</summary>
    /// <param name="angularVelocity">The world-space angular velocity, in radians per second.</param>
    /// <param name="orientation">The unit rotation from body-local to world axes.</param>
    /// <param name="inverseInertiaXX">The body-local inverse principal moment about X, at any placement shared by the
    /// three; must be positive.</param>
    /// <param name="inverseInertiaYY">The body-local inverse principal moment about Y, at the same placement.</param>
    /// <param name="inverseInertiaZZ">The body-local inverse principal moment about Z, at the same placement.</param>
    /// <param name="stepSeconds">The step's duration, in seconds.</param>
    /// <param name="advanced">The advanced world angular velocity; <paramref name="angularVelocity"/> unchanged when the
    /// outcome is <see cref="GyroscopicStepOutcome.Unneeded"/> or <see cref="GyroscopicStepOutcome.Refused"/>, or when
    /// the correction rounds to zero.</param>
    /// <returns>What the step did; see <see cref="GyroscopicStepOutcome"/>.</returns>
    public static GyroscopicStepOutcome Advance(
        FixedVector3 angularVelocity,
        FixedQuaternion orientation,
        long inverseInertiaXX,
        long inverseInertiaYY,
        long inverseInertiaZZ,
        FixedQ4816 stepSeconds,
        out FixedVector3 advanced
    ) {
        advanced = angularVelocity;

        if (
            (inverseInertiaXX <= 0L) ||
            (inverseInertiaYY <= 0L) ||
            (inverseInertiaZZ <= 0L)
        ) {
            return GyroscopicStepOutcome.Refused;
        }
        if (
            (angularVelocity == FixedVector3.Zero) ||
            (stepSeconds <= FixedQ4816.Zero) ||
            ((inverseInertiaXX == inverseInertiaYY) && (inverseInertiaYY == inverseInertiaZZ))
        ) {
            return GyroscopicStepOutcome.Unneeded;
        }

        var least = Math.Min(
            val1: inverseInertiaXX,
            val2: Math.Min(
                val1: inverseInertiaYY,
                val2: inverseInertiaZZ
            )
        );

        if (
            !TryMoment(least: least, inverse: inverseInertiaXX, moment: out var ix) ||
            !TryMoment(least: least, inverse: inverseInertiaYY, moment: out var iy) ||
            !TryMoment(least: least, inverse: inverseInertiaZZ, moment: out var iz)
        ) {
            return GyroscopicStepOutcome.Refused;
        }

        var omega = orientation.RotateInverse(vector: angularVelocity);

        if (
            !TryStepRotation(omega: omega.X, stepSeconds: stepSeconds, rotation: out var px) ||
            !TryStepRotation(omega: omega.Y, stepSeconds: stepSeconds, rotation: out var py) ||
            !TryStepRotation(omega: omega.Z, stepSeconds: stepSeconds, rotation: out var pz)
        ) {
            return GyroscopicStepOutcome.Refused;
        }

        // With diagonal I = (ix, iy, iz) and D = (iz − iy, ix − iz, iy − ix), the Jacobian's off-diagonal entries are
        // h·ω_j·D_i and the residual is f_i = h·ω_j·ω_k·D_i over the cyclic (i, j, k):
        //   J = [[ix, hωz·Dx, hωy·Dx], [hωz·Dy, iy, hωx·Dy], [hωy·Dz, hωx·Dz, iz]],
        //   f = (hωy·ωz·Dx, hωz·ωx·Dy, hωx·ωy·Dz).
        // J and f both sit at sixteen fraction bits over the normalized moments, so the solve's sixteen extra bits
        // land the correction at FixedQ4816's scale.
        var dx = (iz - iy);
        var dy = (ix - iz);
        var dz = (iy - ix);
        const int Q = FixedQ4816.FractionBitCount;

        if (
            !TryProduct(rotation: pz, difference: dx, entry: out var j01) ||
            !TryProduct(rotation: py, difference: dx, entry: out var j02) ||
            !TryProduct(rotation: pz, difference: dy, entry: out var j10) ||
            !TryProduct(rotation: px, difference: dy, entry: out var j12) ||
            !TryProduct(rotation: py, difference: dz, entry: out var j20) ||
            !TryProduct(rotation: px, difference: dz, entry: out var j21) ||
            !FusedArithmetic.TryMixedScaleProduct(a: py, fractionBitsA: StepRotationFractionBits, b: omega.Z.Value, fractionBitsB: Q, c: dx, fractionBitsC: 0, fractionBitsOut: Q, result: out var fx) ||
            !FusedArithmetic.TryMixedScaleProduct(a: pz, fractionBitsA: StepRotationFractionBits, b: omega.X.Value, fractionBitsB: Q, c: dy, fractionBitsC: 0, fractionBitsOut: Q, result: out var fy) ||
            !FusedArithmetic.TryMixedScaleProduct(a: px, fractionBitsA: StepRotationFractionBits, b: omega.Y.Value, fractionBitsB: Q, c: dz, fractionBitsC: 0, fractionBitsOut: Q, result: out var fz) ||
            !FixedLinearSolve.TrySolve3(
                m00: (ix << Q),
                m01: j01,
                m02: j02,
                m10: j10,
                m11: (iy << Q),
                m12: j12,
                m20: j20,
                m21: j21,
                m22: (iz << Q),
                outputFractionShift: Q,
                rhsX: fx,
                rhsY: fy,
                rhsZ: fz,
                x: out var cx,
                y: out var cy,
                z: out var cz
            )
        ) {
            return GyroscopicStepOutcome.Refused;
        }

        var correction = new FixedVector3(
            X: FixedQ4816.FromRawBits(value: cx),
            Y: FixedQ4816.FromRawBits(value: cy),
            Z: FixedQ4816.FromRawBits(value: cz)
        );

        if (correction == FixedVector3.Zero) {
            return GyroscopicStepOutcome.Applied;
        }

        advanced = (angularVelocity - orientation.Rotate(vector: correction));

        if (
            !TryInvariants(x: omega.X.Value, y: omega.Y.Value, z: omega.Z.Value, ix: ix, iy: iy, iz: iz, momentumSquared: out var momentumBefore, twiceEnergy: out var energyBefore) ||
            !TryInvariants(x: (((Int128)omega.X.Value) - cx), y: (((Int128)omega.Y.Value) - cy), z: (((Int128)omega.Z.Value) - cz), ix: ix, iy: iy, iz: iz, momentumSquared: out var momentumAfter, twiceEnergy: out var energyAfter) ||
            (momentumAfter == UInt128.Zero)
        ) {
            return GyroscopicStepOutcome.AppliedUnrescaled;
        }

        // Both sums are positive here: a non-zero vector has a non-zero weighted component, since every moment is at
        // least one, so the energy is zero exactly when the momentum is.
        var ratio = UInt128.Min(
            x: FloorRatio(numerator: momentumBefore, denominator: momentumAfter),
            y: FloorRatio(numerator: energyBefore, denominator: energyAfter)
        );

        if (ratio <= (UInt128.One << RatioFractionBits)) {
            return GyroscopicStepOutcome.Applied;
        }

        var factor = ratio.SquareRoot();

        if (
            !TryScale(component: advanced.X, factor: factor, scaled: out var sx) ||
            !TryScale(component: advanced.Y, factor: factor, scaled: out var sy) ||
            !TryScale(component: advanced.Z, factor: factor, scaled: out var sz)
        ) {
            return GyroscopicStepOutcome.AppliedUnrescaled;
        }

        advanced = new FixedVector3(X: sx, Y: sy, Z: sz);
        return GyroscopicStepOutcome.Applied;
    }

    // least / inverse at InertiaFractionBits, one rounding; inverse ≥ least > 0, so the moment lies in (0, 2^bits].
    private static bool TryMoment(long least, long inverse, out long moment) {
        if (!FusedArithmetic.TryDivideMagnitudeRounded(
            denominatorMagnitude: ((UInt128)inverse),
            fractionBitCount: InertiaFractionBits,
            numeratorMagnitude: ((UInt128)least),
            quotient: out var quotient
        ) || (quotient == UInt128.Zero)) {
            moment = 0L;
            return false;
        }

        moment = ((long)quotient);
        return true;
    }
    // h·ω exactly, at 32 fraction bits.
    private static bool TryStepRotation(FixedQ4816 omega, FixedQ4816 stepSeconds, out long rotation) =>
        FusedArithmetic.TryMixedScaleProduct(
            a: stepSeconds.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: omega.Value,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: StepRotationFractionBits,
            result: out rotation
        );
    // h·ω_j·D_i at FixedQ4816's sixteen fraction bits, one rounding.
    private static bool TryProduct(long rotation, long difference, out long entry) =>
        FusedArithmetic.TryMixedScaleProduct(
            a: rotation,
            fractionBitsA: StepRotationFractionBits,
            b: difference,
            fractionBitsB: 0,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out entry
        );
    // Σ (I_i·ω_i)² and Σ I_i·ω_i² over the normalized moments, exactly: |L|² and twice the energy at a common scale,
    // which is all a ratio needs. Each component is a raw within ±2^64 (a Q48.16 less a Q48.16 correction).
    private static bool TryInvariants(Int128 x, Int128 y, Int128 z, long ix, long iy, long iz, out UInt128 momentumSquared, out UInt128 twiceEnergy) {
        momentumSquared = UInt128.Zero;
        twiceEnergy = UInt128.Zero;

        return (
            TryAccumulate(component: x, moment: ix, momentumSquared: ref momentumSquared, twiceEnergy: ref twiceEnergy) &&
            TryAccumulate(component: y, moment: iy, momentumSquared: ref momentumSquared, twiceEnergy: ref twiceEnergy) &&
            TryAccumulate(component: z, moment: iz, momentumSquared: ref momentumSquared, twiceEnergy: ref twiceEnergy)
        );
    }
    private static bool TryAccumulate(Int128 component, long moment, ref UInt128 momentumSquared, ref UInt128 twiceEnergy) {
        var magnitude = ((UInt128)Int128.Abs(value: component));
        var weighted = (((UInt128)((ulong)moment)) * magnitude);

        if (weighted >= WeightedComponentLimit) {
            return false;
        }

        momentumSquared += (weighted * weighted);
        twiceEnergy += (weighted * magnitude);
        return true;
    }
    // ⌊numerator / denominator · 2^RatioFractionBits⌋ for positive operands. Both are first brought under 2^64 by one
    // shift, the numerator floored and the denominator ceilinged, so the shifted ratio never exceeds the exact one and
    // keeps at least 63 significant bits of each operand; the shifted numerator then takes the 64 fraction bits inside
    // UInt128, and the quotient stays below 2^128.
    private static UInt128 FloorRatio(UInt128 numerator, UInt128 denominator) {
        var shift = Math.Max(
            val1: 0,
            val2: (64 - ((int)UInt128.LeadingZeroCount(value: UInt128.Max(x: numerator, y: denominator))))
        );
        var floored = (numerator >> shift);
        var ceilinged = (denominator >> shift);

        if ((ceilinged << shift) != denominator) {
            ++ceilinged;
        }

        return ((floored << RatioFractionBits) / ceilinged);
    }
    // component · factor at the component's own scale, the magnitude floored; refuses a result outside the raw
    // carrier. factor < 2^64 and the magnitude ≤ 2^63, so the exact product stays inside UInt128.
    private static bool TryScale(FixedQ4816 component, UInt128 factor, out FixedQ4816 scaled) {
        var magnitude = ((((UInt128)FusedArithmetic.RawMagnitude(value: component.Value)) * factor) >> FactorFractionBits);

        if (magnitude > ((UInt128)long.MaxValue)) {
            scaled = FixedQ4816.Zero;
            return false;
        }

        scaled = FixedQ4816.FromRawBits(value: ((component.Value < 0L)
            ? -((long)magnitude)
            : ((long)magnitude)
        ));
        return true;
    }
}
