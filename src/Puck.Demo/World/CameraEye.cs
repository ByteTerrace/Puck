using System.Numerics;

namespace Puck.Demo.World;

/// <summary>What a <see cref="CameraEye"/> rides — the symmetric half of the screen tower's placement model, but for a
/// VIEWPOINT rather than a surface. An eye is either free-standing (a posed marker fixed in world space) or ANCHORED to
/// something that moves, so it rides that thing's live transform frame to frame (a camera clipped to a walking avatar's
/// head, a lens dangling off a creature's body, a security cam bolted to a placed lamp post). The anchor is pure data:
/// a stable id into a resolver the host supplies at render time — no primitive knows what a "creature" is.</summary>
public enum CameraAnchorKind {
    /// <summary>No anchor: the eye's <see cref="CameraEye.Position"/>/<see cref="CameraEye.Yaw"/> pose it directly in
    /// world space, unchanging until an authoring verb moves it.</summary>
    World,
    /// <summary>Anchored to a CREATION shape (a <c>puck.creation.v1</c> shape id): the eye rides that shape's live
    /// pose so it follows IK/animation frames. Its stored pose is then the OFFSET from the shape's frame.</summary>
    Shape,
    /// <summary>Anchored to a WORLD placement (a stamped assembly's id): the eye rides that stamp's transform so a
    /// camera on a placed prop moves when the prop is dragged. Its stored pose is the offset from the stamp's frame.</summary>
    Placement,
}

/// <summary>
/// A placeable camera EYE — a posed viewpoint the developer drops into the world exactly like a screen surface, indexed
/// and wired as pure data. Not a shape that renders: a marker that, resolved against the live world each frame,
/// produces the eye/target pose a <see cref="Overworld.CameraFeedEngine"/> feed renders the world from. The hundredth eye costs a
/// verb, not a redesign; a camera on a lamp post, a security wall's four angles, a mirror's reflection, a creature's
/// lure — all are eyes with different anchors and poses.
/// <para>
/// The pose is stored the same way for every anchor kind: a <see cref="Position"/> and a yaw/pitch, interpreted in
/// world space when <see cref="Anchor"/> is <see cref="CameraAnchorKind.World"/>, or as an OFFSET from the anchored
/// frame otherwise. The eye looks along its facing at a point <see cref="FocusDistance"/> ahead — the caller resolves
/// the anchor frame and calls <see cref="Resolve"/> to get the concrete eye/target the feed poses from.
/// </para>
/// </summary>
/// <param name="Id">The eye's stable id (unique within its owning table; survives deletes — console selection keys on
/// it).</param>
/// <param name="Position">The eye position: world space for an unanchored eye, else the offset from the anchor frame's
/// origin (in the anchor frame's local axes when a rotation is supplied to <see cref="Resolve"/>).</param>
/// <param name="Yaw">The eye's heading, radians (0 = looking down +Z; world space or anchor-relative, matching
/// <see cref="Anchor"/>).</param>
/// <param name="Pitch">The eye's tilt, radians (positive looks up), clamped to a near-vertical envelope at resolve time
/// so the look-at never degenerates.</param>
/// <param name="FieldOfViewRadians">The vertical field of view (null = the engine default).</param>
/// <param name="FocusDistance">How far ahead the look-at target sits (null = 1 world unit; only its direction matters to
/// the look-at, but a finite distance keeps the target well-conditioned).</param>
/// <param name="Anchor">What the eye rides.</param>
/// <param name="AnchorId">The anchored shape/placement id (ignored when <see cref="Anchor"/> is
/// <see cref="CameraAnchorKind.World"/>).</param>
public readonly record struct CameraEye(
    int Id,
    Vector3 Position,
    float Yaw,
    float Pitch,
    float? FieldOfViewRadians,
    float? FocusDistance,
    CameraAnchorKind Anchor,
    int AnchorId
) {
    /// <summary>The default vertical field of view a feed renders an eye at when the eye names none — a moderate ~55°,
    /// matching the diegetic-lens feel the overworld's screens read at.</summary>
    public const float DefaultFieldOfViewRadians = (55f * (MathF.PI / 180f));

    /// <summary>The pitch envelope: an eye is clamped to ±this at resolve time so the look-at direction never collapses
    /// onto the up axis (a straight-up/down look has no well-defined roll for the view basis).</summary>
    public const float MaxPitchRadians = (85f * (MathF.PI / 180f));

    /// <summary>Resolves the eye's concrete world-space pose for the current frame. An unanchored eye poses directly
    /// from its stored fields; an anchored eye composes its stored pose (treated as an offset) onto the supplied anchor
    /// frame, so it rides whatever moved. The result is the eye position and the point it looks at — exactly the two
    /// inputs a look-at camera needs.</summary>
    /// <param name="anchorPosition">The anchored frame's world-space origin (ignored for an unanchored eye). For a
    /// <see cref="CameraAnchorKind.Shape"/>/<see cref="CameraAnchorKind.Placement"/> eye the caller supplies the live
    /// resolved position of the anchored shape/stamp.</param>
    /// <param name="anchorYaw">The anchored frame's world-space heading, radians (the offset yaw adds onto it). Zero for
    /// an unanchored eye.</param>
    /// <returns>The world-space eye position and look-at target.</returns>
    public (Vector3 Eye, Vector3 Target) Resolve(Vector3 anchorPosition, float anchorYaw) {
        var pitch = Math.Clamp(value: Pitch, max: MaxPitchRadians, min: -MaxPitchRadians);
        var totalYaw = ((Anchor == CameraAnchorKind.World) ? Yaw : (anchorYaw + Yaw));

        // The stored position is world space when unanchored; otherwise it is an offset rotated into the anchor frame's
        // heading (yaw only — the anchors this primitive rides are upright frames: a stamp's yaw, a walker's heading)
        // and added to the anchor origin, so a "0.2 above, 0.5 ahead" offset stays above-and-ahead as the anchor turns.
        var eye = ((Anchor == CameraAnchorKind.World)
            ? Position
            : (anchorPosition + RotateYaw(vector: Position, yaw: anchorYaw)));

        // Facing from yaw/pitch: yaw around +Y, pitch tilting the forward vector off the XZ plane. FocusDistance keeps
        // the target a finite, well-conditioned distance ahead (direction is all the look-at consumes).
        var cosPitch = MathF.Cos(x: pitch);
        var forward = new Vector3(
            (MathF.Sin(x: totalYaw) * cosPitch),
            MathF.Sin(x: pitch),
            (MathF.Cos(x: totalYaw) * cosPitch)
        );
        var distance = Math.Max(val1: (FocusDistance ?? 1f), val2: 0.01f);

        return (eye, (eye + (forward * distance)));
    }

    /// <summary>The eye's effective field of view (its own, or the engine default).</summary>
    public float EffectiveFieldOfViewRadians => (FieldOfViewRadians ?? DefaultFieldOfViewRadians);

    // Rotates a vector about +Y by yaw radians — the one axis the eye's upright anchors turn around.
    private static Vector3 RotateYaw(Vector3 vector, float yaw) {
        var cos = MathF.Cos(x: yaw);
        var sin = MathF.Sin(x: yaw);

        return new Vector3(
            ((vector.X * cos) + (vector.Z * sin)),
            vector.Y,
            ((vector.Z * cos) - (vector.X * sin))
        );
    }
}
