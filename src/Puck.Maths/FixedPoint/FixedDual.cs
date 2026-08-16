using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// Provides factory methods and derivative lifts for the <see cref="FixedDual{TValue}"/> struct.
/// </summary>
public static class FixedDual {
    private static readonly FixedQ4816 Two = FixedQ4816.FromRawBits(value: 131072L);

    /// <summary>Creates a constant (zero dual part).</summary>
    /// <typeparam name="TValue">The carrier type providing the required arithmetic operators and identities.</typeparam>
    /// <param name="value">The constant value.</param>
    /// <returns>The dual element <c>value + 0·ε</c>.</returns>
    public static FixedDual<TValue> Constant<TValue>(TValue value)
        where TValue : IAdditionOperators<TValue, TValue, TValue>,
                       ISubtractionOperators<TValue, TValue, TValue>,
                       IMultiplyOperators<TValue, TValue, TValue>,
                       IUnaryNegationOperators<TValue, TValue>,
                       IAdditiveIdentity<TValue, TValue>,
                       IMultiplicativeIdentity<TValue, TValue> =>
        new(
            Real: value,
            Dual: TValue.AdditiveIdentity
        );
    /// <summary>Divides <paramref name="left"/> by <paramref name="right"/> (the quotient rule, preserving operand order for non-commutative carriers).</summary>
    /// <typeparam name="TValue">The carrier type; its division must be the inverse of its multiplication on the right for the operands used.</typeparam>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor; its real part must be invertible.</param>
    /// <returns>The quotient: real part <c>a/c</c>, dual part the quotient-rule value — in exact arithmetic
    /// <c>(b − (a/c)·d)/c</c>, with the multiplication order preserved. Which fused form each carrier evaluates, and
    /// with how many roundings, is stated in the remarks.</returns>
    /// <remarks>Over the house scalar <see cref="FixedQ4816"/> the dual part is evaluated at full width as
    /// <c>(b·c − a·d)/c²</c> — mathematically the same as <c>(b − (a/c)·d)/c</c>, but the two raw Q32 numerator
    /// products accumulate exactly and one restoring division rounds to Q16 once, where the quotient-rule form would
    /// round the intermediate <c>a/c</c>, feed it through a product, and round again. A generic carrier takes the
    /// literal quotient-rule form through its own operators.</remarks>
    /// <exception cref="DivideByZeroException">The carrier's division refuses <paramref name="right"/>'s real part —
    /// over <see cref="FixedQ4816"/>, when it is zero; thrown before the dual denominator is squared.</exception>
    public static FixedDual<TValue> Divide<TValue>(FixedDual<TValue> left, FixedDual<TValue> right)
        where TValue : IAdditionOperators<TValue, TValue, TValue>,
                       ISubtractionOperators<TValue, TValue, TValue>,
                       IMultiplyOperators<TValue, TValue, TValue>,
                       IDivisionOperators<TValue, TValue, TValue>,
                       IUnaryNegationOperators<TValue, TValue>,
                       IAdditiveIdentity<TValue, TValue>,
                       IMultiplicativeIdentity<TValue, TValue> {
        // The typeof comparison folds to a JIT-time constant for every closed value-type instantiation, so non-FixedQ4816
        // carriers never see the fused seam or its raw casts.
        if (typeof(TValue) == typeof(FixedQ4816)) {
            var a = Unsafe.BitCast<TValue, FixedQ4816>(source: left.Real);
            var b = Unsafe.BitCast<TValue, FixedQ4816>(source: left.Dual).Value;
            var c = Unsafe.BitCast<TValue, FixedQ4816>(source: right.Real);
            var d = Unsafe.BitCast<TValue, FixedQ4816>(source: right.Dual).Value;
            // Real = a/c (correctly-rounded carrier division, thrown on a zero divisor before the dual denominator is
            // squared). Dual numerator b·c − a·d as sign + magnitude; denominator c²; one ties-to-even rounding.
            var real = (a / c);
            var numerator = FusedArithmetic.AddProducts(
                firstLeft: b,
                firstRight: c.Value,
                secondLeft: a.Value,
                secondRight: d,
                subtractSecond: true
            );
            var dual = FusedArithmetic.DivideProductSum(
                numerator: numerator,
                denominator: FusedArithmetic.SquareMagnitude(value: c.Value)
            );

            return new(
                Real: Unsafe.BitCast<FixedQ4816, TValue>(source: real),
                Dual: Unsafe.BitCast<FixedQ4816, TValue>(source: FixedQ4816.FromRawBits(value: dual))
            );
        }

        var quotient = (left.Real / right.Real);

        return new(
            Real: quotient,
            Dual: ((left.Dual - (quotient * right.Dual)) / right.Real)
        );
    }
    /// <summary>Computes the base-2 logarithm and its derivative.</summary>
    /// <param name="value">The operand; a non-positive value component yields <c>(MinValue, 0)</c>.</param>
    /// <returns><c>log2(a) + (b·log2(e)/a)·ε</c>.</returns>
    public static FixedDual<FixedQ4816> Log2(FixedDual<FixedQ4816> value) {
        if (value.Real.Value <= 0L) {
            return new(
                Real: FixedQ4816.MinValue,
                Dual: FixedQ4816.Zero
            );
        }

        // Dual = (b·log2(e))/a at full width: the raw Q32 product b·log2e over a raw Q16 a scaled by 2^16, so the ratio
        // is b·log2e/a, with one ties-to-even rounding. The prior round(b·log2e)/a rounded twice.
        return new(
            Real: FixedQ4816.Log2(value: value.Real),
            Dual: FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                numerator: FusedArithmetic.Product(
                    left: value.Dual.Value,
                    right: FixedQ4816.Log2E.Value
                ),
                denominator: (((UInt128)FusedArithmetic.RawMagnitude(value: value.Real.Value)) << FixedQ4816.FractionBitCount)
            ))
        );
    }
    /// <summary>Computes the sine and cosine and their derivatives.</summary>
    /// <param name="angle">The angle in fixed-point radians.</param>
    /// <returns>The pair <c>(sin a + b·cos a·ε, cos a − b·sin a·ε)</c>.</returns>
    public static (FixedDual<FixedQ4816> Sin, FixedDual<FixedQ4816> Cos) SinCos(FixedDual<FixedQ4816> angle) {
        var (sin, cos) = FixedQ4816.SinCos(angle: angle.Real);

        return (
            new(
            Real: sin,
            Dual: (angle.Dual * cos)
        ),
            new(
            Real: cos,
            Dual: -(angle.Dual * sin)
        )
        );
    }
    /// <summary>Computes the square root and its derivative.</summary>
    /// <param name="value">The operand; a non-positive value component yields <c>(0, 0)</c> (the derivative is undefined there).</param>
    /// <returns><c>√a + (b/(2√a))·ε</c>.</returns>
    public static FixedDual<FixedQ4816> Sqrt(FixedDual<FixedQ4816> value) {
        if (value.Real.Value <= 0L) {
            return new(
                Real: FixedQ4816.Zero,
                Dual: FixedQ4816.Zero
            );
        }

        var root = FixedQ4816.Sqrt(value: value.Real);

        return new(
            Real: root,
            Dual: (value.Dual / (root * Two))
        );
    }
    /// <summary>Creates the differentiation variable (unit dual part).</summary>
    /// <typeparam name="TValue">The carrier type providing the required arithmetic operators and identities.</typeparam>
    /// <param name="value">The value to differentiate with respect to.</param>
    /// <returns>The dual element <c>value + 1·ε</c>.</returns>
    public static FixedDual<TValue> Variable<TValue>(TValue value)
        where TValue : IAdditionOperators<TValue, TValue, TValue>,
                       ISubtractionOperators<TValue, TValue, TValue>,
                       IMultiplyOperators<TValue, TValue, TValue>,
                       IUnaryNegationOperators<TValue, TValue>,
                       IAdditiveIdentity<TValue, TValue>,
                       IMultiplicativeIdentity<TValue, TValue> =>
        new(
            Real: value,
            Dual: TValue.MultiplicativeIdentity
        );

}
/// <summary>
/// The dual construction <c>a + b·ε</c> (<c>ε² = 0</c>) over any carrier that supplies the required arithmetic
/// operators and identities: instantiated with
/// <see cref="FixedQ4816"/> it carries a quantized formal forward-mode sensitivity (seed with
/// <see cref="FixedDual.Variable{TValue}"/> and the result's <see cref="Dual"/> follows the chain rule for the ideal
/// operator expression). The raw fixed-point program itself is discrete, so this is not its classical derivative;
/// instantiated with <see cref="FixedQuaternion"/> it is the dual quaternion behind
/// <see cref="FixedRigidTransform"/>. Deterministic and bit-identical across machines, like the carrier it wraps.
/// The constraints describe available operations rather than algebraic laws; rounded fixed-point multiplication is
/// not associative under bitwise equality.
/// </summary>
/// <typeparam name="TValue">The carrier type providing the required arithmetic operators and identities.</typeparam>
/// <param name="Real">The real (value) part.</param>
/// <param name="Dual">The dual (formal sensitivity/infinitesimal) part.</param>
public readonly record struct FixedDual<TValue>(TValue Real, TValue Dual)
    : IAdditionOperators<FixedDual<TValue>, FixedDual<TValue>, FixedDual<TValue>>,
      ISubtractionOperators<FixedDual<TValue>, FixedDual<TValue>, FixedDual<TValue>>,
      IMultiplyOperators<FixedDual<TValue>, FixedDual<TValue>, FixedDual<TValue>>,
      IUnaryNegationOperators<FixedDual<TValue>, FixedDual<TValue>>,
      IAdditiveIdentity<FixedDual<TValue>, FixedDual<TValue>>,
      IMultiplicativeIdentity<FixedDual<TValue>, FixedDual<TValue>>
    where TValue : IAdditionOperators<TValue, TValue, TValue>,
                   ISubtractionOperators<TValue, TValue, TValue>,
                   IMultiplyOperators<TValue, TValue, TValue>,
                   IUnaryNegationOperators<TValue, TValue>,
                   IAdditiveIdentity<TValue, TValue>,
                   IMultiplicativeIdentity<TValue, TValue> {
    /// <summary>Gets the additive identity, <c>0 + 0·ε</c>.</summary>
    public static FixedDual<TValue> AdditiveIdentity => new(
        Real: TValue.AdditiveIdentity,
        Dual: TValue.AdditiveIdentity
    );
    /// <summary>Gets the multiplicative identity, <c>1 + 0·ε</c>.</summary>
    public static FixedDual<TValue> MultiplicativeIdentity => new(
        Real: TValue.MultiplicativeIdentity,
        Dual: TValue.AdditiveIdentity
    );

    /// <summary>Negates a dual element.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The componentwise negation.</returns>
    public static FixedDual<TValue> operator -(FixedDual<TValue> value) =>
        new(
            Real: -value.Real,
            Dual: -value.Dual
        );
    /// <summary>Adds two dual elements.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static FixedDual<TValue> operator +(FixedDual<TValue> left, FixedDual<TValue> right) =>
        new(
            Real: (left.Real + right.Real),
            Dual: (left.Dual + right.Dual)
        );
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/>.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static FixedDual<TValue> operator -(FixedDual<TValue> left, FixedDual<TValue> right) =>
        new(
            Real: (left.Real - right.Real),
            Dual: (left.Dual - right.Dual)
        );
    /// <summary>Multiplies two dual elements (the product rule; factor order is preserved for non-commutative carriers).</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns><c>(a·c) + (a·d + b·c)·ε</c>.</returns>
    /// <remarks>Over the house carriers the dual part is fused: for <see cref="FixedQ4816"/> the sum <c>a·d + b·c</c>
    /// widens both raw Q32 products and rounds once; for <see cref="FixedQuaternion"/> — the production dual quaternion
    /// behind <see cref="FixedRigidTransform"/> — the two Hamilton products are fused across the dual seam so each
    /// output component accumulates its eight leaf products before a single rounding.</remarks>
    public static FixedDual<TValue> operator *(FixedDual<TValue> left, FixedDual<TValue> right) {
        // Both typeof comparisons fold to JIT-time constants for every closed value-type instantiation, so a carrier
        // that is neither house type never sees the fused kernels or their raw casts.
        if (typeof(TValue) == typeof(FixedQ4816)) {
            return MultiplyScalar(
                left: left,
                right: right
            );
        }

        if (typeof(TValue) == typeof(FixedQuaternion)) {
            return MultiplyQuaternion(
                left: left,
                right: right
            );
        }

        return new(
            Real: (left.Real * right.Real),
            Dual: ((left.Real * right.Dual) + (left.Dual * right.Real))
        );
    }

    // Fused product for the FixedQuaternion carrier — the production dual quaternion behind FixedRigidTransform. Real
    // is the ordinary fused Hamilton product a·c. Dual = a·d + b·c is fused ACROSS the dual seam: each output component
    // accumulates the EIGHT leaf Q32 products (four from the Hamilton product a·d, four from b·c, in the exact signed
    // term layout FixedQuaternion.operator * uses per component) before one ties-to-even Q16 rounding, so the two
    // Hamilton products share a single rounding rather than rounding separately and adding.
    // Narrow bound: eight products of operands below 2^B sum below 8·2^2B, which stays in a signed long while
    // 8·2^2B < 2^63, i.e. B ≤ 29 — the symmetric gate. The asymmetric gate mirrors FixedComplex.Rotate: a rotation
    // side (a and c) below 2^17 times a translation side (b and d) below 2^42 gives products below 2^59, eight of them
    // below 2^62, covering unit rotations carrying translations to ~2^26 units. Take the long path when EITHER gate
    // passes, Int128 otherwise. Reached only under the JIT-constant guard in operator *.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static FixedDual<TValue> MultiplyQuaternion(FixedDual<TValue> left, FixedDual<TValue> right) {
        var a = Unsafe.BitCast<TValue, FixedQuaternion>(source: left.Real);
        var b = Unsafe.BitCast<TValue, FixedQuaternion>(source: left.Dual);
        var c = Unsafe.BitCast<TValue, FixedQuaternion>(source: right.Real);
        var d = Unsafe.BitCast<TValue, FixedQuaternion>(source: right.Dual);
        var real = (a * c);

        var (ax, ay, az, aw) = (a.X.Value, a.Y.Value, a.Z.Value, a.W.Value);
        var (bx, by, bz, bw) = (b.X.Value, b.Y.Value, b.Z.Value, b.W.Value);
        var (cx, cy, cz, cw) = (c.X.Value, c.Y.Value, c.Z.Value, c.W.Value);
        var (dx, dy, dz, dw) = (d.X.Value, d.Y.Value, d.Z.Value, d.W.Value);
        var rotationMagnitude = FusedArithmetic.RawMagnitude(value: ax) | FusedArithmetic.RawMagnitude(value: ay) |
                                 FusedArithmetic.RawMagnitude(value: az) | FusedArithmetic.RawMagnitude(value: aw) |
                                 FusedArithmetic.RawMagnitude(value: cx) | FusedArithmetic.RawMagnitude(value: cy) |
                                 FusedArithmetic.RawMagnitude(value: cz) | FusedArithmetic.RawMagnitude(value: cw);
        var dualMagnitude = FusedArithmetic.RawMagnitude(value: bx) | FusedArithmetic.RawMagnitude(value: by) |
                             FusedArithmetic.RawMagnitude(value: bz) | FusedArithmetic.RawMagnitude(value: bw) |
                             FusedArithmetic.RawMagnitude(value: dx) | FusedArithmetic.RawMagnitude(value: dy) |
                             FusedArithmetic.RawMagnitude(value: dz) | FusedArithmetic.RawMagnitude(value: dw);
        var narrow = (((rotationMagnitude | dualMagnitude) < (1UL << 29)) ||
                      ((rotationMagnitude < (1UL << 17)) && (dualMagnitude < (1UL << 42))));
        long dualX;
        long dualY;
        long dualZ;
        long dualW;

        if (narrow) {
            dualX = FixedQ4816.RoundProductSum(productSum: unchecked(
                (((((aw * dx) + (ax * dw)) + (ay * dz)) - (az * dy)) + ((((bw * cx) + (bx * cw)) + (by * cz)) - (bz * cy)))));
            dualY = FixedQ4816.RoundProductSum(productSum: unchecked(
                (((((aw * dy) - (ax * dz)) + (ay * dw)) + (az * dx)) + ((((bw * cy) - (bx * cz)) + (by * cw)) + (bz * cx)))));
            dualZ = FixedQ4816.RoundProductSum(productSum: unchecked(
                (((((aw * dz) + (ax * dy)) - (ay * dx)) + (az * dw)) + ((((bw * cz) + (bx * cy)) - (by * cx)) + (bz * cw)))));
            dualW = FixedQ4816.RoundProductSum(productSum: unchecked(
                (((((aw * dw) - (ax * dx)) - (ay * dy)) - (az * dz)) + ((((bw * cw) - (bx * cx)) - (by * cy)) - (bz * cz)))));
        } else {
            dualX = FixedQ4816.RoundProductSum(productSum: unchecked(
                (((((((Int128)aw) * dx) + (((Int128)ax) * dw)) + (((Int128)ay) * dz)) - (((Int128)az) * dy)) +
                ((((((Int128)bw) * cx) + (((Int128)bx) * cw)) + (((Int128)by) * cz)) - (((Int128)bz) * cy)))));
            dualY = FixedQ4816.RoundProductSum(productSum: unchecked(
                (((((((Int128)aw) * dy) - (((Int128)ax) * dz)) + (((Int128)ay) * dw)) + (((Int128)az) * dx)) +
                ((((((Int128)bw) * cy) - (((Int128)bx) * cz)) + (((Int128)by) * cw)) + (((Int128)bz) * cx)))));
            dualZ = FixedQ4816.RoundProductSum(productSum: unchecked(
                (((((((Int128)aw) * dz) + (((Int128)ax) * dy)) - (((Int128)ay) * dx)) + (((Int128)az) * dw)) +
                ((((((Int128)bw) * cz) + (((Int128)bx) * cy)) - (((Int128)by) * cx)) + (((Int128)bz) * cw)))));
            dualW = FixedQ4816.RoundProductSum(productSum: unchecked(
                (((((((Int128)aw) * dw) - (((Int128)ax) * dx)) - (((Int128)ay) * dy)) - (((Int128)az) * dz)) +
                ((((((Int128)bw) * cw) - (((Int128)bx) * cx)) - (((Int128)by) * cy)) - (((Int128)bz) * cz)))));
        }

        var dual = new FixedQuaternion(
            X: FixedQ4816.FromRawBits(value: dualX),
            Y: FixedQ4816.FromRawBits(value: dualY),
            Z: FixedQ4816.FromRawBits(value: dualZ),
            W: FixedQ4816.FromRawBits(value: dualW)
        );

        return new(
            Real: Unsafe.BitCast<FixedQuaternion, TValue>(source: real),
            Dual: Unsafe.BitCast<FixedQuaternion, TValue>(source: dual)
        );
    }
    // Fused product for the FixedQ4816 carrier: Real = round(a·c) computed from the raw Q32 product inline (the generic
    // path makes three out-of-line carrier multiplies); Dual = ONE rounding of (a·d + b·c). Bit-identical to the fused
    // (0, 0) QuadraticAlgebra kernel. Reached only under the JIT-constant guard in operator *.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static FixedDual<TValue> MultiplyScalar(FixedDual<TValue> left, FixedDual<TValue> right) {
        var a = Unsafe.BitCast<TValue, FixedQ4816>(source: left.Real).Value;
        var b = Unsafe.BitCast<TValue, FixedQ4816>(source: left.Dual).Value;
        var c = Unsafe.BitCast<TValue, FixedQ4816>(source: right.Real).Value;
        var d = Unsafe.BitCast<TValue, FixedQ4816>(source: right.Dual).Value;
        var combinedMagnitude = FusedArithmetic.RawMagnitude(value: a) | FusedArithmetic.RawMagnitude(value: b) |
                                 FusedArithmetic.RawMagnitude(value: c) | FusedArithmetic.RawMagnitude(value: d);
        long real;
        long dual;

        if (combinedMagnitude < (1UL << 31)) {
            // Every raw below 2^31: the single Real product stays below 2^62, and the two Dual products sum below 2^63,
            // so both fit a signed long.
            real = FixedQ4816.RoundProductSum(productSum: unchecked((a * c)));
            dual = FixedQ4816.RoundProductSum(productSum: unchecked(((a * d) + (b * c))));
        } else {
            real = FixedQ4816.RoundProductSum(productSum: (((Int128)a) * c));
            dual = FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)a) * d) + (((Int128)b) * c))));
        }

        return new(
            Real: Unsafe.BitCast<FixedQ4816, TValue>(source: FixedQ4816.FromRawBits(value: real)),
            Dual: Unsafe.BitCast<FixedQ4816, TValue>(source: FixedQ4816.FromRawBits(value: dual))
        );
    }
}
