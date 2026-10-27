using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>
/// Wraps a world's contact field so a body standing in a portal's opening is not held back by geometry behind the
/// glass: a door laid on solid ground lets a body drop through it, and a door on a wall lets a body walk into it, until
/// its origin passes the glass and the door fires (<see cref="WorldFaceRegion"/>).
/// </summary>
/// <remarks>
/// <para>A body is in an opening when its foot point, before the solve, lies within the face's rectangle and within
/// one crossing depth of its plane on either side, and its collider's volumes lie within the rectangle across the
/// glass, so a body too big for a door meets the wall round it; of several openings, the one whose glass is nearest
/// the foot point. The inner field resolves the step as it would with no door; then
/// every push it made out of the door along the face's outward normal is taken back — the position keeps the depth
/// it was stepped to and the velocity keeps the speed it had into the door — and a ground contact facing out of the
/// door no longer grounds the body. Pushes across the opening, from the door's own sides, stand.</para>
/// <para>The geometry behind the glass is therefore ignored by the push it makes, not cut out of the field: geometry
/// in front of the glass within the opening pushes no differently. Every value is fixed point. It wraps the world's
/// own provider, inside any adjacency composition, so the provider it wraps answers for terrain alone.</para>
/// </remarks>
public sealed class WorldPortalHoleContactField : IContactField {
    private readonly IContactField m_inner;
    private readonly WorldFaceAperture.Box[] m_openings;

    private WorldPortalHoleContactField(IContactField inner, WorldFaceAperture.Box[] openings) {
        m_inner = inner;
        m_openings = openings;
    }

    /// <summary>Wraps <paramref name="inner"/> with the openings of every portal face <paramref name="definition"/>
    /// declares, or returns it unwrapped when the world declares none.</summary>
    /// <param name="inner">The world's own contact field.</param>
    /// <param name="definition">The world's definition.</param>
    /// <returns>The field bodies resolve against.</returns>
    public static IContactField Wrap(IContactField inner, WorldDefinition definition) {
        var openings = WorldFacePortalPolicy.PortalApertures(definition: definition);

        return ((openings.Length > 0)
            ? new WorldPortalHoleContactField(
                inner: inner,
                openings: openings
            )
            : inner
        );
    }

    // Whether every volume of the body lies within the opening's rectangle across its glass: a body too big for a door,
    // as a body at full size is for a door a quarter its size, meets the wall round it. A box volume is bounded by the sum
    // of its half-extents along any axis, whatever its rotation.
    private static bool Fits(in WorldFaceFrame frame, in FixedVector3 position, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        foreach (var volume in volumes) {
            var reach = ((volume.Kind is FixedBodyColliderKind.Box or FixedBodyColliderKind.Ellipsoid)
                ? ((FixedQ4816.Abs(value: volume.HalfExtents.X) + FixedQ4816.Abs(value: volume.HalfExtents.Y)) + FixedQ4816.Abs(value: volume.HalfExtents.Z))
                : volume.Radius
            );

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
    // opening's rectangle (Fits). Of several, the one whose glass is nearest the foot point, the earlier in document
    // order on a tie. A crossing band reaches a step of the world's fastest travel past its glass, so two doors back to
    // back across a wall each hold a body standing at the other; the door it stands at is the nearer.
    private bool TryOpening(in FixedVector3 position, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, out WorldFaceAperture.Box opening) {
        var found = false;
        var nearest = FixedQ4816.Zero;

        opening = null!;

        foreach (var candidate in m_openings) {
            var frame = candidate.Frame;
            var offset = (position - frame.Origin);
            var depth = FixedVector3.Dot(
                left: offset,
                right: frame.Normal
            );

            if (
                (FixedQ4816.Abs(value: depth) <= candidate.Depth) &&
                (FixedQ4816.Abs(value: FixedVector3.Dot(left: offset, right: frame.Right)) <= frame.HalfWidth) &&
                (FixedQ4816.Abs(value: FixedVector3.Dot(left: offset, right: frame.Up)) <= frame.HalfHeight) &&
                (!found || (FixedQ4816.Abs(value: depth) < nearest)) &&
                Fits(
                    frame: in frame,
                    orientation: in orientation,
                    position: in position,
                    volumes: volumes
                )
            ) {
                found = true;
                nearest = FixedQ4816.Abs(value: depth);
                opening = candidate;
            }
        }

        return found;
    }
    // Takes back what the solve pushed out of the opening's door: the position's outward displacement and the
    // velocity's outward change along the face normal, and a grounding on a surface that faces out of the door.
    private ContactResolution Open(in FixedVector3 steppedPosition, in FixedVector3 steppedVelocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, ref FixedVector3 position, ref FixedVector3 velocity, ContactResolution resolution) {
        if (
            (resolution.Refusal != ContactRefusal.None) ||
            !TryOpening(
            opening: out var opening,
            orientation: in orientation,
            position: in steppedPosition,
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
        var turned = (FixedVector3.Dot(left: velocity, right: normal) - FixedVector3.Dot(left: steppedVelocity, right: normal));

        if (pushed > FixedQ4816.Zero) {
            position -= (normal * pushed);
        }
        if (turned > FixedQ4816.Zero) {
            velocity -= (normal * turned);
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

        return Open(
            orientation: in orientation,
            volumes: volumes,
            position: ref position,
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
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) => m_inner.TryUp(
        position: in position,
        up: out up
    );
    /// <inheritdoc/>
    public bool TryHoldableSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, out FixedSurfaceAttachCandidate candidate, out bool grantedByOverride) => m_inner.TryHoldableSurfaceAlongDirection(
        candidate: out candidate,
        direction: in direction,
        grantedByOverride: out grantedByOverride,
        maxDistance: maxDistance,
        origin: in origin
    );
    /// <inheritdoc/>
    public bool TryNearestSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 assistHalfAngle, out FixedSurfaceAttachCandidate candidate) => m_inner.TryNearestSurfaceAlongDirection(
        assistHalfAngle: assistHalfAngle,
        candidate: out candidate,
        direction: in direction,
        maxDistance: maxDistance,
        origin: in origin
    );
}
