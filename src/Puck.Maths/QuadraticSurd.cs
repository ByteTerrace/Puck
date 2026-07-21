using System.Numerics;

namespace Puck.Maths;

/// <summary>An exact real number <c>(a + b·√d) / c</c> in a real quadratic field.</summary>
/// <remarks>
/// The denominator is normalized positive and common integer factors are removed. A square radicand is collapsed to a
/// rational value. Arithmetic is exact; operations between irrational values require the same radicand.
/// </remarks>
public readonly struct QuadraticSurd : IComparable<QuadraticSurd>, IEquatable<QuadraticSurd> {
    private readonly BigInteger m_denominator;

    private QuadraticSurd(BigInteger rationalNumerator, BigInteger surdNumerator, BigInteger radicand, BigInteger denominator) {
        RationalNumerator = rationalNumerator;
        SurdNumerator = surdNumerator;
        Radicand = radicand;
        m_denominator = denominator;
    }

    /// <summary>Gets <c>a</c>, the rational numerator.</summary>
    public BigInteger RationalNumerator { get; }
    /// <summary>Gets <c>b</c>, the coefficient of the square root.</summary>
    public BigInteger SurdNumerator { get; }
    /// <summary>Gets the non-negative radicand <c>d</c>; zero denotes a rational value.</summary>
    public BigInteger Radicand { get; }
    /// <summary>Gets the positive common denominator <c>c</c>.</summary>
    public BigInteger Denominator => m_denominator.IsZero ? BigInteger.One : m_denominator;
    /// <summary>Gets whether the value is rational.</summary>
    public bool IsRational => (SurdNumerator == BigInteger.Zero);
    /// <summary>Gets the additive identity.</summary>
    public static QuadraticSurd Zero => Rational(value: BigInteger.Zero);
    /// <summary>Gets the multiplicative identity.</summary>
    public static QuadraticSurd One => Rational(value: BigInteger.One);
    /// <summary>Gets the exact sign of the represented real number.</summary>
    public int Sign {
        get {
            if (SurdNumerator.IsZero) { return RationalNumerator.Sign; }
            if ((RationalNumerator.Sign >= 0) && (SurdNumerator.Sign >= 0)) { return 1; }
            if ((RationalNumerator.Sign <= 0) && (SurdNumerator.Sign <= 0)) { return -1; }

            var rationalSquare = (RationalNumerator * RationalNumerator);
            var surdSquare = ((SurdNumerator * SurdNumerator) * Radicand);
            var comparison = rationalSquare.CompareTo(surdSquare);

            return (RationalNumerator.Sign > 0) ? comparison : -comparison;
        }
    }

    /// <summary>Creates and normalizes <c>(a + b·√d) / c</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radicand"/> is negative.</exception>
    /// <exception cref="DivideByZeroException"><paramref name="denominator"/> is zero.</exception>
    public static QuadraticSurd Create(
        BigInteger rationalNumerator,
        BigInteger surdNumerator,
        BigInteger radicand,
        BigInteger denominator) {
        if (radicand.Sign < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(radicand),
                message: "the radicand must be non-negative"
            );
        }
        if (denominator.IsZero) { throw new DivideByZeroException(); }

        if (denominator.Sign < 0) {
            rationalNumerator = -rationalNumerator;
            surdNumerator = -surdNumerator;
            denominator = -denominator;
        }

        if (!surdNumerator.IsZero) {
            var root = BigIntegerMath.SquareRoot(value: radicand);

            if ((root * root) == radicand) {
                rationalNumerator += (surdNumerator * root);
                surdNumerator = BigInteger.Zero;
                radicand = BigInteger.Zero;
            }
        } else {
            radicand = BigInteger.Zero;
        }

        var divisor = BigInteger.GreatestCommonDivisor(
            BigInteger.GreatestCommonDivisor(BigInteger.Abs(rationalNumerator), BigInteger.Abs(surdNumerator)),
            denominator
        );

        return new QuadraticSurd(
            rationalNumerator: (rationalNumerator / divisor),
            surdNumerator: (surdNumerator / divisor),
            radicand: radicand,
            denominator: (denominator / divisor)
        );
    }

    /// <summary>Creates an exact integer.</summary>
    public static QuadraticSurd Rational(BigInteger value) =>
        new(rationalNumerator: value, surdNumerator: BigInteger.Zero, radicand: BigInteger.Zero, denominator: BigInteger.One);

    /// <summary>Creates an exact rational number.</summary>
    public static QuadraticSurd Rational(BigInteger numerator, BigInteger denominator) =>
        Create(rationalNumerator: numerator, surdNumerator: BigInteger.Zero, radicand: BigInteger.Zero, denominator: denominator);

    /// <summary>Returns the greatest integer no larger than this value.</summary>
    public BigInteger Floor() {
        if (SurdNumerator.IsZero) {
            return BigIntegerMath.FloorDivide(numerator: RationalNumerator, denominator: Denominator);
        }

        var rootRadicand = ((SurdNumerator * SurdNumerator) * Radicand);
        var rootFloor = BigIntegerMath.SquareRoot(value: rootRadicand);
        BigInteger lowerNumerator;

        if (SurdNumerator.Sign > 0) {
            lowerNumerator = (RationalNumerator + rootFloor);
        } else {
            var rootCeiling = (((rootFloor * rootFloor) == rootRadicand) ? rootFloor : (rootFloor + 1));
            lowerNumerator = (RationalNumerator - rootCeiling);
        }

        var candidate = BigIntegerMath.FloorDivide(numerator: lowerNumerator, denominator: Denominator);
        var threshold = (((candidate + 1) * Denominator) - RationalNumerator);
        bool reachesNext;

        if (SurdNumerator.Sign > 0) {
            reachesNext = ((threshold <= 0) || (rootRadicand >= (threshold * threshold)));
        } else {
            var positiveThreshold = -threshold;
            reachesNext = ((positiveThreshold >= 0) && (rootRadicand <= (positiveThreshold * positiveThreshold)));
        }

        return reachesNext ? (candidate + 1) : candidate;
    }

    /// <summary>Returns the least integer no smaller than this value.</summary>
    public BigInteger Ceiling() => -(-this).Floor();

    /// <summary>Returns the absolute value.</summary>
    public QuadraticSurd Abs() => (Sign < 0) ? -this : this;

    /// <summary>Returns a binary64 approximation; exact arithmetic does not use this conversion.</summary>
    public double ToDouble() =>
        (((double)RationalNumerator + ((double)SurdNumerator * Math.Sqrt((double)Radicand))) / (double)Denominator);

    /// <inheritdoc />
    public int CompareTo(QuadraticSurd other) => (this - other).Sign;

    /// <inheritdoc />
    public bool Equals(QuadraticSurd other) =>
        (RationalNumerator == other.RationalNumerator) &&
        (SurdNumerator == other.SurdNumerator) &&
        (Radicand == other.Radicand) &&
        (Denominator == other.Denominator);

    /// <inheritdoc />
    public override bool Equals(object? obj) => (obj is QuadraticSurd other) && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(RationalNumerator, SurdNumerator, Radicand, Denominator);

    /// <inheritdoc />
    public override string ToString() => IsRational
        ? ((Denominator == BigInteger.One) ? RationalNumerator.ToString() : $"{RationalNumerator}/{Denominator}")
        : $"({RationalNumerator} + {SurdNumerator}·√{Radicand})/{Denominator}";

    /// <summary>Adds two values in the same real quadratic field.</summary>
    public static QuadraticSurd operator +(QuadraticSurd left, QuadraticSurd right) {
        var radicand = CommonRadicand(left: left, right: right);

        return Create(
            rationalNumerator: ((left.RationalNumerator * right.Denominator) + (right.RationalNumerator * left.Denominator)),
            surdNumerator: ((left.SurdNumerator * right.Denominator) + (right.SurdNumerator * left.Denominator)),
            radicand: radicand,
            denominator: (left.Denominator * right.Denominator)
        );
    }

    /// <summary>Subtracts two values in the same real quadratic field.</summary>
    public static QuadraticSurd operator -(QuadraticSurd left, QuadraticSurd right) => (left + -right);
    /// <summary>Negates a value.</summary>
    public static QuadraticSurd operator -(QuadraticSurd value) =>
        Create(-value.RationalNumerator, -value.SurdNumerator, value.Radicand, value.Denominator);

    /// <summary>Multiplies two values in the same real quadratic field.</summary>
    public static QuadraticSurd operator *(QuadraticSurd left, QuadraticSurd right) {
        var radicand = CommonRadicand(left: left, right: right);

        return Create(
            rationalNumerator: ((left.RationalNumerator * right.RationalNumerator) +
                (left.SurdNumerator * right.SurdNumerator * radicand)),
            surdNumerator: ((left.RationalNumerator * right.SurdNumerator) +
                (left.SurdNumerator * right.RationalNumerator)),
            radicand: radicand,
            denominator: (left.Denominator * right.Denominator)
        );
    }

    /// <summary>Divides two values in the same real quadratic field.</summary>
    public static QuadraticSurd operator /(QuadraticSurd left, QuadraticSurd right) {
        if (right.Sign == 0) { throw new DivideByZeroException(); }

        var radicand = CommonRadicand(left: left, right: right);
        var norm = ((right.RationalNumerator * right.RationalNumerator) -
            (right.SurdNumerator * right.SurdNumerator * radicand));

        return Create(
            rationalNumerator: (right.Denominator * ((left.RationalNumerator * right.RationalNumerator) -
                (left.SurdNumerator * right.SurdNumerator * radicand))),
            surdNumerator: (right.Denominator * ((left.SurdNumerator * right.RationalNumerator) -
                (left.RationalNumerator * right.SurdNumerator))),
            radicand: radicand,
            denominator: (left.Denominator * norm)
        );
    }

    /// <summary>Tests exact ordering.</summary>
    public static bool operator <(QuadraticSurd left, QuadraticSurd right) => (left.CompareTo(right) < 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator >(QuadraticSurd left, QuadraticSurd right) => (left.CompareTo(right) > 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator <=(QuadraticSurd left, QuadraticSurd right) => (left.CompareTo(right) <= 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator >=(QuadraticSurd left, QuadraticSurd right) => (left.CompareTo(right) >= 0);
    /// <summary>Tests exact equality.</summary>
    public static bool operator ==(QuadraticSurd left, QuadraticSurd right) => left.Equals(right);
    /// <summary>Tests exact inequality.</summary>
    public static bool operator !=(QuadraticSurd left, QuadraticSurd right) => !left.Equals(right);

    private static BigInteger CommonRadicand(QuadraticSurd left, QuadraticSurd right) {
        if (left.IsRational) { return right.Radicand; }
        if (right.IsRational || (left.Radicand == right.Radicand)) { return left.Radicand; }

        throw new ArgumentException(message: "quadratic-surd operands must belong to the same field");
    }
}
