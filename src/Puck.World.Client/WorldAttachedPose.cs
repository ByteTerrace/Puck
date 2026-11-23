using System.Numerics;

namespace Puck.World.Client;

/// <summary>
/// The presented root pose of a placement row that rides a body (<see cref="WorldPlacement.Attach"/>): the body's
/// interpolated pose composed with the row's local offset, rotated into the body's frame, and its local yaw. The one
/// place every presentation reader of an attached row (the stamp pool, the rigid copies, the anchors) composes it.
/// <para>Presentation float, deliberately: it rides the client's interpolated body pose so an attached row moves as
/// smoothly as the body it sits on. The authoritative answer is the fixed-point one
/// (<c>Puck.World.Server.WorldPlacementAttachment.TryResolve</c>, what <c>world.attachments</c> echoes), composed in the
/// same order.</para>
/// </summary>
public static class WorldAttachedPose {
    /// <summary>Returns the presented root pose of a row riding a body.</summary>
    /// <param name="attach">The row's attach facet.</param>
    /// <param name="source">The world whose interpolated body poses the row rides.</param>
    /// <returns>The root's position and orientation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attach"/> or <paramref name="source"/> is
    /// <see langword="null"/>.</exception>
    public static (Vector3 Position, Quaternion Orientation) Root(WorldPlacementAttach attach, IWorldStampSource source) {
        ArgumentNullException.ThrowIfNull(argument: attach);
        ArgumentNullException.ThrowIfNull(argument: source);

        var bodyOrientation = source.Orientation(index: attach.BodyIndex);

        return (
            (source.Position(index: attach.BodyIndex) + Vector3.Transform(value: attach.LocalOffset, rotation: bodyOrientation)),
            Quaternion.Normalize(value: (bodyOrientation * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: float.DegreesToRadians(degrees: attach.LocalYawDegrees))))
        );
    }
}
