using System.Numerics;
using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// The analytic <see cref="IContactField"/> — the document-derived, fixed-point, allocation-free-at-steady-state
/// contact provider. It compiles one convex collider per solid row (a sphere from a solid boulder, an axis-aligned box
/// from a solid slab, an axis-aligned box bounding a solid screen slab's oriented frame) plus the world ground plane,
/// so grounding has one owner and the walkable slope means one thing across the whole world.
/// </summary>
/// <remarks>
/// <para>A body is a vertical capsule (foot point, radius, height). Resolution treats the capsule's core as a vertical
/// segment and depenetrates it from every collider along the minimum-translation axis, killing the velocity component
/// driving into each resolved surface. A push whose normal's <c>+Y</c> alignment clears the compiled
/// <see cref="FixedWorldCollision.GroundedThreshold"/> grounds the body — the same test the ground plane uses.</para>
/// <para>Single-pass-per-iteration relaxation (up to <c>MaxIterations</c>): two adjacent solid boxes can push a body
/// back and forth within one tick. Accepted at authoring scale; the fix (push-order by penetration depth) is cheap and
/// additive. A slab's <c>Round</c> is deliberately NOT modelled — the resolver treats a rounded box as its bounding box,
/// which is conservative and stated as such. No broadphase: O(bodies × solid rows), trivial at the current scale; a
/// Y-sorted array with an AABB reject on the swept bounds is the cheap first cull if profiling ever demands one, behind
/// this seam with no signature change.</para>
/// </remarks>
internal sealed class WorldColliderSet : IContactField {
    private static readonly FixedVector3 s_unitY = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);

    private readonly Collider[] m_colliders;
    private readonly FixedQ4816 m_groundY;
    private readonly FixedQ4816 m_skin;
    private readonly FixedQ4816 m_groundedThreshold;
    private readonly int m_iterations;

    private WorldColliderSet(Collider[] colliders, FixedQ4816 groundY, FixedWorldCollision tuning) {
        m_colliders = colliders;
        m_groundY = groundY;
        m_skin = tuning.ContactSkin;
        m_groundedThreshold = tuning.GroundedThreshold;
        m_iterations = Math.Max(val1: 1, val2: tuning.MaxIterations);
    }

    /// <summary>The number of solid spheres in the set (boulders).</summary>
    public int SphereCount { get; private set; }

    /// <summary>The number of solid boxes in the set (slabs + screens).</summary>
    public int BoxCount { get; private set; }

    /// <summary>The total solid-row count (spheres + boxes).</summary>
    public int SolidCount => (SphereCount + BoxCount);

    /// <summary>Builds the analytic contact field from a definition, or <see langword="null"/> when collision is off
    /// (the absence-coalesced default) — a null field means bodies solve against nothing and keep their flat ground
    /// plane byte-identically.</summary>
    /// <param name="definition">The world definition supplying the collision tuning, the solid rows, and the ground
    /// plane.</param>
    /// <returns>The analytic field, or <see langword="null"/> when collision is disabled.</returns>
    public static WorldColliderSet? Build(WorldDefinition definition) {
        var collision = definition.Collision;

        if (!collision.Enabled) {
            return null;
        }

        var tuning = FixedWorldCollision.Compile(collision: collision);
        var colliders = new List<Collider>();
        var spheres = 0;
        var boxes = 0;

        foreach (var row in definition.Scene.Rows) {
            if (row.Solid is not { } solid) {
                continue;
            }

            var rowMargin = FixedQ4816.FromDouble(value: solid.Margin);

            switch (row) {
                case WorldSceneRow.Boulder boulder:
                    colliders.Add(item: Collider.Sphere(
                        center: ToFixed(value: boulder.Center),
                        radius: (FixedQ4816.FromDouble(value: boulder.Radius) + rowMargin)
                    ));
                    spheres++;

                    break;
                case WorldSceneRow.Slab slab:
                    colliders.Add(item: Collider.AxisAlignedBox(
                        center: ToFixed(value: slab.Center),
                        halfExtents: (ToFixed(value: slab.HalfExtents) + new FixedVector3(X: rowMargin, Y: rowMargin, Z: rowMargin))
                    ));
                    boxes++;

                    break;
            }
        }

        foreach (var screen in definition.Screens) {
            if (screen.Solid is not { } solid) {
                continue;
            }

            var rowMargin = FixedQ4816.FromDouble(value: solid.Margin);
            var (center, halfExtents) = ScreenBox(screen: screen);

            colliders.Add(item: Collider.AxisAlignedBox(
                center: center,
                halfExtents: (halfExtents + new FixedVector3(X: rowMargin, Y: rowMargin, Z: rowMargin))
            ));
            boxes++;
        }

        return new WorldColliderSet(colliders: colliders.ToArray(), groundY: FixedQ4816.FromDouble(value: definition.Motion.GroundY), tuning: tuning) {
            SphereCount = spheres,
            BoxCount = boxes,
        };
    }

    /// <inheritdoc/>
    public bool Resolve(ref FixedVector3 position, ref FixedVector3 velocity, FixedQ4816 radius, FixedQ4816 height) {
        var grounded = false;

        // The ground plane — one owner of grounding, resolved every tick.
        if (position.Y < m_groundY) {
            position = position with { Y = m_groundY };

            if (velocity.Y < FixedQ4816.Zero) {
                velocity = velocity with { Y = FixedQ4816.Zero };
            }

            grounded = true;
        } else if (position.Y <= (m_groundY + m_skin)) {
            grounded = true;
        }

        for (var iteration = 0; (iteration < m_iterations); iteration++) {
            var pushed = false;

            foreach (var collider in m_colliders) {
                pushed |= collider.Resolve(position: ref position, velocity: ref velocity, radius: radius, height: height, skin: m_skin, groundedThreshold: m_groundedThreshold, grounded: ref grounded);
            }

            if (!pushed) {
                break;
            }
        }

        return grounded;
    }

    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) {
        _ = position;
        up = s_unitY;

        return true;
    }

    // The axis-aligned bounding box of a screen slab's oriented frame: the geometry center sits one HalfDepth behind the
    // front-face Origin along the face normal, and each world-axis half-extent is the |projection| of the three oriented
    // axes. Exact for the axis-aligned screens the built-in world ships; conservative (bounding) for a rotated slab.
    private static (FixedVector3 Center, FixedVector3 HalfExtents) ScreenBox(WorldScreen screen) {
        var normal = Vector3.Normalize(value: Vector3.Cross(vector1: screen.Right, vector2: screen.Up));
        var right = Vector3.Normalize(value: screen.Right);
        var up = Vector3.Normalize(value: screen.Up);
        var center = (screen.Origin - (normal * screen.HalfDepth));
        var half = new Vector3(
            x: ((MathF.Abs(x: right.X) * screen.HalfWidth) + (MathF.Abs(x: up.X) * screen.HalfHeight) + (MathF.Abs(x: normal.X) * screen.HalfDepth)),
            y: ((MathF.Abs(x: right.Y) * screen.HalfWidth) + (MathF.Abs(x: up.Y) * screen.HalfHeight) + (MathF.Abs(x: normal.Y) * screen.HalfDepth)),
            z: ((MathF.Abs(x: right.Z) * screen.HalfWidth) + (MathF.Abs(x: up.Z) * screen.HalfHeight) + (MathF.Abs(x: normal.Z) * screen.HalfDepth))
        );

        return (Center: ToFixed(value: center), HalfExtents: ToFixed(value: half));
    }

    private static FixedVector3 ToFixed(Vector3 value) => new(
        X: FixedQ4816.FromDouble(value: value.X),
        Y: FixedQ4816.FromDouble(value: value.Y),
        Z: FixedQ4816.FromDouble(value: value.Z)
    );

    // A convex analytic collider. A sphere carries its radius in Extent.X; a box carries its world-axis half-extents in
    // Extent (Round is not modelled — the bounding box is conservative).
    private readonly record struct Collider(ColliderKind Kind, FixedVector3 Center, FixedVector3 Extent) {
        public static Collider Sphere(FixedVector3 center, FixedQ4816 radius) => new(Kind: ColliderKind.Sphere, Center: center, Extent: new FixedVector3(X: radius, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

        public static Collider AxisAlignedBox(FixedVector3 center, FixedVector3 halfExtents) => new(Kind: ColliderKind.Box, Center: center, Extent: halfExtents);

        // Depenetrate a vertical body capsule from this collider, killing the velocity component into the resolved
        // surface and grounding the body when the contact normal's +Y alignment clears the walkable-slope threshold.
        // Returns whether a push happened.
        public bool Resolve(ref FixedVector3 position, ref FixedVector3 velocity, FixedQ4816 radius, FixedQ4816 height, FixedQ4816 skin, FixedQ4816 groundedThreshold, ref bool grounded) {
            return ((Kind == ColliderKind.Sphere)
                ? ResolveSphere(position: ref position, velocity: ref velocity, radius: radius, height: height, skin: skin, groundedThreshold: groundedThreshold, grounded: ref grounded)
                : ResolveBox(position: ref position, velocity: ref velocity, radius: radius, height: height, skin: skin, groundedThreshold: groundedThreshold, grounded: ref grounded));
        }

        private bool ResolveSphere(ref FixedVector3 position, ref FixedVector3 velocity, FixedQ4816 radius, FixedQ4816 height, FixedQ4816 skin, FixedQ4816 groundedThreshold, ref bool grounded) {
            // The capsule's core vertical segment [foot + radius, foot + height - radius] shares X/Z with the foot; the
            // closest core point to the sphere center clamps its Y into that span.
            var lowerY = (position.Y + radius);
            var upperY = ((position.Y + height) - radius);
            var closest = position with { Y = FixedQ4816.Clamp(value: Center.Y, minimum: lowerY, maximum: upperY) };
            var delta = (closest - Center);
            var distance = delta.Length;
            var minimum = ((radius + Extent.X) + skin);

            if ((distance >= minimum) || (distance <= FixedQ4816.Zero)) {
                return false;
            }

            var normal = (delta / distance);

            position += (normal * (minimum - distance));

            if (normal.Y >= groundedThreshold) {
                grounded = true;
            }

            KillInto(velocity: ref velocity, normal: normal);

            return true;
        }

        private bool ResolveBox(ref FixedVector3 position, ref FixedVector3 velocity, FixedQ4816 radius, FixedQ4816 height, FixedQ4816 skin, FixedQ4816 groundedThreshold, ref bool grounded) {
            // A vertical capsule swept horizontally is a cylinder of radius; the X/Z faces inflate by radius + skin, the
            // foot resolves against the top/bottom faces (inflated by skin only). Push out along the least-overlap axis.
            var inflateXZ = (radius + skin);
            var dx = (position.X - Center.X);
            var dz = (position.Z - Center.Z);
            var overlapX = ((Extent.X + inflateXZ) - FixedQ4816.Abs(value: dx));
            var overlapZ = ((Extent.Z + inflateXZ) - FixedQ4816.Abs(value: dz));

            if ((overlapX <= FixedQ4816.Zero) || (overlapZ <= FixedQ4816.Zero)) {
                return false;
            }

            var boxTop = ((Center.Y + Extent.Y) + skin);
            var boxBottom = ((Center.Y - Extent.Y) - skin);
            var capsuleTop = (position.Y + height);
            var overlapTop = (boxTop - position.Y);           // push the foot up onto the box top
            var overlapBottom = (capsuleTop - boxBottom);      // push the capsule head down under the box bottom

            if ((overlapTop <= FixedQ4816.Zero) || (overlapBottom <= FixedQ4816.Zero)) {
                return false;
            }

            var pushUp = (overlapTop <= overlapBottom);
            var overlapY = (pushUp ? overlapTop : overlapBottom);

            if ((overlapY <= overlapX) && (overlapY <= overlapZ)) {
                if (pushUp) {
                    position = position with { Y = boxTop };
                    grounded |= (FixedQ4816.One >= groundedThreshold);

                    if (velocity.Y < FixedQ4816.Zero) {
                        velocity = velocity with { Y = FixedQ4816.Zero };
                    }
                } else {
                    position = position with { Y = (boxBottom - height) };

                    if (velocity.Y > FixedQ4816.Zero) {
                        velocity = velocity with { Y = FixedQ4816.Zero };
                    }
                }

                return true;
            }

            if (overlapX <= overlapZ) {
                var sign = ((dx >= FixedQ4816.Zero) ? FixedQ4816.One : -FixedQ4816.One);

                position = position with { X = (position.X + (overlapX * sign)) };

                if (((velocity.X > FixedQ4816.Zero) && (sign < FixedQ4816.Zero)) || ((velocity.X < FixedQ4816.Zero) && (sign > FixedQ4816.Zero))) {
                    velocity = velocity with { X = FixedQ4816.Zero };
                }
            } else {
                var sign = ((dz >= FixedQ4816.Zero) ? FixedQ4816.One : -FixedQ4816.One);

                position = position with { Z = (position.Z + (overlapZ * sign)) };

                if (((velocity.Z > FixedQ4816.Zero) && (sign < FixedQ4816.Zero)) || ((velocity.Z < FixedQ4816.Zero) && (sign > FixedQ4816.Zero))) {
                    velocity = velocity with { Z = FixedQ4816.Zero };
                }
            }

            return true;
        }

        // Remove the velocity component driving into a contact normal (a body never re-accelerates into a wall it just
        // resolved against).
        private static void KillInto(ref FixedVector3 velocity, FixedVector3 normal) {
            var into = FixedVector3.Dot(left: velocity, right: normal);

            if (into < FixedQ4816.Zero) {
                velocity -= (normal * into);
            }
        }
    }

    private enum ColliderKind : byte {
        Sphere,
        Box,
    }
}
