using Puck.Maths;

namespace Puck.Physics;

/// <summary>The deterministic convex volume kinds supported by fixed-body contact kernels.</summary>
public enum FixedBodyColliderKind : byte {
    /// <summary>A sphere defined by a local center and radius.</summary>
    Sphere,
    /// <summary>A capsule defined by two local endpoints and a radius.</summary>
    Capsule,
    /// <summary>An oriented box defined by a local center, half-extents, and rotation.</summary>
    Box,
    /// <summary>An ellipsoid defined by local radii and orientation.</summary>
    Ellipsoid,
}
/// <summary>One deterministic convex volume in a body's local frame.</summary>
/// <param name="Kind">The volume kind.</param>
/// <param name="Center">The sphere/box center or capsule lower endpoint.</param>
/// <param name="Endpoint">The capsule upper endpoint.</param>
/// <param name="HalfExtents">The box half-extents or ellipsoid radii.</param>
/// <param name="Rotation">The box or ellipsoid's local orientation.</param>
/// <param name="Radius">The sphere/capsule radius.</param>
public readonly record struct FixedBodyColliderVolume(
    FixedBodyColliderKind Kind,
    FixedVector3 Center,
    FixedVector3 Endpoint,
    FixedVector3 HalfExtents,
    FixedQuaternion Rotation,
    FixedQ4816 Radius
) {
    /// <summary>Creates a sphere volume.</summary>
    /// <param name="center">The body-local center, in metres.</param>
    /// <param name="radius">The radius, in metres.</param>
    /// <returns>The volume.</returns>
    public static FixedBodyColliderVolume Sphere(FixedVector3 center, FixedQ4816 radius) => new(
        Kind: FixedBodyColliderKind.Sphere,
        Center: center,
        Endpoint: FixedVector3.Zero,
        HalfExtents: FixedVector3.Zero,
        Rotation: FixedQuaternion.Identity,
        Radius: radius
    );
    /// <summary>Creates a capsule volume.</summary>
    /// <param name="lower">The body-local lower endpoint of its core, in metres.</param>
    /// <param name="upper">The body-local upper endpoint of its core, in metres.</param>
    /// <param name="radius">The radius, in metres.</param>
    /// <returns>The volume.</returns>
    public static FixedBodyColliderVolume Capsule(FixedVector3 lower, FixedVector3 upper, FixedQ4816 radius) => new(
        Kind: FixedBodyColliderKind.Capsule,
        Center: lower,
        Endpoint: upper,
        HalfExtents: FixedVector3.Zero,
        Rotation: FixedQuaternion.Identity,
        Radius: radius
    );
    /// <summary>Creates an oriented box volume.</summary>
    /// <param name="center">The body-local center, in metres.</param>
    /// <param name="halfExtents">The half-extents, in metres.</param>
    /// <param name="rotation">The body-local orientation.</param>
    /// <returns>The volume.</returns>
    public static FixedBodyColliderVolume Box(FixedVector3 center, FixedVector3 halfExtents, FixedQuaternion rotation) => new(
        Kind: FixedBodyColliderKind.Box,
        Center: center,
        Endpoint: FixedVector3.Zero,
        HalfExtents: halfExtents,
        Rotation: rotation,
        Radius: FixedQ4816.Zero
    );
}
