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
);
