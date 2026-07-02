namespace Puck.Maths;

/// <summary>
/// A three-dimensional vector of <see cref="FixedQ4816"/> components. Every operation is integer-only fixed point, so it
/// is deterministic and bit-identical across machines — the basis for reproducible world-space simulation. The
/// <see cref="ToVector3"/> seam converts to single precision for presentation only (it never feeds back into the sim).
/// </summary>
/// <param name="X">The first component.</param>
/// <param name="Y">The second component.</param>
/// <param name="Z">The third component.</param>
public readonly record struct FixedVector3(FixedQ4816 X, FixedQ4816 Y, FixedQ4816 Z) {
    /// <summary>Gets the zero vector.</summary>
    public static FixedVector3 Zero => default;

    /// <summary>Adds two vectors componentwise.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static FixedVector3 operator +(FixedVector3 left, FixedVector3 right) =>
        new(X: (left.X + right.X), Y: (left.Y + right.Y), Z: (left.Z + right.Z));
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/> componentwise.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static FixedVector3 operator -(FixedVector3 left, FixedVector3 right) =>
        new(X: (left.X - right.X), Y: (left.Y - right.Y), Z: (left.Z - right.Z));
    /// <summary>Scales a vector by a scalar.</summary>
    /// <param name="vector">The vector to scale.</param>
    /// <param name="scalar">The scale factor.</param>
    /// <returns>The scaled vector.</returns>
    public static FixedVector3 operator *(FixedVector3 vector, FixedQ4816 scalar) =>
        new(X: (vector.X * scalar), Y: (vector.Y * scalar), Z: (vector.Z * scalar));

    /// <summary>Converts to a single-precision <see cref="System.Numerics.Vector3"/> for presentation (the renderer).</summary>
    /// <returns>The nearest single-precision vector; precision may be lost for large magnitudes.</returns>
    public System.Numerics.Vector3 ToVector3() =>
        new(x: ((float)X), y: ((float)Y), z: ((float)Z));
}
