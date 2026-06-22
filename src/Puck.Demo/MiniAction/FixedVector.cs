using Puck.Maths;

namespace Puck.Demo.MiniAction;

/// <summary>
/// A two-dimensional vector of <see cref="FixedQ4816"/> — the sim's deterministic planar math (the avatar's XZ
/// movement). Every operation is integer-only fixed point, so it is bit-identical across machines.
/// </summary>
/// <param name="X">The first component.</param>
/// <param name="Y">The second component.</param>
public readonly record struct FixedVector2(FixedQ4816 X, FixedQ4816 Y) {
    /// <summary>The zero vector.</summary>
    public static FixedVector2 Zero => default;

    /// <summary>Adds two vectors componentwise.</summary>
    public static FixedVector2 operator +(FixedVector2 left, FixedVector2 right) =>
        new(X: (left.X + right.X), Y: (left.Y + right.Y));
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/> componentwise.</summary>
    public static FixedVector2 operator -(FixedVector2 left, FixedVector2 right) =>
        new(X: (left.X - right.X), Y: (left.Y - right.Y));
    /// <summary>Scales a vector by a scalar.</summary>
    public static FixedVector2 operator *(FixedVector2 vector, FixedQ4816 scalar) =>
        new(X: (vector.X * scalar), Y: (vector.Y * scalar));

    /// <summary>Gets the squared length, avoiding the square root when only a comparison is needed.</summary>
    public FixedQ4816 LengthSquared => ((X * X) + (Y * Y));
    /// <summary>Gets the Euclidean length.</summary>
    public FixedQ4816 Length => FixedQ4816.Sqrt(value: LengthSquared);
}

/// <summary>
/// A three-dimensional vector of <see cref="FixedQ4816"/> — the sim's deterministic world-space position and
/// velocity. Every operation is integer-only fixed point, so it is bit-identical across machines.
/// </summary>
/// <param name="X">The first component.</param>
/// <param name="Y">The second component.</param>
/// <param name="Z">The third component.</param>
public readonly record struct FixedVector3(FixedQ4816 X, FixedQ4816 Y, FixedQ4816 Z) {
    /// <summary>The zero vector.</summary>
    public static FixedVector3 Zero => default;

    /// <summary>Adds two vectors componentwise.</summary>
    public static FixedVector3 operator +(FixedVector3 left, FixedVector3 right) =>
        new(X: (left.X + right.X), Y: (left.Y + right.Y), Z: (left.Z + right.Z));
    /// <summary>Scales a vector by a scalar.</summary>
    public static FixedVector3 operator *(FixedVector3 vector, FixedQ4816 scalar) =>
        new(X: (vector.X * scalar), Y: (vector.Y * scalar), Z: (vector.Z * scalar));

    /// <summary>Converts to a single-precision <see cref="System.Numerics.Vector3"/> for presentation (the renderer).</summary>
    public System.Numerics.Vector3 ToVector3() =>
        new(x: ((float)X), y: ((float)Y), z: ((float)Z));
}
