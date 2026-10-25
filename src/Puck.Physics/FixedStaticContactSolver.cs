using Puck.Maths;

namespace Puck.Physics;

/// <summary>The static colliders a sweep may reach from one region: every collider whose padded bounds come within
/// the contact skin of the region, in ascending index order. A body whose bounds stay inside the region needs no other
/// collider's exact query; once its bounds leave the region the sweep queries every remaining collider.</summary>
/// <param name="indices">The candidate collider indices, ascending and distinct.</param>
/// <param name="regionCenter">The region's world center.</param>
/// <param name="regionExtent">The region's world-axis half-extent.</param>
public readonly ref struct FixedColliderCandidates(ReadOnlySpan<int> indices, FixedVector3 regionCenter, FixedVector3 regionExtent) {
    /// <summary>Gets the candidate collider indices, ascending and distinct.</summary>
    public ReadOnlySpan<int> Indices { get; } = indices;
    /// <summary>Gets the region's world center.</summary>
    public FixedVector3 RegionCenter { get; } = regionCenter;
    /// <summary>Gets the region's world-axis half-extent.</summary>
    public FixedVector3 RegionExtent { get; } = regionExtent;
    /// <summary>Gets whether a region was supplied; <see langword="false"/> for the default value, which queries
    /// every collider.</summary>
    public bool IsActive { get; } = true;

    /// <summary>Returns whether a world-axis box lies inside the region.</summary>
    /// <param name="center">The box's world center.</param>
    /// <param name="extent">The box's world-axis half-extent.</param>
    /// <returns><see langword="true"/> when the box lies inside the region on every axis.</returns>
    public bool Contains(in FixedVector3 center, in FixedVector3 extent) {
        var slack = (RegionExtent - (FixedVector3.Abs(value: (center - RegionCenter)) + extent));

        return ((slack.X >= FixedQ4816.Zero) && (slack.Y >= FixedQ4816.Zero) && (slack.Z >= FixedQ4816.Zero));
    }
}

/// <summary>
/// Relaxes a body's compiled volumes out of a set of static colliders — the analytic half of
/// <see cref="IContactField"/>, written once over <see cref="FixedStaticCollider.TryGetPush"/>.
/// </summary>
/// <remarks>
/// <para>Single-pass-per-iteration relaxation: two adjacent colliders can push a body back and forth within one call.
/// Push order is collider order, so the caller's array order is part of the result.</para>
/// <para>The solve takes two collider spans and walks both inside each iteration rather than resolving one and then
/// the other, so a caller may hold a set it rebuilds per tick beside one it compiled once without changing how the
/// two interleave.</para>
/// </remarks>
/// <param name="ContactSkin">The signed skin kept between a body and every surface.</param>
/// <param name="GroundedThreshold">The <c>cos(maxSlope)</c> a contact normal's <c>+Y</c> alignment must clear to
/// ground the body.</param>
/// <param name="MaxIterations">The relaxation iteration count; values below one resolve one pass.</param>
public readonly record struct FixedStaticContactSolver(FixedQ4816 ContactSkin, FixedQ4816 GroundedThreshold, int MaxIterations) {
    // The touched edge's local clearance, not the apex of an entire tilted ramp. For rounded boxes, remove
    // the fillet offset before clamping to the inner edge and charge its full radius above that edge. This
    // still prevents repeated partial landings from climbing a kerb taller than the permitted step.
    private static FixedQ4816 EdgeTop(in FixedStaticCollider collider, in FixedVector3 up,
        in FixedVector3 witness, in FixedVector3 normal) {
        if (collider.Kind is FixedStaticColliderKind.AxisAlignedBox or FixedStaticColliderKind.OrientedBox or FixedStaticColliderKind.RoundedBox) {
            var point = (witness - (normal * collider.Radius));
            var local = ((collider.Kind == FixedStaticColliderKind.AxisAlignedBox) ? (point - collider.Center) : collider.Rotation.RotateInverse(vector: (point - collider.Center)));

            local = FixedVector3.Clamp(value: local, minimum: -collider.Extent, maximum: collider.Extent);
            point = (collider.Center + ((collider.Kind == FixedStaticColliderKind.AxisAlignedBox) ? local : collider.Rotation.Rotate(vector: local)));
            return (FixedVector3.Dot(left: point, right: up) + collider.Radius);
        }
        return FixedVector3.Dot(left: witness, right: up);
    }
    private void ApplyPush(
        ref FixedVector3 position,
        ref FixedVector3 velocity,
        in FixedContactPush push,
        bool blockClimb,
        in FixedVector3 up,
        ref bool grounded,
        ref FixedVector3 lastNormal,
        ref FixedVector3 groundNormal
    ) {
        // The obstruction witness (lastNormal) tracks only a NON-walkable push (a wall, not the ground or a ramp) —
        // a standing body re-resolves its ground contact every solver iteration, so an unconditional "last push"
        // would have the ground overwrite a genuine wall push from an earlier iteration in the SAME call.
        FixedContactPushMath.Commit(
            grounded: ref grounded,
            groundNormal: ref groundNormal,
            lastNormal: ref lastNormal,
            position: ref position,
            trial: FixedContactPushMath.ComputeOrdinary(
                // The capsule's Minkowski edge tilts even when the obstacle is a stair. Lateral approach
                // cannot buy elevation through that normal: only the raised step-up may clear the edge.
                blockClimb: blockClimb,
                groundedThreshold: GroundedThreshold,
                normal: push.Normal,
                penetration: push.Penetration,
                up: up,
                velocity: in velocity
            ),
            velocity: ref velocity
        );
    }
    /// <summary>Returns whether a collider's bounds lie further than the contact skin from a body's along some world
    /// axis, so its exact query would find no push.</summary>
    /// <param name="colliderCenter">The collider's world center.</param>
    /// <param name="colliderExtent">The collider's padded world half-extent.</param>
    /// <param name="bodyCenter">The body bounds' world center.</param>
    /// <param name="bodyExtent">The body bounds' world half-extent.</param>
    /// <returns><see langword="true"/> when the collider cannot push the body.</returns>
    public bool Misses(in FixedVector3 colliderCenter, in FixedVector3 colliderExtent, in FixedVector3 bodyCenter, in FixedVector3 bodyExtent) {
        var gap = (FixedVector3.Abs(value: (colliderCenter - bodyCenter)) - ((colliderExtent + bodyExtent) + new FixedVector3(X: ContactSkin, Y: ContactSkin, Z: ContactSkin)));

        return ((gap.X > FixedQ4816.Zero) || (gap.Y > FixedQ4816.Zero) || (gap.Z > FixedQ4816.Zero));
    }
    private bool Sweep(
        ReadOnlySpan<FixedStaticCollider> colliders,
        ReadOnlySpan<FixedVector3?> extents,
        in FixedColliderCandidates candidates,
        FixedSurfaceColliderSource source,
        ref ContactSupportWitness? support,
        ref FixedVector3 position,
        ref FixedVector3 velocity,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        in FixedVector3 movement,
        in FixedVector3 previousPosition,
        in FixedVector3 up,
        ref bool grounded,
        ref FixedVector3 lastNormal,
        ref FixedVector3 groundNormal,
        ref FixedQ4816 requiredStepHeight
    ) {
        var pushed = false;
        var culls = (extents.Length == colliders.Length);
        var bounds = (culls ? FixedColliderBounds.WorldBounds(orientation: in orientation, position: position, volume: in volume) : default);

        var indexed = (culls && candidates.IsActive && candidates.Contains(center: bounds.Center, extent: bounds.Extent));
        var next = 0;

        for (var index = 0; (index < colliders.Length); index++) {
            if (indexed) {
                while ((next < candidates.Indices.Length) && (candidates.Indices[next] < index)) { next++; }
                if (next == candidates.Indices.Length) { break; }
                index = candidates.Indices[next];
            }
            var collider = colliders[index];

            if (culls && (extents[index] is { } extent) && Misses(bodyCenter: bounds.Center, bodyExtent: bounds.Extent,
                colliderCenter: collider.Center, colliderExtent: extent)) {
                continue;
            }
            if (collider.TryGetPush(
                orientation: in orientation,
                position: position,
                push: out var push,
                skin: ContactSkin,
                volume: in volume
            )) {
                var blockClimb = (push.IsEdge &&
                    (FixedVector3.Dot(left: push.Normal, right: up) > FixedQ4816.Zero) &&
                    (FixedVector3.Dot(left: movement.RejectFrom(unitAxis: up), right: push.Normal) < FixedQ4816.Zero));

                if (blockClimb) {
                    var bottom = FixedColliderBounds.MinimumProjection(normal: up, orientation: in orientation,
                        position: previousPosition, volume: in volume);
                    var probe = ((position + FixedRigidWitness.Anchor(volume, FixedVector3.Zero, orientation, -push.Normal)) +
                        (push.Normal * (push.Penetration - ContactSkin)));

                    var (witness, normal) = FixedSurfaceQuery.NearestOnCollider(collider: in collider, probe: probe);

                    // A nearly flat fillet at or below the pre-move foot is support, not a new riser.
                    blockClimb = ((FixedVector3.Dot(left: witness, right: up) - bottom) > FixedQ4816.Epsilon);
                    if (blockClimb) {
                        requiredStepHeight = FixedQ4816.Max(x: requiredStepHeight, y: ((EdgeTop(collider: in collider, normal: in normal,
                            up: in up, witness: in witness) + ContactSkin) - bottom));
                    }
                }
                ApplyPush(
                    blockClimb: blockClimb,
                    groundNormal: ref groundNormal,
                    grounded: ref grounded,
                    lastNormal: ref lastNormal,
                    position: ref position,
                    push: in push,
                    up: in up,
                    velocity: ref velocity
                );
                if (FixedVector3.Dot(left: push.Normal, right: up) >= GroundedThreshold) {
                    support = new ContactSupportWitness(Collider: collider, ColliderIndex: index, Orientation: orientation,
                        Position: position, Push: push, Source: source, Volume: volume) {
                        BodyPoint = (position + FixedRigidWitness.Anchor(volume, FixedVector3.Zero, orientation, -push.Normal)),
                        Up = up,
                        WalkableThreshold = GroundedThreshold,
                    };
                }
                pushed = true;
                if (culls) {
                    bounds = FixedColliderBounds.WorldBounds(orientation: in orientation, position: position, volume: in volume);
                    indexed &= candidates.Contains(center: bounds.Center, extent: bounds.Extent);
                }
            }
        }

        return pushed;
    }

    /// <summary>Resolves a body's swept position and velocity out of both collider spans.</summary>
    /// <param name="colliders">The colliders compiled once.</param>
    /// <param name="dynamicColliders">The colliders the caller recomputes per tick, or empty.</param>
    /// <param name="position">The body's foot point (in/out).</param>
    /// <param name="velocity">The body's velocity (in/out): the component driving into any resolved surface is removed.</param>
    /// <param name="orientation">The body's local-to-world orientation.</param>
    /// <param name="volumes">The body's compiled convex volumes.</param>
    /// <param name="up">The body's up axis, which the grounded test measures a contact normal's alignment against.</param>
    /// <param name="colliderExtents">The <see cref="FixedStaticCollider.WorldHalfExtent"/> of each of
    /// <paramref name="colliders"/>, in order, or empty to query every collider exactly.</param>
    /// <param name="candidates">The colliders reachable from a region around the body, or the default value to visit
    /// every collider; used only with <paramref name="colliderExtents"/>.</param>
    /// <returns>The grounded verdict, the surface it grounded on, and the last resolved non-walkable contact normal.</returns>
    public ContactResolution Resolve(
        ReadOnlySpan<FixedStaticCollider> colliders,
        ReadOnlySpan<FixedStaticCollider> dynamicColliders,
        ref FixedVector3 position,
        ref FixedVector3 velocity,
        in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes,
        in FixedVector3 up,
        ReadOnlySpan<FixedVector3?> colliderExtents = default,
        FixedColliderCandidates candidates = default
    ) {
        return ResolveSweep(colliders: colliders, dynamicColliders: dynamicColliders, orientation: in orientation,
            position: ref position, previousPosition: position, up: in up, velocity: ref velocity, volumes: volumes,
            colliderExtents: colliderExtents, candidates: candidates);
    }
    /// <summary>Resolves a move, keeping lateral edge approach separate from vertical support. An edge above the
    /// pre-move foot met laterally is a wall; a vertical landing still uses its measured surface normal.</summary>
    /// <param name="colliders">The static colliders.</param>
    /// <param name="dynamicColliders">The per-tick colliders.</param>
    /// <param name="previousPosition">The foot point before the move.</param>
    /// <param name="position">The target and resolved foot point.</param>
    /// <param name="velocity">The velocity to resolve.</param>
    /// <param name="orientation">The body orientation.</param>
    /// <param name="volumes">The body volumes.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <param name="colliderExtents">The <see cref="FixedStaticCollider.WorldHalfExtent"/> of each of
    /// <paramref name="colliders"/>, in order, or empty to query every collider exactly. Skipping a collider these
    /// bounds rule out never changes the result.</param>
    /// <param name="candidates">The colliders reachable from a region around the move, or the default value to visit
    /// every collider; used only with <paramref name="colliderExtents"/>.</param>
    /// <returns>The support and obstruction witnesses.</returns>
    public ContactResolution ResolveSweep(ReadOnlySpan<FixedStaticCollider> colliders, ReadOnlySpan<FixedStaticCollider> dynamicColliders,
        in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up, ReadOnlySpan<FixedVector3?> colliderExtents = default,
        FixedColliderCandidates candidates = default) {
        var movement = (position - previousPosition);
        var requiredStepHeight = FixedQ4816.Zero;
        var grounded = false;
        ContactSupportWitness? support = null;
        var groundNormal = FixedVector3.Zero;
        var lastNormal = FixedVector3.Zero;
        var iterations = Math.Max(
            val1: 1,
            val2: MaxIterations
        );

        for (var iteration = 0; (iteration < iterations); iteration++) {
            var pushed = false;

            foreach (ref readonly var volume in volumes) {
                pushed |= Sweep(
                    colliders: colliders,
                    extents: colliderExtents,
                    candidates: in candidates,
                    groundNormal: ref groundNormal,
                    grounded: ref grounded,
                    lastNormal: ref lastNormal,
                    movement: in movement,
                    orientation: in orientation,
                    position: ref position,
                    previousPosition: in previousPosition,
                    requiredStepHeight: ref requiredStepHeight,
                    source: FixedSurfaceColliderSource.Static,
                    support: ref support,
                    up: in up,
                    velocity: ref velocity,
                    volume: in volume
                );
                pushed |= Sweep(
                    colliders: dynamicColliders,
                    extents: default,
                    candidates: default,
                    groundNormal: ref groundNormal,
                    grounded: ref grounded,
                    lastNormal: ref lastNormal,
                    movement: in movement,
                    orientation: in orientation,
                    position: ref position,
                    previousPosition: in previousPosition,
                    requiredStepHeight: ref requiredStepHeight,
                    source: FixedSurfaceColliderSource.Dynamic,
                    support: ref support,
                    up: in up,
                    velocity: ref velocity,
                    volume: in volume
                );
            }

            if (!pushed) {
                break;
            }
        }

        return new ContactResolution(
            RequiredStepHeight: requiredStepHeight,
            GroundNormal: groundNormal,
            Grounded: grounded,
            ObstructionNormal: lastNormal
        ) { Support = support };
    }
}
