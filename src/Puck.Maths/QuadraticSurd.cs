using System.Globalization;
using System.Numerics;

namespace Puck.Maths;

/// <summary>An exact real number <c>(a + b·√d) / c</c> in a real quadratic field.</summary>
/// <remarks>
/// The denominator is normalized positive and common integer factors are removed. A square radicand is collapsed to a
/// rational value. Arithmetic, equality, ordering, and hashing identify square-equivalent radicands without factoring
/// arbitrary-width integers; for example, <c>√8</c> and <c>2√2</c> interoperate exactly.
/// </remarks>
public readonly struct QuadraticSurd : IComparable<QuadraticSurd>, IEquatable<QuadraticSurd> {
    private readonly BigInteger m_denominator;

    private QuadraticSurd(BigInteger rationalNumerator, BigInteger surdNumerator, BigInteger radicand, BigInteger denominator) {
        RationalNumerator = rationalNumerator;
        SurdNumerator = surdNumerator;
        Radicand = radicand;
        m_denominator = denominator;
    }

    /// <summary>Gets the positive common denominator <c>c</c>.</summary>
    public BigInteger Denominator => (m_denominator.IsZero
        ? BigInteger.One
        : m_denominator
    );
    /// <summary>Gets whether the value is rational.</summary>
    public bool IsRational => (SurdNumerator == BigInteger.Zero);
    /// <summary>Gets the multiplicative identity.</summary>
    public static QuadraticSurd One => Rational(value: BigInteger.One);
    /// <summary>Gets the non-negative radicand <c>d</c>; zero denotes a rational value.</summary>
    public BigInteger Radicand { get; }
    /// <summary>Gets <c>a</c>, the rational numerator.</summary>
    public BigInteger RationalNumerator { get; }
    /// <summary>Gets the exact sign of the represented real number.</summary>
    public int Sign {
        get {
            if (SurdNumerator.IsZero) { return RationalNumerator.Sign; }
            if (
                (RationalNumerator.Sign >= 0) &&
                (SurdNumerator.Sign >= 0)
            ) { return 1; }
            if (
                (RationalNumerator.Sign <= 0) &&
                (SurdNumerator.Sign <= 0)
            ) { return -1; }

            var rationalSquare = (RationalNumerator * RationalNumerator);
            var surdSquare = ((SurdNumerator * SurdNumerator) * Radicand);
            var comparison = rationalSquare.CompareTo(other: surdSquare);

            return ((RationalNumerator.Sign > 0)
                ? comparison
                : -comparison
            );
        }
    }
    /// <summary>Gets <c>b</c>, the coefficient of the square root.</summary>
    public BigInteger SurdNumerator { get; }
    /// <summary>Gets the additive identity.</summary>
    public static QuadraticSurd Zero => Rational(value: BigInteger.Zero);

    internal static bool TryCommonRadicalParts(
        QuadraticSurd left,
        QuadraticSurd right,
        out (BigInteger Radicand, BigInteger LeftSurdNumerator, BigInteger RightSurdNumerator) result
    ) {
        if (left.IsRational) {
            result = (right.Radicand, BigInteger.Zero, right.SurdNumerator);
            return true;
        }
        if (right.IsRational) {
            result = (left.Radicand, left.SurdNumerator, BigInteger.Zero);
            return true;
        }
        if (left.Radicand == right.Radicand) {
            result = (left.Radicand, left.SurdNumerator, right.SurdNumerator);
            return true;
        }

        var commonRadicand = BigInteger.GreatestCommonDivisor(
            left: left.Radicand,
            right: right.Radicand
        );
        var leftScaleSquared = (left.Radicand / commonRadicand);
        var rightScaleSquared = (right.Radicand / commonRadicand);
        var leftScale = BigIntegerFunctions.SquareRoot(value: leftScaleSquared);
        var rightScale = BigIntegerFunctions.SquareRoot(value: rightScaleSquared);

        if (
            ((leftScale * leftScale) == leftScaleSquared) &&
            ((rightScale * rightScale) == rightScaleSquared)
        ) {
            result = (
                commonRadicand,
                (left.SurdNumerator * leftScale),
                (right.SurdNumerator * rightScale)
            );
            return true;
        }
        result = default;
        return false;
    }

    private static ScaledBinary Add(ScaledBinary left, ScaledBinary right) {
        if (left.Significand == 0.0) { return right; }
        if (right.Significand == 0.0) { return left; }
        if (left.Exponent < right.Exponent) { (left, right) = (right, left); }

        var difference = (left.Exponent - right.Exponent);

        if (difference > 1075L) { return left; }

        return Normalize(
            (left.Significand + Math.ScaleB(
                n: -((int)difference),
                x: right.Significand
            )),
            left.Exponent
        );
    }
    private static void AddRadicalBounds(
        ref BigInteger lower,
        ref BigInteger upper,
        BigInteger coefficient,
        BigInteger radicand,
        BigInteger scale
    ) {
        if (coefficient.IsZero) { return; }
        var floor = BigIntegerFunctions.SquareRoot(value: ((radicand * scale) * scale));
        var ceiling = (floor + BigInteger.One);

        if (coefficient.Sign > 0) {
            lower += (coefficient * floor);
            upper += (coefficient * ceiling);
        } else {
            lower += (coefficient * ceiling);
            upper += (coefficient * floor);
        }
    }
    private static int BiquadraticSign(
        BigInteger rational,
        BigInteger leftCoefficient,
        BigInteger leftRadicand,
        BigInteger rightCoefficient,
        BigInteger rightRadicand
    ) {
        var precision = 8;

        while (true) {
            var scale = (BigInteger.One << precision);
            var lower = (rational * scale);
            var upper = lower;

            AddRadicalBounds(
                coefficient: leftCoefficient,
                lower: ref lower,
                radicand: leftRadicand,
                scale: scale,
                upper: ref upper
            );
            AddRadicalBounds(
                coefficient: rightCoefficient,
                lower: ref lower,
                radicand: rightRadicand,
                scale: scale,
                upper: ref upper
            );
            if (lower.Sign > 0) { return 1; }
            if (upper.Sign < 0) { return -1; }
            precision = checked((precision * 2));
        }
    }
    private static (BigInteger Radicand, BigInteger LeftSurdNumerator, BigInteger RightSurdNumerator)
        CommonRadicalParts(QuadraticSurd left, QuadraticSurd right) {
        if (TryCommonRadicalParts(
            left: left,
            result: out var result,
            right: right
        )) { return result; }
        throw new ArgumentException(message: "quadratic-surd operands must belong to the same field");
    }
    private static ScaledBinary Divide(ScaledBinary numerator, ScaledBinary denominator) {
        if (numerator.Significand == 0.0) { return default; }

        return Normalize(
            exponent: checked((numerator.Exponent - denominator.Exponent)),
            significand: (numerator.Significand / denominator.Significand)
        );
    }
    private static ScaledBinary Multiply(ScaledBinary left, ScaledBinary right) {
        if (
            (left.Significand == 0.0) ||
            (right.Significand == 0.0)
        ) { return default; }

        return Normalize(
            exponent: checked((left.Exponent + right.Exponent)),
            significand: (left.Significand * right.Significand)
        );
    }
    private static ScaledBinary Negate(ScaledBinary value) =>
        new(
            Significand: -value.Significand,
            Exponent: value.Exponent
        );
    private static ScaledBinary Normalize(double significand, long exponent) {
        if (significand == 0.0) { return default; }

        var adjustment = Math.ILogB(x: Math.Abs(value: significand));

        return new(
            Significand: Math.ScaleB(
                n: -adjustment,
                x: significand
            ),
            Exponent: checked((exponent + adjustment))
        );
    }
    private static ScaledBinary ScaleInteger(BigInteger value) {
        if (value.IsZero) { return default; }

        var magnitude = BigInteger.Abs(value: value);
        var bitLength = magnitude.GetBitLength();
        var shift = checked((int)Math.Max(
            val1: 0L,
            val2: (bitLength - 53L)
        ));
        var leading = ((double)(magnitude >> shift));

        return Normalize(
            exponent: shift,
            significand: ((value.Sign < 0)
            ? -leading
            : leading)
        );
    }
    private static ScaledBinary SquareRoot(ScaledBinary value) {
        var significand = value.Significand;
        var exponent = value.Exponent;

        if (0L != (exponent & 1L)) {
            significand *= 2.0;
            --exponent;
        }

        return Normalize(
            Math.Sqrt(d: significand),
            (exponent / 2L)
        );
    }
    private static double ToDouble(ScaledBinary value) {
        if (value.Significand == 0.0) { return 0.0; }
        if (value.Exponent > 1023L) {
            return Math.CopySign(
                x: double.PositiveInfinity,
                y: value.Significand
            );
        }
        if (value.Exponent < -1075L) {
            return Math.CopySign(
                x: 0.0,
                y: value.Significand
            );
        }

        return Math.ScaleB(
            n: ((int)value.Exponent),
            x: value.Significand
        );
    }

    /// <summary>Returns the absolute value.</summary>
    public QuadraticSurd Abs() => ((Sign < 0)
        ? -this
        : this
    );
    /// <summary>Returns the least integer no smaller than this value.</summary>
    public BigInteger Ceiling() => -(-this).Floor();
    /// <inheritdoc />
    public int CompareTo(QuadraticSurd other) {
        if (Equals(other: other)) { return 0; }
        if (TryCommonRadicalParts(
            left: this,
            result: out _,
            right: other
        )) { return (this - other).Sign; }

        // The positive denominators can be cleared without changing sign.
        // Distinct square classes make 1, √d, √e linearly independent over Q,
        // so the enclosure loop must eventually separate the nonzero value.
        var rational = ((RationalNumerator * other.Denominator) -
            (other.RationalNumerator * Denominator));
        var leftCoefficient = (SurdNumerator * other.Denominator);
        var rightCoefficient = -(other.SurdNumerator * Denominator);

        return BiquadraticSign(
            leftCoefficient: leftCoefficient,
            leftRadicand: Radicand,
            rational: rational,
            rightCoefficient: rightCoefficient,
            rightRadicand: other.Radicand
        );
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
            var root = BigIntegerFunctions.SquareRoot(value: radicand);

            if ((root * root) == radicand) {
                rationalNumerator += (surdNumerator * root);
                surdNumerator = BigInteger.Zero;
                radicand = BigInteger.Zero;
            }
        } else {
            radicand = BigInteger.Zero;
        }

        var divisor = BigInteger.GreatestCommonDivisor(
            left: BigInteger.GreatestCommonDivisor(
                left: BigInteger.Abs(value: rationalNumerator),
                right: BigInteger.Abs(value: surdNumerator)
            ),
            right: denominator
        );

        return new QuadraticSurd(
            denominator: (denominator / divisor),
            radicand: radicand,
            rationalNumerator: (rationalNumerator / divisor),
            surdNumerator: (surdNumerator / divisor)
        );
    }
    /// <inheritdoc />
    public bool Equals(QuadraticSurd other) {
        if ((RationalNumerator * other.Denominator) != (other.RationalNumerator * Denominator)) {
            return false;
        }
        if (
            IsRational ||
            other.IsRational
        ) {
            return (
                IsRational &&
                other.IsRational
            );
        }

        var leftCoefficient = (SurdNumerator * other.Denominator);
        var rightCoefficient = (other.SurdNumerator * Denominator);

        return (
            (leftCoefficient.Sign == rightCoefficient.Sign) &&
            (((leftCoefficient * leftCoefficient) * Radicand) ==
                ((rightCoefficient * rightCoefficient) * other.Radicand))
        );
    }
    /// <inheritdoc />
    public override bool Equals(object? obj) => ((obj is QuadraticSurd other) && Equals(other: other));
    /// <summary>Returns the greatest integer no larger than this value.</summary>
    public BigInteger Floor() {
        if (SurdNumerator.IsZero) {
            return RationalNumerator.FloorDivide(divisor: Denominator);
        }

        var rootRadicand = ((SurdNumerator * SurdNumerator) * Radicand);
        var rootFloor = BigIntegerFunctions.SquareRoot(value: rootRadicand);
        BigInteger lowerNumerator;

        if (SurdNumerator.Sign > 0) {
            lowerNumerator = (RationalNumerator + rootFloor);
        } else {
            var rootCeiling = (((rootFloor * rootFloor) == rootRadicand)
                ? rootFloor
                : (rootFloor + 1)
            );

            lowerNumerator = (RationalNumerator - rootCeiling);
        }

        var candidate = lowerNumerator.FloorDivide(divisor: Denominator);
        var threshold = (((candidate + 1) * Denominator) - RationalNumerator);
        bool reachesNext;

        if (SurdNumerator.Sign > 0) {
            reachesNext = ((threshold <= 0) || (rootRadicand >= (threshold * threshold)));
        } else {
            var positiveThreshold = -threshold;

            reachesNext = ((positiveThreshold >= 0) && (rootRadicand <= (positiveThreshold * positiveThreshold)));
        }

        return (reachesNext
            ? (candidate + 1)
            : candidate
        );
    }
    /// <inheritdoc />
    public override int GetHashCode() {
        var rationalDivisor = BigInteger.GreatestCommonDivisor(
            left: BigInteger.Abs(value: RationalNumerator),
            right: Denominator
        );
        var rationalNumerator = (RationalNumerator / rationalDivisor);
        var rationalDenominator = (Denominator / rationalDivisor);

        if (IsRational) {
            return HashCode.Combine(
                value1: rationalNumerator,
                value2: rationalDenominator
            );
        }

        var irrationalSquareNumerator = ((SurdNumerator * SurdNumerator) * Radicand);
        var irrationalSquareDenominator = (Denominator * Denominator);
        var irrationalDivisor = BigInteger.GreatestCommonDivisor(
            left: irrationalSquareNumerator,
            right: irrationalSquareDenominator
        );

        return HashCode.Combine(
            value1: rationalNumerator,
            value2: rationalDenominator,
            value3: SurdNumerator.Sign,
            value4: (irrationalSquareNumerator / irrationalDivisor),
            value5: (irrationalSquareDenominator / irrationalDivisor)
        );
    }
    /// <summary>Creates an exact integer.</summary>
    public static QuadraticSurd Rational(BigInteger value) =>
        new(
            rationalNumerator: value,
            surdNumerator: BigInteger.Zero,
            radicand: BigInteger.Zero,
            denominator: BigInteger.One
        );
    /// <summary>Creates an exact rational number.</summary>
    public static QuadraticSurd Rational(BigInteger numerator, BigInteger denominator) =>
        Create(
            rationalNumerator: numerator,
            surdNumerator: BigInteger.Zero,
            radicand: BigInteger.Zero,
            denominator: denominator
        );
    /// <summary>Returns a binary64 approximation; exact arithmetic does not use this conversion.</summary>
    /// <remarks>
    /// The arbitrary-width components are converted with one shared binary scale. Oppositely signed rational and
    /// radical terms use the exact conjugate identity when their leading exponents overlap, avoiding both
    /// infinity-over-infinity and loss of a small residual to premature cancellation.
    /// </remarks>
    public double ToDouble() {
        var denominator = ScaleInteger(value: Denominator);
        ScaledBinary numerator;

        if (SurdNumerator.IsZero) {
            numerator = ScaleInteger(value: RationalNumerator);
        } else {
            var rational = ScaleInteger(value: RationalNumerator);
            var radical = Multiply(
                left: ScaleInteger(value: SurdNumerator),
                right: SquareRoot(value: ScaleInteger(value: Radicand))
            );

            if (
                (rational.Significand != 0.0) &&
                (radical.Significand != 0.0) &&
                (Math.Sign(value: rational.Significand) != Math.Sign(value: radical.Significand)) &&
                (Math.Abs(value: (rational.Exponent - radical.Exponent)) <= 2L)
            ) {
                // (a + b√d)(a - b√d) = a² - b²d. The conjugate is a stable sum exactly when the
                // original terms nearly cancel, and the norm is retained as an exact BigInteger.
                var norm = (
                    (RationalNumerator * RationalNumerator) -
                    ((SurdNumerator * SurdNumerator) * Radicand)
                );
                var conjugate = Add(
                    left: rational,
                    right: Negate(value: radical)
                );

                numerator = Divide(
                    ScaleInteger(value: norm),
                    conjugate
                );
            } else {
                numerator = Add(
                    left: rational,
                    right: radical
                );
            }
        }

        return ToDouble(value: Divide(
            denominator: denominator,
            numerator: numerator
        ));
    }
    /// <inheritdoc />
    /// <remarks>Every component is formatted against <see cref="CultureInfo.InvariantCulture"/>. A
    /// <see cref="BigInteger"/> formatted with the ambient provider follows the host's culture: the same value read
    /// <c>(-1234567890 + 1·√2)/3</c> under en-US, carried U+200E under fa-IR, U+061C under ar-SA, and U+2212 for the
    /// minus sign under sv-SE. This library's contract is that nothing in it reads the current culture, and a value
    /// type whose text can reach a log, a snapshot or a golden file has to keep that.</remarks>
    public override string ToString() => (IsRational
        ? ((Denominator == BigInteger.One)
            ? RationalNumerator.ToString(provider: CultureInfo.InvariantCulture)
            : string.Create(
                provider: CultureInfo.InvariantCulture,
                $"{RationalNumerator}/{Denominator}"
            ))
        : string.Create(
            provider: CultureInfo.InvariantCulture,
            $"({RationalNumerator} + {SurdNumerator}·√{Radicand})/{Denominator}"
        )
    );

    /// <summary>Adds two values in the same real quadratic field.</summary>
    public static QuadraticSurd operator +(QuadraticSurd left, QuadraticSurd right) {
        var common = CommonRadicalParts(
            left: left,
            right: right
        );

        return Create(
            rationalNumerator: ((left.RationalNumerator * right.Denominator) + (right.RationalNumerator * left.Denominator)),
            surdNumerator: ((common.LeftSurdNumerator * right.Denominator) +
                (common.RightSurdNumerator * left.Denominator)),
            radicand: common.Radicand,
            denominator: (left.Denominator * right.Denominator)
        );
    }
    /// <summary>Subtracts two values in the same real quadratic field.</summary>
    public static QuadraticSurd operator -(QuadraticSurd left, QuadraticSurd right) => (left + -right);
    /// <summary>Negates a value.</summary>
    public static QuadraticSurd operator -(QuadraticSurd value) =>
        Create(
            denominator: value.Denominator,
            radicand: value.Radicand,
            rationalNumerator: -value.RationalNumerator,
            surdNumerator: -value.SurdNumerator
        );
    /// <summary>Multiplies two values in the same real quadratic field.</summary>
    public static QuadraticSurd operator *(QuadraticSurd left, QuadraticSurd right) {
        var common = CommonRadicalParts(
            left: left,
            right: right
        );

        return Create(
            rationalNumerator: ((left.RationalNumerator * right.RationalNumerator) +
                ((common.LeftSurdNumerator * common.RightSurdNumerator) * common.Radicand)),
            surdNumerator: ((left.RationalNumerator * common.RightSurdNumerator) +
                (common.LeftSurdNumerator * right.RationalNumerator)),
            radicand: common.Radicand,
            denominator: (left.Denominator * right.Denominator)
        );
    }
    /// <summary>Divides two values in the same real quadratic field.</summary>
    public static QuadraticSurd operator /(QuadraticSurd left, QuadraticSurd right) {
        if (right.Sign == 0) { throw new DivideByZeroException(); }

        var common = CommonRadicalParts(
            left: left,
            right: right
        );
        var norm = ((right.RationalNumerator * right.RationalNumerator) -
            ((common.RightSurdNumerator * common.RightSurdNumerator) * common.Radicand));

        return Create(
            rationalNumerator: (right.Denominator * ((left.RationalNumerator * right.RationalNumerator) -
                ((common.LeftSurdNumerator * common.RightSurdNumerator) * common.Radicand))),
            surdNumerator: (right.Denominator * ((common.LeftSurdNumerator * right.RationalNumerator) -
                (left.RationalNumerator * common.RightSurdNumerator))),
            radicand: common.Radicand,
            denominator: (left.Denominator * norm)
        );
    }
    /// <summary>Tests exact ordering.</summary>
    public static bool operator <(QuadraticSurd left, QuadraticSurd right) => (left.CompareTo(other: right) < 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator >(QuadraticSurd left, QuadraticSurd right) => (left.CompareTo(other: right) > 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator <=(QuadraticSurd left, QuadraticSurd right) => (left.CompareTo(other: right) <= 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator >=(QuadraticSurd left, QuadraticSurd right) => (left.CompareTo(other: right) >= 0);
    /// <summary>Tests exact equality.</summary>
    public static bool operator ==(QuadraticSurd left, QuadraticSurd right) => left.Equals(other: right);
    /// <summary>Tests exact inequality.</summary>
    public static bool operator !=(QuadraticSurd left, QuadraticSurd right) => !left.Equals(other: right);

    private readonly record struct ScaledBinary(double Significand, long Exponent);
}
