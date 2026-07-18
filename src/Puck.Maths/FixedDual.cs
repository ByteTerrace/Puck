using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// Provides factory methods and derivative lifts for the <see cref="FixedDual{TValue}"/> struct.
/// </summary>
public static class FixedDual {
    private static readonly FixedQ4816 Log2E = FixedQ4816.FromRawBits(value: 94548L); // round(log2(e) · 2^16)
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

    /// <summary>Divides <paramref name="left"/> by <paramref name="right"/> (the quotient rule, preserving operand order for non-commutative carriers).</summary>
    /// <typeparam name="TValue">The carrier type; its division must be the inverse of its multiplication on the right for the operands used.</typeparam>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor; its real part must be invertible.</param>
    /// <returns><c>(a/c) + ((b − (a/c)·d)/c)·ε</c>, with the multiplication order preserved.</returns>
    public static FixedDual<TValue> Divide<TValue>(FixedDual<TValue> left, FixedDual<TValue> right)
        where TValue : IAdditionOperators<TValue, TValue, TValue>,
                       ISubtractionOperators<TValue, TValue, TValue>,
                       IMultiplyOperators<TValue, TValue, TValue>,
                       IDivisionOperators<TValue, TValue, TValue>,
                       IUnaryNegationOperators<TValue, TValue>,
                       IAdditiveIdentity<TValue, TValue>,
                       IMultiplicativeIdentity<TValue, TValue> {
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

        return new(
            Real: FixedQ4816.Log2(value: value.Real),
            Dual: ((value.Dual * Log2E) / value.Real)
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
    public static FixedDual<TValue> operator *(FixedDual<TValue> left, FixedDual<TValue> right) =>
        new(
        Real: (left.Real * right.Real),
        Dual: ((left.Real * right.Dual) + (left.Dual * right.Real))
    );
}
