using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A two-dimensional vector of <see cref="FixedQ4816"/> components. Every operation is integer-only fixed point, so it
/// is deterministic and bit-identical across machines — the basis for reproducible planar simulation.
/// </summary>
/// <param name="X">The first component.</param>
/// <param name="Y">The second component.</param>
public readonly record struct FixedVector2(FixedQ4816 X, FixedQ4816 Y)
    : IAdditionOperators<FixedVector2, FixedVector2, FixedVector2>,
      ISubtractionOperators<FixedVector2, FixedVector2, FixedVector2>,
      IMultiplyOperators<FixedVector2, FixedQ4816, FixedVector2>,
      IDivisionOperators<FixedVector2, FixedQ4816, FixedVector2>,
      IUnaryNegationOperators<FixedVector2, FixedVector2>,
      IAdditiveIdentity<FixedVector2, FixedVector2> {
    /// <summary>Gets the additive identity, the zero vector.</summary>
    public static FixedVector2 AdditiveIdentity => default;
    /// <summary>Gets the zero vector.</summary>
    public static FixedVector2 Zero => AdditiveIdentity;

    /// <summary>Adds two vectors componentwise.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static FixedVector2 operator +(FixedVector2 left, FixedVector2 right) =>
        new(
        X: (left.X + right.X),
        Y: (left.Y + right.Y)
    );
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/> componentwise.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static FixedVector2 operator -(FixedVector2 left, FixedVector2 right) =>
        new(
        X: (left.X - right.X),
        Y: (left.Y - right.Y)
    );
    /// <summary>Negates a vector componentwise.</summary>
    /// <param name="value">The vector to negate.</param>
    /// <returns>The vector pointing the opposite way, each component negated.</returns>
    public static FixedVector2 operator -(FixedVector2 value) =>
        new(
        X: (-value.X),
        Y: (-value.Y)
    );
    /// <summary>Scales a vector by a scalar.</summary>
    /// <param name="vector">The vector to scale.</param>
    /// <param name="scalar">The scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static FixedVector2 operator *(FixedVector2 vector, FixedQ4816 scalar) =>
        new(
        X: (vector.X * scalar),
        Y: (vector.Y * scalar)
    );
    /// <summary>Divides a vector by a scalar componentwise.</summary>
    /// <param name="vector">The dividend vector.</param>
    /// <param name="scalar">The divisor.</param>
    /// <returns>The vector with each component divided by <paramref name="scalar"/> — genuine per-component division rounded to nearest, more accurate than multiplying by a reciprocal.</returns>
    /// <exception cref="System.DivideByZeroException"><paramref name="scalar"/> is zero.</exception>
    public static FixedVector2 operator /(FixedVector2 vector, FixedQ4816 scalar) =>
        new(
        X: (vector.X / scalar),
        Y: (vector.Y / scalar)
    );

    /// <summary>Gets the dot product of two vectors.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The scalar dot product (two products accumulated exactly, one rounding).</returns>
    public static FixedQ4816 Dot(FixedVector2 left, FixedVector2 right) {
        const ulong NarrowLimit = (1UL << 31);
        var combinedMagnitude = (FixedVectorMath.RawMagnitude(value: left.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Y.Value));

        if (combinedMagnitude < NarrowLimit) {
            return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
                (left.X.Value * right.X.Value) + (left.Y.Value * right.Y.Value))));
        }

        return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
            ((Int128)left.X.Value * right.X.Value) + ((Int128)left.Y.Value * right.Y.Value))));
    }
    /// <summary>Gets the wedge (exterior) product of two vectors: the signed area of the parallelogram they span,
    /// positive when <paramref name="right"/> lies counterclockwise of <paramref name="left"/>.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The bivector coefficient <c>left.X·right.Y − left.Y·right.X</c> (two products accumulated exactly,
    /// one rounding).</returns>
    /// <remarks>Antisymmetric — <c>Wedge(a, b) == -Wedge(b, a)</c> exactly — and zero when the vectors are parallel:
    /// the winding/orientation test, and the planar restriction of <see cref="FixedVector3.Cross"/>.</remarks>
    public static FixedQ4816 Wedge(FixedVector2 left, FixedVector2 right) {
        const ulong NarrowLimit = (1UL << 31);
        var combinedMagnitude = (FixedVectorMath.RawMagnitude(value: left.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Y.Value));

        if (combinedMagnitude < NarrowLimit) {
            return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
                (left.X.Value * right.Y.Value) - (left.Y.Value * right.X.Value))));
        }

        return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
            ((Int128)left.X.Value * right.Y.Value) - ((Int128)left.Y.Value * right.X.Value))));
    }

    /// <summary>Linearly interpolates each component from <paramref name="from"/> to <paramref name="to"/> by <paramref name="amount"/>.</summary>
    /// <param name="from">The vector returned when <paramref name="amount"/> is zero.</param>
    /// <param name="to">The vector returned when <paramref name="amount"/> is one.</param>
    /// <param name="amount">The interpolation fraction; values outside <c>[0, 1]</c> extrapolate.</param>
    /// <returns>The componentwise <see cref="FixedQ4816.Lerp"/> — exactly <paramref name="from"/> at zero and <paramref name="to"/> at one.</returns>
    public static FixedVector2 Lerp(FixedVector2 from, FixedVector2 to, FixedQ4816 amount) =>
        new(
        X: FixedQ4816.Lerp(from: from.X, to: to.X, amount: amount),
        Y: FixedQ4816.Lerp(from: from.Y, to: to.Y, amount: amount)
    );

    /// <summary>Gets the exact raw Q32 sum of squares rounded once to Q16, saturating when it exceeds the scalar
    /// carrier. Use <see cref="TryLengthSquared"/> when overflow must be distinguished.</summary>
    public FixedQ4816 LengthSquared => (TryLengthSquared(out var squaredLength)
        ? squaredLength
        : FixedQ4816.MaxValue);

    /// <summary>Gets the full-width length, saturating when it exceeds the scalar carrier. Unlike taking the square
    /// root of <see cref="LengthSquared"/>, this rounds only the final raw Q32 root.</summary>
    public FixedQ4816 Length => (TryLength(out var length)
        ? length
        : FixedQ4816.MaxValue);

    /// <summary>Tries to get the full-width vector length.</summary>
    public bool TryLength(out FixedQ4816 length) =>
        FixedVectorMath.TryMagnitude(x: X.Value, y: Y.Value, result: out length);

    /// <summary>Tries to get the full-width squared vector length after one ties-to-even Q16 rounding.</summary>
    public bool TryLengthSquared(out FixedQ4816 squaredLength) =>
        FixedVectorMath.TrySquaredMagnitude(x: X.Value, y: Y.Value, result: out squaredLength);
}
