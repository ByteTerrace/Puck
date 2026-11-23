using Puck.Maths;

namespace Puck.Physics;

/// <summary>The fixed-point shape kind used by compiled spatial queries.</summary>
public enum FixedSpatialShapeKind : byte {
    /// <summary>An oriented box.</summary>
    Box,
    /// <summary>A sphere.</summary>
    Sphere,
}
/// <summary>An immutable fixed-point shape compiled from a document volume: an oriented box, or a sphere whose
/// <see cref="Box"/> carries only its center.</summary>
/// <param name="Kind">The shape kind.</param>
/// <param name="Box">The oriented box, or for a sphere a world-axis box at the sphere's center with zero extents.</param>
/// <param name="Radius">The sphere's radius; zero for a box.</param>
public readonly record struct FixedSpatialShape(
    FixedSpatialShapeKind Kind,
    FixedOrientedBox Box,
    FixedQ4816 Radius
) {
    /// <summary>Gets the shape's world-space center.</summary>
    public FixedVector3 Center => Box.Center;
    /// <summary>Gets a box's half-extents along its own axes; zero for a sphere.</summary>
    public FixedVector3 HalfExtents => Box.HalfExtents;
    /// <summary>Gets the shape's world-axis-aligned bounds.</summary>
    public FixedAabb Bounds => ((Kind == FixedSpatialShapeKind.Sphere)
        ? FixedAabb.Around(center: Center, radius: Radius)
        : FixedAabb.FromCenter(center: Center, extent: Box.WorldExtent));

    /// <summary>Builds a sphere.</summary>
    /// <param name="center">The world-space center.</param>
    /// <param name="radius">The radius, world units.</param>
    /// <returns>The sphere, its <see cref="Box"/> a world-axis box of zero extent at its center.</returns>
    public static FixedSpatialShape OfSphere(FixedVector3 center, FixedQ4816 radius) => new(
        Box: FixedOrientedBox.AxisAligned(center: center, halfExtents: FixedVector3.Zero),
        Kind: FixedSpatialShapeKind.Sphere,
        Radius: radius
    );
    /// <summary>Builds an oriented box.</summary>
    /// <param name="center">The world-space center.</param>
    /// <param name="rotation">The unit box-to-world rotation.</param>
    /// <param name="halfExtents">The half-extents along the box's own axes, world units.</param>
    /// <returns>The box.</returns>
    public static FixedSpatialShape OfBox(FixedVector3 center, FixedQuaternion rotation, FixedVector3 halfExtents) => new(
        Box: FixedOrientedBox.FromRotation(center: center, halfExtents: halfExtents, rotation: rotation),
        Kind: FixedSpatialShapeKind.Box,
        Radius: FixedQ4816.Zero
    );
    /// <summary>Returns whether the shape's closed volume holds a point.</summary>
    /// <param name="point">The world-space point.</param>
    /// <returns><see langword="true"/> when the point lies within the sphere's radius or every box-local half-extent;
    /// <see langword="false"/> when it lies outside or its offset from the center does not fit the carrier.</returns>
    public bool Contains(FixedVector3 point) {
        if (Kind == FixedSpatialShapeKind.Sphere) {
            return (FixedVector3.TrySubtract(
                difference: out var delta,
                left: point,
                right: Center
            ) && delta.IsWithin(radius: Radius));
        }

        return Box.Contains(point: point);
    }
}
