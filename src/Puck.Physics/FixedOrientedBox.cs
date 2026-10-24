using Puck.Maths;

namespace Puck.Physics;

/// <summary>An oriented box in world space — its center, three orthonormal unit face axes and half-extents — and the
/// one owner of every box query the simulation asks: projection radius, world-axis extent, the box-local frame,
/// closest point, containment, the sphere test and the complete fifteen-axis separating-axis test. A placement's
/// spatial volume, a body collider's box volume and a static box collider all build one of these rather than
/// carrying a second copy of any of those.</summary>
/// <param name="Center">The box's world-space center.</param>
/// <param name="AxisX">The unit world direction of the box's local +X face axis.</param>
/// <param name="AxisY">The unit world direction of the box's local +Y face axis.</param>
/// <param name="AxisZ">The unit world direction of the box's local +Z face axis.</param>
/// <param name="HalfExtents">The box's half-extents along its own axes.</param>
/// <remarks>The local frame is the three dot products with the stored axes, each rounded once; a caller that holds the
/// box's unit rotation and needs the fused one-rounding-per-component frame change keeps <see cref="FixedQuaternion.RotateInverse"/>
/// (<see cref="FixedSurfaceQuery"/>'s oriented static box does). Every operation here is unchecked: a caller whose
/// inputs may leave the carrier refuses them at its own boundary, as the placement compiler does.</remarks>
public readonly record struct FixedOrientedBox(FixedVector3 Center, FixedVector3 AxisX, FixedVector3 AxisY, FixedVector3 AxisZ, FixedVector3 HalfExtents) {
    // A cross product this short names two parallel edges: its direction is rounding noise, and the face axes
    // already separate what it would.
    private static readonly FixedQ4816 DegenerateAxisLengthSquared = FixedQ4816.FromRawBits(value: 1L);

    /// <summary>Gets the box's extent along each world axis: the half-size of its world-axis-aligned bounds.</summary>
    /// <remarks>Each lane is <see cref="ProjectionRadius"/> on that world axis, which for a unit axis is exactly
    /// <c>|AxisX·e|·h.X + |AxisY·e|·h.Y + |AxisZ·e|·h.Z</c> over the axes' own components.</remarks>
    public FixedVector3 WorldExtent => new(
        X: ProjectionRadius(axis: FixedVector3.UnitX),
        Y: ProjectionRadius(axis: FixedVector3.UnitY),
        Z: ProjectionRadius(axis: FixedVector3.UnitZ)
    );

    // Appends the fifteen candidate separating axes of a box pair in the fixed order every caller shares: left's three
    // face axes, right's three, then the nine edge cross products left-major.
    private static void SeparatingAxes(in FixedOrientedBox left, in FixedOrientedBox right, Span<FixedVector3> axes) {
        axes[0] = left.AxisX;
        axes[1] = left.AxisY;
        axes[2] = left.AxisZ;
        axes[3] = right.AxisX;
        axes[4] = right.AxisY;
        axes[5] = right.AxisZ;

        var index = 6;

        for (var leftAxis = 0; (leftAxis < 3); leftAxis++) {
            for (var rightAxis = 0; (rightAxis < 3); rightAxis++) {
                axes[index++] = FixedVector3.Cross(
                    left: axes[leftAxis],
                    right: axes[(3 + rightAxis)]
                );
            }
        }
    }

    /// <summary>Builds the box a body's box volume occupies at a pose.</summary>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local box volume.</param>
    /// <returns>The box centered at <c>position + orientation·volume.Center</c>, with its axes the unit axes turned
    /// by the normalized product of the body's orientation and the volume's own rotation.</returns>
    public static FixedOrientedBox FromVolume(FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume) =>
        FromRotation(
            center: (position + orientation.Rotate(vector: volume.Center)),
            halfExtents: volume.HalfExtents,
            rotation: (orientation * volume.Rotation).Normalize()
        );
    /// <summary>Builds a box from its center, its box-to-world rotation and its half-extents.</summary>
    /// <param name="center">The world-space center.</param>
    /// <param name="rotation">The unit box-to-world rotation; each world axis is a unit axis turned by it.</param>
    /// <param name="halfExtents">The half-extents along the box's own axes.</param>
    /// <returns>The box.</returns>
    public static FixedOrientedBox FromRotation(FixedVector3 center, FixedQuaternion rotation, FixedVector3 halfExtents) =>
        new(
            AxisX: rotation.Rotate(vector: FixedVector3.UnitX),
            AxisY: rotation.Rotate(vector: FixedVector3.UnitY),
            AxisZ: rotation.Rotate(vector: FixedVector3.UnitZ),
            Center: center,
            HalfExtents: halfExtents
        );
    /// <summary>Builds a world-axis-aligned box.</summary>
    /// <param name="center">The world-space center.</param>
    /// <param name="halfExtents">The half-extents along the world axes.</param>
    /// <returns>The box whose axes are the world axes, so its local frame is an exact translation.</returns>
    public static FixedOrientedBox AxisAligned(FixedVector3 center, FixedVector3 halfExtents) =>
        new(
            AxisX: FixedVector3.UnitX,
            AxisY: FixedVector3.UnitY,
            AxisZ: FixedVector3.UnitZ,
            Center: center,
            HalfExtents: halfExtents
        );
    /// <summary>Returns whether two boxes overlap: the complete separating-axis test over both boxes' face axes and
    /// their nine edge cross products.</summary>
    /// <param name="left">The first box.</param>
    /// <param name="right">The second box.</param>
    /// <returns><see langword="true"/> when no candidate axis separates the boxes' projections; a touching pair
    /// (equal projected radii and center distance on an axis) overlaps. <see langword="false"/> when the center
    /// difference does not fit the carrier.</returns>
    /// <remarks>The projections are compared on each axis at its own length, which a cross product need not have as
    /// unit: the verdict is scale-free, so no axis is normalized and a near-parallel edge pair (a cross product of at
    /// most one raw squared) is skipped.</remarks>
    public static bool Overlaps(in FixedOrientedBox left, in FixedOrientedBox right) {
        if (!FixedVector3.TrySubtract(
            difference: out var delta,
            left: right.Center,
            right: left.Center
        )) {
            return false;
        }

        Span<FixedVector3> axes = stackalloc FixedVector3[15];

        SeparatingAxes(
            axes: axes,
            left: in left,
            right: in right
        );

        foreach (var axis in axes) {
            if (axis.LengthSquared <= DegenerateAxisLengthSquared) {
                continue;
            }

            if (FixedQ4816.Abs(value: FixedVector3.Dot(
                left: delta,
                right: axis
            )) > (left.ProjectionRadius(axis: axis) + right.ProjectionRadius(axis: axis))) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Finds the shortest translation that separates two overlapping boxes: the complete separating-axis test
    /// with depth, over both boxes' face axes and their nine edge cross products, each taken at unit length.</summary>
    /// <param name="left">The box the correction moves.</param>
    /// <param name="right">The box it moves out of.</param>
    /// <param name="tieBreaker">Selects the correction's sign only when the winning axis reads exactly coincident
    /// centers along it: an even value pushes along the axis, an odd one against it.</param>
    /// <param name="correction">The winning unit axis scaled by its overlap depth, directed from
    /// <paramref name="right"/> toward <paramref name="left"/>.</param>
    /// <param name="padding">Additional separation along each unit axis. For a spherical offset, use only after
    /// the unpadded cores have been shown to overlap; separated cores require their Euclidean closest distance.</param>
    /// <returns><see langword="true"/> when every candidate axis reads a positive padded overlap;
    /// <see langword="false"/> the moment any axis proves separation, when an unpadded pair merely touches, or when
    /// the center difference does not fit the carrier.</returns>
    /// <remarks>A face axis is unit already; each edge cross product is normalized once so its overlap is a distance,
    /// and a near-parallel edge pair (a cross product of at most one raw squared) is skipped. Among equal depths the
    /// first axis in order wins: left's face axes, right's, then the cross products left-major.</remarks>
    public static bool TrySeparation(in FixedOrientedBox left, in FixedOrientedBox right, int tieBreaker, out FixedVector3 correction, FixedQ4816 padding = default) {
        correction = default;

        if (!FixedVector3.TrySubtract(
            difference: out var delta,
            left: left.Center,
            right: right.Center
        )) {
            return false;
        }

        Span<FixedVector3> axes = stackalloc FixedVector3[15];

        SeparatingAxes(
            axes: axes,
            left: in left,
            right: in right
        );

        var bestOverlap = FixedQ4816.MaxValue;
        var bestAxis = FixedVector3.Zero;
        var bestCenterDistance = FixedQ4816.Zero;

        for (var index = 0; (index < axes.Length); index++) {
            var axis = axes[index];

            if (index >= 6) {
                if (axis.LengthSquared <= DegenerateAxisLengthSquared) {
                    continue;
                }

                axis = axis.Normalize();
            }

            var centerDistance = FixedVector3.Dot(
                left: delta,
                right: axis
            );
            var overlap = (((left.ProjectionRadius(axis: axis) + right.ProjectionRadius(axis: axis)) + padding) - FixedQ4816.Abs(value: centerDistance));

            if (overlap <= FixedQ4816.Zero) {
                return false;
            }

            if (overlap < bestOverlap) {
                bestOverlap = overlap;
                bestAxis = axis;
                bestCenterDistance = centerDistance;
            }
        }

        var sign = ((bestCenterDistance == FixedQ4816.Zero)
            ? (((tieBreaker & 1) == 0)
                ? FixedQ4816.One
                : -FixedQ4816.One)
            : ((bestCenterDistance > FixedQ4816.Zero)
                ? FixedQ4816.One
                : -FixedQ4816.One
        ));

        correction = (bestAxis * (bestOverlap * sign));

        return true;
    }
    /// <summary>Returns the nearest point of the box's closed volume to a box-local point: the point itself when it
    /// is inside, else its componentwise clamp onto the box.</summary>
    /// <param name="local">A point in the box's own frame, as <see cref="ToLocal"/> gives it.</param>
    /// <returns>The clamp of <paramref name="local"/> into <c>[−HalfExtents, HalfExtents]</c>, exact.</returns>
    public FixedVector3 ClosestLocal(FixedVector3 local) =>
        FixedVector3.Clamp(
            maximum: HalfExtents,
            minimum: -HalfExtents,
            value: local
        );
    /// <summary>Returns whether the closed box contains a world-space point.</summary>
    /// <param name="point">The point to test.</param>
    /// <returns><see langword="true"/> when every box-local coordinate of the point is within its half-extent;
    /// <see langword="false"/> when the point's offset from the center does not fit the carrier.</returns>
    public bool Contains(FixedVector3 point) =>
        (TryToLocal(
            local: out var local,
            point: point
        ) &&
        (FixedQ4816.Abs(value: local.X) <= HalfExtents.X) &&
        (FixedQ4816.Abs(value: local.Y) <= HalfExtents.Y) &&
        (FixedQ4816.Abs(value: local.Z) <= HalfExtents.Z));
    /// <summary>Returns whether a sphere meets the closed box: the sphere's center is within its radius of the box's
    /// nearest point, by the exact <see cref="FixedVector3.IsWithin"/> predicate.</summary>
    /// <param name="center">The sphere's world-space center.</param>
    /// <param name="radius">The sphere's radius.</param>
    /// <returns><see langword="true"/> when the box-local gap between the center and its clamp lies within the
    /// radius, a touching pair included; <see langword="false"/> when the center's offset does not fit the carrier.</returns>
    public bool IntersectsSphere(FixedVector3 center, FixedQ4816 radius) =>
        (TryToLocal(
            local: out var local,
            point: center
        ) &&
        (local - ClosestLocal(local: local)).IsWithin(radius: radius));
    /// <summary>Returns the least projection of the box onto a direction: <c>Center·normal</c> less the box's
    /// <see cref="ProjectionRadius"/> on it, the support in <c>−normal</c>.</summary>
    /// <param name="normal">The direction, a unit vector.</param>
    /// <returns>The box's lowest extent along <paramref name="normal"/>.</returns>
    public FixedQ4816 MinimumProjection(FixedVector3 normal) =>
        (FixedVector3.Dot(
            left: Center,
            right: normal
        ) - ProjectionRadius(axis: normal));
    /// <summary>Returns the box's projected half-width on an axis: <c>|AxisX·axis|·h.X + |AxisY·axis|·h.Y +
    /// |AxisZ·axis|·h.Z</c>, each dot and product rounded once and the sum exact.</summary>
    /// <param name="axis">The axis to project onto; the radius scales with its length.</param>
    /// <returns>The projected half-width.</returns>
    public FixedQ4816 ProjectionRadius(FixedVector3 axis) =>
        (((FixedQ4816.Abs(value: FixedVector3.Dot(
            left: AxisX,
            right: axis
        )) * HalfExtents.X) +
        (FixedQ4816.Abs(value: FixedVector3.Dot(
            left: AxisY,
            right: axis
        )) * HalfExtents.Y)) +
        (FixedQ4816.Abs(value: FixedVector3.Dot(
            left: AxisZ,
            right: axis
        )) * HalfExtents.Z));
    /// <summary>Carries a world-space point into the box's own frame: its offset from the center projected onto
    /// the three axes.</summary>
    /// <param name="point">The world-space point.</param>
    /// <returns>The box-local coordinates, each dot rounded once.</returns>
    public FixedVector3 ToLocal(FixedVector3 point) =>
        ToLocalOffset(offset: (point - Center));
    /// <summary>Carries a world-space direction into the box's own frame.</summary>
    /// <param name="offset">The world-space offset or direction.</param>
    /// <returns>Its projection onto the three axes, each dot rounded once.</returns>
    public FixedVector3 ToLocalOffset(FixedVector3 offset) =>
        new(
            X: FixedVector3.Dot(
                left: offset,
                right: AxisX
            ),
            Y: FixedVector3.Dot(
                left: offset,
                right: AxisY
            ),
            Z: FixedVector3.Dot(
                left: offset,
                right: AxisZ
            )
        );
    /// <summary>Carries a box-local point back to world space: the center plus each axis scaled by its coordinate.</summary>
    /// <param name="local">The box-local point.</param>
    /// <returns>The world-space point.</returns>
    public FixedVector3 ToWorld(FixedVector3 local) =>
        (Center + ToWorldOffset(local: local));
    /// <summary>Carries a box-local direction back to world space: each axis scaled by its coordinate, summed.</summary>
    /// <param name="local">The box-local offset or direction.</param>
    /// <returns>The world-space offset.</returns>
    public FixedVector3 ToWorldOffset(FixedVector3 local) =>
        (((AxisX * local.X) + (AxisY * local.Y)) + (AxisZ * local.Z));
    /// <summary>Carries a world-space point into the box's own frame, refusing an offset the carrier cannot hold.</summary>
    /// <param name="point">The world-space point.</param>
    /// <param name="local">The box-local coordinates when this returns <see langword="true"/>; zero otherwise.</param>
    /// <returns><see langword="false"/> when the point's offset from the center leaves the signed 64-bit raw.</returns>
    public bool TryToLocal(FixedVector3 point, out FixedVector3 local) {
        if (!FixedVector3.TrySubtract(
            difference: out var offset,
            left: point,
            right: Center
        )) {
            local = FixedVector3.Zero;

            return false;
        }

        local = ToLocalOffset(offset: offset);

        return true;
    }
}
