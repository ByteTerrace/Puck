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
    /// <summary>Returns whether the body a row rides is live this frame: the row draws and resolves a pose only then.</summary>
    /// <param name="attach">The row's attach facet.</param>
    /// <param name="source">The world whose bodies the row rides.</param>
    /// <returns><see langword="true"/> when the facet resolves an active body.</returns>
    public static bool IsRiding(WorldPlacementAttach attach, IWorldStampSource source) {
        ArgumentNullException.ThrowIfNull(argument: attach);
        ArgumentNullException.ThrowIfNull(argument: source);

        return (attach.TryCarrier(inhabitants: source, index: out var carrier) && WorldBodiesLimits.IsBodyIndex(index: carrier) && source.IsActive(index: carrier));
    }
    /// <summary>Returns the presented root pose of a row riding a body.</summary>
    /// <param name="attach">The row's attach facet.</param>
    /// <param name="source">The world whose interpolated body poses the row rides.</param>
    /// <returns>The root's position and orientation; the carrier's pose reads as the origin's while the row names a
    /// placement no body inhabits, which <see cref="IsRiding"/> tells apart.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attach"/> or <paramref name="source"/> is
    /// <see langword="null"/>.</exception>
    public static (Vector3 Position, Quaternion Orientation) Root(WorldPlacementAttach attach, IWorldStampSource source) {
        ArgumentNullException.ThrowIfNull(argument: attach);
        ArgumentNullException.ThrowIfNull(argument: source);

        _ = attach.TryCarrier(inhabitants: source, index: out var carrier);

        var bodyOrientation = source.Orientation(index: carrier);

        return (
            (source.Position(index: carrier) + Vector3.Transform(value: attach.LocalOffset, rotation: bodyOrientation)),
            Quaternion.Normalize(value: (bodyOrientation * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: float.DegreesToRadians(degrees: attach.LocalYawDegrees))))
        );
    }
}
