using Puck.Maths;

namespace Puck.Physics;

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
    // Highest support plane of a finite edge-bearing primitive. This is the clearance a step must buy, rather
    // than the small rise where the capsule first touches the edge's Minkowski arc. Otherwise repeated partial
    // landings would climb a 10 cm kerb even with a 5 cm step height.
    private static FixedQ4816 EdgeTop(in FixedStaticCollider collider, in FixedVector3 up) {
        var axis = ((collider.Kind == FixedStaticColliderKind.AxisAlignedBox) ? up : collider.Rotation.RotateInverse(vector: up));

        if (collider.Kind is FixedStaticColliderKind.ChamferedBox or FixedStaticColliderKind.Superellipsoid) {
            return (FixedVector3.Dot(left: collider.Center, right: up) + FixedVector3.Dot(left: FixedConvexGeometry.Support(collider: in collider, direction: axis), right: axis));
        }
        var radius = ((collider.Kind == FixedStaticColliderKind.Cylinder)
            ? ((collider.Extent.X * new FixedVector2(X: axis.X, Y: axis.Z).Length) + (collider.Extent.Y * FixedQ4816.Abs(value: axis.Y)))
            : (FixedVector3.Dot(left: collider.Extent, right: FixedVector3.Abs(value: axis)) + collider.Radius));

        return (FixedVector3.Dot(left: collider.Center, right: up) + radius);
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
    private bool Sweep(
        ReadOnlySpan<FixedStaticCollider> colliders,
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

        for (var index = 0; (index < colliders.Length); index++) {
            var collider = colliders[index];

            if (collider.TryGetPush(
                orientation: in orientation,
                position: position,
                push: out var push,
                skin: ContactSkin,
                volume: in volume
            )) {
                var blockClimb = (push.IsEdge && (FixedVector3.Dot(left: push.Normal, right: up) > FixedQ4816.Zero) &&
                    (FixedVector3.Dot(left: movement.RejectFrom(unitAxis: up), right: push.Normal) < FixedQ4816.Zero));

                if (blockClimb) {
                    var bottom = FixedColliderBounds.MinimumProjection(normal: up, orientation: in orientation,
                        position: previousPosition, volume: in volume);

                    requiredStepHeight = FixedQ4816.Max(x: requiredStepHeight, y: ((EdgeTop(collider: in collider, up: in up) + ContactSkin) - bottom));
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
    /// <returns>The grounded verdict, the surface it grounded on, and the last resolved non-walkable contact normal.</returns>
    public ContactResolution Resolve(
        ReadOnlySpan<FixedStaticCollider> colliders,
        ReadOnlySpan<FixedStaticCollider> dynamicColliders,
        ref FixedVector3 position,
        ref FixedVector3 velocity,
        in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes,
        in FixedVector3 up
    ) {
        return ResolveSweep(colliders: colliders, dynamicColliders: dynamicColliders, orientation: in orientation,
            position: ref position, previousPosition: position, up: in up, velocity: ref velocity, volumes: volumes);
    }
    /// <summary>Resolves a move, keeping lateral edge approach separate from vertical support. An edge met while
    /// moving into it is a wall; a vertical landing still uses its measured surface normal.</summary>
    /// <param name="colliders">The static colliders.</param>
    /// <param name="dynamicColliders">The per-tick colliders.</param>
    /// <param name="previousPosition">The foot point before the move.</param>
    /// <param name="position">The target and resolved foot point.</param>
    /// <param name="velocity">The velocity to resolve.</param>
    /// <param name="orientation">The body orientation.</param>
    /// <param name="volumes">The body volumes.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <returns>The support and obstruction witnesses.</returns>
    public ContactResolution ResolveSweep(ReadOnlySpan<FixedStaticCollider> colliders, ReadOnlySpan<FixedStaticCollider> dynamicColliders,
        in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
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
