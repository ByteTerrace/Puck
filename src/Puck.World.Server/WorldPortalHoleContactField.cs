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
/// one crossing depth of its plane on either side. The inner field resolves the step as it would with no door; then
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

    // The opening the foot point stands in, if any; the first in document order wins.
    private bool TryOpening(in FixedVector3 position, out WorldFaceAperture.Box opening) {
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
                (FixedQ4816.Abs(value: FixedVector3.Dot(left: offset, right: frame.Up)) <= frame.HalfHeight)
            ) {
                opening = candidate;

                return true;
            }
        }

        opening = null!;

        return false;
    }
    // Takes back what the solve pushed out of the opening's door: the position's outward displacement and the
    // velocity's outward change along the face normal, and a grounding on a surface that faces out of the door.
    private ContactResolution Open(in FixedVector3 steppedPosition, in FixedVector3 steppedVelocity, ref FixedVector3 position, ref FixedVector3 velocity, ContactResolution resolution) {
        if (
            (resolution.Refusal != ContactRefusal.None) ||
            !TryOpening(
            opening: out var opening,
            position: in steppedPosition
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
