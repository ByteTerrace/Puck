using System.Numerics;

using Puck.Maths;

namespace Puck.Dynamics.Spike.Tests.Core;

/// <summary>
/// The mixed-scale scalar arithmetic the spike solver needs beyond what <see cref="FusedArithmetic"/>,
/// <see cref="FixedSymmetricSolve"/> and <see cref="FixedDirectedRounding"/> already expose: a dot product whose two
/// operands are carried at independent fraction bit counts, a reciprocal onto a caller-chosen scale, and the exact
/// rational rounding the softness chain is built from. Every member rounds exactly once and refuses rather than
/// wrapping, matching the kernels it is written beside.
/// </summary>
internal static class SpikeArithmetic {
    /// <summary>Rounds the exact rational <c>numerator · 2^fractionBitCount / denominator</c> to a raw carrier, once,
    /// to nearest with ties to even.</summary>
    /// <param name="numerator">The exact numerator.</param>
    /// <param name="denominator">The exact denominator, which must be non-zero.</param>
    /// <param name="fractionBitCount">The result's fraction bit count.</param>
    /// <param name="result">The rounded raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the denominator is zero or the rounded value leaves the signed 64-bit
    /// raw.</returns>
    internal static bool TryRoundRational(BigInteger numerator, BigInteger denominator, int fractionBitCount, out long result) {
        if (denominator.IsZero || (fractionBitCount < 0)) {
            result = 0L;

            return false;
        }

        var negative = ((numerator.Sign < 0) != (denominator.Sign < 0));
        var magnitude = (BigInteger.Abs(value: numerator) << fractionBitCount);
        var divisor = BigInteger.Abs(value: denominator);
        var quotient = BigInteger.DivRem(dividend: magnitude, divisor: divisor, remainder: out var remainder);
        var distanceToNext = (divisor - remainder);

        // The tie is decided against the distance to the next multiple rather than by doubling the remainder — the
        // formulation every sibling kernel in Puck.Maths uses.
        if ((remainder > distanceToNext) || ((remainder == distanceToNext) && !((quotient & BigInteger.One).IsZero))) {
            quotient += BigInteger.One;
        }

        if (negative) {
            quotient = -quotient;
        }

        if ((quotient < long.MinValue) || (quotient > long.MaxValue)) {
            result = 0L;

            return false;
        }

        result = ((long)quotient);

        return true;
    }

    /// <summary>Rounds the dot product of two three-component vectors carried at independent scales onto a third scale,
    /// exactly once.</summary>
    /// <param name="ax">The first vector's X raw.</param>
    /// <param name="ay">The first vector's Y raw.</param>
    /// <param name="az">The first vector's Z raw.</param>
    /// <param name="fractionBitsA">The first vector's fraction bit count.</param>
    /// <param name="bx">The second vector's X raw.</param>
    /// <param name="by">The second vector's Y raw.</param>
    /// <param name="bz">The second vector's Z raw.</param>
    /// <param name="fractionBitsB">The second vector's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <param name="result">The rounded dot product on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the rounded result leaves the signed 64-bit raw.</returns>
    /// <remarks>All three raw products accumulate exactly in sign-plus-<see cref="UInt128"/> magnitude before the one
    /// rounding, so reassociating the terms cannot change the answer.</remarks>
    internal static bool TryMixedDot(
        long ax,
        long ay,
        long az,
        int fractionBitsA,
        long bx,
        long by,
        long bz,
        int fractionBitsB,
        int fractionBitsOut,
        out long result
    ) {
        var accumulator = FusedArithmetic.AddProducts(firstLeft: ax, firstRight: bx, secondLeft: ay, secondRight: by);
        var third = FusedArithmetic.Product(left: az, right: bz);

        accumulator = FusedArithmetic.CombineSigned(
            firstNegative: accumulator.Negative,
            firstMagnitude: accumulator.Magnitude,
            secondNegative: third.Negative,
            secondMagnitude: third.Magnitude
        );

        var scaled = FusedArithmetic.ScaleMagnitudeToNearest(
            magnitude: accumulator.Magnitude,
            shift: FusedArithmetic.MixedScaleShift(fractionBitsOut: fractionBitsOut, first: fractionBitsA, second: fractionBitsB)
        );

        if (scaled.Overflowed) {
            result = 0L;

            return false;
        }

        return FusedArithmetic.TryNarrowSignedMagnitude(negative: accumulator.Negative, magnitude: scaled.Magnitude, result: out result);
    }

    /// <summary>Rounds the reciprocal of a strictly positive raw onto a caller-chosen scale, exactly once.</summary>
    /// <param name="value">The raw to invert, which must be strictly positive.</param>
    /// <param name="fractionBitsIn">The operand's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <param name="result">The rounded reciprocal on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the operand is not strictly positive, when the combined bit count leaves
    /// the divider's envelope, or when the rounded reciprocal leaves the signed 64-bit raw.</returns>
    internal static bool TryReciprocal(long value, int fractionBitsIn, int fractionBitsOut, out long result) {
        if (value <= 0L) {
            result = 0L;

            return false;
        }

        if (!FusedArithmetic.TryDivideMagnitudeRounded(
            numeratorMagnitude: UInt128.One,
            denominatorMagnitude: ((UInt128)(ulong)value),
            fractionBitCount: (fractionBitsIn + fractionBitsOut),
            quotient: out var quotient
        )) {
            result = 0L;

            return false;
        }

        return FusedArithmetic.TryNarrowSignedMagnitude(negative: false, magnitude: quotient, result: out result);
    }

    /// <summary>Returns the least raw at or above the exact magnitude of a vector, at the components' own scale.</summary>
    /// <param name="value">The vector whose magnitude is bounded from above.</param>
    /// <returns>The rounded-up magnitude; <see cref="FixedQ4816.MaxValue"/> when the bound leaves the raw carrier.</returns>
    internal static FixedQ4816 CeilingMagnitude(FixedVector3 value) =>
        (FixedDirectedRounding.TryCeilingMagnitude(x: value.X.Value, y: value.Y.Value, z: value.Z.Value, result: out var magnitude)
            ? FixedQ4816.FromRawBits(value: magnitude)
            : FixedQ4816.MaxValue);

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
            : FixedQ4816.MaxValue);

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
            : FixedQ4816.MaxValue);

    /// <summary>Folds raw carrier words into a running FNV-1a digest — the spike's state fingerprint.</summary>
    /// <param name="digest">The running digest.</param>
    /// <param name="value">The word to fold.</param>
    /// <returns>The updated digest.</returns>
    internal static ulong Fold(ulong digest, long value) {
        const ulong Prime = 1099511628211UL;
        var word = unchecked((ulong)value);

        for (var index = 0; (index < 8); ++index) {
            digest ^= ((word >> (index * 8)) & 0xFFUL);
            digest = unchecked((digest * Prime));
        }

        return digest;
    }

    /// <summary>The FNV-1a offset basis the spike's state fingerprints start from.</summary>
    internal const ulong DigestSeed = 14695981039346656037UL;
}
