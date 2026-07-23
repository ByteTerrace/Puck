using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A split-complex number of <see cref="FixedQ4816"/> components — the deterministic 2D <em>scaling</em> primitive, the
/// hyperbolic sibling of <see cref="FixedComplex"/>. An element is <c>u + v·j</c> where the adjoined root satisfies
/// <c>j² = +1</c> (the complex unit satisfies <c>i² = −1</c>; the dual unit <c>ε² = 0</c>). Multiplication composes
/// squeezes, and <see cref="Transform"/> applies one to a vector. Pure integer arithmetic; identical inputs produce
/// identical bits on every machine. Polynomial operations widen every product, accumulate the complete expression, and
/// round once per returned component.
/// </summary>
/// <remarks>
/// The quadratic form <see cref="Norm"/> <c>= u² − v²</c> is <em>indefinite</em>: it is zero on the two diagonal lines
/// <c>u = ±v</c> (the light cone) and negative beyond them. Consequently the ring has zero divisors — for instance
/// <c>(1 + j)(1 − j) = 1 − j² = 0</c> — so a non-zero element need not be invertible, and <see cref="op_Division"/>
/// requires the divisor to be a unit (non-zero <see cref="Norm"/>). This is the algebra behind scaling flows and
/// rate/boost composition: the metallic matrix <c>[[k, 1], [1, 0]]</c> and, generally, any real diagonalizable planar
/// map acts naturally here, where <see cref="FixedComplex"/> would model a rotation instead.
/// </remarks>
/// <param name="U">The scalar component.</param>
/// <param name="V">The component along the split unit <c>j</c>.</param>
public readonly record struct FixedSplit(FixedQ4816 U, FixedQ4816 V)
    : IAdditionOperators<FixedSplit, FixedSplit, FixedSplit>,
      ISubtractionOperators<FixedSplit, FixedSplit, FixedSplit>,
      IMultiplyOperators<FixedSplit, FixedSplit, FixedSplit>,
      IDivisionOperators<FixedSplit, FixedSplit, FixedSplit>,
      IUnaryNegationOperators<FixedSplit, FixedSplit>,
      IAdditiveIdentity<FixedSplit, FixedSplit>,
      IMultiplicativeIdentity<FixedSplit, FixedSplit> {
    // round(log2(e) · 2^16): converts a natural argument into the base-2 argument Exp2 consumes.
    private static readonly FixedQ4816 Log2E = FixedQ4816.FromRawBits(value: 94548L);
    private static readonly FixedQ4816 Half = FixedQ4816.FromRawBits(value: (1L << (FixedQ4816.FractionBitCount - 1)));

    /// <summary>Gets the additive identity, zero.</summary>
    public static FixedSplit AdditiveIdentity => default;
    /// <summary>Gets the multiplicative identity, one (the identity squeeze).</summary>
    public static FixedSplit MultiplicativeIdentity => new(
        U: FixedQ4816.One,
        V: FixedQ4816.Zero
    );

    /// <summary>Negates a split-complex number.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The componentwise negation.</returns>
    public static FixedSplit operator -(FixedSplit value) =>
        new(
        U: -value.U,
        V: -value.V
    );
    /// <summary>Adds two split-complex numbers.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static FixedSplit operator +(FixedSplit left, FixedSplit right) =>
        new(
        U: (left.U + right.U),
        V: (left.V + right.V)
    );
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/>.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static FixedSplit operator -(FixedSplit left, FixedSplit right) =>
        new(
        U: (left.U - right.U),
        V: (left.V - right.V)
    );
    /// <summary>Multiplies two split-complex numbers (composes squeezes for unit operands).</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product <c>(u₁u₂ + v₁v₂) + (u₁v₂ + u₂v₁)·j</c>, each component's two products accumulated exactly with one rounding.</returns>
    public static FixedSplit operator *(FixedSplit left, FixedSplit right) {
        const ulong NarrowLimit = (1UL << 31);
        var combinedMagnitude = (RawMagnitude(value: left.U.Value) |
                                 RawMagnitude(value: left.V.Value) |
                                 RawMagnitude(value: right.U.Value) |
                                 RawMagnitude(value: right.V.Value));

        if (combinedMagnitude < NarrowLimit) {
            return new(
                U: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((left.U.Value * right.U.Value) + (left.V.Value * right.V.Value)))),
                V: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((left.U.Value * right.V.Value) + (left.V.Value * right.U.Value))))
            );
        }

        return new(
            U: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)left.U.Value * right.U.Value) + ((Int128)left.V.Value * right.V.Value)))),
            V: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)left.U.Value * right.V.Value) + ((Int128)left.V.Value * right.U.Value))))
        );
    }
    /// <summary>Divides <paramref name="left"/> by <paramref name="right"/>.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor; must be a unit (non-zero <see cref="Norm"/>, i.e. off the lines <c>u = ±v</c>).</param>
    /// <returns>The quotient <c>left·conj(right) / (c² − d²)</c>, each component rounded once.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> lies on the light cone (its <see cref="Norm"/> is zero); a zero divisor has no inverse even when it is itself non-zero.</exception>
    public static FixedSplit operator /(FixedSplit left, FixedSplit right) {
        // conj(c + d·j) = c − d·j, and (c + d·j)(c − d·j) = c² − d². The numerators are left·conj(right).
        var realNumerator = AddProducts(
            firstLeft: left.U.Value,
            firstRight: right.U.Value,
            secondLeft: left.V.Value,
            secondRight: right.V.Value,
            subtractSecond: true
        );
        var splitNumerator = AddProducts(
            firstLeft: left.V.Value,
            firstRight: right.U.Value,
            secondLeft: left.U.Value,
            secondRight: right.V.Value,
            subtractSecond: true
        );
        var denominator = AddProducts(
            firstLeft: right.U.Value,
            firstRight: right.U.Value,
            secondLeft: right.V.Value,
            secondRight: right.V.Value,
            subtractSecond: true
        );

        if (denominator.Magnitude == UInt128.Zero) {
            throw new DivideByZeroException(message: "A split-complex zero divisor (|u| = |v|) has no inverse.");
        }

        return new(
            U: DivideProductSum(numerator: realNumerator, denominator: denominator),
            V: DivideProductSum(numerator: splitNumerator, denominator: denominator)
        );
    }

    /// <summary>Creates the unit squeeze of hyperbolic angle <paramref name="rapidity"/> — the split exponential map
    /// <c>exp(j·φ) = cosh φ + j·sinh φ</c>, the scaling analog of <see cref="FixedComplex.FromAngle"/>.</summary>
    /// <param name="rapidity">The hyperbolic angle; rapidities add under multiplication, so squeezes compose by summing this parameter.</param>
    /// <returns>The unit split-complex number <c>cosh φ + j·sinh φ</c>, whose <see cref="Norm"/> is one.</returns>
    /// <remarks>Built from the fixed-point <see cref="FixedQ4816.Exp2"/> machinery: <c>e^φ = Exp2(φ·log2 e)</c>, then
    /// <c>cosh φ = (e^φ + e^−φ)/2</c> and <c>sinh φ = (e^φ − e^−φ)/2</c>. Deterministic and bit-identical across
    /// machines; the exponential's relative error grows with the magnitude of <paramref name="rapidity"/>.</remarks>
    public static FixedSplit FromRapidity(FixedQ4816 rapidity) {
        var scaled = (rapidity * Log2E);
        var forward = FixedQ4816.Exp2(value: scaled);
        var backward = FixedQ4816.Exp2(value: -scaled);

        return new(
            U: ((forward + backward) * Half),
            V: ((forward - backward) * Half)
        );
    }

    /// <summary>Gets the indefinite quadratic form <c>u² − v²</c> — the invariant a unit squeeze preserves.</summary>
    /// <remarks>Positive inside the light cone (<c>|u| &gt; |v|</c>), zero on it, and negative outside; it is not a
    /// magnitude and admits no real square root beyond the interior. The exact raw Q32 difference is rounded once to
    /// Q16 and wraps rather than saturating.</remarks>
    public FixedQ4816 Norm =>
        FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)U.Value * U.Value) - ((Int128)V.Value * V.Value))));
    /// <summary>Gets whether this element is a unit — invertible, off the light cone.</summary>
    public bool IsUnit => (U.Value != V.Value) && (U.Value != -V.Value);

    /// <summary>Returns the conjugate <c>u − v·j</c> — the inverse squeeze for a unit split-complex number.</summary>
    /// <returns>The split-complex number with the split component negated.</returns>
    public FixedSplit Conjugate() =>
        new(
        U: U,
        V: -V
    );
    /// <summary>Applies this split-complex number to a 2D vector as a squeeze (hyperbolic rotation).</summary>
    /// <param name="vector">The vector to transform, read as <c>x + y·j</c>.</param>
    /// <returns>The transformed vector <c>(u·x + v·y, u·y + v·x)</c> — the split product, two products per component with one rounding.</returns>
    public FixedVector2 Transform(FixedVector2 vector) {
        const ulong NarrowLimit = (1UL << 31);
        var combinedMagnitude = (RawMagnitude(value: U.Value) |
                                 RawMagnitude(value: V.Value) |
                                 RawMagnitude(value: vector.X.Value) |
                                 RawMagnitude(value: vector.Y.Value));

        if (combinedMagnitude < NarrowLimit) {
            return new(
                X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((U.Value * vector.X.Value) + (V.Value * vector.Y.Value)))),
                Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((U.Value * vector.Y.Value) + (V.Value * vector.X.Value))))
            );
        }

        return new(
            X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)U.Value * vector.X.Value) + ((Int128)V.Value * vector.Y.Value)))),
            Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)U.Value * vector.Y.Value) + ((Int128)V.Value * vector.X.Value))))
        );
    }

    // Exact a·b ± c·d as sign + UInt128 magnitude; the Q32 product sum is one bit too wide for signed Int128 at the
    // extremes, so magnitude is tracked separately. Mirrors the FixedComplex helper.
    private static (bool Negative, UInt128 Magnitude) AddProducts(
        long firstLeft,
        long firstRight,
        long secondLeft,
        long secondRight,
        bool subtractSecond = false
    ) {
        var first = ((Int128)firstLeft * firstRight);
        var second = ((Int128)secondLeft * secondRight);
        var firstNegative = (first < Int128.Zero);
        var secondProductNegative = (second < Int128.Zero);
        var secondNegative = (secondProductNegative ^ subtractSecond);
        var firstMagnitude = (UInt128)(firstNegative ? -first : first);
        var secondMagnitude = (UInt128)(secondProductNegative ? -second : second);

        if (firstNegative == secondNegative) {
            return (firstNegative, (firstMagnitude + secondMagnitude));
        }

        return ((firstMagnitude >= secondMagnitude)
            ? (firstNegative, (firstMagnitude - secondMagnitude))
            : (secondNegative, (secondMagnitude - firstMagnitude)));
    }

    // round((numerator / denominator) · 2^16), ties to even, applying the combined sign of numerator and denominator.
    // The integer quotient is split off to avoid a 144-bit dividend; the sixteen fractional bits come from
    // overflow-safe restoring division. Mirrors the FixedComplex helper, generalized to a signed denominator.
    private static FixedQ4816 DivideProductSum((bool Negative, UInt128 Magnitude) numerator, (bool Negative, UInt128 Magnitude) denominator) {
        var magnitude = denominator.Magnitude;
        var integer = (numerator.Magnitude / magnitude);
        var remainder = (numerator.Magnitude - (integer * magnitude));
        var quotient = unchecked(((ulong)integer << FixedQ4816.FractionBitCount));

        for (var bit = (FixedQ4816.FractionBitCount - 1); (bit >= 0); --bit) {
            var complement = (magnitude - remainder);

            if (remainder >= complement) {
                remainder -= complement;
                quotient |= (1UL << bit);
            } else {
                remainder <<= 1;
            }
        }

        var distanceToNext = (magnitude - remainder);

        if ((remainder > distanceToNext) || ((remainder == distanceToNext) && ((quotient & 1UL) != 0UL))) {
            ++quotient;
        }

        var result = unchecked((long)quotient);

        return FixedQ4816.FromRawBits(value: ((numerator.Negative ^ denominator.Negative)
            ? unchecked(-result)
            : result));
    }

    private static ulong RawMagnitude(long value) {
        var sign = (value >> 63);

        return unchecked((ulong)((value ^ sign) - sign));
    }
}
