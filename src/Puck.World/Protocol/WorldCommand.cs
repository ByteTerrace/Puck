using System.Numerics;

namespace Puck.World.Protocol;

/// <summary>A validated authority command a client submits for one entity — the closed set of server-side mutations the
/// <c>player.*</c> drive verbs translate into (warp/face/pose → <see cref="Teleport"/>, run/fly → <see cref="EnqueueSegment"/>,
/// press → <see cref="PressLane"/>, motion → <see cref="SetMotion"/>, control → <see cref="SetControl"/>, reconcile →
/// <see cref="Reconcile"/>, stop → <see cref="Stop"/>). Each carries the 0-based <see cref="EntityIndex"/> it acts on;
/// the server validates and applies it at its next step boundary. Every command carries its acting
/// <see cref="Principal"/>; the server checks <see cref="WorldCapability.Drive"/> over the target body before it applies.</summary>
/// <param name="Principal">The acting identity the command is checked against.</param>
/// <param name="EntityIndex">The 0-based entity index the command acts on.</param>
internal abstract record WorldCommand(WorldPrincipal Principal, int EntityIndex) {
    /// <summary>A hard reposition — a warp (planar, heading kept) or a full 6DOF pose. A hard teleport: the server snaps
    /// the sim pose and the snapshot carries <see cref="EntityContinuityKind.Teleport"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Position">The target position (a warp uses only X/Z; the ground plane pins Y).</param>
    /// <param name="YawRadians">The target yaw (ignored by a <see cref="TeleportKind.Warp"/>, which keeps the heading).</param>
    /// <param name="PitchRadians">The target pitch (a <see cref="TeleportKind.Pose"/> only).</param>
    /// <param name="RollRadians">The target roll (a <see cref="TeleportKind.Pose"/> only).</param>
    /// <param name="Kind">Whether this is a planar warp or a full pose.</param>
    internal sealed record Teleport(WorldPrincipal Principal, int EntityIndex, Vector3 Position, float YawRadians, float PitchRadians, float RollRadians, TeleportKind Kind) : WorldCommand(Principal, EntityIndex);

    /// <summary>Sets an entity's heading directly and levels it (a pure yaw attitude) — the planar heading shorthand.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="YawRadians">The new heading in radians (0 = facing -Z).</param>
    internal sealed record Face(WorldPrincipal Principal, int EntityIndex, float YawRadians) : WorldCommand(Principal, EntityIndex);

    /// <summary>Enqueues a timed scripted segment on an entity's tape (run = planar channels, fly = all six) — while live
    /// it overrides that entity's device/wander for <see cref="Seconds"/> of advance time.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Intent">The intent the segment holds while live.</param>
    /// <param name="Seconds">How long (advance seconds) the segment drives before it expires.</param>
    internal sealed record EnqueueSegment(WorldPrincipal Principal, int EntityIndex, PlayerIntent Intent, float Seconds) : WorldCommand(Principal, EntityIndex);

    /// <summary>Presses an action lane for a timed auto-release (the wire <c>player.press</c> path) — independent of the
    /// movement tape.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Lane">The lane to hold.</param>
    /// <param name="HoldSeconds">How long (sim seconds) the lane reads held before auto-releasing, or
    /// <see langword="null"/> for the default host-step-derived tap.</param>
    internal sealed record PressLane(WorldPrincipal Principal, int EntityIndex, ActionLanes Lane, float? HoldSeconds) : WorldCommand(Principal, EntityIndex);

    /// <summary>Sets an entity's <see cref="MotionModel"/> — an authoritative switch (does not glide).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Model">The motion model to integrate under.</param>
    internal sealed record SetMotion(WorldPrincipal Principal, int EntityIndex, MotionModel Model) : WorldCommand(Principal, EntityIndex);

    /// <summary>Sets an entity's <see cref="IntentSource"/> — what fills its intent gaps between tape segments.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Source">The intent source to latch.</param>
    internal sealed record SetControl(WorldPrincipal Principal, int EntityIndex, IntentSource Source) : WorldCommand(Principal, EntityIndex);

    /// <summary>A smoothed server correction: the sim pose snaps to the target while the snapshot carries
    /// <see cref="EntityContinuityKind.Correction"/> so the client eases the pre-snap render error to zero.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="X">The authoritative world X coordinate.</param>
    /// <param name="Z">The authoritative world Z coordinate.</param>
    /// <param name="YawRadians">The authoritative heading in radians (0 = facing -Z).</param>
    /// <param name="Seconds">The smoothing window over which the client eases the render error to zero.</param>
    internal sealed record Reconcile(WorldPrincipal Principal, int EntityIndex, float X, float Z, float YawRadians, float Seconds) : WorldCommand(Principal, EntityIndex);

    /// <summary>Stops an entity dead — clears its whole tape and releases every held key/lane.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    internal sealed record Stop(WorldPrincipal Principal, int EntityIndex) : WorldCommand(Principal, EntityIndex);
}

/// <summary>Which flavor of hard reposition a <see cref="WorldCommand.Teleport"/> is.</summary>
internal enum TeleportKind : byte {
    /// <summary>A planar warp (<c>player.warp</c>): only X/Z are used and the heading is kept.</summary>
    Warp,

    /// <summary>A full 6DOF pose (<c>player.pose</c>): position and yaw/pitch/roll are all written.</summary>
    Pose,
}
