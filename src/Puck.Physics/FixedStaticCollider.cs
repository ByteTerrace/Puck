using Puck.Maths;

namespace Puck.Physics;

/// <summary>The analytic static shapes supported by deterministic fixed-body contact queries.</summary>
public enum FixedStaticColliderKind : byte {
    /// <summary>A sphere.</summary>
    Sphere,
    /// <summary>A world-axis-aligned box.</summary>
    AxisAlignedBox,
    /// <summary>A half-space whose normal points toward its permitted side.</summary>
    HalfSpace,
}
/// <summary>A deterministic depenetration response without any body-state or gameplay policy.</summary>
/// <param name="Normal">The unit push direction.</param>
/// <param name="Penetration">The distance to push along <paramref name="Normal"/>.</param>
public readonly record struct FixedContactPush(FixedVector3 Normal, FixedQ4816 Penetration);
/// <summary>An analytic fixed-point static collider.</summary>
/// <remarks>A sphere carries its radius in <see cref="Extent"/>.X, a box carries world-axis half-extents, and a
/// half-space carries its unit normal in <see cref="Extent"/> and one boundary point in <see cref="Center"/>.</remarks>
/// <param name="Kind">The analytic shape kind.</param>
/// <param name="Center">The sphere/box center or a point on the half-space boundary.</param>
/// <param name="Extent">The radius carrier, box half-extents, or half-space normal.</param>
public readonly record struct FixedStaticCollider(FixedStaticColliderKind Kind, FixedVector3 Center, FixedVector3 Extent) {
    private static FixedQ4816 BoxMinimumProjection(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedVector3 normal
    ) {
        var center = (position + orientation.Rotate(vector: volume.Center));
        var rotation = (orientation * volume.Rotation).Normalize();
        var localNormal = rotation.RotateInverse(vector: normal);
        var support = (((FixedQ4816.Abs(value: localNormal.X) * volume.HalfExtents.X) +
                       (FixedQ4816.Abs(value: localNormal.Y) * volume.HalfExtents.Y)) +
                       (FixedQ4816.Abs(value: localNormal.Z) * volume.HalfExtents.Z));

        return (FixedVector3.Dot(
            left: center,
            right: normal
        ) - support);
    }
    private static FixedVector3 ClosestOnSegment(FixedVector3 point, FixedVector3 start, FixedVector3 end) {
        var segment = (end - start);
        var lengthSquared = FixedVector3.Dot(
            left: segment,
            right: segment
        );

        if (lengthSquared <= FixedQ4816.Zero) {
            return start;
        }

        var amount = FixedQ4816.Clamp(
            value: (FixedVector3.Dot(
                left: (point - start),
                right: segment
            ) / lengthSquared),
            minimum: FixedQ4816.Zero,
            maximum: FixedQ4816.One
        );

        return (start + (segment * amount));
    }
    private bool TryBoxPush(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedQ4816 skin,
        out FixedContactPush push
    ) {
        var (bodyCenter, bodyExtent) = FixedColliderBounds.WorldBounds(
            orientation: in orientation,
            position: position,
            volume: in volume
        );
        var delta = (bodyCenter - Center);
        var overlapX = (((Extent.X + bodyExtent.X) + skin) - FixedQ4816.Abs(value: delta.X));
        var overlapY = (((Extent.Y + bodyExtent.Y) + skin) - FixedQ4816.Abs(value: delta.Y));
        var overlapZ = (((Extent.Z + bodyExtent.Z) + skin) - FixedQ4816.Abs(value: delta.Z));

        if (
            (overlapX <= FixedQ4816.Zero) ||
            (overlapY <= FixedQ4816.Zero) ||
            (overlapZ <= FixedQ4816.Zero)
        ) {
            push = default;
            return false;
        }

        if (
            (overlapX <= overlapY) &&
            (overlapX <= overlapZ)
        ) {
            push = new FixedContactPush(
                Normal: (FixedAxisMath.UnitX * FixedAxisMath.Sign(value: delta.X)),
                Penetration: overlapX
            );
        } else if (overlapY <= overlapZ) {
            push = new FixedContactPush(
                Normal: (FixedAxisMath.UnitY * FixedAxisMath.Sign(value: delta.Y)),
                Penetration: overlapY
            );
        } else {
            push = new FixedContactPush(
                Normal: (FixedAxisMath.UnitZ * FixedAxisMath.Sign(value: delta.Z)),
                Penetration: overlapZ
            );
        }
        return true;
    }
    private bool TryHalfSpacePush(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedQ4816 skin,
        out FixedContactPush push
    ) {
        var minimumProjection = volume.Kind switch {
            FixedBodyColliderKind.Sphere => (FixedVector3.Dot(
            left: (position + orientation.Rotate(vector: volume.Center)),
            right: Extent
        ) - volume.Radius),
            FixedBodyColliderKind.Capsule => (FixedQ4816.Min(
            x: FixedVector3.Dot(
                left: (position + orientation.Rotate(vector: volume.Center)),
                right: Extent
            ),
            y: FixedVector3.Dot(
                left: (position + orientation.Rotate(vector: volume.Endpoint)),
                right: Extent
            )
        ) - volume.Radius),
            FixedBodyColliderKind.Box => BoxMinimumProjection(
            position: position,
            orientation: in orientation,
            volume: in volume,
            normal: Extent
        ),
            _ => throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}."),
        };
        var distance = (minimumProjection - FixedVector3.Dot(
            left: Center,
            right: Extent
        ));

        if (distance >= skin) {
            push = default;
            return false;
        }

        push = new FixedContactPush(
            Normal: Extent,
            Penetration: (skin - distance)
        );
        return true;
    }
    private bool TrySpherePush(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedQ4816 skin,
        out FixedContactPush push
    ) {
        FixedVector3 closest;
        FixedQ4816 bodyRadius;

        switch (volume.Kind) {
            case FixedBodyColliderKind.Sphere:
                closest = (position + orientation.Rotate(vector: volume.Center));
                bodyRadius = volume.Radius;
                break;
            case FixedBodyColliderKind.Capsule: {
                    var lower = (position + orientation.Rotate(vector: volume.Center));
                    var upper = (position + orientation.Rotate(vector: volume.Endpoint));

                    closest = ClosestOnSegment(
                        point: Center,
                        start: lower,
                        end: upper
                    );
                    bodyRadius = volume.Radius;
                    break;
                }
            case FixedBodyColliderKind.Box: {
                    var boxCenter = (position + orientation.Rotate(vector: volume.Center));
                    var boxRotation = (orientation * volume.Rotation).Normalize();
                    var local = boxRotation.RotateInverse(vector: (Center - boxCenter));
                    var clamped = new FixedVector3(
                        X: FixedQ4816.Clamp(
                            value: local.X,
                            minimum: -volume.HalfExtents.X,
                            maximum: volume.HalfExtents.X
                        ),
                        Y: FixedQ4816.Clamp(
                            value: local.Y,
                            minimum: -volume.HalfExtents.Y,
                            maximum: volume.HalfExtents.Y
                        ),
                        Z: FixedQ4816.Clamp(
                            value: local.Z,
                            minimum: -volume.HalfExtents.Z,
                            maximum: volume.HalfExtents.Z
                        )
                    );

                    closest = (boxCenter + boxRotation.Rotate(vector: clamped));
                    bodyRadius = FixedQ4816.Zero;

                    if (closest == Center) {
                        var (localNormal, _, gap) = FixedAxisMath.BoxInteriorExit(
                            halfExtents: volume.HalfExtents,
                            local: local
                        );

                        push = new FixedContactPush(
                            Normal: boxRotation.Rotate(vector: localNormal),
                            Penetration: ((Extent.X + skin) + gap)
                        );
                        return true;
                    }
                    break;
                }
            default:
                throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}.");
        }

        var delta = (closest - Center);
        var distance = delta.Length;
        var minimum = ((bodyRadius + Extent.X) + skin);

        if (
            (distance >= minimum) ||
            (distance <= FixedQ4816.Zero)
        ) {
            push = default;
            return false;
        }

        push = new FixedContactPush(
            Normal: (delta / distance),
            Penetration: (minimum - distance)
        );
        return true;
    }

    /// <summary>Creates a static world-axis-aligned box.</summary>
    public static FixedStaticCollider AxisAlignedBox(FixedVector3 center, FixedVector3 halfExtents) =>
        new(
            Center: center,
            Extent: halfExtents,
            Kind: FixedStaticColliderKind.AxisAlignedBox
        );
    /// <summary>Creates a static half-space.</summary>
    public static FixedStaticCollider HalfSpace(FixedVector3 point, FixedVector3 normal) =>
        new(
            Center: point,
            Extent: normal,
            Kind: FixedStaticColliderKind.HalfSpace
        );
    /// <summary>Creates a static sphere.</summary>
    public static FixedStaticCollider Sphere(FixedVector3 center, FixedQ4816 radius) =>
        new(
            Kind: FixedStaticColliderKind.Sphere,
            Center: center,
            Extent: new FixedVector3(
                X: radius,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            )
        );
    /// <summary>Queries the depenetration required for one body volume.</summary>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local convex volume.</param>
    /// <param name="skin">The requested separation skin.</param>
    /// <param name="push">The contact push when overlap or skin intrusion exists.</param>
    /// <returns><see langword="true"/> when a push is required.</returns>
    public bool TryGetPush(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedQ4816 skin,
        out FixedContactPush push
    ) => Kind switch {
        FixedStaticColliderKind.Sphere => TrySpherePush(
        orientation: in orientation,
        position: position,
        push: out push,
        skin: skin,
        volume: in volume
    ),
        FixedStaticColliderKind.AxisAlignedBox => TryBoxPush(
        orientation: in orientation,
        position: position,
        push: out push,
        skin: skin,
        volume: in volume
    ),
        FixedStaticColliderKind.HalfSpace => TryHalfSpacePush(
        orientation: in orientation,
        position: position,
        push: out push,
        skin: skin,
        volume: in volume
    ),
        _ => throw new InvalidOperationException(message: $"Unknown collider kind {Kind}."),
    };
}
