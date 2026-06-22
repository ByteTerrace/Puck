namespace Puck.Maths;

/// <summary>
/// A two-dimensional vector of <see cref="FixedQ4816"/> components. Every operation is integer-only fixed point, so it
/// is deterministic and bit-identical across machines — the basis for reproducible planar simulation.
/// </summary>
/// <param name="X">The first component.</param>
/// <param name="Y">The second component.</param>
public readonly record struct FixedVector2(FixedQ4816 X, FixedQ4816 Y) {
    /// <summary>Gets the zero vector.</summary>
    public static FixedVector2 Zero => default;

    /// <summary>Adds two vectors componentwise.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static FixedVector2 operator +(FixedVector2 left, FixedVector2 right) =>
        new(X: (left.X + right.X), Y: (left.Y + right.Y));
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/> componentwise.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static FixedVector2 operator -(FixedVector2 left, FixedVector2 right) =>
        new(X: (left.X - right.X), Y: (left.Y - right.Y));
    /// <summary>Scales a vector by a scalar.</summary>
    /// <param name="vector">The vector to scale.</param>
    /// <param name="scalar">The scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static FixedVector2 operator *(FixedVector2 vector, FixedQ4816 scalar) =>
        new(X: (vector.X * scalar), Y: (vector.Y * scalar));

    /// <summary>Gets the squared length, avoiding the square root when only a comparison is needed.</summary>
    public FixedQ4816 LengthSquared => ((X * X) + (Y * Y));
    /// <summary>Gets the Euclidean length.</summary>
    public FixedQ4816 Length => FixedQ4816.Sqrt(value: LengthSquared);
}
