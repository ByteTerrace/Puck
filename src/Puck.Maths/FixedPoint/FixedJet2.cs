using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The second-order jet <c>(f, f′, f″)</c> over <see cref="FixedQ4816"/>: a value carried with its first and second
/// derivatives with respect to one variable, so one evaluation of a formula answers the value, the slope and the
/// curvature that finite differences would take three or more evaluations to estimate. Seed with
/// <see cref="Variable"/> and the result follows the chain rule for the ideal expression, as
/// <see cref="FixedDual{TValue}"/> does for the slope alone. Deterministic and bit-identical across machines, like the
/// carrier it wraps.
/// </summary>
/// <param name="Real">The value <c>f</c>.</param>
/// <param name="First">The first derivative <c>f′</c>.</param>
/// <param name="Second">The second derivative <c>f″</c>.</param>
/// <remarks>
/// <para>Each output is the exact value of its rule over the operands as given, rounded onto the Q48.16 grid once
/// (ties to even): the product's second derivative <c>f″g + 2f′g′ + fg″</c> is one fused sum of raw Q32 products, the
/// way <see cref="FixedDual{TValue}"/>'s product fuses <c>a·d + b·c</c>. The quotient and the square root take their
/// first derivative from the fused dual kernels (<see cref="FixedDual.Divide{TValue}"/>, <see cref="FixedDual.Sqrt"/>)
/// and their second from the rule that follows from differentiating <c>q·g = f</c> and <c>s² = f</c> once more,
/// evaluated from the rounded lower orders, so a higher order rounds after the lower ones, never before.
/// Bare operators wrap as the carrier's do. The jet is the same unit as the variable: a variable in seconds gives
/// metres per second and metres per second squared; an interval parameter gives the interval's own units.</para>
/// <para>Order two is the curvature that decides how a quantity bends: a distance's second derivative along a motion
/// is nonnegative where the nearest feature is convex, which is why a tangent line is a safe lower bound there and the
/// quadratic term only describes the bend.</para>
/// </remarks>
public readonly record struct FixedJet2(FixedQ4816 Real, FixedQ4816 First, FixedQ4816 Second)
    : IAdditionOperators<FixedJet2, FixedJet2, FixedJet2>,
      ISubtractionOperators<FixedJet2, FixedJet2, FixedJet2>,
      IMultiplyOperators<FixedJet2, FixedJet2, FixedJet2>,
      IDivisionOperators<FixedJet2, FixedJet2, FixedJet2>,
      IUnaryNegationOperators<FixedJet2, FixedJet2>,
      IAdditiveIdentity<FixedJet2, FixedJet2>,
      IMultiplicativeIdentity<FixedJet2, FixedJet2> {
    /// <summary>Gets the additive identity, <c>(0, 0, 0)</c>.</summary>
    public static FixedJet2 AdditiveIdentity => default;
    /// <summary>Gets the multiplicative identity, <c>(1, 0, 0)</c>.</summary>
    public static FixedJet2 MultiplicativeIdentity => Constant(value: FixedQ4816.One);

    /// <summary>Creates a constant (both derivatives zero).</summary>
    /// <param name="value">The constant value.</param>
    /// <returns>The jet <c>(value, 0, 0)</c>.</returns>
    public static FixedJet2 Constant(FixedQ4816 value) => new(Real: value, First: FixedQ4816.Zero, Second: FixedQ4816.Zero);
    /// <summary>Creates the differentiation variable (unit slope, no curvature).</summary>
    /// <param name="value">The value to differentiate at.</param>
    /// <returns>The jet <c>(value, 1, 0)</c>.</returns>
    public static FixedJet2 Variable(FixedQ4816 value) => new(Real: value, First: FixedQ4816.One, Second: FixedQ4816.Zero);

    /// <summary>Negates a jet.</summary>
    /// <param name="value">The jet to negate.</param>
    /// <returns>The componentwise negation.</returns>
    public static FixedJet2 operator -(FixedJet2 value) => new(Real: -value.Real, First: -value.First, Second: -value.Second);
    /// <summary>Adds two jets.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static FixedJet2 operator +(FixedJet2 left, FixedJet2 right) =>
        new(Real: (left.Real + right.Real), First: (left.First + right.First), Second: (left.Second + right.Second));
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/>.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static FixedJet2 operator -(FixedJet2 left, FixedJet2 right) =>
        new(Real: (left.Real - right.Real), First: (left.First - right.First), Second: (left.Second - right.Second));
    /// <summary>Scales a jet by a constant: each component rounded once.</summary>
    /// <param name="left">The jet.</param>
    /// <param name="right">The constant factor.</param>
    /// <returns>The componentwise product.</returns>
    public static FixedJet2 operator *(FixedJet2 left, FixedQ4816 right) =>
        new(Real: (left.Real * right), First: (left.First * right), Second: (left.Second * right));
    /// <summary>Scales a jet by a constant: each component rounded once.</summary>
    /// <param name="left">The constant factor.</param>
    /// <param name="right">The jet.</param>
    /// <returns>The componentwise product.</returns>
    public static FixedJet2 operator *(FixedQ4816 left, FixedJet2 right) => (right * left);
    /// <summary>Multiplies two jets by the product rule and its second-order extension (Leibniz).</summary>
    /// <param name="left">The multiplicand <c>f</c>.</param>
    /// <param name="right">The multiplier <c>g</c>.</param>
    /// <returns><c>(fg, f′g + fg′, f″g + 2f′g′ + fg″)</c>, each component one rounding of its exact raw sum.</returns>
    public static FixedJet2 operator *(FixedJet2 left, FixedJet2 right) {
        var f = ((Int128)left.Real.Value);
        var f1 = ((Int128)left.First.Value);
        var f2 = ((Int128)left.Second.Value);
        var g = ((Int128)right.Real.Value);
        var g1 = ((Int128)right.First.Value);
        var g2 = ((Int128)right.Second.Value);

        return new(
            Real: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: (f * g))),
            First: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((f * g1) + (f1 * g)))),
            Second: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((f2 * g) + (2 * (f1 * g1))) + (f * g2))))
        );
    }
    /// <summary>Divides <paramref name="left"/> by <paramref name="right"/> by the quotient rule.</summary>
    /// <param name="left">The dividend <c>f</c>.</param>
    /// <param name="right">The divisor <c>g</c>; its real part must be nonzero.</param>
    /// <returns><c>q = f/g</c>, <c>q′ = (f′g − fg′)/g²</c> (the fused dual kernel) and
    /// <c>q″ = (f″ − 2q′g′ − qg″)/g</c>, the last as one rounding of its exact sum over the rounded <c>q</c> and
    /// <c>q′</c>.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="right"/>'s real part is zero.</exception>
    public static FixedJet2 operator /(FixedJet2 left, FixedJet2 right) {
        var dual = FixedDual.Divide(
            left: new FixedDual<FixedQ4816>(Real: left.Real, Dual: left.First),
            right: new FixedDual<FixedQ4816>(Real: right.Real, Dual: right.First)
        );
        // f″·2¹⁶ − 2·q′·g′ − q·g″ at Q32, three signed terms combined as sign plus magnitude (their magnitudes sum
        // below 2¹²⁸), then one division by g.
        var cross = FusedArithmetic.Product(left: dual.Dual.Value, right: right.First.Value);
        var tail = FusedArithmetic.Product(left: dual.Real.Value, right: right.Second.Value);
        var numerator = FusedArithmetic.CombineSigned(
            firstMagnitude: (((UInt128)FusedArithmetic.RawMagnitude(value: left.Second.Value)) << FixedQ4816.FractionBitCount),
            firstNegative: (left.Second.Value < 0L),
            secondMagnitude: Twice(value: cross.Magnitude),
            secondNegative: !cross.Negative
        );

        numerator = FusedArithmetic.CombineSigned(
            firstMagnitude: numerator.Magnitude,
            firstNegative: numerator.Negative,
            secondMagnitude: tail.Magnitude,
            secondNegative: !tail.Negative
        );

        return new(
            Real: dual.Real,
            First: dual.Dual,
            Second: FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                denominator: ((right.Real.Value < 0L), (((UInt128)FusedArithmetic.RawMagnitude(value: right.Real.Value)) << FixedQ4816.FractionBitCount)),
                numerator: numerator
            ))
        );
    }

    /// <summary>Squares a jet.</summary>
    /// <param name="value">The jet <c>f</c>.</param>
    /// <returns><c>(f², 2ff′, 2f′² + 2ff″)</c>, each component one rounding.</returns>
    public static FixedJet2 Square(FixedJet2 value) => (value * value);
    /// <summary>Computes the square root and its first two derivatives.</summary>
    /// <param name="value">The operand; a non-positive real part yields the zero jet (the derivatives are undefined there).</param>
    /// <returns><c>s = √f</c>, <c>s′ = f′/(2s)</c> (the fused dual kernel) and <c>s″ = (f″ − 2s′²)/(2s)</c>, the
    /// last as one rounding of its exact sum over the rounded <c>s′</c>.</returns>
    public static FixedJet2 Sqrt(FixedJet2 value) {
        if (value.Real.Value <= 0L) {
            return default;
        }

        var dual = FixedDual.Sqrt(value: new FixedDual<FixedQ4816>(Real: value.Real, Dual: value.First));
        var root = dual.Real.Value;

        if (root == 0L) {
            return new(Real: dual.Real, First: dual.Dual, Second: FixedQ4816.Zero);
        }

        var square = FusedArithmetic.Product(left: dual.Dual.Value, right: dual.Dual.Value);
        var numerator = FusedArithmetic.CombineSigned(
            firstMagnitude: (((UInt128)FusedArithmetic.RawMagnitude(value: value.Second.Value)) << FixedQ4816.FractionBitCount),
            firstNegative: (value.Second.Value < 0L),
            secondMagnitude: Twice(value: square.Magnitude),
            secondNegative: true
        );

        return new(
            Real: dual.Real,
            First: dual.Dual,
            Second: FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                denominator: (((UInt128)FusedArithmetic.RawMagnitude(value: root)) << (FixedQ4816.FractionBitCount + 1)),
                numerator: numerator
            ))
        );
    }
    /// <summary>Computes the sine and cosine and their first two derivatives.</summary>
    /// <param name="angle">The angle in fixed-point radians.</param>
    /// <returns>The pair <c>(sin a, a′·cos a, a″·cos a − a′²·sin a)</c> and
    /// <c>(cos a, −a′·sin a, −a″·sin a − a′²·cos a)</c>, the second derivatives each one rounding over the rounded
    /// <c>a′²</c>.</returns>
    public static (FixedJet2 Sin, FixedJet2 Cos) SinCos(FixedJet2 angle) {
        var (sin, cos) = FixedQ4816.SinCos(angle: angle.Real);
        var speedSquared = ((Int128)(angle.First * angle.First).Value);
        var s = ((Int128)sin.Value);
        var c = ((Int128)cos.Value);
        var b = ((Int128)angle.First.Value);
        var e = ((Int128)angle.Second.Value);

        return (
            new(
                Real: sin,
                First: (angle.First * cos),
                Second: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((e * c) - (speedSquared * s))))
            ),
            new(
                Real: cos,
                First: -(angle.First * sin),
                Second: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(-((e * s) + (speedSquared * c)))))
            )
        );
    }

    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static UInt128 Twice(UInt128 value) => (value << 1);
}
