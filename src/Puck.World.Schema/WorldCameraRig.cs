using Puck.Assets.Documents;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>Composes a camera's local motion, framing policy, and lens as independent presentation axes.</summary>
/// <param name="Motion">The camera-local eye motion evaluated after its reference frame resolves.</param>
/// <param name="Aim">The framing policy that selects a target from the resolved eye and reference frame.</param>
/// <param name="Lens">The optical state applied to the rendered camera.</param>
/// <param name="SmoothRate">The presentation-only exponential response rate (per second) the resolved eye eases at;
/// zero disables smoothing. Applies to whichever motion the rig carries.</param>
public sealed record WorldCameraRig(WorldCameraMotion Motion, WorldCameraAim Aim, WorldCameraLens Lens, float SmoothRate = 0f);
/// <summary>Defines presentation-only eye motion relative to a camera's resolved reference frame.</summary>
[JsonDerivedType(typeof(WorldCameraMotion.Fly), typeDiscriminator: "fly")]
[JsonDerivedType(typeof(WorldCameraMotion.Follow), typeDiscriminator: "follow")]
[JsonDerivedType(typeof(WorldCameraMotion.Orbit), typeDiscriminator: "orbit")]
[JsonDerivedType(typeof(WorldCameraMotion.Static), typeDiscriminator: "static")]
[JsonDerivedType(typeof(WorldCameraMotion.Track), typeDiscriminator: "track")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldCameraMotion {
    /// <summary>A free camera driven directly by a seat's channel-role input rather than a reference frame — the
    /// live eye/yaw/pitch are presentation-carried state (never authored per instance), advanced each frame from the
    /// SAME move/look samples a body seat's channels carry. Never resolved through
    /// <c>Puck.World.WorldCameraRigCompiler</c> (it has no reference frame to resolve against); the seat's own
    /// fly-rig integrator (<c>Client.WorldSeatFlyRig</c>) reads only these tunables.</summary>
    /// <param name="MinSpeed">The slowest operator-settable fly speed, world units per second.</param>
    /// <param name="MaxSpeed">The fastest operator-settable fly speed, world units per second.</param>
    /// <param name="DefaultSpeed">The fly speed a seat starts at on entry, clamped to
    /// [<paramref name="MinSpeed"/>, <paramref name="MaxSpeed"/>].</param>
    /// <param name="LookRateRadiansPerSecond">The look input's angular rate at full deflection.</param>
    /// <param name="MaxPitchRadians">The pitch clamp, symmetric about level.</param>
    public sealed record Fly(float MinSpeed, float MaxSpeed, float DefaultSpeed, float LookRateRadiansPerSecond, float MaxPitchRadians) : WorldCameraMotion;
    /// <summary>Follows the reference frame at a fixed offset.</summary>
    /// <param name="Offset">The eye offset in world or reference-local axes.</param>
    /// <param name="WorldAxes">Whether <paramref name="Offset"/> uses world axes.</param>
    /// <param name="SpreadPullback">The group-spread multiplier applied to the offset.</param>
    public sealed record Follow(DocumentVector3 Offset, bool WorldAxes, float SpreadPullback) : WorldCameraMotion;
    /// <summary>Orbits a reference-frame pivot.</summary>
    /// <param name="Distance">The orbit distance.</param>
    /// <param name="Yaw">The orbit heading in radians.</param>
    /// <param name="Pitch">The orbit tilt in radians.</param>
    /// <param name="PivotOffset">The world-axis offset from the reference-frame origin to the pivot.</param>
    public sealed record Orbit(float Distance, float Yaw, float Pitch, DocumentVector3 PivotOffset) : WorldCameraMotion;
    /// <summary>Holds an eye position in world or reference-local axes.</summary>
    /// <param name="Position">The eye position or offset.</param>
    /// <param name="WorldAxes">Whether <paramref name="Position"/> is an absolute world position.</param>
    public sealed record Static(DocumentVector3 Position, bool WorldAxes) : WorldCameraMotion;
    /// <summary>Evaluates a durable camera track through separate playback state.</summary>
    /// <param name="Definition">The durable keyframes, interpolation, and clock domain.</param>
    /// <param name="Playback">The playback start and loop policy.</param>
    /// <param name="WorldAxes">Whether evaluated positions are absolute world positions.</param>
    public sealed record Track(WorldCameraTrack Definition, WorldCameraTrackPlayback Playback, bool WorldAxes) : WorldCameraMotion;
}
/// <summary>Defines how a camera frames from its resolved eye.</summary>
[JsonDerivedType(typeof(WorldCameraAim.Anchor), typeDiscriminator: "anchor")]
[JsonDerivedType(typeof(WorldCameraAim.Forward), typeDiscriminator: "forward")]
[JsonDerivedType(typeof(WorldCameraAim.WorldPoint), typeDiscriminator: "worldPoint")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldCameraAim {
    /// <summary>Looks at an offset from the resolved reference frame.</summary>
    /// <param name="Offset">The target offset.</param>
    /// <param name="WorldAxes">Whether <paramref name="Offset"/> uses world axes.</param>
    public sealed record Anchor(DocumentVector3 Offset, bool WorldAxes) : WorldCameraAim;
    /// <summary>Looks along the reference frame's forward axis.</summary>
    /// <param name="FocusDistance">The finite target distance along forward.</param>
    public sealed record Forward(float FocusDistance) : WorldCameraAim;
    /// <summary>Looks at a fixed world-space point.</summary>
    /// <param name="Target">The world-space target.</param>
    public sealed record WorldPoint(DocumentVector3 Target) : WorldCameraAim;
}
/// <summary>Defines presentation-only camera optics.</summary>
/// <param name="FieldOfViewRadians">The vertical field of view in radians.</param>
public sealed record WorldCameraLens(float FieldOfViewRadians);
/// <summary>One camera-track keyframe at a clock-relative tick.</summary>
/// <param name="Tick">The non-negative track-relative tick.</param>
/// <param name="Position">The camera position at the tick.</param>
public sealed record WorldCameraTrackKeyframe(ulong Tick, DocumentVector3 Position);
/// <summary>A durable camera track with an explicit clock domain.</summary>
/// <param name="ClockDomain">The clock that advances the track.</param>
/// <param name="Interpolation">The interpolation applied between adjacent keyframes.</param>
/// <param name="Keyframes">The keyframes in strictly increasing tick order.</param>
public sealed record WorldCameraTrack(WorldCameraTrackClockDomain ClockDomain, WorldCameraTrackInterpolation Interpolation, IReadOnlyList<WorldCameraTrackKeyframe> Keyframes);
/// <summary>The mutable-by-replacement playback state for a camera track.</summary>
/// <param name="StartTick">The selected clock's absolute tick at which playback starts.</param>
/// <param name="LoopMode">The end-of-track playback policy.</param>
public sealed record WorldCameraTrackPlayback(ulong StartTick, WorldCameraTrackLoopMode LoopMode);
/// <summary>Identifies which presentation clock advances a camera track.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldCameraTrackClockDomain>))]
public enum WorldCameraTrackClockDomain : byte {
    /// <summary>The continuously accumulated presentation clock expressed at 240 ticks per second.</summary>
    PresentationTime,
    /// <summary>The authoritative simulation tick published to presentation.</summary>
    AuthoritativeTick,
}
/// <summary>Identifies camera-track interpolation between adjacent keyframes.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldCameraTrackInterpolation>))]
public enum WorldCameraTrackInterpolation : byte {
    /// <summary>Holds the prior keyframe's position unchanged until the next keyframe boundary.</summary>
    Step,
    /// <summary>Linearly interpolates the position between the two bracketing keyframes.</summary>
    Linear,
}
/// <summary>Identifies camera-track playback at the final keyframe.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldCameraTrackLoopMode>))]
public enum WorldCameraTrackLoopMode : byte {
    /// <summary>Clamps playback at the final keyframe once elapsed time passes the track's duration.</summary>
    Once,
    /// <summary>Wraps playback back to the first keyframe on each full duration.</summary>
    Loop,
    /// <summary>Reverses playback direction at each end of the track every full duration.</summary>
    PingPong,
}
