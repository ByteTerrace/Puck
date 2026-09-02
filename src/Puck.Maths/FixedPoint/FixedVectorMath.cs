using System.Diagnostics;
using System.Numerics;

namespace Puck.Maths;

/// <summary>Full-range helpers shared by fixed-point direction and norm operations.</summary>
internal static class FixedVectorMath {
    private const int DirectionLeadingBit = 45;

    internal readonly struct NormalizationScale(ulong denominator, int numeratorShift) {
        internal long Apply(long value) =>
            ScaleByPowerOfTwoRatio(
                denominator: denominator,
                numeratorShift: numeratorShift,
                value: value
            );
    }

    // Rounding a right shift can carry into bit 46. Four resulting squares, shifted by 32 for a Q16 norm, therefore
    // still fit UInt128 (at most 2^126) while preserving roughly 46 bits of source direction.
    internal static int DirectionShift(ulong rawMagnitude) =>
        (DirectionLeadingBit - (63 - BitOperations.LeadingZeroCount(value: rawMagnitude)));
    internal static long DivideBySquaredSum(long value, UInt128 squaredSum) {
        var negative = (value < 0L);
        var magnitude = RawMagnitude(value: value);
        var numerator = (((UInt128)magnitude) << (FixedQ4816.FractionBitCount * 2));
        var quotient = (numerator / squaredSum);
        var remainder = (numerator - (quotient * squaredSum));

        quotient = FixedPointRounding.RoundToNearestTiesToEven(
            distanceToNext: (squaredSum - remainder),
            distanceToTruncated: remainder,
            truncated: quotient
        );

        var raw = unchecked((long)((ulong)quotient));

        return (negative
            ? unchecked(-raw)
            : raw
        );
    }
    internal static (long X, long Y) Normalize(long x, long y) {
        var max = Math.Max(
            val1: RawMagnitude(value: x),
            val2: RawMagnitude(value: y)
        );

        if (max == 0UL) {
            return default;
        }

        var shift = DirectionShift(rawMagnitude: max);

        x = ScaleRaw(
            shift: shift,
            value: x
        );
        y = ScaleRaw(
            shift: shift,
            value: y
        );
        var denominator = NormalizationDenominator(squaredSum: (Square(value: x) + Square(value: y)));

        return (
            NormalizeComponent(
            denominator: denominator,
            value: x
        ),
            NormalizeComponent(
            denominator: denominator,
            value: y
        )
        );
    }
    internal static (long X, long Y, long Z) Normalize(long x, long y, long z) {
        var max = Math.Max(
            val1: RawMagnitude(value: x),
            val2: Math.Max(
                val1: RawMagnitude(value: y),
                val2: RawMagnitude(value: z)
            )
        );

        if (max == 0UL) {
            return default;
        }

        var shift = DirectionShift(rawMagnitude: max);

        x = ScaleRaw(
            shift: shift,
            value: x
        );
        y = ScaleRaw(
            shift: shift,
            value: y
        );
        z = ScaleRaw(
            shift: shift,
            value: z
        );
        var denominator = NormalizationDenominator(squaredSum: ((Square(value: x) + Square(value: y)) + Square(value: z)));

        return (
            NormalizeComponent(
            denominator: denominator,
            value: x
        ),
            NormalizeComponent(
            denominator: denominator,
            value: y
        ),
            NormalizeComponent(
            denominator: denominator,
            value: z
        )
        );
    }
    internal static (long X, long Y, long Z, long W) Normalize(long x, long y, long z, long w) {
        var max = Math.Max(
            val1: Math.Max(
                val1: RawMagnitude(value: x),
                val2: RawMagnitude(value: y)
            ),
            val2: Math.Max(
                val1: RawMagnitude(value: z),
                val2: RawMagnitude(value: w)
            )
        );

        if (max == 0UL) {
            return default;
        }

        var shift = DirectionShift(rawMagnitude: max);

        x = ScaleRaw(
            shift: shift,
            value: x
        );
        y = ScaleRaw(
            shift: shift,
            value: y
        );
        z = ScaleRaw(
            shift: shift,
            value: z
        );
        w = ScaleRaw(
            shift: shift,
            value: w
        );
        var denominator = NormalizationDenominator(squaredSum: (((Square(value: x) + Square(value: y)) + Square(value: z)) + Square(value: w)));

        return (
            NormalizeComponent(
            denominator: denominator,
            value: x
        ),
            NormalizeComponent(
            denominator: denominator,
            value: y
        ),
            NormalizeComponent(
            denominator: denominator,
            value: z
        ),
            NormalizeComponent(
            denominator: denominator,
            value: w
        )
        );
    }
    internal static ulong RawMagnitude(long value) =>
        FusedArithmetic.RawMagnitude(value: value);
    /// <summary>The exact separation between two raw carrier readings, independent of ordinary signed subtraction: the
    /// true difference between two <see cref="long"/> values always fits the unsigned 64-bit carrier (its magnitude is
    /// at most <c>2⁶⁴ − 1</c>, reached exactly at the opposing carrier extremes), so this never wraps regardless of how
    /// far apart the two readings are — unlike <c>target − current</c> in the signed carrier, which does.</summary>
    /// <param name="currentRaw">The current raw.</param>
    /// <param name="targetRaw">The target raw.</param>
    /// <returns>The non-negative separation, and whether <paramref name="targetRaw"/> is the greater of the two.</returns>
    internal static (ulong Separation, bool TargetIsGreater) RawSeparation(long currentRaw, long targetRaw) {
        var targetIsGreater = (targetRaw > currentRaw);

        return (
            Separation: (targetIsGreater
                ? unchecked((((ulong)targetRaw) - ((ulong)currentRaw)))
                : unchecked((((ulong)currentRaw) - ((ulong)targetRaw)))),
            TargetIsGreater: targetIsGreater
        );
    }
    /// <summary>Reconstructs a signed raw from a non-negative magnitude and a sign — the same two's-complement
    /// identity <see cref="ScaleRaw"/> and its siblings use: a magnitude of exactly <c>2⁶³</c> negated reproduces
    /// <see cref="long.MinValue"/> unchanged, so the full magnitude range <c>[0, 2⁶³]</c> round-trips.</summary>
    /// <param name="magnitude">The non-negative magnitude, at most <c>2⁶³</c>.</param>
    /// <param name="negative">Whether the result is negative.</param>
    /// <returns>The signed raw.</returns>
    internal static long SignedFromMagnitude(ulong magnitude, bool negative) {
        var raw = unchecked((long)magnitude);

        return (negative
            ? unchecked(-raw)
            : raw
        );
    }
    /// <summary>Returns the nearest raw Q16 square root of an exact raw Q32 sum; no integral halfway case exists, so no tie rule is needed.</summary>
    internal static FixedQ4816 RootOfSquaredSum(ulong squaredSum) {
        var root = squaredSum.SquareRoot();
        var remainder = (squaredSum - (root * root));

        // Consecutive squares differ by 2r+1. An integer radicand is nearer (r+1)^2 exactly when its remainder
        // above r^2 is greater than r; there is no integral halfway case.
        if (remainder > root) {
            ++root;
        }

        return FixedQ4816.FromRawBits(value: unchecked((long)root));
    }
    internal static long ScaleRaw(long value, int shift) {
        var negative = (value < 0L);
        var magnitude = RawMagnitude(value: value);
        ulong scaled;

        if (shift >= 0) {
            scaled = unchecked((magnitude << shift));
        } else {
            var rightShift = -shift;
            var quotient = (magnitude >> rightShift);
            var remainder = magnitude & ((1UL << rightShift) - 1UL);
            var half = (1UL << (rightShift - 1));

            if (
                (remainder > half) ||
                ((remainder == half) && ((quotient & 1UL) != 0UL))
            ) {
                ++quotient;
            }

            scaled = quotient;
        }

        var raw = unchecked((long)scaled);

        return (negative
            ? unchecked(-raw)
            : raw
        );
    }
    /// <summary>Creates the full-precision common ratio that normalizes the supplied four-component direction.</summary>
    /// <remarks>The ratio is represented as an integer power-of-two numerator over a 64-bit denominator instead of
    /// as Q16, so it can be applied to other components without first quantizing the reciprocal.</remarks>
    internal static bool TryCreateNormalizationScale(
        long x,
        long y,
        long z,
        long w,
        out NormalizationScale scale
    ) {
        var max = Math.Max(
            val1: Math.Max(
                val1: RawMagnitude(value: x),
                val2: RawMagnitude(value: y)
            ),
            val2: Math.Max(
                val1: RawMagnitude(value: z),
                val2: RawMagnitude(value: w)
            )
        );

        if (max == 0UL) {
            scale = default;
            return false;
        }

        var directionShift = DirectionShift(rawMagnitude: max);

        x = ScaleRaw(
            shift: directionShift,
            value: x
        );
        y = ScaleRaw(
            shift: directionShift,
            value: y
        );
        z = ScaleRaw(
            shift: directionShift,
            value: z
        );
        w = ScaleRaw(
            shift: directionShift,
            value: w
        );
        var denominator = NormalizationDenominator(squaredSum: (((Square(value: x) + Square(value: y)) + Square(value: z)) + Square(value: w)));

        // NormalizeComponent(preconditioned, denominator) represents preconditioned * 2^32 / denominator.
        // Retain the preconditioner's power of two in the numerator so every caller-supplied component receives
        // exactly the same ratio and only one final rounding.
        scale = new(
            denominator: denominator,
            numeratorShift: (directionShift + (FixedQ4816.FractionBitCount * 2))
        );
        return true;
    }
    internal static bool TryMagnitude(long x, long y, out FixedQ4816 result) {
        var squaredSum = (Square(value: x) + Square(value: y));

        return TryRoot(
            squaredSum: squaredSum,
            out result
        );
    }
    internal static bool TryMagnitude(long x, long y, long z, out FixedQ4816 result) {
        var squaredSum = ((Square(value: x) + Square(value: y)) + Square(value: z));

        return TryRoot(
            squaredSum: squaredSum,
            out result
        );
    }
    internal static bool TryMagnitude(long x, long y, long z, long w, out FixedQ4816 result) {
        var squaredSum = ((Square(value: x) + Square(value: y)) + Square(value: z));
        var fourthSquare = Square(value: w);
        var completeSum = (squaredSum + fourthSquare);

        // Four Int64 squares can total exactly 2^128. Any carry implies a root of at least 2^64, which cannot fit the
        // signed Q48.16 carrier, so the wrapped sum is never observed.
        if (completeSum < squaredSum) {
            result = default;
            return false;
        }

        return TryRoot(
            squaredSum: completeSum,
            out result
        );
    }
    /// <summary>Normalizes a three-component direction and produces its raw Q16 magnitude in the same pass. The
    /// magnitude spans the full unsigned 64-bit range (three squared longs always root within it), so callers can
    /// phase-reduce norms that exceed the signed Q48.16 carrier instead of saturating.</summary>
    internal static bool TryNormalizeWithMagnitude(
        long x,
        long y,
        long z,
        out long unitX,
        out long unitY,
        out long unitZ,
        out ulong rawMagnitude
    ) {
        var squaredSum = ((Square(value: x) + Square(value: y)) + Square(value: z));

        if (squaredSum == UInt128.Zero) {
            unitX = 0L;
            unitY = 0L;
            unitZ = 0L;
            rawMagnitude = 0UL;
            return false;
        }

        // The upper 2^32-wide band below 2^96 is excluded: there the rounded Q16-scaled root can carry to exactly
        // 2^64, which a ulong denominator cannot hold.
        if (squaredSum < ((UInt128.One << 96) - (UInt128.One << 32))) {
            // One root serves both outputs: root(sum · 2^32) is the Q16-scaled norm — exactly the normalization
            // denominator — and its high bits are within one of the directly rounded raw magnitude. Rounding the
            // denominator again would double-round, so the candidate settles against the exact half-way square:
            // round(√sum) bumps exactly when 4·sum > (2t + 1)², and even-vs-odd parity means no tie exists.
            var denominator = NormalizationDenominator(squaredSum: squaredSum);
            var truncated = (denominator >> FixedQ4816.FractionBitCount);
            var doubledPlusOne = ((UInt128)((truncated << 1) + 1UL));

            if ((squaredSum << 2) > (doubledPlusOne * doubledPlusOne)) {
                ++truncated;
            }

            unitX = NormalizeComponent(
                denominator: denominator,
                value: x
            );
            unitY = NormalizeComponent(
                denominator: denominator,
                value: y
            );
            unitZ = NormalizeComponent(
                denominator: denominator,
                value: z
            );
            rawMagnitude = truncated;
            return true;
        }

        // Near and beyond a 2^32 norm the Q16-scaled radicand would overflow (or carry past) the narrow root: root
        // the exact sum once and divide the Q16-shifted components by it — the root's ≤2^-49 relative error is
        // negligible against the unit direction's own Q16 quantization.
        var root = squaredSum.SquareRoot();

        if ((squaredSum - (root * root)) > root) {
            ++root;
        }

        var magnitude = ((ulong)root);

        unitX = NormalizeByMagnitude(
            rawMagnitude: magnitude,
            value: x
        );
        unitY = NormalizeByMagnitude(
            rawMagnitude: magnitude,
            value: y
        );
        unitZ = NormalizeByMagnitude(
            rawMagnitude: magnitude,
            value: z
        );
        rawMagnitude = magnitude;
        return true;
    }
    internal static bool TrySquaredMagnitude(long x, long y, out FixedQ4816 result) =>
        TryRoundSquaredSum(
            squaredSum: (Square(value: x) + Square(value: y)),
            overflowed: false,
            out result
        );
    internal static bool TrySquaredMagnitude(long x, long y, long z, out FixedQ4816 result) =>
        TryRoundSquaredSum(
            squaredSum: ((Square(value: x) + Square(value: y)) + Square(value: z)),
            overflowed: false,
            out result
        );
    internal static bool TrySquaredMagnitude(long x, long y, long z, long w, out FixedQ4816 result) {
        var complete = TrySumSquares(
            squaredSum: out var squaredSum,
            w: w,
            x: x,
            y: y,
            z: z
        );

        return TryRoundSquaredSum(
            squaredSum: squaredSum,
            overflowed: !complete,
            out result
        );
    }
    internal static bool TrySumSquares(long x, long y, long z, long w, out UInt128 squaredSum) {
        var firstThree = ((Square(value: x) + Square(value: y)) + Square(value: z));
        var complete = (firstThree + Square(value: w));

        squaredSum = complete;
        return (complete >= firstThree);
    }

    // PRECONDITION: squaredSum must stay below 2^96 - 2^32, or the Q32 shift wraps silently and the rounded root can
    // carry to exactly 2^64, which the ulong cast truncates. Two caller arguments discharge it today: the Normalize
    // overloads and TryCreateNormalizationScale precondition through DirectionShift, landing the largest magnitude at
    // DirectionLeadingBit = 45 so four squares reach at most 2^94; TryNormalizeWithMagnitude tests the band explicitly.
    // A sixth caller — or a DirectionLeadingBit bump — must re-establish the bound or the assert below names the break.
    private static ulong NormalizationDenominator(UInt128 squaredSum) {
        Debug.Assert(
            condition: (squaredSum < ((UInt128.One << 96) - (UInt128.One << 32))),
            message: "NormalizationDenominator requires squaredSum below 2^96 - 2^32; see the precondition comment."
        );

        var radicand = (squaredSum << (FixedQ4816.FractionBitCount * 2));
        var root = radicand.SquareRoot();

        if ((radicand - (root * root)) > root) {
            ++root;
        }

        return ((ulong)root);
    }
    private static long NormalizeByMagnitude(long value, ulong rawMagnitude) {
        var negative = (value < 0L);
        var numerator = (((UInt128)RawMagnitude(value: value)) << FixedQ4816.FractionBitCount);
        var quotient = (numerator / rawMagnitude);
        var remainder = ((ulong)(numerator - (quotient * rawMagnitude)));

        quotient = FixedPointRounding.RoundToNearestTiesToEven(
            distanceToNext: ((UInt128)(rawMagnitude - remainder)),
            distanceToTruncated: ((UInt128)remainder),
            truncated: quotient
        );

        var raw = ((long)((ulong)quotient));

        return (negative
            ? -raw
            : raw
        );
    }
    // The normalization denominator is a ulong; the computation is otherwise the squared-sum division exactly.
    private static long NormalizeComponent(long value, ulong denominator) =>
        DivideBySquaredSum(
            squaredSum: ((UInt128)denominator),
            value: value
        );
    private static long ScaleByPowerOfTwoRatio(long value, int numeratorShift, ulong denominator) {
        var negative = (value < 0L);
        var magnitude = RawMagnitude(value: value);
        var low = ((numeratorShift < 64)
            ? unchecked((magnitude << numeratorShift))
            : 0UL
        );
        var middle = ((numeratorShift < 64)
            ? (magnitude >> (64 - numeratorShift))
            : unchecked((magnitude << (numeratorShift - 64)))
        );
        var high = ((numeratorShift <= 64)
            ? 0UL
            : (magnitude >> (128 - numeratorShift))
        );

        ulong quotient;
        ulong remainder;

        if (high == 0UL) {
            // Unit-ish rigid transforms stay within 128 bits and take one division. .NET 10 lowers this common
            // 128-by-64 shape to the x64 DivRem instruction when the quotient fits a machine word.
            var numerator = (((UInt128)middle) << 64) | low;
            var fullQuotient = (numerator / denominator);

            quotient = unchecked((ulong)fullQuotient);
            remainder = ((ulong)(numerator - (fullQuotient * denominator)));
        } else {
            // Divide the at-most-141-bit shifted magnitude one base-2^64 limb at a time. Only the low quotient limb
            // is needed because FixedQ4816 arithmetic wraps on overflow, but the exact carried remainder preserves
            // ties-to-even rounding for wrapped results too. Here high is at most 13 bits and is below denominator.
            var partial = (((UInt128)high) << 64) | middle;

            remainder = ((ulong)(partial % denominator));
            partial = (((UInt128)remainder) << 64) | low;
            quotient = ((ulong)(partial / denominator));
            remainder = ((ulong)(partial - (((UInt128)quotient) * denominator)));
        }

        quotient = FixedPointRounding.RoundToNearestTiesToEven(
            distanceToNext: (denominator - remainder),
            distanceToTruncated: remainder,
            truncated: quotient
        );

        var raw = unchecked((long)quotient);

        return (negative
            ? unchecked(-raw)
            : raw
        );
    }
    private static UInt128 Square(long value) {
        var magnitude = RawMagnitude(value: value);

        return (((UInt128)magnitude) * magnitude);
    }
    private static bool TryRoot(UInt128 squaredSum, out FixedQ4816 result) {
        var root = squaredSum.SquareRoot();

        if ((squaredSum - (root * root)) > root) {
            ++root;
        }

        if (root > long.MaxValue) {
            result = default;
            return false;
        }

        result = FixedQ4816.FromRawBits(value: ((long)root));
        return true;
    }
    private static bool TryRoundSquaredSum(UInt128 squaredSum, bool overflowed, out FixedQ4816 result) {
        if (overflowed) {
            result = default;
            return false;
        }

        var scale = (((UInt128)1) << FixedQ4816.FractionBitCount);
        var rounded = (squaredSum >> FixedQ4816.FractionBitCount);
        var remainder = squaredSum & (scale - UInt128.One);
        var half = (scale >> 1);

        if (
            (remainder > half) ||
            ((remainder == half) && ((rounded & UInt128.One) != UInt128.Zero))
        ) {
            ++rounded;
        }

        if (rounded > long.MaxValue) {
            result = default;
            return false;
        }

        result = FixedQ4816.FromRawBits(value: ((long)rounded));
        return true;
    }
}
