using System.Numerics;

namespace Puck.SdfVm.Views;

/// <summary>
/// Poses a camera against a live <see cref="SdfAnchor"/> — the engine-side half of a placeable camera, generalizing
/// what every demo camera consumer already computes by hand (the debug orbit, the creator workpiece camera, the
/// overworld's chase framing, a placed <c>CameraEye</c>'s own anchor math) into ONE small vocabulary of REUSABLE
/// shapes. A rig owns its own authoring state (an orbit's yaw/pitch/distance, a follow's offset) and projects that
/// state against whatever pose the anchor resolves to this frame — the anchor supplies WHERE the rig's subject
/// currently is; the rig supplies HOW a camera looks at it. See <see cref="OrbitRig"/>/<see cref="FollowRig"/>/
/// <see cref="FirstPersonRig"/>/<see cref="FixedRig"/>/<see cref="DollyRig"/> for the concrete shapes — each one's
/// doc comment says which demo consumer it was extracted from (or, for the two with none yet, says so explicitly).
/// </summary>
public interface ISdfCameraRig {
    /// <summary>Resolves this rig's concrete eye/target/field-of-view for the current frame.</summary>
    /// <param name="anchor">The live pose the rig's subject resolved to this frame (see <see cref="SdfAnchor"/>) —
    /// a <see cref="FixedRig"/> ignores it entirely; every other rig shape is a function of it.</param>
    /// <param name="time">The presentation clock (seconds) — only <see cref="DollyRig"/> consumes it today; every
    /// other rig's motion comes from its OWN authoring state (a stick-driven yaw, a host-updated offset), not from
    /// wall/sim time.</param>
    /// <returns>The eye position, the look-at target, and the vertical field of view (radians) to render this frame
    /// with.</returns>
    (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, float time);
}

/// <summary>
/// Orbits a target at a yaw/pitch/distance — the shape EVERY object-intent camera in this codebase already computes
/// by hand: <c>Puck.Demo.World.CameraEye.Resolve</c>'s forward vector, <c>Puck.Demo.Overworld.ScreenLayoutDirector</c>'s
/// creator-workpiece and scenario-shot framing (both inline the identical formula today), and
/// <see cref="Debug.SdfDebugController"/>'s own orbit camera (which now HOLDS one of these internally instead of
/// carrying its own bare yaw/pitch/distance/target fields — see that type). <see cref="Offset"/> is the shared pure
/// function all of them reduce to; a caller that only wants the vector (not a full anchor-driven <see cref="Resolve(float)"/>)
/// can call it directly instead of duplicating the trig.
/// </summary>
public sealed class OrbitRig : ISdfCameraRig {
    /// <summary>The rig's default vertical field of view — the same ~55° every diegetic lens in this codebase
    /// defaults to (<c>Puck.Demo.World.CameraEye.DefaultFieldOfViewRadians</c>), duplicated here rather than
    /// referenced because <c>Puck.SdfVm</c> cannot depend on <c>Puck.Demo</c>.</summary>
    public const float DefaultFieldOfViewRadians = (55f * (MathF.PI / 180f));

    /// <summary>The orbit heading, radians (0 = looking down +Z). Mutable — a controller (a pad, a scripted pose)
    /// drives this directly frame to frame, exactly like <see cref="Debug.SdfDebugController"/>'s stick-driven
    /// orbit.</summary>
    public float Yaw { get; set; }

    /// <summary>The orbit tilt, radians (positive looks up). The caller owns any envelope clamp (this type applies
    /// none) — <see cref="Debug.SdfDebugController"/> clamps to its own ±1.35 rad envelope before assigning here.</summary>
    public float Pitch { get; set; }

    /// <summary>The orbit distance, world units. The caller owns any envelope clamp, same as <see cref="Pitch"/>.</summary>
    public float Distance { get; set; } = 4f;

    /// <summary>The rig's OWN orbit pivot, world space — for a caller that orbits a host-controlled point (panned by
    /// a stick, like <see cref="Debug.SdfDebugController"/>'s right-stick pan) rather than a live external anchor.
    /// Read by the parameterless <see cref="Resolve(float)"/> convenience overload; the interface's
    /// <see cref="Resolve(in SdfAnchor, float)"/> uses <see cref="SdfAnchor.Position"/> instead (for orbiting
    /// something that moves on its own, like a companion) and never reads this property.</summary>
    public Vector3 Target { get; set; }

    /// <summary>When set, the resolved eye locks HEAD-ON (<c>+Z</c> at zero pitch, ignoring <see cref="Yaw"/>/
    /// <see cref="Pitch"/> for this resolve while leaving them stored for when this flips back off) — the "sprite
    /// intent" framing <c>ScreenLayoutDirector</c>'s creator-workpiece/scenario-shot code locks to so the authored
    /// silhouette is exactly what a bake rasterizes. Equivalent to (and replaces the need for) a separate boolean
    /// parameter threaded through every orbit consumer.</summary>
    public bool HeadOn { get; set; }

    /// <summary>The field of view this rig resolves at.</summary>
    public float FovRadians { get; set; } = DefaultFieldOfViewRadians;

    /// <summary>The pure orbit math every object-intent camera in this codebase shares: the forward vector at
    /// <paramref name="yaw"/>/<paramref name="pitch"/>, scaled to <paramref name="distance"/> — added to a target
    /// position, this is the eye; negated and added to an eye, the near-side point a subject-relative probe (like
    /// <see cref="Debug.SdfDebugController"/>'s pad-carve center) wants.</summary>
    /// <param name="yaw">The heading, radians (0 = +Z).</param>
    /// <param name="pitch">The tilt, radians (positive = up).</param>
    /// <param name="distance">The scale to apply to the resulting direction.</param>
    /// <returns>The offset vector from the orbit target to the orbit eye.</returns>
    public static Vector3 Offset(float yaw, float pitch, float distance) {
        var cosPitch = MathF.Cos(x: pitch);
        var forward = new Vector3(
            x: (MathF.Sin(x: yaw) * cosPitch),
            y: MathF.Sin(x: pitch),
            z: (MathF.Cos(x: yaw) * cosPitch)
        );

        return (forward * distance);
    }

    /// <inheritdoc/>
    public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, float time) {
        var target = anchor.Position;
        var offset = (HeadOn ? new Vector3(x: 0f, y: 0f, z: Distance) : Offset(yaw: Yaw, pitch: Pitch, distance: Distance));

        return ((target + offset), target, FovRadians);
    }

    /// <summary>Resolves against this rig's OWN <see cref="Target"/> rather than an externally-supplied anchor — the
    /// convenience overload a self-contained/pad-panned orbit (like <see cref="Debug.SdfDebugController"/>'s) uses.</summary>
    /// <param name="time">The presentation clock (unused — see the interface member's remarks).</param>
    public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(float time) =>
        Resolve(anchor: new SdfAnchor(Position: Target, Orientation: Quaternion.Identity), time: time);
}

/// <summary>
/// Chases the anchor with a fixed (host-updated) offset — the shape of <c>Puck.Demo.Overworld.ScreenLayoutDirector</c>'s
/// STANDARD/IMMERSED chase framing (<c>eye = centroid + (0, 6.5 + spread, 11 + spread * 1.5); target = centroid +
/// TargetLift</c>): the eye and target are both a constant offset from a moving subject, where "constant" is
/// per-frame HOST state (the director recomputes the offset from the current player spread every frame, then holds it
/// steady for this rig to apply) rather than something the rig itself derives. A generalization of a
/// <see cref="Puck.SdfVm.SdfAnchorKind.Instance"/>-anchored <c>CameraEye</c> too — riding a moving stamp with a fixed
/// local offset is the same shape as riding a moving player centroid.
/// </summary>
public sealed class FollowRig : ISdfCameraRig {
    /// <summary>The rig's default vertical field of view (see <see cref="OrbitRig.DefaultFieldOfViewRadians"/>).</summary>
    public const float DefaultFieldOfViewRadians = OrbitRig.DefaultFieldOfViewRadians;

    /// <summary>The eye's offset from the anchor position, world-space axes (not anchor-relative — a follow camera's
    /// "up and back" reads the same regardless of which way the subject faces, unlike an anchor-relative rig).</summary>
    public Vector3 EyeOffset { get; set; } = new(x: 0f, y: 6.5f, z: 11f);

    /// <summary>The look-at target's offset from the anchor position.</summary>
    public Vector3 TargetOffset { get; set; }

    /// <summary>The field of view this rig resolves at.</summary>
    public float FovRadians { get; set; } = DefaultFieldOfViewRadians;

    /// <inheritdoc/>
    public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, float time) =>
        ((anchor.Position + EyeOffset), (anchor.Position + TargetOffset), FovRadians);
}

/// <summary>
/// Chases the anchor with a fixed offset expressed in the ANCHOR'S OWN FRAME — <see cref="FollowRig"/>'s sibling for
/// a subject whose orientation actually matters. <see cref="FollowRig"/>'s offset stays in world axes deliberately
/// ("a follow camera's up and back reads the same regardless of which way the subject faces"), which is correct for
/// a Y-up biped but wrong when the subject's own "up" can point anywhere — for example,
/// <c>Puck.Demo.Overworld.FieldWalkerBody</c> on a planetoid: on the far side the walker's up is
/// the world's down, so a world-axis chase offset would frame the camera from beneath the walker's feet instead of
/// over its shoulder. This rig rotates BOTH offsets by <see cref="SdfAnchor.Orientation"/> before adding them, so
/// "up and back" tracks the SUBJECT's up, not the world's.
/// <para>
/// <b>Subsumes <see cref="FirstPersonRig"/> at zero pullback.</b> Set <see cref="EyeOffset"/> to a head-height lift
/// with no depth component (e.g. <c>(0, 1.6, 0)</c>) and <see cref="TargetOffset"/> to the same lift plus a small
/// step along the anchor's local forward (<c>-Z</c>) axis, and this rig resolves identically to
/// <see cref="FirstPersonRig"/>'s eye-at-the-anchor, look-along-facing shape — that type's own "offset rotated into
/// the anchor's own frame" contract is exactly this rig's general case with the pullback (the offset's Z/depth
/// component) at zero. <see cref="FirstPersonRig"/> is kept as its own type rather than folded away here (its
/// <c>FocusDistance</c>-as-a-forward-step framing reads more directly for that one shape); a caller free to choose
/// either should prefer THIS rig once it needs any pullback at all.
/// </para>
/// </summary>
public sealed class OrientedFollowRig : ISdfCameraRig {
    /// <summary>The rig's default vertical field of view (see <see cref="OrbitRig.DefaultFieldOfViewRadians"/>).</summary>
    public const float DefaultFieldOfViewRadians = OrbitRig.DefaultFieldOfViewRadians;

    /// <summary>The eye's offset from the anchor position, in the ANCHOR's own local axes (rotated by
    /// <see cref="SdfAnchor.Orientation"/> before adding — like <see cref="FirstPersonRig.EyeOffset"/>, unlike
    /// <see cref="FollowRig.EyeOffset"/>). The default lifts and pulls back along the anchor's local <c>+Z</c> (its
    /// "behind"), the over-the-shoulder chase shape.</summary>
    public Vector3 EyeOffset { get; set; } = new(x: 0f, y: 2.2f, z: 5f);

    /// <summary>The look-at target's offset from the anchor position, likewise in the anchor's own local axes.</summary>
    public Vector3 TargetOffset { get; set; } = new(x: 0f, y: 1f, z: 0f);

    /// <summary>The field of view this rig resolves at.</summary>
    public float FovRadians { get; set; } = DefaultFieldOfViewRadians;

    /// <inheritdoc/>
    public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, float time) {
        var eye = (anchor.Position + Vector3.Transform(value: EyeOffset, rotation: anchor.Orientation));
        var target = (anchor.Position + Vector3.Transform(value: TargetOffset, rotation: anchor.Orientation));

        return (eye, target, FovRadians);
    }
}

/// <summary>
/// Sits AT the anchor and looks along its facing — the shape a first-person view needs: the eye rides the anchor's
/// own orientation (an avatar's head height and forward direction), not a fixed world-space offset like
/// <see cref="FollowRig"/>. The eye offset and focus direction are expressed in anchor-local coordinates, including
/// the anchor's full orientation. <see cref="OrientedFollowRig"/> provides the equivalent zero-pullback framing with
/// independently configurable eye and target offsets.
/// </summary>
public sealed class FirstPersonRig : ISdfCameraRig {
    /// <summary>The rig's default vertical field of view (see <see cref="OrbitRig.DefaultFieldOfViewRadians"/>).</summary>
    public const float DefaultFieldOfViewRadians = OrbitRig.DefaultFieldOfViewRadians;

    /// <summary>The eye's offset from the anchor position, in the ANCHOR's own local axes (rotated by
    /// <see cref="SdfAnchor.Orientation"/> before adding — unlike <see cref="FollowRig.EyeOffset"/>, which stays in
    /// world axes). The default lifts to a roughly human eye height above the anchor's own origin.</summary>
    public Vector3 EyeOffset { get; set; } = new(x: 0f, y: 1.6f, z: 0f);

    /// <summary>How far ahead (along the anchor's local <c>-Z</c>, its forward axis) the look-at target sits. Only
    /// the direction matters to a look-at camera; this keeps the target a finite, well-conditioned distance out.</summary>
    public float FocusDistance { get; set; } = 1f;

    /// <summary>The field of view this rig resolves at.</summary>
    public float FovRadians { get; set; } = DefaultFieldOfViewRadians;

    /// <inheritdoc/>
    public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, float time) {
        var eye = (anchor.Position + Vector3.Transform(value: EyeOffset, rotation: anchor.Orientation));
        var forward = Vector3.Transform(value: -Vector3.UnitZ, rotation: anchor.Orientation);
        var distance = MathF.Max(x: FocusDistance, y: 0.01f);

        return (eye, (eye + (forward * distance)), FovRadians);
    }
}

/// <summary>
/// A camera posed directly in world space, unaffected by anything else — the shape of a
/// <see cref="Puck.SdfVm.SdfAnchorKind.World"/>-anchored <c>Puck.Demo.World.CameraEye</c> ("no anchor: the eye poses
/// directly in world space via its own stored fields, unchanging until an authoring verb moves it") generalized to
/// the rig vocabulary. <see cref="Resolve"/> ignores its <see cref="SdfAnchor"/> parameter entirely — this rig's own
/// <see cref="Eye"/>/<see cref="Target"/> ARE the pose; a caller with no live anchor to ride (a fixed security-camera
/// eye, the studio backdrop's static establishing shot) uses this rather than inventing a degenerate always-World
/// anchor just to satisfy the interface.
/// </summary>
public sealed class FixedRig : ISdfCameraRig {
    /// <summary>The rig's default vertical field of view (see <see cref="OrbitRig.DefaultFieldOfViewRadians"/>).</summary>
    public const float DefaultFieldOfViewRadians = OrbitRig.DefaultFieldOfViewRadians;

    /// <summary>The fixed eye position, world space.</summary>
    public Vector3 Eye { get; set; }

    /// <summary>The fixed look-at target, world space.</summary>
    public Vector3 Target { get; set; }

    /// <summary>The field of view this rig resolves at.</summary>
    public float FovRadians { get; set; } = DefaultFieldOfViewRadians;

    /// <inheritdoc/>
    public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, float time) =>
        (Eye, Target, FovRadians);
}

/// <summary>
/// Sweeps the eye between two points over time while looking at the anchor. The motion is a simple back-and-forth
/// track suitable for deterministic scripted shots.
/// </summary>
public sealed class DollyRig : ISdfCameraRig {
    /// <summary>The rig's default vertical field of view (see <see cref="OrbitRig.DefaultFieldOfViewRadians"/>).</summary>
    public const float DefaultFieldOfViewRadians = OrbitRig.DefaultFieldOfViewRadians;

    /// <summary>The eye's position at time 0 (and every even multiple of
    /// <see cref="DurationSeconds"/> when <see cref="PingPong"/> is set).</summary>
    public Vector3 Start { get; set; }

    /// <summary>The eye's position at one half-sweep in.</summary>
    public Vector3 End { get; set; }

    /// <summary>How long one <see cref="Start"/>-to-<see cref="End"/> sweep takes, seconds. Clamped to a small
    /// positive floor internally so a caller can never divide by zero by setting this to 0.</summary>
    public float DurationSeconds { get; set; } = 4f;

    /// <summary>When set (the default), the sweep reverses at each end (a back-and-forth dolly) rather than
    /// snapping back to <see cref="Start"/> and repeating (a one-way loop).</summary>
    public bool PingPong { get; set; } = true;

    /// <summary>The field of view this rig resolves at.</summary>
    public float FovRadians { get; set; } = DefaultFieldOfViewRadians;

    /// <inheritdoc/>
    public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, float time) {
        var duration = MathF.Max(x: DurationSeconds, y: 0.01f);
        var elapsed = MathF.Max(x: time, y: 0f);
        var phase = ((elapsed / duration) % (PingPong ? 2f : 1f));
        var u = ((PingPong && (phase > 1f)) ? (2f - phase) : phase);
        var eye = Vector3.Lerp(value1: Start, value2: End, amount: u);

        return (eye, anchor.Position, FovRadians);
    }
}
