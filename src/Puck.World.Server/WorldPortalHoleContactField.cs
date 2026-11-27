using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>
/// Wraps a world's contact field so a body standing in a portal's opening is not held back by geometry behind the
/// glass: a door laid on solid ground lets a body drop through it, and a door on a wall lets a body walk into it, until
/// its origin passes the glass and the door fires (<see cref="WorldFaceRegion"/>).
/// </summary>
/// <remarks>
/// <para>A body is in an opening when its foot point, before the solve, lies within the face's rectangle and, on either
/// side of the glass, within the body's own band: as far as its collider reaches along the face's normal, plus its travel
/// along the normal this step and the contact skin, never thinner than the face's slab. Its collider's volumes must also
/// lie within the rectangle across the glass, so a body too big for a door meets the wall round it; of several openings,
/// the one whose glass is nearest the foot point. The inner field resolves the step as it would with no door; then
/// every push it made out of the door along the face's outward normal is taken back — the position keeps the depth
/// it was stepped to and the velocity keeps the speed it had into the door — and a ground contact facing out of the
/// door no longer grounds the body. Pushes across the opening, from the door's own sides, stand.</para>
/// <para>The geometry behind the glass is therefore ignored by the push it makes, not cut out of the field: geometry
/// in front of the glass within the opening pushes no differently. Every value is fixed point. It wraps the world's
/// own provider, inside any adjacency composition, so the provider it wraps answers for terrain alone.</para>
/// </remarks>
public sealed class WorldPortalHoleContactField : IContactField {
    private readonly IContactField m_inner;
    private readonly (string PlacementId, string Face, WorldFaceAperture.Box Box)[] m_openings;
    private readonly WorldPortalClosures? m_closures;
    // The world's contact skin, which the solve keeps a pushed body's collider clear by.
    private readonly FixedQ4816 m_skin;

    private WorldPortalHoleContactField(IContactField inner, (string PlacementId, string Face, WorldFaceAperture.Box Box)[] openings, WorldPortalClosures? closures, FixedQ4816 skin) {
        m_inner = inner;
        m_openings = openings;
        m_closures = closures;
        m_skin = skin;
    }

    /// <summary>Wraps <paramref name="inner"/> with the openings of every portal face <paramref name="definition"/>
    /// declares, or returns it unwrapped when the world declares none.</summary>
    /// <param name="inner">The world's own contact field.</param>
    /// <param name="definition">The world's definition.</param>
    /// <param name="closures">The doors standing closed, whose openings hold a body back as their walls do; <see langword="null"/>
    /// treats every door as open.</param>
    /// <returns>The field bodies resolve against.</returns>
    public static IContactField Wrap(IContactField inner, WorldDefinition definition, WorldPortalClosures? closures = null) {
        var doors = WorldFaceCatalog.For(definition: definition).PortalDoors(definition: definition);
        var openings = new List<(string PlacementId, string Face, WorldFaceAperture.Box Box)>(capacity: doors.Count);

        for (var index = 0; (index < doors.Count); index++) {
            if (doors[index].Aperture is WorldFaceAperture.Box box) {
                openings.Add(item: (definition.Placements[doors[index].Row].Id, doors[index].Face.Face, box));
            }
        }

        return ((openings.Count > 0)
            ? new WorldPortalHoleContactField(
                closures: closures,
                inner: inner,
                openings: [.. openings],
                skin: WorldFacePortalPolicy.SlabFloor(definition: definition)
            )
            : inner
        );
    }

    // How far a volume reaches from its centre (and a capsule's from its endpoint) along any axis: its radius, or for a
    // box or an ellipsoid the sum of its half-extents, which bounds it whatever its rotation.
    private static FixedQ4816 Extent(in FixedBodyColliderVolume volume) => ((volume.Kind is FixedBodyColliderKind.Box or FixedBodyColliderKind.Ellipsoid)
        ? FixedVector3.L1Norm(value: volume.HalfExtents)
        : volume.Radius
    );
    // How far the body's collider reaches from its origin along a face's normal, either way, from each volume's support
    // (FixedColliderBounds.MinimumProjection): how far from a door's glass its origin can stand while the collider meets
    // the glass's plane.
    private static FixedQ4816 ReachAlong(in FixedVector3 normal, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        var reach = FixedQ4816.Zero;

        foreach (var volume in volumes) {
            reach = FixedQ4816.Max(x: reach, y: FixedQ4816.Max(
                x: -FixedColliderBounds.MinimumProjection(normal: normal, orientation: in orientation, position: FixedVector3.Zero, volume: in volume),
                y: -FixedColliderBounds.MinimumProjection(normal: -normal, orientation: in orientation, position: FixedVector3.Zero, volume: in volume)
            ));
        }

        return reach;
    }
    // Whether every volume of the body lies within the opening's rectangle across its glass: a body too big for a door,
    // as a body at full size is for a door a quarter its size, meets the wall round it.
    private static bool Fits(in WorldFaceFrame frame, in FixedVector3 position, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        foreach (var volume in volumes) {
            var reach = Extent(volume: in volume);

            if (
                !Within(frame: in frame, point: (position + orientation.Rotate(vector: volume.Center)), reach: reach) ||
                ((volume.Kind == FixedBodyColliderKind.Capsule) && !Within(frame: in frame, point: (position + orientation.Rotate(vector: volume.Endpoint)), reach: reach))
            ) {
                return false;
            }
        }

        return true;

        static bool Within(in WorldFaceFrame frame, FixedVector3 point, FixedQ4816 reach) {
            var offset = (point - frame.Origin);

            return (
                ((FixedQ4816.Abs(value: FixedVector3.Dot(left: offset, right: frame.Right)) + reach) <= frame.HalfWidth) &&
                ((FixedQ4816.Abs(value: FixedVector3.Dot(left: offset, right: frame.Up)) + reach) <= frame.HalfHeight)
            );
        }
    }
    // The opening the body stands in, if any: its foot point within the opening's band and its volumes within the
    // opening's rectangle (Fits). The band is the body's own, on either side of the glass: as far as its collider reaches
    // along the face's normal, plus its travel along the normal this step (from `previous`, the step's start) and the
    // contact skin, and never thinner than the face's slab. So a body clear of a door is pushed as anywhere else, however
    // fast another body can go. Of several, the one whose glass is nearest the foot point, the earlier in document order
    // on a tie: two doors back to back across a wall each hold a body standing at the other, and the door it stands at is
    // the nearer.
    private bool TryOpening(in FixedVector3 position, in FixedVector3 previous, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, out WorldFaceAperture.Box opening) {
        var found = false;
        var nearest = FixedQ4816.Zero;

        opening = null!;

        foreach (var (placementId, face, candidate) in m_openings) {
            if ((m_closures is { Any: true } closures) && closures.IsClosed(placementId: placementId, face: face)) { continue; }

            var frame = candidate.Frame;
            var offset = (position - frame.Origin);
            var depth = FixedQ4816.Abs(value: FixedVector3.Dot(
                left: offset,
                right: frame.Normal
            ));

            if (
                (FixedQ4816.Abs(value: FixedVector3.Dot(left: offset, right: frame.Right)) <= frame.HalfWidth) &&
                (FixedQ4816.Abs(value: FixedVector3.Dot(left: offset, right: frame.Up)) <= frame.HalfHeight) &&
                (!found || (depth < nearest)) &&
                ((depth <= candidate.Depth) || (depth <= ((ReachAlong(normal: frame.Normal, orientation: in orientation, volumes: volumes) +
                    FixedQ4816.Abs(value: FixedVector3.Dot(left: (position - previous), right: frame.Normal))) + m_skin))) &&
                Fits(
                    frame: in frame,
                    orientation: in orientation,
                    position: in position,
                    volumes: volumes
                )
            ) {
                found = true;
                nearest = depth;
                opening = candidate;
            }
        }

        return found;
    }
    // Takes back what the solve pushed out of the opening's door: the position's outward displacement and the
    // velocity's outward change along the face normal, and a grounding on a surface that faces out of the door.
    private ContactResolution Open(in FixedVector3 previousPosition, in FixedVector3 steppedPosition, in FixedVector3 steppedVelocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, ref FixedVector3 position, ref FixedVector3 velocity, ContactResolution resolution) {
        if (
            (resolution.Refusal != ContactRefusal.None) ||
            !TryOpening(
            opening: out var opening,
            orientation: in orientation,
            position: in steppedPosition,
            previous: in previousPosition,
            volumes: volumes
        )
        ) {
            return resolution;
        }

        var normal = opening.Frame.Normal;
        var pushed = FixedVector3.Dot(
            left: (position - steppedPosition),
            right: normal
        );
        if (pushed > FixedQ4816.Zero) {
            position -= (normal * pushed);
        }
        velocity = velocity.LimitAlong(maximum: FixedVector3.Dot(left: steppedVelocity, right: normal), unitAxis: normal);

        // The push out of the door was taken back, so a wall behind the glass that made it obstructs nothing: the
        // obstruction it reported goes with it, as a ground facing out of the door no longer grounds.
        if (FixedVector3.Dot(left: resolution.ObstructionNormal, right: normal) > FixedQ4816.Zero) {
            resolution = (resolution with { ObstructionNormal = FixedVector3.Zero });
        }

        return ((resolution.Grounded && (FixedVector3.Dot(left: resolution.GroundNormal, right: normal) > FixedQ4816.Zero))
            ? (resolution with {
                Grounded = false,
                GroundNormal = FixedVector3.Zero,
            })
            : resolution
        );
    }

    /// <inheritdoc/>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
        var steppedPosition = position;
        var steppedVelocity = velocity;

        // A resolve in place has no step behind it, so the body's band is its collider's reach alone.
        return Open(
            orientation: in orientation,
            volumes: volumes,
            position: ref position,
            previousPosition: in steppedPosition,
            resolution: m_inner.Resolve(
                orientation: in orientation,
                position: ref position,
                up: in up,
                velocity: ref velocity,
                volumes: volumes
            ),
            steppedPosition: in steppedPosition,
            steppedVelocity: in steppedVelocity,
            velocity: ref velocity
        );
    }
    /// <inheritdoc/>
    public ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
        var steppedPosition = position;
        var steppedVelocity = velocity;

        return Open(
            orientation: in orientation,
            volumes: volumes,
            position: ref position,
            previousPosition: in previousPosition,
            resolution: m_inner.ResolveSweep(
                orientation: in orientation,
                position: ref position,
                previousPosition: in previousPosition,
                up: in up,
                velocity: ref velocity,
                volumes: volumes
            ),
            steppedPosition: in steppedPosition,
            steppedVelocity: in steppedVelocity,
            velocity: ref velocity
        );
    }
    /// <inheritdoc/>
    /// <remarks>A body standing in an opening declines, so the caller's sweep takes back what the wall behind the glass
    /// pushed; anywhere else the inner field answers.</remarks>
    public int CollectContacts(in FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume, FixedQ4816 margin,
        Span<FixedSurfaceContact> destination) =>
        (TryOpening(opening: out _, orientation: in orientation, position: in position, previous: in position, volumes: [volume])
            ? -1
            : m_inner.CollectContacts(destination: destination, margin: margin, orientation: in orientation, position: in position, volume: in volume));
    /// <inheritdoc/>
    /// <remarks>The wrapped field's own: an opening takes back a push only at a landing, so the landing sweep, which runs
    /// through this field, is what lets a body standing in a door drop through it instead of standing on the ground
    /// behind its glass.</remarks>
    public bool TrySupportDistance(in FixedVector3 position, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes,
        in FixedVector3 up, FixedQ4816 reach, out FixedQ4816 descent) =>
        m_inner.TrySupportDistance(descent: out descent, orientation: in orientation, position: in position, reach: reach, up: in up, volumes: volumes);
    /// <inheritdoc/>
    public ContactSupportWitness? SupportWitness(ReadOnlySpan<FixedSurfaceContact> contacts, in FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, in FixedVector3 up) =>
        m_inner.SupportWitness(contacts: contacts, orientation: in orientation, position: in position, up: in up, volume: in volume);
    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) => m_inner.TryUp(
        position: in position,
        up: out up
    );
    // Whether the ray from a probe's origin to the surface it found passes through an open door's glass from the front
    // (the door's own crossing test, WorldFaceRegion.Sweep): the surface then stands behind the door, where a body that
    // reached it would already have crossed, so it is no surface of this world to hold or anchor to.
    private bool BehindOpenGlass(in FixedVector3 origin, in FixedVector3 point) {
        foreach (var (placementId, face, opening) in m_openings) {
            if ((m_closures is { Any: true } closures) && closures.IsClosed(placementId: placementId, face: face)) { continue; }
            if (WorldFaceRegion.Sweep(aperture: opening, from: origin, to: point).Crossed) { return true; }
        }

        return false;
    }

    /// <inheritdoc/>
    /// <remarks>A surface the ray reaches through an open door's glass is not answered: the wall a door hangs on
    /// stands behind its glass, and a body walking into the door would otherwise take hold of it there and climb it
    /// instead of walking through.</remarks>
    public bool TryHoldableSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, out FixedSurfaceAttachCandidate candidate, out bool grantedByOverride) {
        if (
            m_inner.TryHoldableSurfaceAlongDirection(
                candidate: out candidate,
                direction: in direction,
                grantedByOverride: out grantedByOverride,
                maxDistance: maxDistance,
                origin: in origin
            ) &&
            !BehindOpenGlass(origin: in origin, point: candidate.Point)
        ) {
            return true;
        }

        candidate = default;
        grantedByOverride = false;

        return false;
    }
    /// <inheritdoc/>
    /// <remarks>The holdable answers also read which doors stand open (<see cref="BehindOpenGlass"/>), so the stamp
    /// moves with the closures as well as with the wrapped field's colliders. Both counts only grow, so their sum
    /// stands still exactly while neither moves.</remarks>
    public bool TryHoldableStamp(out ContactHoldableStamp stamp) {
        if (!m_inner.TryHoldableStamp(stamp: out stamp)) {
            return false;
        }
        if (m_closures is { } closures) {
            stamp = (stamp with { Revision = unchecked(stamp.Revision + (ulong)closures.Revision) });
        }

        return true;
    }
    /// <inheritdoc/>
    /// <remarks>An anchor the aim reaches through an open door's glass is not answered, as a holdable surface there is
    /// not (<see cref="TryHoldableSurfaceAlongDirection"/>).</remarks>
    public bool TryNearestSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 assistHalfAngle, out FixedSurfaceAttachCandidate candidate) {
        if (
            m_inner.TryNearestSurfaceAlongDirection(
                assistHalfAngle: assistHalfAngle,
                candidate: out candidate,
                direction: in direction,
                maxDistance: maxDistance,
                origin: in origin
            ) &&
            !BehindOpenGlass(origin: in origin, point: candidate.Point)
        ) {
            return true;
        }

        candidate = default;

        return false;
    }
}
