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
    private void ApplyPush(
        ref FixedVector3 position,
        ref FixedVector3 velocity,
        in FixedContactPush push,
        in FixedVector3 up,
        ref bool grounded,
        ref FixedVector3 lastNormal,
        ref FixedVector3 groundNormal
    ) {
        position += (push.Normal * push.Penetration);

        var walkable = (FixedVector3.Dot(
            left: push.Normal,
            right: up
        ) >= GroundedThreshold);

        grounded |= walkable;

        if (walkable) {
            groundNormal = push.Normal;
        }

        // The obstruction witness tracks only a NON-walkable push (a wall, not the ground or a ramp) — a standing body
        // re-resolves its ground contact every solver iteration, so an unconditional "last push" would have the ground
        // overwrite a genuine wall push from an earlier iteration in the SAME call.
        if (!walkable) {
            lastNormal = push.Normal;
        }

        var into = FixedVector3.Dot(
            left: velocity,
            right: push.Normal
        );

        if (into < FixedQ4816.Zero) {
            velocity -= (push.Normal * into);
        }
    }
    private bool Sweep(
        ReadOnlySpan<FixedStaticCollider> colliders,
        ref FixedVector3 position,
        ref FixedVector3 velocity,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        in FixedVector3 up,
        ref bool grounded,
        ref FixedVector3 lastNormal,
        ref FixedVector3 groundNormal
    ) {
        var pushed = false;

        foreach (var collider in colliders) {
            if (collider.TryGetPush(
                orientation: in orientation,
                position: position,
                push: out var push,
                skin: ContactSkin,
                volume: in volume
            )) {
                ApplyPush(
                    groundNormal: ref groundNormal,
                    grounded: ref grounded,
                    lastNormal: ref lastNormal,
                    position: ref position,
                    push: in push,
                    up: in up,
                    velocity: ref velocity
                );
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
        var grounded = false;
        var groundNormal = FixedVector3.Zero;
        var lastNormal = FixedVector3.Zero;
        var iterations = Math.Max(
            val1: 1,
            val2: MaxIterations
        );

        for (var iteration = 0; (iteration < iterations); iteration++) {
            var pushed = false;

            foreach (var volume in volumes) {
                pushed |= Sweep(
                    colliders: colliders,
                    groundNormal: ref groundNormal,
                    grounded: ref grounded,
                    lastNormal: ref lastNormal,
                    orientation: in orientation,
                    position: ref position,
                    up: in up,
                    velocity: ref velocity,
                    volume: in volume
                );
                pushed |= Sweep(
                    colliders: dynamicColliders,
                    groundNormal: ref groundNormal,
                    grounded: ref grounded,
                    lastNormal: ref lastNormal,
                    orientation: in orientation,
                    position: ref position,
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
            GroundNormal: groundNormal,
            Grounded: grounded,
            ObstructionNormal: lastNormal
        );
    }
}
