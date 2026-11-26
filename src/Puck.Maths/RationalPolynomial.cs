using System.Numerics;
using System.Text;

namespace Puck.Maths;

/// <summary>
/// A polynomial in one variable with exact <see cref="Rational"/> coefficients, held constant term first and trimmed of
/// trailing zeros, so the zero polynomial holds none and reports degree <c>-1</c>. Every operation is exact: an
/// evaluation, a derivative, an antiderivative and a composition give the rational the algebra defines, never a rounded
/// one, so a value built from them narrows once, where its consumer rounds it.
/// </summary>
/// <remarks>
/// <para>Composition is closed: a polynomial of a polynomial is a polynomial of the product degree, so a quantity that
/// is a polynomial in another polynomial quantity (a clock whose value is a polynomial in a parent clock's phase, that
/// parent's phase a polynomial in time) is one polynomial in the innermost variable, evaluated in one pass.</para>
/// <para>The static span members are the coefficient kernels every exact polynomial in this library shares (the
/// curvature spline's Sturm sequences among them), for callers that hold a coefficient array of their own.</para>
/// </remarks>
public sealed class RationalPolynomial : IEquatable<RationalPolynomial> {
    private readonly Rational[] m_coefficients;

    private RationalPolynomial(Rational[] trimmed) {
        m_coefficients = trimmed;
    }

    /// <summary>Initializes a new instance of the <see cref="RationalPolynomial"/> class from its coefficients.</summary>
    /// <param name="coefficients">The coefficients, constant term first; trailing zeros are dropped.</param>
    public RationalPolynomial(ReadOnlySpan<Rational> coefficients) : this(trimmed: coefficients[..(DegreeOf(coefficients: coefficients) + 1)].ToArray()) { }

    /// <summary>Gets the zero polynomial, which holds no coefficient.</summary>
    public static RationalPolynomial Zero { get; } = new(trimmed: []);
    /// <summary>Gets the constant polynomial one.</summary>
    public static RationalPolynomial One { get; } = new(trimmed: [Rational.One]);
    /// <summary>Gets the identity polynomial <c>x</c>.</summary>
    public static RationalPolynomial Identity { get; } = new(trimmed: [Rational.Zero, Rational.One]);

    /// <summary>Gets the coefficients, constant term first, with no trailing zero.</summary>
    public ReadOnlySpan<Rational> Coefficients => m_coefficients;
    /// <summary>Gets the degree: the highest power with a nonzero coefficient, or <c>-1</c> for the zero
    /// polynomial.</summary>
    public int Degree => (m_coefficients.Length - 1);
    /// <summary>Gets whether this is the zero polynomial.</summary>
    public bool IsZero => (m_coefficients.Length == 0);

    /// <summary>Gets the coefficient of a power, zero past the degree.</summary>
    /// <param name="power">The power, zero or greater.</param>
    /// <returns>The coefficient.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="power"/> is negative.</exception>
    public Rational this[int power] {
        get {
            ArgumentOutOfRangeException.ThrowIfNegative(value: power);

            return ((power < m_coefficients.Length)
                ? m_coefficients[power]
                : Rational.Zero
            );
        }
    }

    /// <summary>Returns the constant polynomial of a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The constant polynomial; the zero polynomial for zero.</returns>
    public static RationalPolynomial Constant(Rational value) => (value.IsZero
        ? Zero
        : new(trimmed: [value])
    );
    /// <summary>Returns the degree of a coefficient span: its highest power with a nonzero coefficient, or <c>-1</c> when
    /// every coefficient is zero.</summary>
    /// <param name="coefficients">The coefficients, constant term first.</param>
    /// <returns>The degree.</returns>
    public static int DegreeOf(ReadOnlySpan<Rational> coefficients) {
        for (var index = (coefficients.Length - 1); (index >= 0); --index) {
            if (!coefficients[index].IsZero) { return index; }
        }

        return -1;
    }
    /// <summary>Returns the derivative of a coefficient span, by the power rule.</summary>
    /// <param name="coefficients">The coefficients, constant term first.</param>
    /// <returns>The derivative's coefficients, one fewer than the span's degree plus one; none for a span of degree zero
    /// or less.</returns>
    public static Rational[] DerivativeOf(ReadOnlySpan<Rational> coefficients) {
        var degree = DegreeOf(coefficients: coefficients);

        if (degree <= 0) { return []; }

        var result = new Rational[degree];

        for (var power = 1; (power <= degree); ++power) {
            result[(power - 1)] = (coefficients[power] * ((BigInteger)power));
        }

        return result;
    }
    /// <summary>Evaluates a coefficient span at a point by Horner's rule, exactly.</summary>
    /// <param name="coefficients">The coefficients, constant term first.</param>
    /// <param name="x">The point.</param>
    /// <returns>The exact value; zero for an empty span.</returns>
    public static Rational Evaluate(ReadOnlySpan<Rational> coefficients, Rational x) {
        var result = Rational.Zero;

        for (var index = (coefficients.Length - 1); (index >= 0); --index) {
            result = ((result * x) + coefficients[index]);
        }

        return result;
    }
    /// <summary>Returns the remainder of the Euclidean division of one coefficient span by another.</summary>
    /// <param name="dividend">The dividend's coefficients, constant term first.</param>
    /// <param name="divisor">The divisor's coefficients, constant term first; not the zero polynomial.</param>
    /// <returns>The remainder's coefficients, as many as the dividend holds, every power at or above the divisor's
    /// degree zero.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is the zero polynomial.</exception>
    public static Rational[] RemainderOf(ReadOnlySpan<Rational> dividend, ReadOnlySpan<Rational> divisor) {
        var divisorDegree = DegreeOf(coefficients: divisor);

        if (divisorDegree < 0) {
            throw new DivideByZeroException(message: "A polynomial has no remainder on division by the zero polynomial.");
        }

        var leading = divisor[divisorDegree];
        var remainder = dividend.ToArray();

        while (true) {
            var remainderDegree = DegreeOf(coefficients: remainder);

            if (remainderDegree < divisorDegree) { break; }

            var scale = (remainder[remainderDegree] / leading);
            var shift = (remainderDegree - divisorDegree);

            for (var power = 0; (power <= divisorDegree); ++power) {
                remainder[(shift + power)] = (remainder[(shift + power)] - (scale * divisor[power]));
            }
        }

        return remainder;
    }

    /// <summary>Returns the antiderivative whose value at zero is zero: each coefficient divided by its new power.</summary>
    /// <returns>The antiderivative, of degree one more than this polynomial's; the zero polynomial for zero.</returns>
    public RationalPolynomial Antiderivative() {
        if (IsZero) { return Zero; }

        var result = new Rational[(m_coefficients.Length + 1)];

        for (var power = 0; (power < m_coefficients.Length); ++power) {
            result[(power + 1)] = (m_coefficients[power] / ((BigInteger)(power + 1)));
        }

        return new(trimmed: result);
    }
    /// <summary>Returns this polynomial of another: <c>this(inner(x))</c>, by Horner's rule over polynomials.</summary>
    /// <param name="inner">The inner polynomial.</param>
    /// <returns>The composition, of degree the product of the two degrees (zero when either is a constant).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    public RationalPolynomial Compose(RationalPolynomial inner) {
        ArgumentNullException.ThrowIfNull(argument: inner);

        var result = Zero;

        for (var index = (m_coefficients.Length - 1); (index >= 0); --index) {
            result = ((result * inner) + Constant(value: m_coefficients[index]));
        }

        return result;
    }
    /// <summary>Returns the derivative, by the power rule.</summary>
    /// <returns>The derivative; the zero polynomial for a constant.</returns>
    public RationalPolynomial Derivative() => new(trimmed: DerivativeOf(coefficients: m_coefficients));
    /// <summary>Evaluates the polynomial at a point by Horner's rule, exactly.</summary>
    /// <param name="x">The point.</param>
    /// <returns>The exact value.</returns>
    public Rational Evaluate(Rational x) => Evaluate(
        coefficients: m_coefficients,
        x: x
    );
    /// <summary>Returns the polynomial with its variable moved by an offset: <c>this(x + offset)</c>, the Taylor shift
    /// that re-centres a polynomial on another origin.</summary>
    /// <param name="offset">The offset.</param>
    /// <returns>The shifted polynomial, of the same degree.</returns>
    public RationalPolynomial Shift(Rational offset) => (offset.IsZero
        ? this
        : Compose(inner: new(trimmed: [offset, Rational.One]))
    );

    /// <inheritdoc />
    public bool Equals(RationalPolynomial? other) => ((other is not null) && m_coefficients.AsSpan().SequenceEqual(other: other.m_coefficients));
    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(other: (obj as RationalPolynomial));
    /// <inheritdoc />
    public override int GetHashCode() {
        var hash = new HashCode();

        foreach (var coefficient in m_coefficients) {
            hash.Add(value: coefficient);
        }

        return hash.ToHashCode();
    }
    /// <inheritdoc />
    /// <remarks>The coefficients print constant term first, each as <see cref="Rational.ToString"/> writes it, between
    /// brackets: <c>[1, 0, -1/2]</c> is <c>1 - x²/2</c>.</remarks>
    public override string ToString() {
        var text = new StringBuilder(value: "[");

        for (var power = 0; (power < m_coefficients.Length); ++power) {
            if (power > 0) { _ = text.Append(value: ", "); }

            _ = text.Append(value: m_coefficients[power].ToString());
        }

        return text.Append(value: ']').ToString();
    }

    /// <summary>Tests two polynomials for equality by their coefficients.</summary>
    /// <param name="left">The first polynomial, or <see langword="null"/>.</param>
    /// <param name="right">The second polynomial, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when both are <see langword="null"/> or hold the same coefficients.</returns>
    public static bool operator ==(RationalPolynomial? left, RationalPolynomial? right) => (left?.Equals(other: right) ?? (right is null));
    /// <summary>Tests two polynomials for inequality by their coefficients.</summary>
    /// <param name="left">The first polynomial, or <see langword="null"/>.</param>
    /// <param name="right">The second polynomial, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when exactly one is <see langword="null"/> or their coefficients differ.</returns>
    public static bool operator !=(RationalPolynomial? left, RationalPolynomial? right) => !(left == right);
    /// <summary>Adds two polynomials.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum.</returns>
    public static RationalPolynomial operator +(RationalPolynomial left, RationalPolynomial right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        var result = new Rational[Math.Max(val1: left.m_coefficients.Length, val2: right.m_coefficients.Length)];

        for (var power = 0; (power < result.Length); ++power) {
            result[power] = (left[power] + right[power]);
        }

        return new(coefficients: result);
    }
    /// <summary>Subtracts one polynomial from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference.</returns>
    public static RationalPolynomial operator -(RationalPolynomial left, RationalPolynomial right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        var result = new Rational[Math.Max(val1: left.m_coefficients.Length, val2: right.m_coefficients.Length)];

        for (var power = 0; (power < result.Length); ++power) {
            result[power] = (left[power] - right[power]);
        }

        return new(coefficients: result);
    }
    /// <summary>Negates a polynomial.</summary>
    /// <param name="value">The polynomial.</param>
    /// <returns>The negation.</returns>
    public static RationalPolynomial operator -(RationalPolynomial value) {
        ArgumentNullException.ThrowIfNull(argument: value);

        var result = new Rational[value.m_coefficients.Length];

        for (var power = 0; (power < result.Length); ++power) {
            result[power] = -value.m_coefficients[power];
        }

        return new(trimmed: result);
    }
    /// <summary>Multiplies two polynomials.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <returns>The product, of degree the sum of the two degrees.</returns>
    public static RationalPolynomial operator *(RationalPolynomial left, RationalPolynomial right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        if (left.IsZero || right.IsZero) { return Zero; }

        var result = new Rational[(left.m_coefficients.Length + right.m_coefficients.Length - 1)];

        for (var i = 0; (i < left.m_coefficients.Length); ++i) {
            for (var j = 0; (j < right.m_coefficients.Length); ++j) {
                result[(i + j)] = (result[(i + j)] + (left.m_coefficients[i] * right.m_coefficients[j]));
            }
        }

        return new(trimmed: result);
    }
    /// <summary>Scales a polynomial by a rational.</summary>
    /// <param name="scale">The scale.</param>
    /// <param name="value">The polynomial.</param>
    /// <returns>The scaled polynomial; the zero polynomial for a zero scale.</returns>
    public static RationalPolynomial operator *(Rational scale, RationalPolynomial value) {
        ArgumentNullException.ThrowIfNull(argument: value);

        if (scale.IsZero) { return Zero; }

        var result = new Rational[value.m_coefficients.Length];

        for (var power = 0; (power < result.Length); ++power) {
            result[power] = (scale * value.m_coefficients[power]);
        }

        return new(trimmed: result);
    }
}
