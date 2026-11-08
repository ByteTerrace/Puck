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
    /// <summary>A capped circular cylinder along its rotated local Y axis.</summary>
    Cylinder,
    /// <summary>An oriented inner box offset by a spherical radius.</summary>
    RoundedBox,
    /// <summary>The drawn chamfered XY rectangle extruded along Z, including its edge fillet.</summary>
    ChamferedBox,
    /// <summary>An oriented Lp ball with independently scaled radii.</summary>
    Superellipsoid,
    /// <summary>The zero set of <see cref="FixedStaticCollider.Field"/>'s scalar field in the collider's frame: a
    /// relieved creation collides with the surface its bake draws.</summary>
    Field,
}
/// <summary>A deterministic depenetration response without any body-state or gameplay policy.</summary>
/// <param name="Normal">The unit push direction.</param>
/// <param name="Penetration">The distance to push along <paramref name="Normal"/>.</param>
/// <param name="IsEdge">Whether the closest feature joins faces (including a fillet or cylinder rim).</param>
public readonly record struct FixedContactPush(FixedVector3 Normal, FixedQ4816 Penetration, bool IsEdge = false);
/// <summary>An analytic fixed-point static collider.</summary>
/// <remarks>A sphere carries its radius in <see cref="Extent"/>.X, an axis-aligned box carries world-axis
/// half-extents, an oriented box carries its own half-extents and the unit <see cref="Rotation"/> that turns its axes
/// into world space. A cylinder carries radius in X/Z and half-height in Y, with the same local-to-world rotation.
/// A rounded box carries inner half-extents in Extent and the spherical offset in Radius.
/// A chamfered box carries the drawing builder's inset half-extents, inset Chamfer and final Radius offset.
/// A superellipsoid carries its per-axis radii in Extent and its Lp Exponent.
/// A half-space carries its unit normal in <see cref="Extent"/> and one boundary point in <see cref="Center"/>.</remarks>
/// <param name="Kind">The analytic shape kind.</param>
/// <param name="Center">The sphere/box center or a point on the half-space boundary.</param>
/// <param name="Extent">The radius carrier, box half-extents, or half-space normal.</param>
/// <param name="Rotation">The oriented box or cylinder's local-to-world rotation.</param>
/// <param name="Radius">The rounded box's offset radius; Extent carries its inner half-extents.</param>
/// <param name="Chamfer">The inset chamfer of a lifted chamfered box.</param>
/// <param name="Exponent">The superellipsoid's Lp exponent.</param>
/// <param name="Field">A field collider's surface, read in the frame of <paramref name="Center"/> and
/// <paramref name="Rotation"/>; its local half-extent about that centre is in <paramref name="Extent"/>.</param>
public readonly record struct FixedStaticCollider(FixedStaticColliderKind Kind, FixedVector3 Center, FixedVector3 Extent, FixedQuaternion Rotation = default, FixedQ4816 Radius = default, FixedQ4816 Chamfer = default, FixedQ4816 Exponent = default, FixedColliderField? Field = null) {
    private bool TryBoxPush(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedQ4816 skin,
        out FixedContactPush push
    ) {
        // A sharp box is the same closest-feature query with no spherical offset.
        var box = RoundedBox(center: Center,
            rotation: ((Kind == FixedStaticColliderKind.AxisAlignedBox) ? FixedQuaternion.Identity : Rotation),
            innerHalfExtents: Extent, radius: FixedQ4816.Zero);

        return FixedRoundedBoxContact.TryPush(box: in box, orientation: in orientation, position: position,
            push: out push, skin: skin, volume: in volume);
    }
    private bool TryHalfSpacePush(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedQ4816 skin,
        out FixedContactPush push
    ) {
        var minimumProjection = FixedColliderBounds.MinimumProjection(position: position, orientation: in orientation,
            volume: in volume, normal: Extent);
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
        switch (volume.Kind) {
            case FixedBodyColliderKind.Sphere or FixedBodyColliderKind.Capsule: {
                    var (start, end) = FixedDynamicBodyContacts.Segment(orientation: orientation, position: position,
                        volume: in volume);

                    // A body core through the sphere's center exits along +X, across an upright capsule's axis, as an
                    // even dynamic tie does.
                    return FixedDynamicBodyContacts.TryRoundPush(p1: start, q1: end, p2: Center, q2: Center,
                        radius: ((volume.Radius + Extent.X) + skin), tieNormal: FixedVector3.UnitX, push: out push);
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
    }

    /// <summary>Creates a static world-axis-aligned box.</summary>
    public static FixedStaticCollider AxisAlignedBox(FixedVector3 center, FixedVector3 halfExtents) =>
        new(
            Center: center,
            Extent: halfExtents,
            Kind: FixedStaticColliderKind.AxisAlignedBox
        );
    /// <summary>Creates an oriented inner box offset by a sphere. Its outer face extents are innerHalfExtents + radius.</summary>
    public static FixedStaticCollider RoundedBox(FixedVector3 center, FixedQuaternion rotation, FixedVector3 innerHalfExtents, FixedQ4816 radius) =>
        new(Kind: FixedStaticColliderKind.RoundedBox, Center: center, Extent: innerHalfExtents,
            Rotation: rotation.Normalize(), Radius: radius);
    /// <summary>Creates the exact lifted chamfered rectangle. Extents and chamfer are the inset dimensions emitted
    /// by the drawing vocabulary; radius is its final scalar-field offset.</summary>
    public static FixedStaticCollider ChamferedBox(FixedVector3 center, FixedQuaternion rotation, FixedVector3 innerHalfExtents, FixedQ4816 chamfer, FixedQ4816 radius) =>
        new(Kind: FixedStaticColliderKind.ChamferedBox, Center: center, Extent: innerHalfExtents,
            Rotation: rotation.Normalize(), Radius: radius, Chamfer: chamfer);
    /// <summary>Creates an oriented superellipsoid with positive radii and exponent at least two.</summary>
    public static FixedStaticCollider Superellipsoid(FixedVector3 center, FixedQuaternion rotation, FixedVector3 radii, FixedQ4816 exponent) =>
        new(Kind: FixedStaticColliderKind.Superellipsoid, Center: center, Extent: radii,
            Rotation: rotation.Normalize(), Exponent: exponent);
    /// <summary>Creates a field collider: the zero set of a scalar field read in the frame of a centre and rotation.</summary>
    /// <param name="center">The frame's world origin, which is also the centre its extent bounds the surface about.</param>
    /// <param name="rotation">The frame-to-world rotation; normalized on entry.</param>
    /// <param name="halfExtent">The surface's half-extent along the frame's axes about the centre.</param>
    /// <param name="field">The field.</param>
    /// <returns>The <see cref="FixedStaticColliderKind.Field"/> collider.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public static FixedStaticCollider FieldSurface(FixedVector3 center, FixedQuaternion rotation, FixedVector3 halfExtent, FixedColliderField field) =>
        new(Kind: FixedStaticColliderKind.Field, Center: center, Extent: halfExtent, Rotation: rotation.Normalize(),
            Field: (field ?? throw new ArgumentNullException(paramName: nameof(field))));
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
    internal static bool IsWorldAxis(FixedVector3 axis) =>
        ((((((axis.X == FixedQ4816.Zero) ? 1 : 0) + ((axis.Y == FixedQ4816.Zero) ? 1 : 0)) + ((axis.Z == FixedQ4816.Zero) ? 1 : 0)) == 2) &&
         (((FixedQ4816.Abs(value: axis.X) + FixedQ4816.Abs(value: axis.Y)) + FixedQ4816.Abs(value: axis.Z)) == FixedQ4816.One));

    /// <summary>Creates a static half-space.</summary>
    public static FixedStaticCollider HalfSpace(FixedVector3 point, FixedVector3 normal) =>
        new(
            Center: point,
            Extent: normal,
            Kind: FixedStaticColliderKind.HalfSpace
        );
    /// <summary>Creates a capped circular cylinder, including a thin disc.</summary>
    /// <param name="center">The cylinder center.</param>
    /// <param name="radius">The circular radius.</param>
    /// <param name="halfHeight">The half-height along local Y.</param>
    /// <param name="rotation">The cylinder-to-world rotation.</param>
    /// <returns>The cylinder collider.</returns>
    public static FixedStaticCollider Cylinder(FixedVector3 center, FixedQ4816 radius, FixedQ4816 halfHeight, FixedQuaternion rotation) =>
        new(Kind: FixedStaticColliderKind.Cylinder, Center: center,
            Extent: new FixedVector3(X: radius, Y: halfHeight, Z: radius), Rotation: rotation.Normalize());
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
    /// <summary>Returns a finite solid's supporting surface point in a world-space direction. A zero direction
    /// chooses world X. A half-space has no finite support and is refused.</summary>
    public FixedVector3 SupportPoint(FixedVector3 direction) {
        if (Kind == FixedStaticColliderKind.HalfSpace) { throw new InvalidOperationException(message: "A half-space has no finite support point."); }
        if (Kind == FixedStaticColliderKind.Field) { throw new InvalidOperationException(message: "A field collider has no support map."); }
        var unit = ((direction == FixedVector3.Zero) ? FixedVector3.UnitX : direction.Normalize());

        if (Kind == FixedStaticColliderKind.Sphere) { return (Center + (unit * Extent.X)); }
        var local = ((Kind == FixedStaticColliderKind.AxisAlignedBox) ? unit : Rotation.RotateInverse(vector: unit));
        FixedVector3 point;

        if (Kind is FixedStaticColliderKind.ChamferedBox or FixedStaticColliderKind.Superellipsoid) {
            point = FixedConvexGeometry.Support(collider: in this, direction: local);
        } else if (Kind == FixedStaticColliderKind.Cylinder) {
            var radial = (new FixedVector3(X: local.X, Y: FixedQ4816.Zero, Z: local.Z).Normalize() * Extent.X);

            point = (radial + (FixedVector3.UnitY * ((local.Y == FixedQ4816.Zero) ? FixedQ4816.Zero : FixedQ4816.CopySign(Extent.Y, local.Y))));
        } else {
            point = new FixedVector3(
                X: ((local.X == FixedQ4816.Zero) ? FixedQ4816.Zero : FixedQ4816.CopySign(Extent.X, local.X)),
                Y: ((local.Y == FixedQ4816.Zero) ? FixedQ4816.Zero : FixedQ4816.CopySign(Extent.Y, local.Y)),
                Z: ((local.Z == FixedQ4816.Zero) ? FixedQ4816.Zero : FixedQ4816.CopySign(Extent.Z, local.Z)));
            if (Kind == FixedStaticColliderKind.RoundedBox) { point += (local.Normalize() * Radius); }
        }
        return (Center + ((Kind == FixedStaticColliderKind.AxisAlignedBox) ? point : Rotation.Rotate(vector: point)));
    }
    /// <summary>Returns a finite solid's world-axis half-extent about <see cref="Center"/>, padded past fixed-point
    /// rounding. A body whose bounds, grown by the contact skin, miss this box needs no push from the solid, so a
    /// sweep may skip its exact query.</summary>
    /// <returns>The padded half-extent, in metres; <see langword="null"/> for a half-space, which has no bounds.</returns>
    public FixedVector3? WorldHalfExtent() {
        if ((Kind == FixedStaticColliderKind.HalfSpace) || ((Kind == FixedStaticColliderKind.Field) && (Field?.Bounded != true))) { return null; }
        var padding = new FixedVector3(X: BoundsPadding, Y: BoundsPadding, Z: BoundsPadding);

        if (Kind == FixedStaticColliderKind.Sphere) { return (new FixedVector3(X: Extent.X, Y: Extent.X, Z: Extent.X) + padding); }
        if (Kind == FixedStaticColliderKind.AxisAlignedBox) { return (Extent + padding); }
        var local = LocalHalfExtent();
        var axisX = FixedVector3.Abs(value: Rotation.Rotate(vector: FixedVector3.UnitX));
        var axisY = FixedVector3.Abs(value: Rotation.Rotate(vector: FixedVector3.UnitY));
        var axisZ = FixedVector3.Abs(value: Rotation.Rotate(vector: FixedVector3.UnitZ));

        return ((((axisX * local.X) + (axisY * local.Y)) + (axisZ * local.Z)) + padding);
    }
    // A solid turned by Rotation reaches no further than this along each of its own axes about Center, unpadded: the
    // outer face of a rounded box, the support of a chamfered box or superellipsoid (both symmetric about Center), and
    // Extent for an oriented box, a cylinder's radius and half-height, or a field's frame.
    internal FixedVector3 LocalHalfExtent() => Kind switch {
        FixedStaticColliderKind.RoundedBox => (Extent + new FixedVector3(X: Radius, Y: Radius, Z: Radius)),
        FixedStaticColliderKind.ChamferedBox or FixedStaticColliderKind.Superellipsoid => new FixedVector3(
            X: FixedQ4816.Abs(value: FixedConvexGeometry.Support(collider: in this, direction: FixedVector3.UnitX).X),
            Y: FixedQ4816.Abs(value: FixedConvexGeometry.Support(collider: in this, direction: FixedVector3.UnitY).Y),
            Z: FixedQ4816.Abs(value: FixedConvexGeometry.Support(collider: in this, direction: FixedVector3.UnitZ).Z)),
        _ => Extent,
    };

    // Far wider than any rounding in a rotation or a contact query, and still tight enough to cull at room scale.
    internal static readonly FixedQ4816 BoundsPadding = (FixedQ4816.One / FixedQ4816.FromInteger(value: 64));

    /// <summary>Queries the depenetration required for one body volume.</summary>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local convex volume.</param>
    /// <param name="skin">The requested separation skin.</param>
    /// <param name="push">The contact push when overlap or skin intrusion exists.</param>
    /// <param name="uncertain">Whether the query proved neither clearance nor its contact: only a field collider's
    /// certified solve can leave a volume unproved (<see cref="FixedFieldContact.Uncertain"/>).</param>
    /// <returns><see langword="true"/> when a push is required.</returns>
    public bool TryGetPush(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedQ4816 skin,
        out FixedContactPush push,
        out bool uncertain
    ) => TryGetAnsweredPush(answered: out _, orientation: in orientation, position: position, push: out push, skin: skin, uncertain: out uncertain, volume: in volume);
    /// <summary>Queries the depenetration required for one body volume, naming the collider that answered: the exact
    /// analytic piece a field collider narrowed to (<see cref="Narrow"/>), or this collider.</summary>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local convex volume.</param>
    /// <param name="skin">The requested separation skin.</param>
    /// <param name="push">The contact push when overlap or skin intrusion exists.</param>
    /// <param name="uncertain">Whether the query proved neither clearance nor its contact.</param>
    /// <param name="answered">The collider whose surface the answer measured, which a support witness or a surface query
    /// about the push reads.</param>
    /// <returns><see langword="true"/> when a push is required.</returns>
    internal bool TryGetAnsweredPush(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedQ4816 skin,
        out FixedContactPush push,
        out bool uncertain,
        out FixedStaticCollider answered
    ) {
        answered = this;

        if (Kind == FixedStaticColliderKind.Field) {
            var bounds = FixedColliderBounds.WorldBounds(orientation: in orientation, position: position, volume: in volume);

            switch (NarrowQuery(center: bounds.Center, extent: bounds.Extent, reach: skin, collider: out var narrowed)) {
                case FixedFieldRegion.Empty: {
                        push = default;
                        uncertain = false;
                        return false;
                    }
                case FixedFieldRegion.Base: {
                        uncertain = false;
                        answered = narrowed;
                        return narrowed.TryGetAnalyticPush(orientation: in orientation, position: position, push: out push, skin: skin, volume: in volume);
                    }
                default: {
                        return FixedFieldColliderContact.TryPush(collider: in this, orientation: in orientation, position: position, push: out push, skin: skin,
                            uncertain: out uncertain, volume: in volume);
                    }
            }
        }

        uncertain = false;
        return TryGetAnalyticPush(orientation: in orientation, position: position, push: out push, skin: skin, volume: in volume);
    }
    /// <summary>Narrows a field collider for a query confined to a world box grown by a reach: the one exact analytic
    /// collider its <see cref="FixedColliderField.Partition"/> proves the solid there is, or none of the solid. The
    /// contact queries a field collider answers from a bounded region (a push, a support descent, rigid contact
    /// collection) ask here first, so a body its carves cannot reach reads the analytic surface the field equals there,
    /// and a support witness names the piece. A projection from a point (<see cref="FixedSurfaceQuery"/>'s nearest
    /// point for a hold or an anchor) has no bounded region and reads the field, which agrees with the piece within
    /// the field's bracket where both answer.</summary>
    /// <param name="center">The region's world centre.</param>
    /// <param name="extent">The region's world half-extent before the reach.</param>
    /// <param name="reach">The distance, in metres, the query looks past the region; a negative reach counts as zero.</param>
    /// <param name="collider">The exact analytic collider when the answer is <see cref="FixedFieldRegion.Base"/>.</param>
    /// <returns><see cref="FixedFieldRegion.Field"/> for a collider that is not a field, a field without a partition, or a
    /// region the partition does not narrow.</returns>
    internal FixedFieldRegion Narrow(FixedVector3 center, FixedVector3 extent, FixedQ4816 reach, out FixedStaticCollider collider) {
        collider = default;

        if ((Kind != FixedStaticColliderKind.Field) || (Field?.Partition is not { } partition)) {
            return FixedFieldRegion.Field;
        }

        var grow = (FixedQ4816.Max(x: reach, y: FixedQ4816.Zero) + BoundsPadding);

        return partition.Narrow(center: in center, collider: out collider, extent: (extent + new FixedVector3(X: grow, Y: grow, Z: grow)));
    }
    /// <summary>Narrows a field collider for a query that a narrowed answer replaces a field solve for, counting the
    /// replacement as <see cref="FixedStaticContactWork.FieldNarrowed"/>.</summary>
    /// <param name="center">The region's world centre.</param>
    /// <param name="extent">The region's world half-extent before the reach.</param>
    /// <param name="reach">The distance, in metres, the query looks past the region.</param>
    /// <param name="collider">The exact analytic collider when the answer is <see cref="FixedFieldRegion.Base"/>.</param>
    /// <returns>What <see cref="Narrow"/> answers.</returns>
    internal FixedFieldRegion NarrowQuery(FixedVector3 center, FixedVector3 extent, FixedQ4816 reach, out FixedStaticCollider collider) {
        var region = Narrow(center: center, collider: out collider, extent: extent, reach: reach);

        if (region != FixedFieldRegion.Field) {
            FixedStaticContactWork.Process.CountNarrowed();
        }

        return region;
    }
    /// <summary>Returns whether a query against this collider can answer with a collider: this one, or one of the exact
    /// pieces its field's partition narrows to (<see cref="Narrow"/>), as a support witness names.</summary>
    /// <param name="effective">The collider a solve answered with.</param>
    /// <returns><see langword="true"/> when <paramref name="effective"/> is this collider or one of its pieces.</returns>
    public bool AnswersWith(in FixedStaticCollider effective) =>
        ((effective == this) || ((Kind == FixedStaticColliderKind.Field) && (Field?.Partition is { } partition) && partition.Holds(collider: in effective)));
    private bool TryGetAnalyticPush(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        FixedQ4816 skin,
        out FixedContactPush push
    ) => (((volume.Kind == FixedBodyColliderKind.Ellipsoid) && (Kind != FixedStaticColliderKind.HalfSpace))
        ? FixedConvexContact.TryPush(collider: in this, orientation: in orientation, position: position,
            push: out push, skin: skin, volume: in volume)
        : Kind switch {
            FixedStaticColliderKind.ChamferedBox or FixedStaticColliderKind.Superellipsoid => FixedConvexContact.TryPush(
                collider: in this, orientation: in orientation, position: position, push: out push, skin: skin, volume: in volume),
            FixedStaticColliderKind.RoundedBox => FixedRoundedBoxContact.TryPush(box: in this, orientation: in orientation,
                position: position, push: out push, skin: skin, volume: in volume),
            FixedStaticColliderKind.Cylinder => FixedCylinderContact.TryPush(cylinder: in this, orientation: in orientation,
                position: position, push: out push, skin: skin, volume: in volume),
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
            FixedStaticColliderKind.OrientedBox => TryBoxPush(
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
        });
}
