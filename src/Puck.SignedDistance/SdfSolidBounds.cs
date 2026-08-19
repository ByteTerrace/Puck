using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>A solid primitive's local axis-aligned extent, in the primitive's own unit frame.</summary>
/// <param name="Center">The bound center in primitive-local coordinates.</param>
/// <param name="HalfExtents">The distances from <paramref name="Center"/> to the bound faces.</param>
/// <param name="IsUnbounded">Whether the primitive has no finite bound.</param>
public readonly record struct SdfSolidBounds(Vector3 Center, Vector3 HalfExtents, bool IsUnbounded = false) {
    /// <summary>Gets the extent marker for an unbounded primitive.</summary>
    public static SdfSolidBounds Unbounded { get; } = new(
        Center: Vector3.Zero,
        HalfExtents: Vector3.Zero,
        IsUnbounded: true
    );
}
