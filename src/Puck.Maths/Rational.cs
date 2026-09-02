using System.Globalization;
using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// An exact rational over <see cref="BigInteger"/>, always reduced to lowest terms with a positive denominator — the
/// number the fixed-point wing rounds <em>from</em>. Every <see cref="BigInteger"/>-exact authoring and compile-time
/// derivation in this library, and <c>Puck.Physics</c>'s soft-constraint chain, forms its intermediates here and
/// narrows once, at the end, through <see cref="FixedPointRounding.TryRoundRational"/>.
/// </summary>
/// <remarks>
/// Reduction on construction is what makes the record's own equality a valid rational equality and keeps a long
/// chain of operations from growing its denominators multiplicatively; the one GCD it costs per operation is far
/// below the multiplications an unreduced chain would spend on its own growth. The denominator is always nonzero:
/// the constructor refuses an explicit zero, and <c>/</c> refuses a zero divisor rather than manufacturing one.
/// <see langword="default"/> reads back as the canonical zero <c>0/1</c>.
/// </remarks>
public readonly record struct Rational : IComparable<Rational> {
    // The reduced, positive denominator, or zero for a zero-initialized default(Rational) — which never ran the
    // constructor and so could not be validated. The getter turns that unvalidated zero into the canonical 1, so the
    // invariant lives in the read rather than in every possible zero-fill; equality and hashing read through the
    // getter too, so the default and a constructed 0/1 are one value.
    private readonly BigInteger m_denominatorOrDefaultZero;

    /// <summary>Constructs an exact rational, reduced to lowest terms with a positive denominator.</summary>
    /// <param name="Numerator">The numerator.</param>
    /// <param name="Denominator">The denominator, which must be nonzero.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="Denominator"/> is zero.</exception>
    public Rational(BigInteger Numerator, BigInteger Denominator) {
        if (Denominator.IsZero) {
            throw new ArgumentOutOfRangeException(paramName: nameof(Denominator), message: "A Rational's denominator must be nonzero.");
        }

        if (Denominator.Sign < 0) {
            Numerator = -Numerator;
            Denominator = -Denominator;
        }

        if (Numerator.IsZero) {
            this.Numerator = BigInteger.Zero;
            m_denominatorOrDefaultZero = BigInteger.One;

            return;
        }

        var divisor = BigInteger.GreatestCommonDivisor(
            left: BigInteger.Abs(value: Numerator),
            right: Denominator
        );

        this.Numerator = (divisor.IsOne ? Numerator : (Numerator / divisor));
        m_denominatorOrDefaultZero = (divisor.IsOne ? Denominator : (Denominator / divisor));
    }

    /// <summary>Gets the reduced numerator; its sign is the value's sign.</summary>
    public BigInteger Numerator { get; }
    /// <summary>Gets the reduced, positive denominator. <see langword="default"/> reads back <c>1</c> here rather than
    /// the zero its zero-initialized storage holds, so the all-zero default is the canonical <c>0/1</c>.</summary>
    public BigInteger Denominator => (m_denominatorOrDefaultZero.IsZero ? BigInteger.One : m_denominatorOrDefaultZero);
    /// <summary>Gets whether the value is a whole number.</summary>
    public bool IsInteger => Denominator.IsOne;
    /// <summary>Gets whether the value is zero.</summary>
    public bool IsZero => Numerator.IsZero;
    /// <summary>Gets the exact sign: <c>-1</c>, <c>0</c> or <c>1</c>.</summary>
    public int Sign => Numerator.Sign;

    /// <summary>Gets the rational <c>1</c>.</summary>
    public static Rational One { get; } = new(Numerator: BigInteger.One, Denominator: BigInteger.One);
    /// <summary>Gets the rational <c>2</c>.</summary>
    public static Rational Two { get; } = new(Numerator: (2 * BigInteger.One), Denominator: BigInteger.One);
    /// <summary>Gets the rational <c>0</c>.</summary>
    public static Rational Zero => default;

    /// <summary>Tests exact equality of two reduced rationals.</summary>
    public bool Equals(Rational other) => ((Numerator == other.Numerator) && (Denominator == other.Denominator));
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(value1: Numerator, value2: Denominator);
    /// <summary>Returns the absolute value.</summary>
    public Rational Abs() => ((Numerator.Sign < 0) ? -this : this);
    /// <summary>Returns the least integer no smaller than this value.</summary>
    public BigInteger Ceiling() => -(-this).Floor();
    /// <inheritdoc />
    public int CompareTo(Rational other) =>
        (Numerator * other.Denominator).CompareTo(other: (other.Numerator * Denominator));
    /// <summary>Returns the greatest integer no larger than this value.</summary>
    public BigInteger Floor() => Numerator.FloorDivide(divisor: Denominator);
    /// <summary>Returns the multiplicative inverse.</summary>
    /// <exception cref="DivideByZeroException">The value is zero.</exception>
    public Rational Reciprocal() {
        if (Numerator.IsZero) { throw new DivideByZeroException(message: "The rational zero has no reciprocal."); }

        return new(Numerator: Denominator, Denominator: Numerator);
    }
    /// <summary>Returns the nearest <see cref="double"/>, ties to even: the magnitude is truncated at a binary scale
    /// wide enough to leave more than sixty bits, and that one integer plus the truncation's remainder round once
    /// through <see cref="BigIntegerFunctions.ToDouble(BigInteger, int, bool)"/>.</summary>
    public double ToDouble() {
        if (Numerator.IsZero) { return 0.0; }

        var magnitude = BigInteger.Abs(value: Numerator);
        var magnitudeBits = ((long)magnitude.GetBitLength() - (long)Denominator.GetBitLength());

        // Below 2^-1100 the value is under every subnormal's half, so the nearest double is zero at either sign.
        if (magnitudeBits < -1100L) { return ((Numerator.Sign < 0) ? -0.0 : 0.0); }

        var scale = DoubleScale(magnitudeBitLength: magnitudeBits);
        var quotient = BigInteger.DivRem(
            dividend: (magnitude << scale),
            divisor: Denominator,
            remainder: out var remainder
        );
        var result = BigIntegerFunctions.ToDouble(
            binaryExponent: -scale,
            hasRemainder: !remainder.IsZero,
            truncatedMagnitude: quotient
        );

        return ((Numerator.Sign < 0) ? -result : result);
    }
    /// <inheritdoc />
    /// <remarks>Both components are formatted against <see cref="CultureInfo.InvariantCulture"/>, so the text is the
    /// same on every host; a whole number prints without its denominator.</remarks>
    public override string ToString() => (IsInteger
        ? Numerator.ToString(provider: CultureInfo.InvariantCulture)
        : string.Create(provider: CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}"));

    // The left shift that lands a value of the given (possibly negative) magnitude bit length at about 2^72, so the
    // truncated integer carries more than the fifty-four bits the rounding needs below it; a value already wider than
    // that is truncated at scale zero.
    internal static int DoubleScale(long magnitudeBitLength) =>
        ((int)Math.Clamp(value: (72L - magnitudeBitLength), min: 0L, max: 4096L));

    /// <summary>Widens an integer to the rational with denominator one.</summary>
    public static implicit operator Rational(BigInteger value) => new(Numerator: value, Denominator: BigInteger.One);

    /// <summary>Adds two rationals.</summary>
    public static Rational operator +(Rational left, Rational right) => new(
        Numerator: ((left.Numerator * right.Denominator) + (right.Numerator * left.Denominator)),
        Denominator: (left.Denominator * right.Denominator)
    );
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/>.</summary>
    public static Rational operator -(Rational left, Rational right) => new(
        Numerator: ((left.Numerator * right.Denominator) - (right.Numerator * left.Denominator)),
        Denominator: (left.Denominator * right.Denominator)
    );
    /// <summary>Negates a rational.</summary>
    public static Rational operator -(Rational value) => new(
        Numerator: -value.Numerator,
        Denominator: value.Denominator
    );
    /// <summary>Multiplies two rationals.</summary>
    public static Rational operator *(Rational left, Rational right) => new(
        Numerator: (left.Numerator * right.Numerator),
        Denominator: (left.Denominator * right.Denominator)
    );
    /// <summary>Divides one rational by another.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor.</param>
    /// <returns>The exact quotient.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> is the rational zero.</exception>
    public static Rational operator /(Rational left, Rational right) {
        if (right.Numerator.IsZero) {
            throw new DivideByZeroException(message: "Cannot divide a Rational by the rational zero.");
        }

        return new(
            Numerator: (left.Numerator * right.Denominator),
            Denominator: (left.Denominator * right.Numerator)
        );
    }
    /// <summary>Tests exact ordering.</summary>
    public static bool operator <(Rational left, Rational right) => (left.CompareTo(other: right) < 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator >(Rational left, Rational right) => (left.CompareTo(other: right) > 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator <=(Rational left, Rational right) => (left.CompareTo(other: right) <= 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator >=(Rational left, Rational right) => (left.CompareTo(other: right) >= 0);
}
