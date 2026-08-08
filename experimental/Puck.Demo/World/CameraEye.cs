using System.Numerics;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.Demo.World;

/// <summary>
/// A placeable camera EYE — a posed viewpoint the developer drops into the world exactly like a screen surface, indexed
/// and wired as pure data. Not a shape that renders: a marker that, resolved against the live world each frame,
/// produces the eye/target pose a <see cref="SdfCameraView"/> renders the world from — this type itself IS an
/// <see cref="ISdfCameraRig"/> (see <see cref="Resolve(in SdfAnchor, float)"/>). The hundredth eye costs a
/// verb, not a redesign; a camera on a lamp post, a security wall's four angles, a mirror's reflection, a creature's
/// lure — all are eyes with different anchors and poses.
/// <para>
/// The pose is stored the same way for every anchor kind: a <see cref="Position"/> and a yaw/pitch, interpreted in
/// world space when <see cref="Anchor"/> is <see cref="SdfAnchorKind.World"/>, or as an OFFSET from the anchored
/// frame otherwise. The eye looks along its facing at a point <see cref="FocusDistance"/> ahead — the caller resolves
/// the anchor frame and calls <see cref="Resolve(in SdfAnchor, float)"/> to get the concrete eye/target the feed poses from.
/// </para>
/// <para>
/// <see cref="Anchor"/> is <see cref="Puck.SdfVm.SdfAnchorKind"/>; the engine
/// vocabulary a host's anchor kinds map onto): <see cref="SdfAnchorKind.World"/> = unanchored, <see cref="SdfAnchorKind.Body"/>
/// = rides a CREATION shape's live pose (a <c>puck.creation.v1</c> shape id), <see cref="SdfAnchorKind.Instance"/> =
/// rides a WORLD placement's transform (a stamped assembly's id). <see cref="Resolve(in SdfAnchor, float)"/>'s math is the SAME shape
/// <see cref="OrbitRig"/> generalizes (see <see cref="OrbitRig.Offset"/>) — a <see cref="SdfAnchorKind.World"/> eye is
/// a <see cref="FixedRig"/>; an anchored eye is a fixed local offset composed onto a moving frame, the same shape
/// <see cref="FollowRig"/> generalizes.
/// </para>
/// </summary>
/// <param name="Id">The eye's stable id (unique within its owning table; survives deletes — console selection keys on
/// it).</param>
/// <param name="Position">The eye position: world space for an unanchored eye, else the offset from the anchor frame's
/// origin (in the anchor frame's local axes when a rotation is supplied to <see cref="Resolve(in SdfAnchor, float)"/>).</param>
/// <param name="Yaw">The eye's heading, radians (0 = looking down +Z; world space or anchor-relative, matching
/// <see cref="Anchor"/>).</param>
/// <param name="Pitch">The eye's tilt, radians (positive looks up), clamped to a near-vertical envelope at resolve time
/// so the look-at never degenerates.</param>
/// <param name="FieldOfViewRadians">The vertical field of view (null = the engine default).</param>
/// <param name="FocusDistance">How far ahead the look-at target sits (null = 1 world unit; only its direction matters to
/// the look-at, but a finite distance keeps the target well-conditioned).</param>
/// <param name="Anchor">What the eye rides.</param>
/// <param name="AnchorId">The anchored shape/placement id (ignored when <see cref="Anchor"/> is
/// <see cref="SdfAnchorKind.World"/>).</param>
public readonly record struct CameraEye(
    int Id,
    Vector3 Position,
    float Yaw,
    float Pitch,
    float? FieldOfViewRadians,
    float? FocusDistance,
    SdfAnchorKind Anchor,
    int AnchorId
) : ISdfCameraRig {
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
    /// <see cref="SdfAnchorKind.Body"/>/<see cref="SdfAnchorKind.Instance"/> eye the caller supplies the live
    /// resolved position of the anchored shape/stamp.</param>
    /// <param name="anchorYaw">The anchored frame's world-space heading, radians (the offset yaw adds onto it). Zero for
    /// an unanchored eye.</param>
    /// <returns>The world-space eye position and look-at target.</returns>
    public (Vector3 Eye, Vector3 Target) Resolve(Vector3 anchorPosition, float anchorYaw) {
        var pitch = Math.Clamp(value: Pitch, max: MaxPitchRadians, min: -MaxPitchRadians);
        var totalYaw = ((Anchor == SdfAnchorKind.World) ? Yaw : (anchorYaw + Yaw));

        // The stored position is world space when unanchored; otherwise it is an offset rotated into the anchor frame's
        // heading (yaw only — the anchors this primitive rides are upright frames: a stamp's yaw, a walker's heading)
        // and added to the anchor origin, so a "0.2 above, 0.5 ahead" offset stays above-and-ahead as the anchor turns.
        var eye = ((Anchor == SdfAnchorKind.World)
            ? Position
            : (anchorPosition + RotateYaw(vector: Position, yaw: anchorYaw)));

        // Facing from yaw/pitch: the SAME orbit-offset shape OrbitRig.Offset shares with every other object-intent
        // camera in this codebase. FocusDistance keeps the target a finite, well-conditioned distance ahead (only its
        // direction matters to the look-at).
        var distance = Math.Max(val1: (FocusDistance ?? 1f), val2: 0.01f);
        var forward = OrbitRig.Offset(yaw: totalYaw, pitch: pitch, distance: 1f);

        return (eye, (eye + (forward * distance)));
    }

    /// <summary>The eye's effective field of view (its own, or the engine default).</summary>
    public float EffectiveFieldOfViewRadians => (FieldOfViewRadians ?? DefaultFieldOfViewRadians);

    /// <summary>
    /// The <see cref="ISdfCameraRig"/> binding derives the anchor's heading (yaw about +Y)
    /// from its orientation and forwards to <see cref="Resolve(Vector3, float)"/> — the SAME math this type has
    /// always used, now reached through the shared rig vocabulary <see cref="SdfCameraView"/> consumes, so a
    /// placed diegetic camera and an engine rig (<see cref="OrbitRig"/>/<see cref="FixedRig"/>/…) speak
    /// one interface. <paramref name="time"/> is unused (this rig's motion comes from its own stored fields, not the
    /// presentation clock, matching every rig but <see cref="DollyRig"/>).</summary>
    public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, float time) {
        var anchorYaw = YawOf(orientation: anchor.Orientation);

        var (eye, target) = Resolve(anchorPosition: anchor.Position, anchorYaw: anchorYaw);

        return (eye, target, EffectiveFieldOfViewRadians);
    }

    // The heading (yaw about +Y) a quaternion orientation faces — the one axis every anchor this eye rides turns
    // around (a companion's upright frame, a placement's stamp).
    private static float YawOf(Quaternion orientation) {
        var forward = Vector3.Transform(value: Vector3.UnitZ, rotation: orientation);

        return MathF.Atan2(y: forward.X, x: forward.Z);
    }

    // Rotates a vector about +Y by yaw radians — the one axis the eye's upright anchors turn around.
    private static Vector3 RotateYaw(Vector3 vector, float yaw) {
        var cos = MathF.Cos(x: yaw);
        var sin = MathF.Sin(x: yaw);

        return new Vector3(
            x: ((vector.X * cos) + (vector.Z * sin)),
            y: vector.Y,
            z: ((vector.Z * cos) - (vector.X * sin))
        );
    }
}
