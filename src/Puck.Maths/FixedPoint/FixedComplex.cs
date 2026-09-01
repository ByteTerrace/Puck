using System.Numerics;
using System.Runtime.CompilerServices;

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
            return (TryMagnitude(magnitude: out var magnitude)
                ? magnitude
                : FixedQ4816.MaxValue
            );
        }
    }
    /// <summary>Gets the exact sum of two raw Q32 squares rounded once to Q16, saturating when the mathematical result
    /// exceeds <see cref="FixedQ4816.MaxValue"/>.</summary>
    public FixedQ4816 MagnitudeSquared {
        get {
            return (TryMagnitudeSquared(squaredMagnitude: out var squaredMagnitude)
                ? squaredMagnitude
                : FixedQ4816.MaxValue
            );
        }
    }
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
        if (ProductFitsLong(left: left, right: right)) {
            return new(
                Real: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((left.Real.Value * right.Real.Value) - (left.Imaginary.Value * right.Imaginary.Value))))),
                Imaginary: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((left.Real.Value * right.Imaginary.Value) + (left.Imaginary.Value * right.Real.Value)))))
            );
        }

        return new(
            Real: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)left.Real.Value) * right.Real.Value) - (((Int128)left.Imaginary.Value) * right.Imaginary.Value))))),
            Imaginary: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)left.Real.Value) * right.Imaginary.Value) + (((Int128)left.Imaginary.Value) * right.Real.Value)))))
        );
    }
    /// <summary>Returns the real component of <c>left * right</c>, rounded once — the same bits the product's
    /// <see cref="Real"/> would carry, without forming the imaginary component.</summary>
    internal static FixedQ4816 RealOfProduct(FixedComplex left, FixedComplex right) =>
        FixedQ4816.FromRawBits(value: (ProductFitsLong(left: left, right: right)
            ? FixedQ4816.RoundProductSum(productSum: unchecked(((left.Real.Value * right.Real.Value) - (left.Imaginary.Value * right.Imaginary.Value))))
            : FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)left.Real.Value) * right.Real.Value) - (((Int128)left.Imaginary.Value) * right.Imaginary.Value))))));
    // The narrow-lane gate, asymmetric in the operands: each component product is below 2^(bits(left) + bits(right)),
    // so a sum of two stays inside a signed long whenever those bit lengths total at most 62 — which a unit rotation
    // (raw components at most 2^16) satisfies against any operand below 2^46, not merely 2^31 against 2^31.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ProductFitsLong(FixedComplex left, FixedComplex right) {
        var leftMagnitude = (FusedArithmetic.RawMagnitude(value: left.Real.Value) | FusedArithmetic.RawMagnitude(value: left.Imaginary.Value));
        var rightMagnitude = (FusedArithmetic.RawMagnitude(value: right.Real.Value) | FusedArithmetic.RawMagnitude(value: right.Imaginary.Value));

        return ((BitOperations.LeadingZeroCount(value: leftMagnitude) + BitOperations.LeadingZeroCount(value: rightMagnitude)) >= 66);
    }
    /// <summary>Divides <paramref name="left"/> by <paramref name="right"/>.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor; must be non-zero.</param>
    /// <returns>The quotient <c>left·conj(right) / |right|²</c>, each component rounded once.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> is zero.</exception>
    public static FixedComplex operator /(FixedComplex left, FixedComplex right) {
        const ulong NarrowLimit = (1UL << 31);

        if (
            (FusedArithmetic.RawMagnitude(value: left.Real.Value) < NarrowLimit) &&
            (FusedArithmetic.RawMagnitude(value: left.Imaginary.Value) < NarrowLimit) &&
            (FusedArithmetic.RawMagnitude(value: right.Real.Value) < NarrowLimit) &&
            (FusedArithmetic.RawMagnitude(value: right.Imaginary.Value) < NarrowLimit)
        ) {
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
        var denominator = (FusedArithmetic.SquareMagnitude(value: right.Real.Value) + FusedArithmetic.SquareMagnitude(value: right.Imaginary.Value));

        if (denominator == UInt128.Zero) {
            throw new DivideByZeroException();
        }

        var realNumerator = FusedArithmetic.AddProducts(
            firstLeft: left.Real.Value,
            firstRight: right.Real.Value,
            secondLeft: left.Imaginary.Value,
            secondRight: right.Imaginary.Value
        );
        var imaginaryNumerator = FusedArithmetic.AddProducts(
            firstLeft: left.Imaginary.Value,
            firstRight: right.Real.Value,
            secondLeft: left.Real.Value,
            secondRight: right.Imaginary.Value,
            subtractSecond: true
        );

        return new(
            Real: FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                denominator: denominator,
                numerator: realNumerator
            )),
            Imaginary: FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                denominator: denominator,
                numerator: imaginaryNumerator
            ))
        );
    }

    private static FixedComplex NormalizeScaled(long real, long imaginary) {
        (real, imaginary) = FixedVectorMath.Normalize(
            x: real,
            y: imaginary
        );

        return new(
            Real: FixedQ4816.FromRawBits(value: real),
            Imaginary: FixedQ4816.FromRawBits(value: imaginary)
        );
    }

    /// <summary>Returns the conjugate — the inverse rotation for a unit complex number.</summary>
    /// <returns>The complex number with the imaginary component negated.</returns>
    public FixedComplex Conjugate() =>
        new(
            Real: Real,
            Imaginary: -Imaginary
        );
    /// <summary>Creates the unit rotation of <paramref name="angle"/> (fixed-point radians): the 2D exponential
    /// map, <c>exp(i·θ)</c> (the planar analog of <see cref="FixedQuaternion.Exp"/>, with no half-angle — 2D
    /// rotations compose one-sided).</summary>
    /// <param name="angle">The rotation angle in radians; positive angles rotate counterclockwise.</param>
    /// <returns>The unit complex number <c>cos θ + i·sin θ</c>.</returns>
    public static FixedComplex FromAngle(FixedQ4816 angle) {
        var (sin, cos) = FixedQ4816.SinCos(angle: angle);

        return new(
            Imaginary: sin,
            Real: cos
        );
    }
    /// <summary>Creates the rotation taking the direction of <paramref name="from"/> to the direction of
    /// <paramref name="to"/> — the normalized geometric product <c>(from·to, from∧to)</c>.</summary>
    /// <param name="from">The start direction; any non-zero magnitude.</param>
    /// <param name="to">The end direction; any non-zero magnitude.</param>
    /// <returns>The unit rotation with <c>Rotate(from)</c> along <paramref name="to"/>;
    /// <see cref="MultiplicativeIdentity"/> when either vector is zero, and the exact half turn <c>(−1, 0)</c> for
    /// antiparallel directions (unambiguous in 2D — see <see cref="FixedQuaternion.FromTo"/> for the 3D case).</returns>
    /// <remarks>Scale-free: the exact raw product sums are shifted into a fixed magnitude window before any Q16
    /// rounding — the down-shift itself rounds its discarded low bits to even when the sums outgrow the window, some
    /// thirty bits below the result's grid — so the angle survives inputs of any representable scale (rounding the
    /// products to Q16 first would erase vectors below 2⁻⁸).</remarks>
    public static FixedComplex FromTo(FixedVector2 from, FixedVector2 to) {
        const ulong NarrowLimit = (1UL << 31);
        var combinedMagnitude = FusedArithmetic.RawMagnitude(value: from.X.Value) |
                                 FusedArithmetic.RawMagnitude(value: from.Y.Value) |
                                 FusedArithmetic.RawMagnitude(value: to.X.Value) |
                                 FusedArithmetic.RawMagnitude(value: to.Y.Value);

        if (combinedMagnitude < NarrowLimit) {
            var dot = unchecked(((from.X.Value * to.X.Value) + (from.Y.Value * to.Y.Value)));
            var wedge = unchecked(((from.X.Value * to.Y.Value) - (from.Y.Value * to.X.Value)));

            if ((dot | wedge) == 0L) {
                return MultiplicativeIdentity;
            }

            var (real, imaginary) = FixedVectorMath.Normalize(
                x: dot,
                y: wedge
            );

            return new(
                Real: FixedQ4816.FromRawBits(value: real),
                Imaginary: FixedQ4816.FromRawBits(value: imaginary)
            );
        }

        var dotSum = FusedArithmetic.AddProducts(
            firstLeft: from.X.Value,
            firstRight: to.X.Value,
            secondLeft: from.Y.Value,
            secondRight: to.Y.Value
        );
        var wedgeSum = FusedArithmetic.AddProducts(
            firstLeft: from.X.Value,
            firstRight: to.Y.Value,
            secondLeft: from.Y.Value,
            secondRight: to.X.Value,
            subtractSecond: true
        );
        var magnitude = UInt128.Max(
            x: dotSum.Magnitude,
            y: wedgeSum.Magnitude
        );

        if (magnitude == UInt128.Zero) {
            return MultiplicativeIdentity;
        }

        // Land the larger component in [2^45, 2^46): only the direction of (dot, wedge) matters. The shared
        // normalizer retains this precision while keeping its shifted Q16 norm inside UInt128.
        var shift = (46 - FusedArithmetic.BitLength(value: magnitude));

        return NormalizeScaled(
            real: FusedArithmetic.ScaleProductSum(
                shift: shift,
                value: dotSum
            ),
            imaginary: FusedArithmetic.ScaleProductSum(
                shift: shift,
                value: wedgeSum
            )
        );
    }
    /// <summary>Returns the unit complex number along the same direction; zero normalizes to <see cref="MultiplicativeIdentity"/>.</summary>
    /// <returns>The normalized complex number.</returns>
    public FixedComplex Normalize() {
        var rawMagnitude = FixedVectorMath.RawMagnitude(value: Real.Value) | FixedVectorMath.RawMagnitude(value: Imaginary.Value);

        if (rawMagnitude == 0UL) {
            return MultiplicativeIdentity;
        }

        var (real, imaginary) = FixedVectorMath.Normalize(
            x: Real.Value,
            y: Imaginary.Value
        );

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

        if (
            ((FusedArithmetic.RawMagnitude(value: Real.Value) | FusedArithmetic.RawMagnitude(value: Imaginary.Value)) < RotationLimit) &&
            ((FusedArithmetic.RawMagnitude(value: vector.X.Value) | FusedArithmetic.RawMagnitude(value: vector.Y.Value)) < VectorLimit)
        ) {
            return new(
                X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Real.Value * vector.X.Value) - (Imaginary.Value * vector.Y.Value))))),
                Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Real.Value * vector.Y.Value) + (Imaginary.Value * vector.X.Value)))))
            );
        }

        return new(
            X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)Real.Value) * vector.X.Value) - (((Int128)Imaginary.Value) * vector.Y.Value))))),
            Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)Real.Value) * vector.Y.Value) + (((Int128)Imaginary.Value) * vector.X.Value)))))
        );
    }
    /// <summary>Converts to a double-precision <see cref="Complex"/> for presentation.</summary>
    /// <returns>The nearest double-precision complex number.</returns>
    public Complex ToComplex() =>
        new(
            real: ((double)Real),
            imaginary: ((double)Imaginary)
        );
    /// <summary>Tries to get the full-width magnitude. Returns <see langword="false"/> only when the nonnegative
    /// mathematical result is larger than <see cref="FixedQ4816.MaxValue"/>.</summary>
    public bool TryMagnitude(out FixedQ4816 magnitude) =>
        FixedVectorMath.TryMagnitude(
            x: Real.Value,
            y: Imaginary.Value,
            result: out magnitude
        );
    /// <summary>Tries to get the full-width squared magnitude after one ties-to-even Q16 rounding.</summary>
    public bool TryMagnitudeSquared(out FixedQ4816 squaredMagnitude) =>
        FixedVectorMath.TrySquaredMagnitude(
            x: Real.Value,
            y: Imaginary.Value,
            result: out squaredMagnitude
        );
}
