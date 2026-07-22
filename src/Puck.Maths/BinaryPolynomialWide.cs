using System.Globalization;
using System.Numerics;
using System.Text;

namespace Puck.Maths;

/// <summary>
/// A polynomial over the two-element field packed into a <see cref="UInt128"/>: bit <c>i</c> is the coefficient of
/// <c>t^i</c>. It is the exact carrier for the product of two <see cref="BinaryPolynomial"/> values and the input to
/// field reduction.
/// </summary>
/// <remarks>
/// The type deliberately has no multiplication operator: the exact product of two degree-127 polynomials needs 255
/// bits, so no closed multiplication on this carrier could be honest. Reduce into a <see cref="BinaryField{T}"/>
/// instead.
/// </remarks>
public readonly record struct BinaryPolynomialWide :
    IAdditionOperators<BinaryPolynomialWide, BinaryPolynomialWide, BinaryPolynomialWide>,
    IAdditiveIdentity<BinaryPolynomialWide, BinaryPolynomialWide>,
    IShiftOperators<BinaryPolynomialWide, int, BinaryPolynomialWide>,
    ISubtractionOperators<BinaryPolynomialWide, BinaryPolynomialWide, BinaryPolynomialWide>,
    IUnaryNegationOperators<BinaryPolynomialWide, BinaryPolynomialWide> {
    /// <summary>The largest exponent the packed carrier can hold.</summary>
    private const int MaximumDegree = 127;

    /// <summary>Creates a polynomial from its packed coefficients.</summary>
    /// <param name="bits">Bit <c>i</c> is the coefficient of <c>t^i</c>.</param>
    public BinaryPolynomialWide(UInt128 bits) => Bits = bits;

    /// <summary>Gets the zero polynomial, which is the identity for addition.</summary>
    public static BinaryPolynomialWide AdditiveIdentity => default;
    /// <summary>Gets the zero polynomial.</summary>
    public static BinaryPolynomialWide Zero => default;

    /// <summary>Gets the packed coefficients.</summary>
    public UInt128 Bits { get; }
    /// <summary>Gets the largest exponent carrying a non-zero coefficient, or minus one for the zero polynomial.</summary>
    public int Degree => (MaximumDegree - ((int)UInt128.LeadingZeroCount(value: Bits)));
    /// <summary>Gets the coefficients of exponents 64 through 127, shifted down so that <c>t^64</c> becomes the constant term.</summary>
    public BinaryPolynomial High => new(bits: ((ulong)(Bits >>> 64)));
    /// <summary>Gets whether this is the zero polynomial.</summary>
    public bool IsZero => (UInt128.Zero == Bits);
    /// <summary>Gets the coefficients of exponents 0 through 63.</summary>
    public BinaryPolynomial Low => new(bits: ((ulong)Bits));

    /// <summary>Adds two binary polynomials; subtraction is the same operation.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum, which is the coefficient-wise exclusive or.</returns>
    public static BinaryPolynomialWide operator +(BinaryPolynomialWide left, BinaryPolynomialWide right) =>
        new(bits: (left.Bits ^ right.Bits));
    /// <summary>Subtracts one binary polynomial from another; addition is the same operation.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference, which is the coefficient-wise exclusive or.</returns>
    public static BinaryPolynomialWide operator -(BinaryPolynomialWide left, BinaryPolynomialWide right) =>
        new(bits: (left.Bits ^ right.Bits));
    /// <summary>Negates a binary polynomial, which in characteristic two returns it unchanged.</summary>
    /// <param name="value">The polynomial to negate.</param>
    /// <returns><paramref name="value"/>, because every coefficient is its own additive inverse.</returns>
    public static BinaryPolynomialWide operator -(BinaryPolynomialWide value) =>
        value;
    /// <summary>Multiplies by <c>t^<paramref name="count"/></c>, discarding any coefficient that would land above degree 127.</summary>
    /// <param name="value">The polynomial to shift up.</param>
    /// <param name="count">The number of exponents to shift by.</param>
    /// <returns>The product with <c>t^<paramref name="count"/></c>, truncated to the packed carrier.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static BinaryPolynomialWide operator <<(BinaryPolynomialWide value, int count) {
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
    public static BinaryPolynomialWide operator >>(BinaryPolynomialWide value, int count) =>
        (value >>> count);
    /// <summary>Divides by <c>t^<paramref name="count"/></c>, discarding the remainder.</summary>
    /// <param name="value">The polynomial to shift down.</param>
    /// <param name="count">The number of exponents to shift by.</param>
    /// <returns>The quotient by <c>t^<paramref name="count"/></c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static BinaryPolynomialWide operator >>>(BinaryPolynomialWide value, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);

        if (MaximumDegree < count) { return Zero; }

        return new(bits: (value.Bits >>> count));
    }
    /// <summary>Narrows to the packed 64-bit carrier, discarding every coefficient above degree 63.</summary>
    /// <param name="value">The polynomial to narrow.</param>
    /// <returns>The coefficients of exponents 0 through 63.</returns>
    public static explicit operator BinaryPolynomial(BinaryPolynomialWide value) =>
        value.Low;
    /// <summary>Narrows to the packed 64-bit carrier, reporting any coefficient above degree 63.</summary>
    /// <param name="value">The polynomial to narrow.</param>
    /// <returns>The coefficients of exponents 0 through 63.</returns>
    /// <exception cref="OverflowException"><paramref name="value"/> has a non-zero coefficient above degree 63.</exception>
    public static explicit operator checked BinaryPolynomial(BinaryPolynomialWide value) {
        if (!value.High.IsZero) { throw new OverflowException("The binary polynomial exceeds degree 63."); }

        return value.Low;
    }

    /// <summary>Returns this polynomial modulo <paramref name="divisor"/>.</summary>
    /// <param name="divisor">The polynomial to reduce by.</param>
    /// <returns>The remainder, whose degree is below <paramref name="divisor"/>'s and therefore fits the narrow carrier.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is zero.</exception>
    /// <remarks>
    /// Euclidean division against a narrow divisor is a named method rather than an operator precisely so that the
    /// asymmetry between the two carriers stays visible at the call site.
    /// </remarks>
    public BinaryPolynomial Remainder(BinaryPolynomial divisor) {
        if (divisor.IsZero) { throw new DivideByZeroException(); }

        var divisorBits = ((UInt128)divisor.Bits);
        var divisorDegree = divisor.Degree;
        var remainder = Bits;
        var remainderDegree = Degree;

        while (remainderDegree >= divisorDegree) {
            remainder ^= (divisorBits << (remainderDegree - divisorDegree));
            remainderDegree = (MaximumDegree - ((int)UInt128.LeadingZeroCount(value: remainder)));
        }

        return new BinaryPolynomial(bits: ((ulong)remainder));
    }
    /// <summary>Returns the conventional written form of this polynomial, such as <c>t^5+t^2+1</c>.</summary>
    /// <returns>The terms in descending exponent order, or <c>0</c> for the zero polynomial.</returns>
    /// <remarks>The form is diagnostic; no parsing round trip is claimed.</remarks>
    public override string ToString() {
        if (IsZero) { return "0"; }

        var builder = new StringBuilder();

        for (var exponent = Degree; (0 <= exponent); --exponent) {
            if (UInt128.Zero == ((Bits >>> exponent) & UInt128.One)) { continue; }
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
