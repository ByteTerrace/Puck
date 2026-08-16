using Puck.Maths;

namespace Puck.Physics;

/// <summary>The symmetric tidal Jacobian of a softened monopole, carried as raw Q32.32 entries.</summary>
internal readonly record struct GravityGradient(
    long Xx,
    long Xy,
    long Xz,
    long Yy,
    long Yz,
    long Zz
) {
    private static bool TryAddComponent(long left, long right, out long sum) {
        sum = unchecked((left + right));

        return ((((left ^ sum) & (right ^ sum)) >= 0L));
    }
    private static bool TryOuterComponent(long first, long second, ulong softenedDistanceSquaredRaw, long baseRaw, out long component) {
        var ratioRaw = GravityKernel.RoundDivide(
            numerator: checked((((Int128)first) * second)),
            positiveDenominator: softenedDistanceSquaredRaw
        );

        return FusedArithmetic.TryMixedScaleProduct(
            a: baseRaw,
            b: ratioRaw,
            c: 3L,
            fractionBitsA: FixedQ3232.FractionBitCount,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsC: 0,
            fractionBitsOut: FixedQ3232.FractionBitCount,
            result: out component
        );
    }

    public FixedVector3 Apply(FixedVector3 offset) {
        if (!FixedSymmetricSolve.TryApplySymmetric3(
            a: Xx,
            b: Xy,
            c: Xz,
            d: Yy,
            e: Yz,
            f: Zz,
            vX: offset.X.Value,
            vY: offset.Y.Value,
            vZ: offset.Z.Value,
            fractionBitsMatrix: FixedQ3232.FractionBitCount,
            fractionBitsVector: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            x: out var x,
            y: out var y,
            z: out var z
        )) {
            throw new OverflowException(message: "A local-expansion evaluation exceeds Q48.16 range.");
        }

        return new FixedVector3(
            X: FixedQ4816.FromRawBits(value: x),
            Y: FixedQ4816.FromRawBits(value: y),
            Z: FixedQ4816.FromRawBits(value: z)
        );
    }
    /// <summary>Sums two gradients, declining instead of throwing when any entry would leave the Q32.32 grid.</summary>
    public static bool TryAdd(GravityGradient left, GravityGradient right, out GravityGradient sum) {
        if (
            TryAddComponent(
            left: left.Xx,
            right: right.Xx,
            sum: out var xx
        ) &&
            TryAddComponent(
            left: left.Xy,
            right: right.Xy,
            sum: out var xy
        ) &&
            TryAddComponent(
            left: left.Xz,
            right: right.Xz,
            sum: out var xz
        ) &&
            TryAddComponent(
            left: left.Yy,
            right: right.Yy,
            sum: out var yy
        ) &&
            TryAddComponent(
            left: left.Yz,
            right: right.Yz,
            sum: out var yz
        ) &&
            TryAddComponent(
            left: left.Zz,
            right: right.Zz,
            sum: out var zz
        )
        ) {
            sum = new GravityGradient(
                Xx: xx,
                Xy: xy,
                Xz: xz,
                Yy: yy,
                Yz: yz,
                Zz: zz
            );

            return true;
        }

        sum = default;

        return false;
    }
    /// <summary>Forms the tidal Jacobian of one accepted monopole, declining when any entry would leave the Q32.32
    /// grid so the caller can open the cell pair instead of aborting the solve.</summary>
    public static bool TryFromInteraction(
        in PreparedGravityInteraction interaction,
        FixedQ4816 sourceMass,
        FixedQ4816 gravitationalConstant,
        out GravityGradient gradient
    ) {
        gradient = default;

        if (
            (sourceMass <= FixedQ4816.Zero) ||
            (gravitationalConstant <= FixedQ4816.Zero)
        ) {
            return true;
        }

        var strength = checked((gravitationalConstant * sourceMass));

        if (strength <= FixedQ4816.Zero) {
            return true;
        }

        var distanceSquaredRaw = unchecked((ulong)interaction.SoftenedDistanceSquared.Value);
        var distanceRaw = unchecked((ulong)interaction.SoftenedDistance.Value);
        var denominator = checked((((UInt128)distanceSquaredRaw) * distanceRaw));
        var baseNumerator = (((UInt128)((ulong)strength.Value)) << (FixedQ4816.FractionBitCount + FixedQ3232.FractionBitCount));

        if (
            !FusedArithmetic.TryDivideMagnitudeRounded(
            denominatorMagnitude: denominator,
            fractionBitCount: 0,
            numeratorMagnitude: baseNumerator,
            quotient: out var baseRawUnsigned
        ) ||
            (baseRawUnsigned > long.MaxValue)
        ) {
            return false;
        }

        var baseRaw = unchecked((long)baseRawUnsigned);
        var delta = interaction.Delta;

        if (
            !TryOuterComponent(
            first: delta.X.Value,
            second: delta.X.Value,
            softenedDistanceSquaredRaw: distanceSquaredRaw,
            baseRaw: baseRaw,
            component: out var xx
        ) ||
            !TryOuterComponent(
            first: delta.X.Value,
            second: delta.Y.Value,
            softenedDistanceSquaredRaw: distanceSquaredRaw,
            baseRaw: baseRaw,
            component: out var xy
        ) ||
            !TryOuterComponent(
            first: delta.X.Value,
            second: delta.Z.Value,
            softenedDistanceSquaredRaw: distanceSquaredRaw,
            baseRaw: baseRaw,
            component: out var xz
        ) ||
            !TryOuterComponent(
            first: delta.Y.Value,
            second: delta.Y.Value,
            softenedDistanceSquaredRaw: distanceSquaredRaw,
            baseRaw: baseRaw,
            component: out var yy
        ) ||
            !TryOuterComponent(
            first: delta.Y.Value,
            second: delta.Z.Value,
            softenedDistanceSquaredRaw: distanceSquaredRaw,
            baseRaw: baseRaw,
            component: out var yz
        ) ||
            !TryOuterComponent(
            first: delta.Z.Value,
            second: delta.Z.Value,
            softenedDistanceSquaredRaw: distanceSquaredRaw,
            baseRaw: baseRaw,
            component: out var zz
        )
        ) {
            return false;
        }

        gradient = new GravityGradient(
            Xx: checked((xx - baseRaw)),
            Xy: xy,
            Xz: xz,
            Yy: checked((yy - baseRaw)),
            Yz: yz,
            Zz: checked((zz - baseRaw))
        );

        return true;
    }
}
