using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A three-dimensional vector of <see cref="FixedQ4816"/> components. Every operation is integer-only fixed point, so it
/// is deterministic and bit-identical across machines — the basis for reproducible world-space simulation. The
/// <see cref="ToVector3"/> seam converts to single precision for presentation only (it never feeds back into the sim).
/// </summary>
/// <param name="X">The first component.</param>
/// <param name="Y">The second component.</param>
/// <param name="Z">The third component.</param>
public readonly record struct FixedVector3(FixedQ4816 X, FixedQ4816 Y, FixedQ4816 Z)
    : IAdditionOperators<FixedVector3, FixedVector3, FixedVector3>,
      ISubtractionOperators<FixedVector3, FixedVector3, FixedVector3>,
      IMultiplyOperators<FixedVector3, FixedQ4816, FixedVector3>,
      IDivisionOperators<FixedVector3, FixedQ4816, FixedVector3>,
      IUnaryNegationOperators<FixedVector3, FixedVector3>,
      IAdditiveIdentity<FixedVector3, FixedVector3> {
    /// <summary>Gets the additive identity, the zero vector.</summary>
    public static FixedVector3 AdditiveIdentity => default;
    /// <summary>Gets the zero vector.</summary>
    public static FixedVector3 Zero => AdditiveIdentity;

    /// <summary>Adds two vectors componentwise.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static FixedVector3 operator +(FixedVector3 left, FixedVector3 right) =>
        new(
        X: (left.X + right.X),
        Y: (left.Y + right.Y),
        Z: (left.Z + right.Z)
    );
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/> componentwise.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static FixedVector3 operator -(FixedVector3 left, FixedVector3 right) =>
        new(
        X: (left.X - right.X),
        Y: (left.Y - right.Y),
        Z: (left.Z - right.Z)
    );
    /// <summary>Negates a vector componentwise.</summary>
    /// <param name="value">The vector to negate.</param>
    /// <returns>The vector pointing the opposite way, each component negated.</returns>
    public static FixedVector3 operator -(FixedVector3 value) =>
        new(
        X: (-value.X),
        Y: (-value.Y),
        Z: (-value.Z)
    );
    /// <summary>Scales a vector by a scalar.</summary>
    /// <param name="vector">The vector to scale.</param>
    /// <param name="scalar">The scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static FixedVector3 operator *(FixedVector3 vector, FixedQ4816 scalar) =>
        new(
        X: (vector.X * scalar),
        Y: (vector.Y * scalar),
        Z: (vector.Z * scalar)
    );
    /// <summary>Divides a vector by a scalar componentwise.</summary>
    /// <param name="vector">The dividend vector.</param>
    /// <param name="scalar">The divisor.</param>
    /// <returns>The vector with each component divided by <paramref name="scalar"/> — genuine per-component division rounded to nearest, more accurate than multiplying by a reciprocal.</returns>
    /// <exception cref="System.DivideByZeroException"><paramref name="scalar"/> is zero.</exception>
    public static FixedVector3 operator /(FixedVector3 vector, FixedQ4816 scalar) =>
        new(
        X: (vector.X / scalar),
        Y: (vector.Y / scalar),
        Z: (vector.Z / scalar)
    );

    /// <summary>The dot product of two vectors — integer-only, deterministic.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The scalar dot product, with all three Q32 products accumulated before a single Q16 rounding.</returns>
    public static FixedQ4816 Dot(FixedVector3 left, FixedVector3 right) {
        const ulong NarrowLimit = (1UL << 30);
        var combinedMagnitude = (FixedVectorMath.RawMagnitude(value: left.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Z.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Z.Value));

        if (combinedMagnitude < NarrowLimit) {
            return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
                (left.X.Value * right.X.Value) + (left.Y.Value * right.Y.Value) + (left.Z.Value * right.Z.Value))));
        }

        return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(
            ((Int128)left.X.Value * right.X.Value) +
            ((Int128)left.Y.Value * right.Y.Value) +
            ((Int128)left.Z.Value * right.Z.Value))));
    }

    /// <summary>The cross product of two vectors — integer-only, deterministic.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The vector cross product — the wedge product <c>left ∧ right</c> read as an axis
    /// (see <see cref="FixedVector2.Wedge"/> for the planar case). Each component accumulates two Q32 products and
    /// rounds once to Q16.</returns>
    public static FixedVector3 Cross(FixedVector3 left, FixedVector3 right) {
        const ulong NarrowLimit = (1UL << 31);
        var combinedMagnitude = (FixedVectorMath.RawMagnitude(value: left.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: left.Z.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.X.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Y.Value) |
                                 FixedVectorMath.RawMagnitude(value: right.Z.Value));

        if (combinedMagnitude < NarrowLimit) {
            return new(
                X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((left.Y.Value * right.Z.Value) - (left.Z.Value * right.Y.Value)))),
                Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((left.Z.Value * right.X.Value) - (left.X.Value * right.Z.Value)))),
                Z: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((left.X.Value * right.Y.Value) - (left.Y.Value * right.X.Value))))
            );
        }

        return new(
            X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)left.Y.Value * right.Z.Value) - ((Int128)left.Z.Value * right.Y.Value)))),
            Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)left.Z.Value * right.X.Value) - ((Int128)left.X.Value * right.Z.Value)))),
            Z: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((Int128)left.X.Value * right.Y.Value) - ((Int128)left.Y.Value * right.X.Value))))
        );
    }

    /// <summary>Linearly interpolates each component from <paramref name="from"/> to <paramref name="to"/> by <paramref name="amount"/>.</summary>
    /// <param name="from">The vector returned when <paramref name="amount"/> is zero.</param>
    /// <param name="to">The vector returned when <paramref name="amount"/> is one.</param>
    /// <param name="amount">The interpolation fraction; values outside <c>[0, 1]</c> extrapolate.</param>
    /// <returns>The componentwise <see cref="FixedQ4816.Lerp"/> — exactly <paramref name="from"/> at zero and <paramref name="to"/> at one.</returns>
    public static FixedVector3 Lerp(FixedVector3 from, FixedVector3 to, FixedQ4816 amount) =>
        new(
        X: FixedQ4816.Lerp(from: from.X, to: to.X, amount: amount),
        Y: FixedQ4816.Lerp(from: from.Y, to: to.Y, amount: amount),
        Z: FixedQ4816.Lerp(from: from.Z, to: to.Z, amount: amount)
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
        FixedVectorMath.TryMagnitude(x: X.Value, y: Y.Value, z: Z.Value, result: out length);

    /// <summary>Tries to get the full-width squared vector length after one ties-to-even Q16 rounding.</summary>
    public bool TryLengthSquared(out FixedQ4816 squaredLength) =>
        FixedVectorMath.TrySquaredMagnitude(x: X.Value, y: Y.Value, z: Z.Value, result: out squaredLength);

    /// <summary>Normalizes the vector to Q16 unit length at every representable input scale. The calculation applies
    /// one common power-of-two scale before its exact sum of squares, so tiny directions do not disappear and extreme
    /// directions do not overflow. Zero normalizes to <see cref="Zero"/>.</summary>
    /// <returns>The unit-length vector along the same direction, or <see cref="Zero"/> when this vector is zero.</returns>
    public FixedVector3 Normalize() {
        var (x, y, z) = FixedVectorMath.Normalize(x: X.Value, y: Y.Value, z: Z.Value);

        if ((x | y | z) == 0L) {
            return Zero;
        }

        return new(
            X: FixedQ4816.FromRawBits(value: x),
            Y: FixedQ4816.FromRawBits(value: y),
            Z: FixedQ4816.FromRawBits(value: z)
        );
    }

    /// <summary>Converts to a single-precision <see cref="System.Numerics.Vector3"/> for presentation (the renderer).</summary>
    /// <returns>The nearest single-precision vector; precision may be lost for large magnitudes.</returns>
    public System.Numerics.Vector3 ToVector3() =>
        new(
        x: ((float)X),
        y: ((float)Y),
        z: ((float)Z)
    );
}
