using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// The scalar helpers the fixed rigid solver needs beyond what <see cref="FusedArithmetic"/>,
/// <see cref="FixedSymmetricSolve"/>, <see cref="FixedDirectedRounding"/> and <see cref="FixedPointRounding"/>
/// already expose, called directly at their public faces: the rounded-up magnitude and product bounds conservative
/// tests read. The state digest folds through <see cref="Fnv1aHash"/>.
/// </summary>
internal static class FixedRigidArithmetic {
    /// <summary>Returns the least raw at or above the exact magnitude of a vector, at the components' own scale.</summary>
    /// <param name="value">The vector whose magnitude is bounded from above.</param>
    /// <returns>The rounded-up magnitude; <see cref="FixedQ4816.MaxValue"/> when the bound leaves the raw carrier.</returns>
    internal static FixedQ4816 CeilingMagnitude(FixedVector3 value) =>
        (FixedDirectedRounding.TryCeilingMagnitude(
            x: value.X.Value,
            y: value.Y.Value,
            z: value.Z.Value,
            result: out var magnitude
        )
            ? FixedQ4816.FromRawBits(value: magnitude)
            : FixedQ4816.MaxValue
        );
    /// <summary>Returns the least raw at or above the exact product of two non-negative values at Q48.16.</summary>
    /// <param name="left">The first non-negative factor.</param>
    /// <param name="right">The second non-negative factor.</param>
    /// <returns>The rounded-up product; <see cref="FixedQ4816.MaxValue"/> when the bound leaves the raw carrier.</returns>
    internal static FixedQ4816 CeilingProduct(FixedQ4816 left, FixedQ4816 right) =>
        (FixedDirectedRounding.TryCeilingProduct(
            a: left.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: right.Value,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var product
        )
            ? FixedQ4816.FromRawBits(value: product)
            : FixedQ4816.MaxValue
        );
    /// <summary>Returns the least raw at or above <c>left · right + addend</c> for non-negative Q48.16 operands.</summary>
    /// <param name="left">The first non-negative factor.</param>
    /// <param name="right">The second non-negative factor.</param>
    /// <param name="addend">The non-negative addend.</param>
    /// <returns>The rounded-up sum; <see cref="FixedQ4816.MaxValue"/> when the bound leaves the raw carrier.</returns>
    internal static FixedQ4816 CeilingProductSum(FixedQ4816 left, FixedQ4816 right, FixedQ4816 addend) =>
        (FixedDirectedRounding.TryCeilingProductSum(
            a: left.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: right.Value,
            fractionBitsB: FixedQ4816.FractionBitCount,
            addend: addend.Value,
            fractionBitsAddend: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var sum
        )
            ? FixedQ4816.FromRawBits(value: sum)
            : FixedQ4816.MaxValue
        );
}
