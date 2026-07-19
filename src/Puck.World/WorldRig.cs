using System.Numerics;
using System.Text.Json.Serialization;
using Puck.SdfVm.Views;

namespace Puck.World;

/// <summary>
/// HOW a camera frames from wherever it rides — the authored half of a placeable camera, orthogonal to the
/// <see cref="WorldAnchor"/> that says WHERE it rides. Each variant compiles to exactly one engine
/// <see cref="ISdfCameraRig"/> (see <see cref="WorldRigCompiler"/>): a camera's <em>kind</em> no longer decides both
/// what it rides and how it frames — the two are independent axes. The <c>$type</c> string is the JSON discriminator.
/// </summary>
/// <param name="FieldOfViewRadians">The vertical field of view, radians — the rig's honest home (every engine rig owns a
/// <c>DefaultFieldOfViewRadians</c>). Validated finite and in (0, pi).</param>
[JsonDerivedType(typeof(WorldRig.Chase), typeDiscriminator: "chase")]
[JsonDerivedType(typeof(WorldRig.FirstPerson), typeDiscriminator: "firstPerson")]
[JsonDerivedType(typeof(WorldRig.Orbit), typeDiscriminator: "orbit")]
[JsonDerivedType(typeof(WorldRig.LookAt), typeDiscriminator: "lookAt")]
[JsonDerivedType(typeof(WorldRig.Dolly), typeDiscriminator: "dolly")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal abstract record WorldRig(float FieldOfViewRadians) {
    /// <summary>Chases the subject with a fixed eye/target offset — compiles to <see cref="OrientedFollowRig"/> when
    /// <paramref name="WorldAxes"/> is <see langword="false"/> (offsets in the anchor's own frame — the over-the-shoulder
    /// seat framing) or <see cref="FollowRig"/> when <see langword="true"/> (offsets in world axes — a group establishing
    /// shot). The two engine rigs are one authored row distinguished by a bool.</summary>
    /// <param name="EyeOffset">The eye's offset from the resolved pose.</param>
    /// <param name="TargetOffset">The look-at target's offset from the resolved pose.</param>
    /// <param name="WorldAxes">Whether the offsets are world axes (<see cref="FollowRig"/>) or the anchor's own frame
    /// (<see cref="OrientedFollowRig"/>).</param>
    /// <param name="SpreadPullback">Scales <paramref name="EyeOffset"/> by <c>(1 + SpreadPullback * spread)</c>, where
    /// <c>spread</c> is published by a <see cref="WorldAnchor.Group"/> anchor and is <c>0</c> for every other anchor kind —
    /// inert unless the camera rides a group. The establishing shot's spread-adaptive framing, as one scalar.</param>
    /// <param name="FieldOfViewRadians">The vertical field of view, radians.</param>
    internal sealed record Chase(Vector3 EyeOffset, Vector3 TargetOffset, bool WorldAxes, float SpreadPullback, float FieldOfViewRadians) : WorldRig(FieldOfViewRadians);

    /// <summary>Sits at the resolved pose and looks along its facing — compiles to <see cref="FirstPersonRig"/>.</summary>
    /// <param name="EyeOffset">The eye's offset from the resolved pose, in the anchor's own frame.</param>
    /// <param name="FocusDistance">How far ahead the look-at target sits (only the direction matters).</param>
    /// <param name="FieldOfViewRadians">The vertical field of view, radians.</param>
    internal sealed record FirstPerson(Vector3 EyeOffset, float FocusDistance, float FieldOfViewRadians) : WorldRig(FieldOfViewRadians);

    /// <summary>Orbits the resolved pose at a yaw/pitch/distance — compiles to <see cref="OrbitRig"/>.</summary>
    /// <param name="Distance">The orbit distance, world units.</param>
    /// <param name="Yaw">The orbit heading, radians (0 = looking down +Z).</param>
    /// <param name="Pitch">The orbit tilt, radians (positive looks up).</param>
    /// <param name="PivotLift">A lift added to the resolved pivot before orbiting (frames a subject's chest, not its feet).</param>
    /// <param name="FieldOfViewRadians">The vertical field of view, radians.</param>
    internal sealed record Orbit(float Distance, float Yaw, float Pitch, Vector3 PivotLift, float FieldOfViewRadians) : WorldRig(FieldOfViewRadians);

    /// <summary>A camera posed directly in world space looking at a fixed point — compiles to <see cref="FixedRig"/>.
    /// The eye is the camera's own <see cref="WorldCamera.Offset"/> (an unanchored camera's offset IS its world
    /// position).</summary>
    /// <param name="Target">The fixed look-at target, world space.</param>
    /// <param name="FieldOfViewRadians">The vertical field of view, radians.</param>
    internal sealed record LookAt(Vector3 Target, float FieldOfViewRadians) : WorldRig(FieldOfViewRadians);

    /// <summary>Sweeps the eye between two world points over time while looking at the resolved pose — compiles to
    /// <see cref="DollyRig"/>. A deterministic scripted shot driven by the presentation clock.</summary>
    /// <param name="Start">The eye's position at each sweep start.</param>
    /// <param name="End">The eye's position at one half-sweep in.</param>
    /// <param name="DurationSeconds">How long one start-to-end sweep takes, seconds (validated positive).</param>
    /// <param name="PingPong">Whether the sweep reverses at each end (a back-and-forth dolly) rather than looping.</param>
    /// <param name="FieldOfViewRadians">The vertical field of view, radians.</param>
    internal sealed record Dolly(Vector3 Start, Vector3 End, float DurationSeconds, bool PingPong, float FieldOfViewRadians) : WorldRig(FieldOfViewRadians);
}
