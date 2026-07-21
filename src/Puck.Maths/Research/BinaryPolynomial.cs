using System.Numerics;

namespace Puck.Maths.Research;

/// <summary>
/// A polynomial over the two-element field, packed into a <see cref="ulong"/>: bit <c>i</c> is the coefficient of
/// <c>t^i</c>. Addition is XOR. The type is the exact arithmetic carrier used by <see cref="OddCyclicIncidence"/>;
/// it is also useful for CRC-style recurrences, binary linear codes, and finite-field moduli.
/// </summary>
public readonly record struct BinaryPolynomial {
    /// <summary>Creates a polynomial from its packed coefficients.</summary>
    /// <param name="bits">Bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    public BinaryPolynomial(ulong bits) => Bits = bits;

    /// <summary>Gets the packed coefficients.</summary>
    public ulong Bits { get; }
    /// <summary>Gets the largest non-zero exponent, or minus one for the zero polynomial.</summary>
    public int Degree => ((Bits == 0UL) ? -1 : BitOperations.Log2(Bits));
    /// <summary>Gets whether this is the zero polynomial.</summary>
    public bool IsZero => (Bits == 0UL);
    /// <summary>Gets whether this is the constant polynomial one.</summary>
    public bool IsOne => (Bits == 1UL);

    /// <summary>Adds two binary polynomials; subtraction is the same operation.</summary>
    public static BinaryPolynomial operator +(BinaryPolynomial left, BinaryPolynomial right) =>
        new(bits: (left.Bits ^ right.Bits));

    /// <summary>Returns the product when it fits the packed carrier.</summary>
    /// <exception cref="OverflowException">The product has a non-zero coefficient above degree 63.</exception>
    public static BinaryPolynomial operator *(BinaryPolynomial left, BinaryPolynomial right) =>
        new(bits: MultiplyExact(left: left.Bits, right: right.Bits));

    /// <summary>Returns this polynomial modulo <paramref name="divisor"/>.</summary>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is zero.</exception>
    public BinaryPolynomial Remainder(BinaryPolynomial divisor) =>
        new(bits: Remainder(dividend: Bits, divisor: divisor.Bits));

    /// <summary>Returns the monic greatest common divisor.</summary>
    public BinaryPolynomial GreatestCommonDivisor(BinaryPolynomial other) {
        var left = Bits;
        var right = other.Bits;

        while (right != 0UL) { (left, right) = (right, Remainder(dividend: left, divisor: right)); }

        return new BinaryPolynomial(bits: left);
    }

    /// <summary>
    /// Gets whether this non-constant polynomial is irreducible over <c>GF(2)</c>, using the Frobenius/GCD criterion.
    /// </summary>
    public bool IsIrreducible() {
        var degree = Degree;

        if ((degree <= 0) || ((Bits & 1UL) == 0UL)) { return false; }

        var reducedT = Remainder(dividend: 2UL, divisor: Bits);
        var power = reducedT;

        for (var exponent = 1; (exponent <= degree); ++exponent) {
            power = MultiplyModulo(left: power, right: power, modulus: Bits);

            if ((exponent <= (degree / 2)) && (GreatestCommonDivisorBits(left: (power ^ reducedT), right: Bits) != 1UL)) {
                return false;
            }
        }

        return (power == reducedT);
    }

    /// <summary>
    /// Factors <c>t^n + 1</c> over <c>GF(2)</c> for an odd positive <paramref name="cycleOrder"/>. In characteristic two
    /// this is also <c>t^n - 1</c>, the group polynomial of a cyclic action. Automatic trial factorization is deliberately
    /// bounded at order 31; larger systems can supply already-known factors to <see cref="OddCyclicIncidence"/>, which
    /// validates them independently.
    /// </summary>
    /// <param name="cycleOrder">An odd order in <c>[1, 31]</c>.</param>
    /// <returns>The distinct monic irreducible factors, ordered by degree and then packed value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cycleOrder"/> is even or outside <c>[1, 31]</c>.</exception>
    public static BinaryPolynomial[] FactorOddCycle(int cycleOrder) {
        if ((cycleOrder <= 0) || (cycleOrder > 31) || ((cycleOrder & 1) == 0)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(cycleOrder),
                actualValue: cycleOrder,
                message: "Automatic binary factorization requires an odd cycle order in [1, 31]."
            );
        }

        var remaining = ((1UL << cycleOrder) | 1UL);
        var factors = new List<BinaryPolynomial>();

        for (var degree = 1; (degree <= (cycleOrder / 2)) && (remaining != 1UL); ++degree) {
            var middleCount = (1UL << (degree - 1));

            for (var middle = 0UL; (middle < middleCount) && (remaining != 1UL); ++middle) {
                var candidate = ((1UL << degree) | (middle << 1) | 1UL);

                if (!new BinaryPolynomial(bits: candidate).IsIrreducible()) { continue; }
                if (Remainder(dividend: remaining, divisor: candidate) != 0UL) { continue; }

                factors.Add(item: new BinaryPolynomial(bits: candidate));
                remaining = DivideExact(dividend: remaining, divisor: candidate);
            }
        }

        if (remaining != 1UL) {
            var finalFactor = new BinaryPolynomial(bits: remaining);

            if (!finalFactor.IsIrreducible()) {
                throw new InvalidOperationException("The binary factorization did not finish with an irreducible factor.");
            }

            factors.Add(item: finalFactor);
        }

        return [.. factors.OrderBy(keySelector: factor => factor.Degree).ThenBy(keySelector: factor => factor.Bits)];
    }

    internal static ulong MultiplyModulo(ulong left, ulong right, ulong modulus) {
        if (modulus == 0UL) { throw new DivideByZeroException(); }

        var degree = BitOperations.Log2(modulus);
        var topBit = (1UL << degree);
        var result = 0UL;

        left = Remainder(dividend: left, divisor: modulus);
        while (right != 0UL) {
            if ((right & 1UL) != 0UL) { result ^= left; }

            right >>= 1;
            if (right == 0UL) { break; }

            left <<= 1;
            if ((left & topBit) != 0UL) { left ^= modulus; }
        }

        return result;
    }

    internal static ulong Remainder(ulong dividend, ulong divisor) {
        if (divisor == 0UL) { throw new DivideByZeroException(); }

        var divisorDegree = BitOperations.Log2(divisor);
        while ((dividend != 0UL) && (BitOperations.Log2(dividend) >= divisorDegree)) {
            dividend ^= (divisor << (BitOperations.Log2(dividend) - divisorDegree));
        }

        return dividend;
    }

    private static ulong GreatestCommonDivisorBits(ulong left, ulong right) {
        while (right != 0UL) { (left, right) = (right, Remainder(dividend: left, divisor: right)); }

        return left;
    }

    private static ulong MultiplyExact(ulong left, ulong right) {
        var result = 0UL;

        while (right != 0UL) {
            if ((right & 1UL) != 0UL) { result ^= left; }

            right >>= 1;
            if (right == 0UL) { break; }
            if ((left & (1UL << 63)) != 0UL) { throw new OverflowException("The binary-polynomial product exceeds degree 63."); }

            left <<= 1;
        }

        return result;
    }

    private static ulong DivideExact(ulong dividend, ulong divisor) {
        var divisorDegree = BitOperations.Log2(divisor);
        var quotient = 0UL;

        while ((dividend != 0UL) && (BitOperations.Log2(dividend) >= divisorDegree)) {
            var shift = (BitOperations.Log2(dividend) - divisorDegree);

            quotient |= (1UL << shift);
            dividend ^= (divisor << shift);
        }

        if (dividend != 0UL) { throw new InvalidOperationException("The binary-polynomial division was not exact."); }

        return quotient;
    }
}
