using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Claim bodies for the <c>directed-rounding</c> family — <see cref="FixedDirectedRounding"/>'s conservative
/// upper bounds. Every law here asserts the DIRECTION and the TIGHTNESS, not mere proximity: the returned raw
/// <c>r</c> must satisfy <c>r ≥ exact</c> AND <c>r − 1 &lt; exact</c> against the exact rational computed in
/// <see cref="BigInteger"/>, which is what makes it the LEAST representable upper bound rather than some upper bound.
/// A law that only compared <c>r</c> to a nearest-rounded value would pass a kernel that rounded to nearest and added
/// a unit in the last place — precisely the call-site habit this family exists to remove.</summary>
internal static class DirectedRoundingClaims {
    // Every operand is folded onto the non-negative half of the carrier by one logical shift, which keeps the raw's
    // own bit pattern (so the committed edge battery still lands on the seams) while satisfying the family's
    // non-negative contract. The negative-operand refusals are a separate claim.
    private static long FoldNonNegative(long raw) => ((long)(((ulong)raw) >> 1));
    private static int FoldScale(long raw) => ((int)(((ulong)raw) % 65UL));
    // A strictly positive divisor, the only kind TryCeilingQuotient admits.
    private static long FoldPositive(long raw) {
        var folded = FoldNonNegative(raw: raw);

        return ((folded == 0L) ? 1L : folded);
    }

    /// <summary>The ceiling square root is the least raw whose square is at or above the scaled radicand.</summary>
    /// <param name="left">Lane 0 = the radicand, folded non-negative.</param>
    /// <param name="right">Lane 0 drives the output fraction bit count.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CeilingSquareRootIsLeastUpperBound(long[] left, long[] right) {
        var value = FoldNonNegative(raw: left[0]);
        var fractionBitCount = FoldScale(raw: right[0]);

        var subjectOk = FixedDirectedRounding.TryCeilingSquareRoot(fractionBitCount: fractionBitCount, result: out var result, value: value);
        var radicand = (((BigInteger)value) << fractionBitCount);
        var exact = Oracles.CeilingIntegerRoot(value: radicand);
        var expected = ((radicand < (BigInteger.One << 128)) && (exact <= long.MaxValue));

        if (subjectOk != expected) {
            return $"ceiling square root outcome at ({value} @ {fractionBitCount} fraction bits): subject={subjectOk} expected={expected}";
        }

        if (!subjectOk) {
            return ((result == 0L)
                ? null
                : $"ceiling square root refused at ({value} @ {fractionBitCount}) but left {result} behind");
        }

        if ((((BigInteger)result) * result) < radicand) {
            return $"ceiling square root {result} at ({value} @ {fractionBitCount}) is BELOW the exact root — it is not an upper bound at all";
        }

        if ((result > 0L) && (((((BigInteger)result) - BigInteger.One) * (result - 1L)) >= radicand)) {
            return $"ceiling square root {result} at ({value} @ {fractionBitCount}) is more than one unit above the exact root — it is not the LEAST upper bound";
        }

        return ((result == ((long)exact))
            ? null
            : $"ceiling square root at ({value} @ {fractionBitCount}): subject={result} oracle={exact}");
    }
    /// <summary>The ceiling product is the least raw at or above the exact mixed-scale product.</summary>
    /// <param name="left">Lanes 0..1 = the two factors, folded non-negative.</param>
    /// <param name="right">Lanes 0..2 drive the two operand fraction bit counts and the output's.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CeilingProductIsLeastUpperBound(long[] left, long[] right) {
        var a = FoldNonNegative(raw: left[0]);
        var b = FoldNonNegative(raw: left[1]);
        var fractionBitsA = FoldScale(raw: right[0]);
        var fractionBitsB = FoldScale(raw: right[1]);
        var fractionBitsOut = FoldScale(raw: right[2]);

        var subjectOk = FixedDirectedRounding.TryCeilingProduct(
            a: a,
            b: b,
            fractionBitsA: fractionBitsA,
            fractionBitsB: fractionBitsB,
            fractionBitsOut: fractionBitsOut,
            result: out var result
        );

        var (numerator, denominator) = Scaled(shift: ((((long)fractionBitsOut) - fractionBitsA) - fractionBitsB), value: (((BigInteger)a) * b));

        return Verdict(
            denominator: denominator,
            numerator: numerator,
            operands: $"ceiling product ({a}@{fractionBitsA} x {b}@{fractionBitsB} -> {fractionBitsOut})",
            result: result,
            subjectOk: subjectOk
        );
    }
    /// <summary>The ceiling quotient is the least raw at or above the exact mixed-scale quotient.</summary>
    /// <param name="left">Lane 0 = the dividend, folded non-negative; lane 1 = the divisor, folded strictly positive.</param>
    /// <param name="right">Lanes 0..2 drive the two operand fraction bit counts and the output's.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CeilingQuotientIsLeastUpperBound(long[] left, long[] right) {
        var numeratorRaw = FoldNonNegative(raw: left[0]);
        var denominatorRaw = FoldPositive(raw: left[1]);
        var fractionBitsNumerator = FoldScale(raw: right[0]);
        var fractionBitsDenominator = FoldScale(raw: right[1]);
        var fractionBitsOut = FoldScale(raw: right[2]);

        var subjectOk = FixedDirectedRounding.TryCeilingQuotient(
            denominator: denominatorRaw,
            fractionBitsDenominator: fractionBitsDenominator,
            fractionBitsNumerator: fractionBitsNumerator,
            fractionBitsOut: fractionBitsOut,
            numerator: numeratorRaw,
            result: out var result
        );

        var (numerator, denominator) = Scaled(
            shift: ((((long)fractionBitsOut) + fractionBitsDenominator) - fractionBitsNumerator),
            value: numeratorRaw
        );

        return Verdict(
            denominator: (denominator * denominatorRaw),
            numerator: numerator,
            operands: $"ceiling quotient ({numeratorRaw}@{fractionBitsNumerator} / {denominatorRaw}@{fractionBitsDenominator} -> {fractionBitsOut})",
            result: result,
            subjectOk: subjectOk
        );
    }
    /// <summary>The ceiling product-sum is the least raw at or above the exact <c>a·b + addend</c>, and it answers
    /// exactly when both lifted terms stay inside the declared 127-bit per-term budget and the result fits the raw.</summary>
    /// <param name="left">Lanes 0..2 = the two factors and the addend, folded non-negative.</param>
    /// <param name="right">Lanes 0..3 drive the three operand fraction bit counts and the output's.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CeilingProductSumIsLeastUpperBound(long[] left, long[] right) {
        var a = FoldNonNegative(raw: left[0]);
        var b = FoldNonNegative(raw: left[1]);
        var addend = FoldNonNegative(raw: left[2]);
        var fractionBitsA = FoldScale(raw: right[0]);
        var fractionBitsB = FoldScale(raw: right[1]);
        var fractionBitsAddend = FoldScale(raw: right[2]);
        var fractionBitsOut = FoldScale(raw: right[3]);

        var subjectOk = FixedDirectedRounding.TryCeilingProductSum(
            a: a,
            addend: addend,
            b: b,
            fractionBitsA: fractionBitsA,
            fractionBitsAddend: fractionBitsAddend,
            fractionBitsB: fractionBitsB,
            fractionBitsOut: fractionBitsOut,
            result: out var result
        );

        var productScale = (((long)fractionBitsA) + fractionBitsB);
        var addendScale = ((long)fractionBitsAddend);
        var commonScale = Math.Max(val1: productScale, val2: addendScale);
        var productTerm = ((((BigInteger)a) * b) << ((int)(commonScale - productScale)));
        var addendTerm = (((BigInteger)addend) << ((int)(commonScale - addendScale)));
        var budget = (BigInteger.One << 127);

        var (numerator, denominator) = Scaled(shift: (fractionBitsOut - commonScale), value: (productTerm + addendTerm));
        var withinBudget = ((productTerm < budget) && (addendTerm < budget));
        var operands = $"ceiling product-sum ({a}@{fractionBitsA} x {b}@{fractionBitsB} + {addend}@{fractionBitsAddend} -> {fractionBitsOut})";

        if (!withinBudget) {
            return (!subjectOk
                ? ((result == 0L) ? null : $"{operands}: refused outside the 127-bit per-term budget but left {result} behind")
                : $"{operands}: answered {result} outside the declared 127-bit per-term budget");
        }

        return Verdict(denominator: denominator, numerator: numerator, operands: operands, result: result, subjectOk: subjectOk);
    }
    /// <summary>The ceiling vector magnitude is the least raw at or above the exact Euclidean norm, at two and three
    /// components alike, and never below the round-to-nearest sibling it is meant to bound.</summary>
    /// <param name="left">Lanes 0..2 = the three components, taken at full signed range.</param>
    /// <param name="right">Unused; the norm carries no scale parameter.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CeilingMagnitudeIsLeastUpperBound(long[] left, long[] right) {
        _ = right;

        var x = left[0];
        var y = left[1];
        var z = left[2];

        var planarOk = FixedDirectedRounding.TryCeilingMagnitude(result: out var planar, x: x, y: y);
        var spatialOk = FixedDirectedRounding.TryCeilingMagnitude(result: out var spatial, x: x, y: y, z: z);

        if (Bound(x: x, y: y, z: BigInteger.Zero, subjectOk: planarOk, result: planar, label: "planar") is { } planarDetail) {
            return planarDetail;
        }

        if (Bound(label: "spatial", result: spatial, subjectOk: spatialOk, x: x, y: y, z: z) is { } spatialDetail) {
            return spatialDetail;
        }

        // The round-to-nearest sibling stays untouched, and the directed one must never fall below it.
        if (spatialOk && FixedVectorMath.TryMagnitude(x: x, y: y, z: z, result: out var nearest) && (spatial < nearest.Value)) {
            return $"ceiling magnitude {spatial} at ({x},{y},{z}) fell BELOW the round-to-nearest magnitude {nearest.Value}";
        }

        return null;
    }
    /// <summary>Every member of the family refuses a negative operand — and a non-positive divisor — with its output
    /// left at zero, rather than choosing silently between "up" meaning toward positive infinity and "up" meaning away
    /// from zero. The vector magnitudes are the one exception and are checked to ACCEPT negative components, because a
    /// magnitude is non-negative whatever the signs are.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? NegativeOperandsRefuse() {
        if (FixedDirectedRounding.TryCeilingSquareRoot(fractionBitCount: 16, result: out var root, value: -1L) || (root != 0L)) {
            return $"TryCeilingSquareRoot accepted a negative radicand (or left {root} behind)";
        }

        if (FixedDirectedRounding.TryCeilingSquareRoot(fractionBitCount: -1, result: out var negativeScale, value: 1L) || (negativeScale != 0L)) {
            return $"TryCeilingSquareRoot accepted a negative fraction bit count (or left {negativeScale} behind)";
        }

        if (FixedDirectedRounding.TryCeilingProduct(a: -1L, b: 1L, fractionBitsA: 0, fractionBitsB: 0, fractionBitsOut: 0, result: out var product) || (product != 0L)) {
            return $"TryCeilingProduct accepted a negative factor (or left {product} behind)";
        }

        // The same negative factor at a scale that shifts its two's-complement magnitude back down inside the carrier:
        // without the sign guard this answers 1, because -1 reads as 2^64 - 1 and a 64-bit right shift brings it home.
        // The witness above cannot see that — there the huge magnitude overflows the narrowing anyway, so the guard's
        // removal is masked.
        if (FixedDirectedRounding.TryCeilingProduct(a: -1L, b: 1L, fractionBitsA: 64, fractionBitsB: 0, fractionBitsOut: 0, result: out var masked) || (masked != 0L)) {
            return $"TryCeilingProduct accepted a negative factor at a 64-bit down-scale (or left {masked} behind) — the sign guard is not load-bearing";
        }

        if (FixedDirectedRounding.TryCeilingQuotient(denominator: 0L, fractionBitsDenominator: 0, fractionBitsNumerator: 0, fractionBitsOut: 0, numerator: 1L, result: out var byZero) || (byZero != 0L)) {
            return $"TryCeilingQuotient accepted a zero divisor (or left {byZero} behind)";
        }

        if (FixedDirectedRounding.TryCeilingQuotient(denominator: -4L, fractionBitsDenominator: 0, fractionBitsNumerator: 0, fractionBitsOut: 0, numerator: 1L, result: out var negativeDivisor) || (negativeDivisor != 0L)) {
            return $"TryCeilingQuotient accepted a negative divisor (or left {negativeDivisor} behind)";
        }

        if (FixedDirectedRounding.TryCeilingProductSum(a: 1L, addend: -1L, b: 1L, fractionBitsA: 0, fractionBitsAddend: 0, fractionBitsB: 0, fractionBitsOut: 0, result: out var sum) || (sum != 0L)) {
            return $"TryCeilingProductSum accepted a negative addend (or left {sum} behind)";
        }

        // The exact ratio 1/3 at zero output fraction bits: the ceiling is one, where a round-to-nearest kernel would
        // answer zero. A directed kernel that quietly rounded to nearest would pass every proximity check and fail here.
        if (!FixedDirectedRounding.TryCeilingQuotient(denominator: 3L, fractionBitsDenominator: 0, fractionBitsNumerator: 0, fractionBitsOut: 0, numerator: 1L, result: out var third) || (third != 1L)) {
            return $"TryCeilingQuotient answered {third} for 1/3 at zero fraction bits, expected the ceiling 1";
        }

        if (!FixedDirectedRounding.TryCeilingMagnitude(result: out var magnitude, x: -3L, y: -4L) || (magnitude != 5L)) {
            return $"TryCeilingMagnitude answered {magnitude} for (-3,-4), expected the exact 5 — negative components are legal here";
        }

        return null;
    }

    // The exact rational value, as a numerator over a positive denominator, of an integer scaled by a signed power of
    // two.
    private static (BigInteger Numerator, BigInteger Denominator) Scaled(BigInteger value, long shift) =>
        ((shift >= 0L)
            ? ((value << ((int)shift)), BigInteger.One)
            : (value, (BigInteger.One << ((int)-shift))));
    // The shared verdict: refuse exactly when the exact ceiling leaves the raw, and otherwise be the least
    // representable value at or above the exact rational.
    private static string? Verdict(bool subjectOk, long result, BigInteger numerator, BigInteger denominator, string operands) {
        var exact = Oracles.CeilingRational(denominator: denominator, numerator: numerator);
        var expected = (exact <= long.MaxValue);

        if (subjectOk != expected) {
            return $"{operands}: subject={subjectOk} expected={expected} (exact ceiling {exact})";
        }

        if (!subjectOk) {
            return ((result == 0L)
                ? null
                : $"{operands}: refused but left {result} behind");
        }

        if ((((BigInteger)result) * denominator) < numerator) {
            return $"{operands}: answered {result}, which is BELOW the exact value — it is not an upper bound at all";
        }

        if (((((BigInteger)result) - BigInteger.One) * denominator) >= numerator) {
            return $"{operands}: answered {result}, more than one unit above the exact value — it is not the LEAST upper bound";
        }

        return ((result == ((long)exact))
            ? null
            : $"{operands}: subject={result} oracle={exact}");
    }
    private static string? Bound(long x, long y, BigInteger z, bool subjectOk, long result, string label) {
        var radicand = (((((BigInteger)x) * x) + (((BigInteger)y) * y)) + (z * z));
        var exact = Oracles.CeilingIntegerRoot(value: radicand);
        var expected = (exact <= long.MaxValue);

        if (subjectOk != expected) {
            return $"{label} ceiling magnitude outcome at ({x},{y},{z}): subject={subjectOk} expected={expected}";
        }

        if (!subjectOk) {
            return ((result == 0L)
                ? null
                : $"{label} ceiling magnitude refused at ({x},{y},{z}) but left {result} behind");
        }

        if ((((BigInteger)result) * result) < radicand) {
            return $"{label} ceiling magnitude {result} at ({x},{y},{z}) is BELOW the exact norm — it is not an upper bound at all";
        }

        if ((result > 0L) && (((((BigInteger)result) - BigInteger.One) * (result - 1L)) >= radicand)) {
            return $"{label} ceiling magnitude {result} at ({x},{y},{z}) is more than one unit above the exact norm — it is not the LEAST upper bound";
        }

        return ((result == ((long)exact))
            ? null
            : $"{label} ceiling magnitude at ({x},{y},{z}): subject={result} oracle={exact}");
    }
}
