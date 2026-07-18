using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A complex number of <see cref="FixedQ4816"/> components: the deterministic 2D rotation primitive (the yaw-plane
/// analog of <see cref="FixedQuaternion"/>) — multiplication composes rotations, <see cref="Rotate"/> applies one
/// to a vector. Pure integer arithmetic; identical inputs produce identical bits on every machine. Polynomial
/// operations widen every product, accumulate the complete expression, and round once per returned component.
/// </summary>
/// <param name="Real">The real component.</param>
/// <param name="Imaginary">The imaginary component.</param>
public readonly record struct FixedComplex(FixedQ4816 Real, FixedQ4816 Imaginary)
    : IAdditionOperators<FixedComplex, FixedComplex, FixedComplex>,
      ISubtractionOperators<FixedComplex, FixedComplex, FixedComplex>,
      IMultiplyOperators<FixedComplex, FixedComplex, FixedComplex>,
      IDivisionOperators<FixedComplex, FixedComplex, FixedComplex>,
      IUnaryNegationOperators<FixedComplex, FixedComplex>,
      IAdditiveIdentity<FixedComplex, FixedComplex>,
      IMultiplicativeIdentity<FixedComplex, FixedComplex> {
    /// <summary>Gets the additive identity, zero.</summary>
    public static FixedComplex AdditiveIdentity => default;
    /// <summary>Gets the multiplicative identity, one (the identity rotation).</summary>
    public static FixedComplex MultiplicativeIdentity => new(
        Real: FixedQ4816.One,
        Imaginary: FixedQ4816.Zero
    );

    /// <summary>Negates a complex number.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The componentwise negation.</returns>
    public static FixedComplex operator -(FixedComplex value) =>
        new(
        Real: -value.Real,
        Imaginary: -value.Imaginary
    );
    /// <summary>Adds two complex numbers.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static FixedComplex operator +(FixedComplex left, FixedComplex right) =>
        new(
        Real: (left.Real + right.Real),
        Imaginary: (left.Imaginary + right.Imaginary)
    );
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/>.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static FixedComplex operator -(FixedComplex left, FixedComplex right) =>
        new(
        Real: (left.Real - right.Real),
        Imaginary: (left.Imaginary - right.Imaginary)
    );
    /// <summary>Multiplies two complex numbers (composes rotations for unit operands).</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product, each component's two products accumulated exactly with one rounding.</returns>
    public static FixedComplex operator *(FixedComplex left, FixedComplex right) {
        const ulong NarrowLimit = (1UL << 31);
        var combinedMagnitude = (RawMagnitude(value: left.Real.Value) |
                                 RawMagnitude(value: left.Imaginary.Value) |
                                 RawMagnitude(value: right.Real.Value) |
                                 RawMagnitude(value: right.Imaginary.Value));

        if (combinedMagnitude < NarrowLimit) {
            return new(
                Real: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((left.Real.Value * right.Real.Value) - (left.Imaginary.Value * right.Imaginary.Value)))),
                Imaginary: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((left.Real.Value * right.Imaginary.Value) + (left.Imaginary.Value * right.Real.Value))))
            );
        }

        return new(
            Real: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)left.Real.Value * right.Real.Value) - ((Int128)left.Imaginary.Value * right.Imaginary.Value)))),
            Imaginary: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)left.Real.Value * right.Imaginary.Value) + ((Int128)left.Imaginary.Value * right.Real.Value))))
        );
    }
    /// <summary>Divides <paramref name="left"/> by <paramref name="right"/>.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor; must be non-zero.</param>
    /// <returns>The quotient <c>left·conj(right) / |right|²</c>, each component rounded once.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> is zero.</exception>
    public static FixedComplex operator /(FixedComplex left, FixedComplex right) {
        const ulong NarrowLimit = (1UL << 31);

        if ((RawMagnitude(value: left.Real.Value) < NarrowLimit) &&
            (RawMagnitude(value: left.Imaginary.Value) < NarrowLimit) &&
            (RawMagnitude(value: right.Real.Value) < NarrowLimit) &&
            (RawMagnitude(value: right.Imaginary.Value) < NarrowLimit)) {
            // Exact-equivalent fast path: every product sum fits Int64 in this range, so the scalar divider produces
            // the same result as the full-width sign/magnitude path without its UInt128 restoring division.
            var narrowDenominator = FixedQ4816.FromRawBits(value: unchecked(((right.Real.Value * right.Real.Value) + (right.Imaginary.Value * right.Imaginary.Value))));

            return new(
                Real: (FixedQ4816.FromRawBits(value: unchecked(((left.Real.Value * right.Real.Value) + (left.Imaginary.Value * right.Imaginary.Value)))) / narrowDenominator),
                Imaginary: (FixedQ4816.FromRawBits(value: unchecked(((left.Imaginary.Value * right.Real.Value) - (left.Real.Value * right.Imaginary.Value)))) / narrowDenominator)
            );
        }

        // Keep the Q32 products at full width. A signed Int128 sum is one bit too narrow for the positive extreme
        // (MinValue*MinValue + MinValue*MinValue == 2^127), so product sums use sign + UInt128 magnitude.
        var denominator = (SquareMagnitude(value: right.Real.Value) + SquareMagnitude(value: right.Imaginary.Value));

        if (denominator == UInt128.Zero) {
            throw new DivideByZeroException();
        }

        var realNumerator = AddProducts(
            firstLeft: left.Real.Value,
            firstRight: right.Real.Value,
            secondLeft: left.Imaginary.Value,
            secondRight: right.Imaginary.Value
        );
        var imaginaryNumerator = AddProducts(
            firstLeft: left.Imaginary.Value,
            firstRight: right.Real.Value,
            secondLeft: left.Real.Value,
            secondRight: right.Imaginary.Value,
            subtractSecond: true
        );

        return new(
            Real: DivideProductSum(numerator: realNumerator, denominator: denominator),
            Imaginary: DivideProductSum(numerator: imaginaryNumerator, denominator: denominator)
        );
    }

    /// <summary>Creates the unit rotation of <paramref name="angle"/> (fixed-point radians): the 2D exponential
    /// map, <c>exp(i·θ)</c> (the planar analog of <see cref="FixedQuaternion.Exp"/>, with no half-angle — 2D
    /// rotations compose one-sided).</summary>
    /// <param name="angle">The rotation angle in radians; positive angles rotate counterclockwise.</param>
    /// <returns>The unit complex number <c>cos θ + i·sin θ</c>.</returns>
    public static FixedComplex FromAngle(FixedQ4816 angle) {
        var (sin, cos) = FixedQ4816.SinCos(angle: angle);

        return new(
            Real: cos,
            Imaginary: sin
        );
    }

    /// <summary>Creates the rotation taking the direction of <paramref name="from"/> to the direction of
    /// <paramref name="to"/> — the normalized geometric product <c>(from·to, from∧to)</c>.</summary>
    /// <param name="from">The start direction; any non-zero magnitude.</param>
    /// <param name="to">The end direction; any non-zero magnitude.</param>
    /// <returns>The unit rotation with <c>Rotate(from)</c> along <paramref name="to"/>;
    /// <see cref="MultiplicativeIdentity"/> when either vector is zero, and the exact half turn <c>(−1, 0)</c> for
    /// antiparallel directions (unambiguous in 2D — see <see cref="FixedQuaternion.FromTo"/> for the 3D case).</returns>
    /// <remarks>Scale-free: the exact raw product sums are shifted into a fixed magnitude window before any
    /// rounding, so the angle survives inputs of any representable scale (rounding the products to Q16 first would
    /// erase vectors below 2⁻⁸).</remarks>
    public static FixedComplex FromTo(FixedVector2 from, FixedVector2 to) {
        const ulong NarrowLimit = (1UL << 31);
        var combinedMagnitude = (RawMagnitude(value: from.X.Value) |
                                 RawMagnitude(value: from.Y.Value) |
                                 RawMagnitude(value: to.X.Value) |
                                 RawMagnitude(value: to.Y.Value));

        if (combinedMagnitude < NarrowLimit) {
            var dot = unchecked((from.X.Value * to.X.Value) + (from.Y.Value * to.Y.Value));
            var wedge = unchecked((from.X.Value * to.Y.Value) - (from.Y.Value * to.X.Value));

            if ((dot | wedge) == 0L) {
                return MultiplicativeIdentity;
            }

            var (real, imaginary) = FixedVectorMath.Normalize(x: dot, y: wedge);

            return new(Real: FixedQ4816.FromRawBits(value: real), Imaginary: FixedQ4816.FromRawBits(value: imaginary));
        }

        var dotSum = AddProducts(
            firstLeft: from.X.Value,
            firstRight: to.X.Value,
            secondLeft: from.Y.Value,
            secondRight: to.Y.Value
        );
        var wedgeSum = AddProducts(
            firstLeft: from.X.Value,
            firstRight: to.Y.Value,
            secondLeft: from.Y.Value,
            secondRight: to.X.Value,
            subtractSecond: true
        );
        var magnitude = UInt128.Max(dotSum.Magnitude, wedgeSum.Magnitude);

        if (magnitude == UInt128.Zero) {
            return MultiplicativeIdentity;
        }

        // Land the larger component in [2^45, 2^46): only the direction of (dot, wedge) matters. The shared
        // normalizer retains this precision while keeping its shifted Q16 norm inside UInt128.
        var shift = (46 - BitLength(value: magnitude));

        return NormalizeScaled(
            real: ScaleProductSum(value: dotSum, shift: shift),
            imaginary: ScaleProductSum(value: wedgeSum, shift: shift)
        );
    }

    private static int BitLength(UInt128 value) {
        var high = ((ulong)(value >> 64));

        return ((high != 0UL)
            ? (128 - BitOperations.LeadingZeroCount(value: high))
            : (64 - BitOperations.LeadingZeroCount(value: ((ulong)value))));
    }

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

    // round((numerator / denominator) * 2^16), ties to even, retaining the scalar type's wrapping result semantics.
    // Splitting off the integer quotient avoids a potentially 144-bit numerator. The remaining sixteen fractional
    // bits are generated by overflow-safe restoring division; denominator - remainder replaces the unsafe 2*r test.
    private static FixedQ4816 DivideProductSum((bool Negative, UInt128 Magnitude) numerator, UInt128 denominator) {
        var integer = (numerator.Magnitude / denominator);
        var remainder = (numerator.Magnitude - (integer * denominator));
        var quotient = unchecked(((ulong)integer << FixedQ4816.FractionBitCount));

        for (var bit = (FixedQ4816.FractionBitCount - 1); (bit >= 0); --bit) {
            var complement = (denominator - remainder);

            if (remainder >= complement) {
                remainder -= complement;
                quotient |= (1UL << bit);
            } else {
                remainder <<= 1;
            }
        }

        var distanceToNext = (denominator - remainder);

        if ((remainder > distanceToNext) || ((remainder == distanceToNext) && ((quotient & 1UL) != 0UL))) {
            ++quotient;
        }

        var result = unchecked((long)quotient);

        return FixedQ4816.FromRawBits(value: (numerator.Negative
            ? unchecked(-result)
            : result));
    }

    private static long ScaleProductSum((bool Negative, UInt128 Magnitude) value, int shift) {
        UInt128 magnitude;

        if (shift >= 0) {
            magnitude = (value.Magnitude << shift);
        } else {
            var rightShift = -shift;
            magnitude = (value.Magnitude >> rightShift);
            var remainder = (value.Magnitude & ((((UInt128)1) << rightShift) - UInt128.One));
            var half = (((UInt128)1) << (rightShift - 1));

            if ((remainder > half) || ((remainder == half) && ((magnitude & UInt128.One) != UInt128.Zero))) {
                ++magnitude;
            }
        }

        var raw = ((long)magnitude);

        return (value.Negative
            ? -raw
            : raw);
    }

    private static UInt128 SquareMagnitude(long value) {
        var magnitude = RawMagnitude(value: value);

        return ((UInt128)magnitude * magnitude);
    }

    private static ulong RawMagnitude(long value) {
        var sign = (value >> 63);

        return unchecked((ulong)((value ^ sign) - sign));
    }

    /// <summary>Gets the angle from the positive real axis, in <c>(−π, π]</c> fixed-point radians — for a unit
    /// complex number, the logarithm (the inverse of <see cref="FromAngle"/>; the planar analog of
    /// <see cref="FixedQuaternion.Log"/>).</summary>
    public FixedQ4816 Argument => FixedQ4816.Atan2(
        y: Imaginary,
        x: Real
    );
    /// <summary>Gets the exact raw Q32 magnitude rounded to Q16, saturating only when the mathematical magnitude is
    /// larger than <see cref="FixedQ4816.MaxValue"/>.</summary>
    public FixedQ4816 Magnitude {
        get {
            return (TryMagnitude(out var magnitude)
                ? magnitude
                : FixedQ4816.MaxValue);
        }
    }
    /// <summary>Gets the exact sum of two raw Q32 squares rounded once to Q16, saturating when the mathematical result
    /// exceeds <see cref="FixedQ4816.MaxValue"/>.</summary>
    public FixedQ4816 MagnitudeSquared {
        get {
            return (TryMagnitudeSquared(out var squaredMagnitude)
                ? squaredMagnitude
                : FixedQ4816.MaxValue);
        }
    }

    /// <summary>Tries to get the full-width magnitude. Returns <see langword="false"/> only when the nonnegative
    /// mathematical result is larger than <see cref="FixedQ4816.MaxValue"/>.</summary>
    public bool TryMagnitude(out FixedQ4816 magnitude) =>
        FixedVectorMath.TryMagnitude(x: Real.Value, y: Imaginary.Value, result: out magnitude);

    /// <summary>Tries to get the full-width squared magnitude after one ties-to-even Q16 rounding.</summary>
    public bool TryMagnitudeSquared(out FixedQ4816 squaredMagnitude) =>
        FixedVectorMath.TrySquaredMagnitude(x: Real.Value, y: Imaginary.Value, result: out squaredMagnitude);

    /// <summary>Returns the conjugate — the inverse rotation for a unit complex number.</summary>
    /// <returns>The complex number with the imaginary component negated.</returns>
    public FixedComplex Conjugate() =>
        new(
        Real: Real,
        Imaginary: -Imaginary
    );
    /// <summary>Returns the unit complex number along the same direction; zero normalizes to <see cref="MultiplicativeIdentity"/>.</summary>
    /// <returns>The normalized complex number.</returns>
    public FixedComplex Normalize() {
        var rawMagnitude = (FixedVectorMath.RawMagnitude(value: Real.Value) | FixedVectorMath.RawMagnitude(value: Imaginary.Value));

        if (rawMagnitude == 0UL) {
            return MultiplicativeIdentity;
        }

        var (real, imaginary) = FixedVectorMath.Normalize(x: Real.Value, y: Imaginary.Value);

        return new(
            Real: FixedQ4816.FromRawBits(value: real),
            Imaginary: FixedQ4816.FromRawBits(value: imaginary)
        );
    }

    private static FixedComplex NormalizeScaled(long real, long imaginary) {
        (real, imaginary) = FixedVectorMath.Normalize(x: real, y: imaginary);

        return new(
            Real: FixedQ4816.FromRawBits(value: real),
            Imaginary: FixedQ4816.FromRawBits(value: imaginary)
        );
    }
    /// <summary>Rotates a 2D vector by this complex number, which must be unit length.</summary>
    /// <param name="vector">The vector to rotate.</param>
    /// <returns>The rotated vector (the complex product, two products per component with one rounding).</returns>
    public FixedVector2 Rotate(FixedVector2 vector) {
        const ulong RotationLimit = (1UL << 17);
        const ulong VectorLimit = (1UL << 45);

        if (((RawMagnitude(value: Real.Value) | RawMagnitude(value: Imaginary.Value)) < RotationLimit) &&
            ((RawMagnitude(value: vector.X.Value) | RawMagnitude(value: vector.Y.Value)) < VectorLimit)) {
            return new(
                X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((Real.Value * vector.X.Value) - (Imaginary.Value * vector.Y.Value)))),
                Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((Real.Value * vector.Y.Value) + (Imaginary.Value * vector.X.Value))))
            );
        }

        return new(
            X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)Real.Value * vector.X.Value) - ((Int128)Imaginary.Value * vector.Y.Value)))),
            Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)Real.Value * vector.Y.Value) + ((Int128)Imaginary.Value * vector.X.Value))))
        );
    }
    /// <summary>Converts to a double-precision <see cref="Complex"/> for presentation.</summary>
    /// <returns>The nearest double-precision complex number.</returns>
    public Complex ToComplex() =>
        new(
        real: ((double)Real),
        imaginary: ((double)Imaginary)
    );
}
