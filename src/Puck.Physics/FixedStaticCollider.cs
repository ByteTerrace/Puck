using Puck.Maths;

namespace Puck.Physics;

/// <summary>The analytic static shapes supported by deterministic fixed-body contact queries.</summary>
public enum FixedStaticColliderKind : byte {
    /// <summary>A sphere.</summary>
    Sphere,
    /// <summary>A world-axis-aligned box.</summary>
    AxisAlignedBox,
    /// <summary>A box turned by <see cref="FixedStaticCollider.Rotation"/>.</summary>
    OrientedBox,
    /// <summary>A half-space whose normal points toward its permitted side.</summary>
    HalfSpace,
}
/// <summary>A deterministic depenetration response without any body-state or gameplay policy.</summary>
/// <param name="Normal">The unit push direction.</param>
/// <param name="Penetration">The distance to push along <paramref name="Normal"/>.</param>
public readonly record struct FixedContactPush(FixedVector3 Normal, FixedQ4816 Penetration);
/// <summary>An analytic fixed-point static collider.</summary>
/// <remarks>A sphere carries its radius in <see cref="Extent"/>.X, an axis-aligned box carries world-axis
/// half-extents, an oriented box carries its own half-extents and the unit <see cref="Rotation"/> that turns its axes
/// into world space, and a half-space carries its unit normal in <see cref="Extent"/> and one boundary point in
/// <see cref="Center"/>. Only an oriented box reads <see cref="Rotation"/>.</remarks>
/// <param name="Kind">The analytic shape kind.</param>
/// <param name="Center">The sphere/box center or a point on the half-space boundary.</param>
/// <param name="Extent">The radius carrier, box half-extents, or half-space normal.</param>
/// <param name="Rotation">An oriented box's box-to-world rotation; unread by every other kind.</param>
public readonly record struct FixedStaticCollider(FixedStaticColliderKind Kind, FixedVector3 Center, FixedVector3 Extent, FixedQuaternion Rotation = default) {
    // The point of a segment nearest to a point: the point less the one segment witness every segment query shares.
    private static FixedVector3 ClosestOnSegment(FixedVector3 point, FixedVector3 start, FixedVector3 end) =>
        (point - FixedDynamicBodyContacts.ClosestDelta(
            p1: point,
            p2: start,
            q1: point,
            q2: end
        ));
    private bool TryBoxPush(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedQ4816 skin,
        out FixedContactPush push
    ) {
        if (volume.Kind == FixedBodyColliderKind.Sphere) {
            return FixedSphereBoxContact.TryPush(
                sphereCenter: (position + orientation.Rotate(vector: volume.Center)),
                radius: (volume.Radius + skin),
                boxCenter: Center,
                boxRotation: FixedQuaternion.Identity,
                halfExtents: Extent,
                push: out push
            );
        }

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
                Normal: (FixedVector3.UnitX * FixedQ4816.CopySign(
                    sign: delta.X,
                    value: FixedQ4816.One
                )),
                Penetration: overlapX
            );
        } else if (overlapY <= overlapZ) {
            push = new FixedContactPush(
                Normal: (FixedVector3.UnitY * FixedQ4816.CopySign(
                    sign: delta.Y,
                    value: FixedQ4816.One
                )),
                Penetration: overlapY
            );
        } else {
            push = new FixedContactPush(
                Normal: (FixedVector3.UnitZ * FixedQ4816.CopySign(
                    sign: delta.Z,
                    value: FixedQ4816.One
                )),
                Penetration: overlapZ
            );
        }
        return true;
    }
    // An oriented box is the axis-aligned box at the origin seen from the box's own frame: carry the body into that
    // frame, push there, and turn the push back. Every volume kind then meets the box's true faces, and a face push
    // points along the face's own normal.
    private bool TryOrientedBoxPush(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedQ4816 skin,
        out FixedContactPush push
    ) {
        var local = AxisAlignedBox(
            center: FixedVector3.Zero,
            halfExtents: Extent
        );

        if (!local.TryBoxPush(
            orientation: (Rotation.Conjugate() * orientation).Normalize(),
            position: Rotation.RotateInverse(vector: (position - Center)),
            push: out var localPush,
            skin: skin,
            volume: in volume
        )) {
            push = default;
            return false;
        }

        push = new FixedContactPush(
            Normal: Rotation.Rotate(vector: localPush.Normal),
            Penetration: localPush.Penetration
        );
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
            FixedBodyColliderKind.Box => FixedOrientedBox.FromVolume(
            orientation: in orientation,
            position: position,
            volume: in volume
        ).MinimumProjection(normal: Extent),
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
                    var overlaps = FixedSphereBoxContact.TryPush(
                        sphereCenter: Center,
                        radius: (Extent.X + skin),
                        boxCenter: (position + orientation.Rotate(vector: volume.Center)),
                        boxRotation: (orientation * volume.Rotation).Normalize(),
                        halfExtents: volume.HalfExtents,
                        push: out push
                    );
                    // The moving body is the box; the shared query moves the sphere.
                    push = push with { Normal = -push.Normal };
                    return overlaps;
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
    /// <summary>Creates a static box turned by a rotation: an axis-aligned box when the rotation carries every box axis
    /// exactly onto a world axis, so an unturned or quarter-turned box keeps the world-axis fast path, and an oriented
    /// box otherwise.</summary>
    /// <param name="center">The box center.</param>
    /// <param name="rotation">The box-to-world rotation; normalized on entry.</param>
    /// <param name="halfExtents">The box's own half-extents along its axes.</param>
    /// <returns>The <see cref="FixedStaticColliderKind.AxisAlignedBox"/> or <see cref="FixedStaticColliderKind.OrientedBox"/>
    /// collider.</returns>
    public static FixedStaticCollider Box(FixedVector3 center, FixedQuaternion rotation, FixedVector3 halfExtents) {
        var unit = rotation.Normalize();
        var axisX = unit.Rotate(vector: FixedVector3.UnitX);
        var axisY = unit.Rotate(vector: FixedVector3.UnitY);
        var axisZ = unit.Rotate(vector: FixedVector3.UnitZ);

        if (
            !IsWorldAxis(axis: axisX) ||
            !IsWorldAxis(axis: axisY) ||
            !IsWorldAxis(axis: axisZ)
        ) {
            return new(
                Center: center,
                Extent: halfExtents,
                Kind: FixedStaticColliderKind.OrientedBox,
                Rotation: unit
            );
        }

        // Each axis is a signed world unit, so the projection below only permutes the half-extents, exactly.
        return AxisAlignedBox(
            center: center,
            halfExtents: new FixedVector3(
                X: (((FixedQ4816.Abs(value: axisX.X) * halfExtents.X) + (FixedQ4816.Abs(value: axisY.X) * halfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.X) * halfExtents.Z)),
                Y: (((FixedQ4816.Abs(value: axisX.Y) * halfExtents.X) + (FixedQ4816.Abs(value: axisY.Y) * halfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.Y) * halfExtents.Z)),
                Z: (((FixedQ4816.Abs(value: axisX.Z) * halfExtents.X) + (FixedQ4816.Abs(value: axisY.Z) * halfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.Z) * halfExtents.Z))
            )
        );
    }
    // A rotated unit axis that lands exactly on a world axis: two of its components are exactly zero and the third is
    // exactly one in magnitude.
    private static bool IsWorldAxis(FixedVector3 axis) =>
        ((((((axis.X == FixedQ4816.Zero) ? 1 : 0) + ((axis.Y == FixedQ4816.Zero) ? 1 : 0)) + ((axis.Z == FixedQ4816.Zero) ? 1 : 0)) == 2) &&
         (((FixedQ4816.Abs(value: axis.X) + FixedQ4816.Abs(value: axis.Y)) + FixedQ4816.Abs(value: axis.Z)) == FixedQ4816.One));
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
        FixedStaticColliderKind.OrientedBox => TryOrientedBoxPush(
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
