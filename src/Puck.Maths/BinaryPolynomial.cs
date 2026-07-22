using System.Globalization;
using System.Numerics;
using System.Text;

namespace Puck.Maths;

/// <summary>
/// A polynomial over the two-element field, packed into a <see cref="ulong"/>: bit <c>i</c> is the coefficient of
/// <c>t^i</c>. Addition is XOR, and subtraction is the same operation. The type is the exact <c>GF(2)[t]</c> carrier
/// beneath <see cref="BinaryField{T}"/>, CRC-style recurrences, binary linear codes, and cyclic-incidence analysis.
/// </summary>
/// <remarks>
/// The type carries a polynomial, not a field element: it has no modulus, no inverse, and no order. Ordinary
/// multiplication truncates above degree 63 exactly as the library's other fixed-width operators wrap;
/// <see cref="MultiplyWide(BinaryPolynomial)"/> returns the exact product and the checked operator reports the loss.
/// </remarks>
public readonly record struct BinaryPolynomial :
    IAdditionOperators<BinaryPolynomial, BinaryPolynomial, BinaryPolynomial>,
    IAdditiveIdentity<BinaryPolynomial, BinaryPolynomial>,
    IDivisionOperators<BinaryPolynomial, BinaryPolynomial, BinaryPolynomial>,
    IModulusOperators<BinaryPolynomial, BinaryPolynomial, BinaryPolynomial>,
    IMultiplicativeIdentity<BinaryPolynomial, BinaryPolynomial>,
    IMultiplyOperators<BinaryPolynomial, BinaryPolynomial, BinaryPolynomial>,
    IShiftOperators<BinaryPolynomial, int, BinaryPolynomial>,
    ISubtractionOperators<BinaryPolynomial, BinaryPolynomial, BinaryPolynomial>,
    IUnaryNegationOperators<BinaryPolynomial, BinaryPolynomial> {
    /// <summary>The largest exponent the packed carrier can hold.</summary>
    private const int MaximumDegree = 63;

    /// <summary>Creates a polynomial from its packed coefficients.</summary>
    /// <param name="bits">Bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    public BinaryPolynomial(ulong bits) => Bits = bits;

    /// <summary>Gets the zero polynomial, which is the identity for addition.</summary>
    public static BinaryPolynomial AdditiveIdentity => default;
    /// <summary>Gets the indeterminate <c>t</c>.</summary>
    public static BinaryPolynomial Indeterminate => new(bits: 2UL);
    /// <summary>Gets the constant polynomial one, which is the identity for multiplication.</summary>
    public static BinaryPolynomial MultiplicativeIdentity => new(bits: 1UL);
    /// <summary>Gets the constant polynomial one.</summary>
    public static BinaryPolynomial One => new(bits: 1UL);
    /// <summary>Gets the zero polynomial.</summary>
    public static BinaryPolynomial Zero => default;

    /// <summary>Gets the packed coefficients.</summary>
    public ulong Bits { get; }
    /// <summary>Gets the largest exponent carrying a non-zero coefficient, or minus one for the zero polynomial.</summary>
    public int Degree => (MaximumDegree - BitOperations.LeadingZeroCount(value: Bits));
    /// <summary>Gets whether this is the constant polynomial one.</summary>
    public bool IsOne => (1UL == Bits);
    /// <summary>Gets whether this is the zero polynomial.</summary>
    public bool IsZero => (0UL == Bits);

    /// <summary>Adds two binary polynomials; subtraction is the same operation.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum, which is the coefficient-wise exclusive or.</returns>
    public static BinaryPolynomial operator +(BinaryPolynomial left, BinaryPolynomial right) =>
        new(bits: (left.Bits ^ right.Bits));
    /// <summary>Subtracts one binary polynomial from another; addition is the same operation.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference, which is the coefficient-wise exclusive or.</returns>
    public static BinaryPolynomial operator -(BinaryPolynomial left, BinaryPolynomial right) =>
        new(bits: (left.Bits ^ right.Bits));
    /// <summary>Negates a binary polynomial, which in characteristic two returns it unchanged.</summary>
    /// <param name="value">The polynomial to negate.</param>
    /// <returns><paramref name="value"/>, because every coefficient is its own additive inverse.</returns>
    public static BinaryPolynomial operator -(BinaryPolynomial value) =>
        value;
    /// <summary>Multiplies two binary polynomials, discarding any coefficient above degree 63.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <returns>The product's coefficients of exponents 0 through 63.</returns>
    /// <remarks>Truncation matches the library's other fixed-width operators. Use <see cref="MultiplyWide(BinaryPolynomial)"/> for the exact product, or the checked operator to have the loss reported.</remarks>
    public static BinaryPolynomial operator *(BinaryPolynomial left, BinaryPolynomial right) =>
        new(bits: BinaryFieldKernels.CarrylessMultiply64(left: left.Bits, right: right.Bits).Low);
    /// <summary>Multiplies two binary polynomials, reporting any coefficient above degree 63.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <returns>The exact product.</returns>
    /// <exception cref="OverflowException">The product has a non-zero coefficient above degree 63.</exception>
    public static BinaryPolynomial operator checked *(BinaryPolynomial left, BinaryPolynomial right) {
        var product = BinaryFieldKernels.CarrylessMultiply64(left: left.Bits, right: right.Bits);

        if (0UL != product.High) { throw new OverflowException("The binary-polynomial product exceeds degree 63."); }

        return new(bits: product.Low);
    }
    /// <summary>Divides one binary polynomial by another, discarding the remainder.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor.</param>
    /// <returns>The Euclidean quotient.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> is zero.</exception>
    public static BinaryPolynomial operator /(BinaryPolynomial left, BinaryPolynomial right) =>
        left.DivRem(divisor: right).Quotient;
    /// <summary>Reduces one binary polynomial by another.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor.</param>
    /// <returns>The Euclidean remainder, whose degree is below <paramref name="right"/>'s.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> is zero.</exception>
    public static BinaryPolynomial operator %(BinaryPolynomial left, BinaryPolynomial right) =>
        left.DivRem(divisor: right).Remainder;
    /// <summary>Multiplies by <c>t^<paramref name="count"/></c>, discarding any coefficient that would land above degree 63.</summary>
    /// <param name="value">The polynomial to shift up.</param>
    /// <param name="count">The number of exponents to shift by.</param>
    /// <returns>The product with <c>t^<paramref name="count"/></c>, truncated to the packed carrier.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static BinaryPolynomial operator <<(BinaryPolynomial value, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);

        // The carrier's own shift masks its count to the carrier width, so a count at or past the width would wrap
        // around and resurrect exactly the coefficients this operator promises to discard.
        if (MaximumDegree < count) { return Zero; }

        return new(bits: (value.Bits << count));
    }
    /// <summary>Divides by <c>t^<paramref name="count"/></c>, discarding the remainder.</summary>
    /// <param name="value">The polynomial to shift down.</param>
    /// <param name="count">The number of exponents to shift by.</param>
    /// <returns>The quotient by <c>t^<paramref name="count"/></c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static BinaryPolynomial operator >>(BinaryPolynomial value, int count) =>
        (value >>> count);
    /// <summary>Divides by <c>t^<paramref name="count"/></c>, discarding the remainder.</summary>
    /// <param name="value">The polynomial to shift down.</param>
    /// <param name="count">The number of exponents to shift by.</param>
    /// <returns>The quotient by <c>t^<paramref name="count"/></c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static BinaryPolynomial operator >>>(BinaryPolynomial value, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);

        if (MaximumDegree < count) { return Zero; }

        return new(bits: (value.Bits >>> count));
    }
    /// <summary>Widens to the packed 128-bit carrier.</summary>
    /// <param name="value">The polynomial to widen.</param>
    /// <returns>The same polynomial over the wide carrier.</returns>
    public static implicit operator BinaryPolynomialWide(BinaryPolynomial value) =>
        new(bits: ((UInt128)value.Bits));

    /// <summary>
    /// Factors <c>t^n + 1</c> over the two-element field for an odd positive <paramref name="cycleOrder"/>. In
    /// characteristic two this is also <c>t^n - 1</c>, the group polynomial of a cyclic action. Automatic trial
    /// factorization is deliberately bounded at order 31; larger systems can supply already-known factors to a
    /// cyclic-incidence analysis, which validates them independently.
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

        var factors = new List<BinaryPolynomial>();
        var remaining = new BinaryPolynomial(bits: ((1UL << cycleOrder) | 1UL));

        for (var degree = 1; (degree <= (cycleOrder / 2)) && !remaining.IsOne; ++degree) {
            var middleCount = (1UL << (degree - 1));

            for (var middle = 0UL; (middle < middleCount) && !remaining.IsOne; ++middle) {
                var candidate = new BinaryPolynomial(bits: ((1UL << degree) | (middle << 1) | 1UL));

                if (!candidate.IsIrreducible()) { continue; }

                var division = remaining.DivRem(divisor: candidate);

                if (!division.Remainder.IsZero) { continue; }

                factors.Add(item: candidate);
                remaining = division.Quotient;
            }
        }

        if (!remaining.IsOne) {
            if (!remaining.IsIrreducible()) {
                throw new InvalidOperationException("The binary factorization did not finish with an irreducible factor.");
            }

            factors.Add(item: remaining);
        }

        return [.. factors.OrderBy(keySelector: factor => factor.Degree).ThenBy(keySelector: factor => factor.Bits)];
    }

    /// <summary>Divides this polynomial by <paramref name="divisor"/>, returning the quotient and the remainder together.</summary>
    /// <param name="divisor">The polynomial to divide by.</param>
    /// <returns>The Euclidean quotient and remainder, which satisfy <c>(quotient * divisor) + remainder == this</c> exactly.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is zero.</exception>
    public (BinaryPolynomial Quotient, BinaryPolynomial Remainder) DivRem(BinaryPolynomial divisor) {
        if (divisor.IsZero) { throw new DivideByZeroException(); }

        var divisorDegree = divisor.Degree;
        var quotient = 0UL;
        var remainder = Bits;
        var remainderDegree = Degree;

        while (remainderDegree >= divisorDegree) {
            var shift = (remainderDegree - divisorDegree);

            quotient |= (1UL << shift);
            remainder ^= (divisor.Bits << shift);
            remainderDegree = (MaximumDegree - BitOperations.LeadingZeroCount(value: remainder));
        }

        return (Quotient: new BinaryPolynomial(bits: quotient), Remainder: new BinaryPolynomial(bits: remainder));
    }
    /// <summary>Returns the monic greatest common divisor.</summary>
    /// <param name="other">The polynomial to take the common divisor with.</param>
    /// <returns>The monic greatest common divisor; when one operand is zero, the other is returned.</returns>
    public BinaryPolynomial GreatestCommonDivisor(BinaryPolynomial other) {
        var left = this;
        var right = other;

        while (!right.IsZero) { (left, right) = (right, (left % right)); }

        return left;
    }
    /// <summary>Gets whether this non-constant polynomial is irreducible over the two-element field.</summary>
    /// <returns><see langword="true"/> when the polynomial is irreducible; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// A polynomial of degree at least one with a non-zero constant term defines a quotient ring, and that ring is a
    /// field exactly when the polynomial is irreducible, so the decision is delegated to <see cref="BinaryField{T}"/>
    /// rather than derived a second time here.
    /// </remarks>
    public bool IsIrreducible() {
        if ((1 > Degree) || (0UL == (Bits & 1UL))) { return false; }

        return BinaryField<ulong>.FromModulus(modulus: this).IsIrreducible();
    }
    /// <summary>Returns the exact product of this polynomial and <paramref name="other"/>.</summary>
    /// <param name="other">The second factor.</param>
    /// <returns>The exact product over the wide carrier, which always holds it.</returns>
    public BinaryPolynomialWide MultiplyWide(BinaryPolynomial other) {
        var product = BinaryFieldKernels.CarrylessMultiply64(left: Bits, right: other.Bits);

        return new BinaryPolynomialWide(bits: ((((UInt128)product.High) << 64) | product.Low));
    }
    /// <summary>Returns the conventional written form of this polynomial, such as <c>t^5+t^2+1</c>.</summary>
    /// <returns>The terms in descending exponent order, or <c>0</c> for the zero polynomial.</returns>
    /// <remarks>The form is diagnostic; no parsing round trip is claimed.</remarks>
    public override string ToString() {
        if (IsZero) { return "0"; }

        var builder = new StringBuilder();

        for (var exponent = Degree; (0 <= exponent); --exponent) {
            if (0UL == ((Bits >>> exponent) & 1UL)) { continue; }
            if (0 < builder.Length) { builder.Append(value: '+'); }

            if (0 == exponent) {
                builder.Append(value: '1');
            } else if (1 == exponent) {
                builder.Append(value: 't');
            } else {
                builder.Append(value: "t^").Append(value: exponent.ToString(provider: CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
