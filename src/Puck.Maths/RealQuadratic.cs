using System.Globalization;
using System.Numerics;

namespace Puck.Maths;

/// <summary>An exact real number <c>(a + b·√d) / c</c> of the real quadratic field its <see cref="Field"/> names.</summary>
/// <remarks>
/// <para>
/// The denominator is positive and the three integers share no common factor; the radicand is the field's canonical
/// one. Two values of one field therefore compare and hash as tuples. Values of different fields cannot be combined
/// by the operators (<see cref="ArgumentException"/>), except that a rational value — one with a zero surd
/// coefficient, whose field is <see cref="RealQuadraticField.Rationals"/> — belongs to every field.
/// </para>
/// <para>
/// Equality and hashing are exact across representations: a radicand that kept a large square factor through
/// canonicalization still equals its square-free twin, by the field-identification test rather than the tuple.
/// <see cref="GetHashCode"/> folds through the framework's per-process <see cref="HashCode"/> and is for hash
/// tables only, never for a replay fingerprint.
/// </para>
/// </remarks>
public readonly struct RealQuadratic : IComparable<RealQuadratic>, IEquatable<RealQuadratic> {
    private readonly BigInteger m_denominator;

    private RealQuadratic(BigInteger rationalNumerator, BigInteger surdNumerator, RealQuadraticField field, BigInteger denominator) {
        RationalNumerator = rationalNumerator;
        SurdNumerator = surdNumerator;
        Field = field;
        m_denominator = denominator;
    }

    /// <summary>Gets the positive common denominator <c>c</c>.</summary>
    public BigInteger Denominator => (m_denominator.IsZero
        ? BigInteger.One
        : m_denominator
    );
    /// <summary>Gets the field the value belongs to; <see cref="RealQuadraticField.Rationals"/> for a rational value.</summary>
    public RealQuadraticField Field { get; }
    /// <summary>Gets a value indicating whether the value is rational.</summary>
    public bool IsRational => SurdNumerator.IsZero;
    /// <summary>Gets the multiplicative identity.</summary>
    public static RealQuadratic One => Rational(value: BigInteger.One);
    /// <summary>Gets the field's canonical radicand <c>d</c>; zero for a rational value.</summary>
    public BigInteger Radicand => Field.Radicand;
    /// <summary>Gets <c>a</c>, the rational numerator.</summary>
    public BigInteger RationalNumerator { get; }
    /// <summary>Gets the rational coordinate <c>a / c</c>.</summary>
    public Rational RationalPart => new(Numerator: RationalNumerator, Denominator: Denominator);
    /// <summary>Gets the exact sign of the represented real number.</summary>
    public int Sign => SignOf(
        radicand: Radicand,
        rationalNumerator: RationalNumerator,
        surdNumerator: SurdNumerator
    );
    /// <summary>Gets <c>b</c>, the coefficient of the square root.</summary>
    public BigInteger SurdNumerator { get; }
    /// <summary>Gets the surd coordinate <c>b / c</c>.</summary>
    public Rational SurdPart => new(Numerator: SurdNumerator, Denominator: Denominator);
    /// <summary>Gets the additive identity.</summary>
    public static RealQuadratic Zero => Rational(value: BigInteger.Zero);

    // The sign of a + b√d over a positive denominator, exactly: agreeing signs decide outright, and opposing signs
    // compare a² against b²d.
    private static int SignOf(BigInteger rationalNumerator, BigInteger surdNumerator, BigInteger radicand) {
        if (surdNumerator.IsZero) { return rationalNumerator.Sign; }
        if (
            (rationalNumerator.Sign >= 0) &&
            (surdNumerator.Sign >= 0)
        ) { return 1; }
        if (
            (rationalNumerator.Sign <= 0) &&
            (surdNumerator.Sign <= 0)
        ) { return -1; }

        var comparison = (rationalNumerator * rationalNumerator).CompareTo(other: ((surdNumerator * surdNumerator) * radicand));

        return ((rationalNumerator.Sign > 0)
            ? comparison
            : -comparison
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
    // The sign of rational + left√d₁ + right√d₂ across two different fields, by an interval enclosure at doubling
    // precision: 1, √d₁ and √d₂ are linearly independent over ℚ, so a nonzero value is eventually separated.
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
    // The field two operands share, with each side's surd coefficient carried into it: a rational operand adopts the
    // other's field; equal fields need no work; two canonical radicands that still name one field (a large square
    // factor survived on one side) are identified by the exact ratio test.
    private static (RealQuadraticField Field, BigInteger LeftSurdNumerator, BigInteger RightSurdNumerator) CommonField(in RealQuadratic left, in RealQuadratic right) {
        if (TryCommonField(
            field: out var field,
            left: left,
            leftSurdNumerator: out var leftSurd,
            right: right,
            rightSurdNumerator: out var rightSurd
        )) {
            return (field, leftSurd, rightSurd);
        }

        throw new ArgumentException(message: $"√{left.Radicand} and √{right.Radicand} lie in different real quadratic fields; their values cannot be combined.");
    }
    private static RealQuadratic Normalize(BigInteger rationalNumerator, BigInteger surdNumerator, RealQuadraticField field, BigInteger denominator) {
        if (denominator.Sign < 0) {
            rationalNumerator = -rationalNumerator;
            surdNumerator = -surdNumerator;
            denominator = -denominator;
        }

        if (surdNumerator.IsZero) {
            field = RealQuadraticField.Rationals;
        }

        var divisor = BigInteger.GreatestCommonDivisor(
            left: BigInteger.GreatestCommonDivisor(
                left: BigInteger.Abs(value: rationalNumerator),
                right: BigInteger.Abs(value: surdNumerator)
            ),
            right: denominator
        );

        return new(
            denominator: (divisor.IsOne ? denominator : (denominator / divisor)),
            field: field,
            rationalNumerator: (divisor.IsOne ? rationalNumerator : (rationalNumerator / divisor)),
            surdNumerator: (divisor.IsOne ? surdNumerator : (surdNumerator / divisor))
        );
    }

    internal static RealQuadratic FromCanonical(BigInteger rationalNumerator, BigInteger surdNumerator, RealQuadraticField field, BigInteger denominator) =>
        Normalize(
            denominator: denominator,
            field: field,
            rationalNumerator: rationalNumerator,
            surdNumerator: surdNumerator
        );
    internal static bool TryCommonField(in RealQuadratic left, in RealQuadratic right, out RealQuadraticField field, out BigInteger leftSurdNumerator, out BigInteger rightSurdNumerator) {
        if (left.IsRational) {
            field = right.Field;
            leftSurdNumerator = BigInteger.Zero;
            rightSurdNumerator = right.SurdNumerator;

            return true;
        }

        if (right.IsRational) {
            field = left.Field;
            leftSurdNumerator = left.SurdNumerator;
            rightSurdNumerator = BigInteger.Zero;

            return true;
        }

        if (left.Field == right.Field) {
            field = left.Field;
            leftSurdNumerator = left.SurdNumerator;
            rightSurdNumerator = right.SurdNumerator;

            return true;
        }

        if (RealQuadraticField.TrySame(
            common: out var common,
            left: left.Radicand,
            leftScale: out var leftScale,
            right: right.Radicand,
            rightScale: out var rightScale
        )) {
            field = RealQuadraticField.Create(radicand: common);
            leftSurdNumerator = (left.SurdNumerator * leftScale);
            rightSurdNumerator = (right.SurdNumerator * rightScale);

            return true;
        }

        field = default;
        leftSurdNumerator = default;
        rightSurdNumerator = default;

        return false;
    }

    /// <summary>Returns the absolute value.</summary>
    public RealQuadratic Abs() => ((Sign < 0)
        ? -this
        : this
    );
    /// <summary>Returns the least integer no smaller than this value.</summary>
    public BigInteger Ceiling() => -(-this).Floor();
    /// <inheritdoc />
    public int CompareTo(RealQuadratic other) {
        if (TryCommonField(
            field: out var field,
            left: this,
            leftSurdNumerator: out var leftSurd,
            right: other,
            rightSurdNumerator: out var rightSurd
        )) {
            // The sign of the difference, read directly from its cross-multiplied coordinates: no value is built.
            return SignOf(
                radicand: field.Radicand,
                rationalNumerator: ((RationalNumerator * other.Denominator) - (other.RationalNumerator * Denominator)),
                surdNumerator: ((leftSurd * other.Denominator) - (rightSurd * Denominator))
            );
        }

        return BiquadraticSign(
            leftCoefficient: (SurdNumerator * other.Denominator),
            leftRadicand: Radicand,
            rational: ((RationalNumerator * other.Denominator) - (other.RationalNumerator * Denominator)),
            rightCoefficient: -(other.SurdNumerator * Denominator),
            rightRadicand: other.Radicand
        );
    }
    /// <summary>Returns the conjugate <c>(a − b·√d) / c</c>.</summary>
    public RealQuadratic Conjugate() =>
        new(
            denominator: Denominator,
            field: Field,
            rationalNumerator: RationalNumerator,
            surdNumerator: -SurdNumerator
        );
    /// <summary>Creates and normalizes <c>(a + b·√d) / c</c>: the radicand is canonicalized, a perfect-square radicand
    /// folds into the rational part, and the three integers are reduced.</summary>
    /// <param name="rationalNumerator">The rational numerator <c>a</c>.</param>
    /// <param name="surdNumerator">The surd coefficient's numerator <c>b</c>.</param>
    /// <param name="radicand">The radicand <c>d</c>; must be non-negative.</param>
    /// <param name="denominator">The denominator <c>c</c>; must be nonzero.</param>
    /// <returns>The reduced value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radicand"/> is negative.</exception>
    /// <exception cref="DivideByZeroException"><paramref name="denominator"/> is zero.</exception>
    public static RealQuadratic Create(
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

        var field = RealQuadraticField.Rationals;

        if (!surdNumerator.IsZero) {
            var (canonical, scale) = RealQuadraticField.Canonicalize(radicand: radicand);

            if (canonical.IsOne) {
                rationalNumerator += (surdNumerator * scale);
                surdNumerator = BigInteger.Zero;
            } else {
                surdNumerator *= scale;
                field = RealQuadraticField.Create(radicand: canonical);
            }
        }

        return Normalize(
            denominator: denominator,
            field: field,
            rationalNumerator: rationalNumerator,
            surdNumerator: surdNumerator
        );
    }
    /// <inheritdoc />
    public bool Equals(RealQuadratic other) {
        if (Field == other.Field) {
            return (
                (RationalNumerator == other.RationalNumerator) &&
                (SurdNumerator == other.SurdNumerator) &&
                (Denominator == other.Denominator)
            );
        }

        if (
            IsRational ||
            other.IsRational
        ) { return false; }

        // Different canonical radicands can still name one field; identify the square class exactly.
        if ((RationalNumerator * other.Denominator) != (other.RationalNumerator * Denominator)) { return false; }

        var leftCoefficient = (SurdNumerator * other.Denominator);
        var rightCoefficient = (other.SurdNumerator * Denominator);

        return (
            (leftCoefficient.Sign == rightCoefficient.Sign) &&
            (((leftCoefficient * leftCoefficient) * Radicand) == ((rightCoefficient * rightCoefficient) * other.Radicand))
        );
    }
    /// <inheritdoc />
    public override bool Equals(object? obj) => ((obj is RealQuadratic other) && Equals(other: other));
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
    /// <summary>Widens an exact rational into the field of the rationals.</summary>
    /// <param name="value">The rational.</param>
    /// <returns>The value as a rational <see cref="RealQuadratic"/>.</returns>
    public static RealQuadratic FromRational(Rational value) =>
        new(
            denominator: value.Denominator,
            field: RealQuadraticField.Rationals,
            rationalNumerator: value.Numerator,
            surdNumerator: BigInteger.Zero
        );
    /// <inheritdoc />
    /// <remarks>Folds the square class rather than the tuple, so a radicand that kept a square factor hashes with its
    /// square-free twin, as <see cref="Equals(RealQuadratic)"/> equates them.</remarks>
    public override int GetHashCode() {
        if (IsRational) {
            return HashCode.Combine(
                value1: RationalNumerator,
                value2: Denominator
            );
        }

        var irrationalSquareNumerator = ((SurdNumerator * SurdNumerator) * Radicand);
        var irrationalSquareDenominator = (Denominator * Denominator);
        var irrationalDivisor = BigInteger.GreatestCommonDivisor(
            left: irrationalSquareNumerator,
            right: irrationalSquareDenominator
        );
        var rationalDivisor = BigInteger.GreatestCommonDivisor(
            left: BigInteger.Abs(value: RationalNumerator),
            right: Denominator
        );

        return HashCode.Combine(
            value1: (RationalNumerator / rationalDivisor),
            value2: (Denominator / rationalDivisor),
            value3: SurdNumerator.Sign,
            value4: (irrationalSquareNumerator / irrationalDivisor),
            value5: (irrationalSquareDenominator / irrationalDivisor)
        );
    }
    /// <summary>Returns the field norm <c>(a² − b²·d) / c²</c>, the product with the conjugate.</summary>
    public Rational Norm() =>
        new(
            Numerator: ((RationalNumerator * RationalNumerator) - ((SurdNumerator * SurdNumerator) * Radicand)),
            Denominator: (Denominator * Denominator)
        );
    /// <summary>Creates an exact integer.</summary>
    /// <param name="value">The integer.</param>
    /// <returns>The value as a rational <see cref="RealQuadratic"/>.</returns>
    public static RealQuadratic Rational(BigInteger value) =>
        new(
            denominator: BigInteger.One,
            field: RealQuadraticField.Rationals,
            rationalNumerator: value,
            surdNumerator: BigInteger.Zero
        );
    /// <summary>Creates an exact rational number.</summary>
    /// <param name="numerator">The numerator.</param>
    /// <param name="denominator">The denominator; must be nonzero.</param>
    /// <returns>The reduced rational value.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="denominator"/> is zero.</exception>
    public static RealQuadratic Rational(BigInteger numerator, BigInteger denominator) {
        if (denominator.IsZero) { throw new DivideByZeroException(); }

        return Normalize(
            denominator: denominator,
            field: RealQuadraticField.Rationals,
            rationalNumerator: numerator,
            surdNumerator: BigInteger.Zero
        );
    }
    /// <summary>Returns the nearest <see cref="double"/>, ties to even: the magnitude is floored at a binary scale wide
    /// enough to leave more than sixty bits, using one exact integer square root, and that one integer rounds once
    /// through <see cref="BigIntegerFunctions.ToDouble(BigInteger, int, bool)"/> with the floor's strict remainder as
    /// the sticky bit.</summary>
    public double ToDouble() {
        if (IsRational) { return RationalPart.ToDouble(); }
        if (Sign < 0) { return -(-this).ToDouble(); }

        // The scale is refined from the floored magnitude itself rather than from the terms' sizes alone: when the
        // rational and radical terms nearly cancel the value is far smaller than either, and a scale read off the
        // terms would floor it to nothing. An irrational value is never zero, so the refinement terminates.
        var magnitudeBits = Math.Max(
            val1: (long)BigInteger.Abs(value: RationalNumerator).GetBitLength(),
            val2: ((long)BigInteger.Abs(value: SurdNumerator).GetBitLength() + ((long)((Radicand.GetBitLength() + 1) / 2)))
        );
        var scale = Puck.Maths.Rational.DoubleScale(magnitudeBitLength: ((magnitudeBits + 1L) - (long)Denominator.GetBitLength()));
        var scaled = FloorScaled(scale: scale);

        while (true) {
            var scaledBits = (long)scaled.GetBitLength();

            if (scaledBits >= 60L) { break; }

            scale += ((int)Math.Min(val1: (72L - scaledBits), val2: 4096L));
            scaled = FloorScaled(scale: scale);
        }

        // A positive irrational value lies strictly above its floor, so the remainder is always present.
        return BigIntegerFunctions.ToDouble(
            binaryExponent: -scale,
            hasRemainder: true,
            truncatedMagnitude: scaled
        );
    }
    // ⌊value · 2^scale⌋, exactly: the root term is bounded below by the floor root for a positive coefficient and by
    // the negated ceiling root for a negative one, so the floor of the sum is the floor of the value.
    private BigInteger FloorScaled(int scale) {
        var rootRadicand = (((SurdNumerator * SurdNumerator) * Radicand) << (2 * scale));
        var root = BigIntegerFunctions.SquareRoot(value: rootRadicand);
        var rootTerm = ((SurdNumerator.Sign > 0)
            ? root
            : -(((root * root) == rootRadicand) ? root : (root + BigInteger.One)));

        return ((RationalNumerator << scale) + rootTerm).FloorDivide(divisor: Denominator);
    }
    /// <inheritdoc />
    /// <remarks>Every component is formatted against <see cref="CultureInfo.InvariantCulture"/>, so the text is the
    /// same on every host and can reach a log, a snapshot or a golden file.</remarks>
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
    /// <summary>Returns the field trace <c>2a / c</c>, the sum with the conjugate.</summary>
    public Rational Trace() =>
        new(
            Numerator: (RationalNumerator << 1),
            Denominator: Denominator
        );

    /// <summary>Adds two values of one field.</summary>
    /// <exception cref="ArgumentException">The operands lie in different fields.</exception>
    public static RealQuadratic operator +(RealQuadratic left, RealQuadratic right) {
        var (field, leftSurd, rightSurd) = CommonField(
            left: left,
            right: right
        );

        return Normalize(
            denominator: (left.Denominator * right.Denominator),
            field: field,
            rationalNumerator: ((left.RationalNumerator * right.Denominator) + (right.RationalNumerator * left.Denominator)),
            surdNumerator: ((leftSurd * right.Denominator) + (rightSurd * left.Denominator))
        );
    }
    /// <summary>Subtracts two values of one field.</summary>
    /// <exception cref="ArgumentException">The operands lie in different fields.</exception>
    public static RealQuadratic operator -(RealQuadratic left, RealQuadratic right) => (left + -right);
    /// <summary>Negates a value.</summary>
    public static RealQuadratic operator -(RealQuadratic value) =>
        new(
            denominator: value.Denominator,
            field: value.Field,
            rationalNumerator: -value.RationalNumerator,
            surdNumerator: -value.SurdNumerator
        );
    /// <summary>Multiplies two values of one field.</summary>
    /// <exception cref="ArgumentException">The operands lie in different fields.</exception>
    public static RealQuadratic operator *(RealQuadratic left, RealQuadratic right) {
        var (field, leftSurd, rightSurd) = CommonField(
            left: left,
            right: right
        );

        return Normalize(
            denominator: (left.Denominator * right.Denominator),
            field: field,
            rationalNumerator: ((left.RationalNumerator * right.RationalNumerator) + ((leftSurd * rightSurd) * field.Radicand)),
            surdNumerator: ((left.RationalNumerator * rightSurd) + (leftSurd * right.RationalNumerator))
        );
    }
    /// <summary>Divides two values of one field.</summary>
    /// <exception cref="ArgumentException">The operands lie in different fields.</exception>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> is zero.</exception>
    public static RealQuadratic operator /(RealQuadratic left, RealQuadratic right) {
        if (right.Sign == 0) { throw new DivideByZeroException(); }

        var (field, leftSurd, rightSurd) = CommonField(
            left: left,
            right: right
        );
        // left / right = left · conj(right) / N(right), with N(right) = a'² − b'²·d nonzero for a nonzero right.
        var norm = ((right.RationalNumerator * right.RationalNumerator) - ((rightSurd * rightSurd) * field.Radicand));

        return Normalize(
            denominator: (left.Denominator * norm),
            field: field,
            rationalNumerator: (right.Denominator * ((left.RationalNumerator * right.RationalNumerator) - ((leftSurd * rightSurd) * field.Radicand))),
            surdNumerator: (right.Denominator * ((leftSurd * right.RationalNumerator) - (left.RationalNumerator * rightSurd)))
        );
    }
    /// <summary>Tests exact ordering.</summary>
    public static bool operator <(RealQuadratic left, RealQuadratic right) => (left.CompareTo(other: right) < 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator >(RealQuadratic left, RealQuadratic right) => (left.CompareTo(other: right) > 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator <=(RealQuadratic left, RealQuadratic right) => (left.CompareTo(other: right) <= 0);
    /// <summary>Tests exact ordering.</summary>
    public static bool operator >=(RealQuadratic left, RealQuadratic right) => (left.CompareTo(other: right) >= 0);
    /// <summary>Tests exact equality.</summary>
    public static bool operator ==(RealQuadratic left, RealQuadratic right) => left.Equals(other: right);
    /// <summary>Tests exact inequality.</summary>
    public static bool operator !=(RealQuadratic left, RealQuadratic right) => !left.Equals(other: right);
}
