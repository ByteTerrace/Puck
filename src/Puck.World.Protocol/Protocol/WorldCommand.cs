using System.Numerics;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Protocol;

/// <summary>A validated authority command a client submits for one entity — the closed set of server-side mutations the
/// <c>player.*</c> drive verbs translate into (pose → <see cref="SnapPose"/>, fly → <see cref="EnqueueSegment"/>,
/// press → <see cref="PressChannel"/>, motion → <see cref="SetBodyMotion"/>, control → <see cref="SetControl"/>, reconcile →
/// <see cref="Reconcile"/>, stop → <see cref="Stop"/>). Each carries the 0-based <see cref="EntityIndex"/> it acts on;
/// the server validates and applies it at its next step boundary. Every command carries its acting
/// <see cref="Principal"/>; the server checks <see cref="WorldCapability.Drive"/> over the target body before it applies.</summary>
/// <param name="Principal">The acting identity the command is checked against.</param>
/// <param name="EntityIndex">The 0-based entity index the command acts on.</param>
public abstract record WorldCommand(WorldPrincipal Principal, int EntityIndex) {
    /// <summary>Snaps selected pose components while preserving every component omitted by <see cref="Mode"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Position">The replacement position.</param>
    /// <param name="YawRadians">The replacement yaw.</param>
    /// <param name="PitchRadians">The replacement pitch.</param>
    /// <param name="RollRadians">The replacement roll.</param>
    /// <param name="Mode">The closed replacement shape.</param>
    public sealed record SnapPose(WorldPrincipal Principal, int EntityIndex, Vector3 Position, float YawRadians, float PitchRadians, float RollRadians, SnapPoseMode Mode) : WorldCommand(
        Principal,
        EntityIndex
    );
    /// <summary>Enqueues a timed scripted segment on an entity's tape (run = planar channels, fly = all six) — while live
    /// it overrides that entity's device/wander for <see cref="Seconds"/> of advance time.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Intent">The intent the segment holds while live.</param>
    /// <param name="Seconds">How long (advance seconds) the segment drives before it expires.</param>
    public sealed record EnqueueSegment(WorldPrincipal Principal, int EntityIndex, PlayerIntent Intent, float Seconds) : WorldCommand(
        Principal,
        EntityIndex
    );
    /// <summary>Presses a channel for a timed auto-release (the wire <c>body.press</c> path) — independent of the
    /// movement tape, reaching any declared channel (movement roles included).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="ChannelOrdinal">The channel ordinal to hold.</param>
    /// <param name="Value">The raw fixed-point value to hold the channel at.</param>
    /// <param name="HoldSeconds">How long (sim seconds) the channel reads held before auto-releasing, or
    /// <see langword="null"/> for the default host-step-derived tap.</param>
    public sealed record PressChannel(WorldPrincipal Principal, int EntityIndex, int ChannelOrdinal, FixedQ4816 Value, float? HoldSeconds) : WorldCommand(
        Principal,
        EntityIndex
    );
    /// <summary>Sets an entity's named body motion program — an authoritative switch (does not glide).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="BodyMotionProgram">The body motion program to execute.</param>
    public sealed record SetBodyMotion(WorldPrincipal Principal, int EntityIndex, string BodyMotionProgram) : WorldCommand(
        Principal,
        EntityIndex
    );
    /// <summary>Sets an entity's <see cref="IntentSource"/> — what fills its intent gaps between tape segments.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Source">The intent source to latch.</param>
    public sealed record SetControl(WorldPrincipal Principal, int EntityIndex, IntentSource Source) : WorldCommand(
        Principal,
        EntityIndex
    );
    /// <summary>A smoothed server correction: the sim pose snaps to the target while the snapshot carries
    /// <see cref="EntityContinuityKind.Correction"/> so the client eases the pre-snap render error to zero.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="X">The authoritative world X coordinate.</param>
    /// <param name="Z">The authoritative world Z coordinate.</param>
    /// <param name="YawRadians">The authoritative heading in radians (0 = facing -Z).</param>
    /// <param name="Seconds">The smoothing window over which the client eases the render error to zero.</param>
    public sealed record Reconcile(WorldPrincipal Principal, int EntityIndex, float X, float Z, float YawRadians, float Seconds) : WorldCommand(
        Principal,
        EntityIndex
    );
    /// <summary>Stops an entity dead — clears its whole tape and releases every held key/lane.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    public sealed record Stop(WorldPrincipal Principal, int EntityIndex) : WorldCommand(
        Principal,
        EntityIndex
    );
    /// <summary>Composes a <see cref="ControlApplication"/> onto <see cref="TargetPrincipal"/>'s application set —
    /// the <c>body.engage</c> wire path. The target is a diegetic screen's booted machine or another body
    /// (possession); the application's kit and channel reach are resolved SERVER-SIDE from document data (a screen's
    /// authored <c>WorldScreenRoute.Kit</c>/<c>Channels</c>, or passthrough over every ordinal for a body target), so
    /// nothing here needs carrying that is a deterministic function of already-replayed state. The server checks
    /// <see cref="Principal"/> — the submitter, never <see cref="TargetPrincipal"/> — holds
    /// <see cref="WorldCapability.Control"/> over <see cref="Target"/> before any mutation (never the generic
    /// <see cref="WorldCapability.Drive"/>-over-body gate every other command passes through — see
    /// <c>Server.WorldServer.ApplyCommand</c>'s own remarks).</summary>
    /// <param name="Principal">The acting identity — the submitter, checked for Control over <see cref="Target"/>.</param>
    /// <param name="EntityIndex">The 0-based entity index whose intent the composed application carries.</param>
    /// <param name="Target">The application's target subject — a screen or a body.</param>
    /// <param name="Exclusive">Whether composing DROPS the participant's own-body application (the source avatar
    /// idles — the classic capture) or retains it beside the new one (the source keeps integrating its own pose
    /// while the same resolved intent also reaches the target).</param>
    /// <param name="TargetPrincipal">The identity whose application set is composed — the resolved identity of the
    /// entity being applied (a local seat's own claimed identity, or a population entry's
    /// <see cref="PrincipalKind.Peer"/> identity), distinct from <see cref="Principal"/> whenever an actor composes
    /// an application for an entity that is not itself.</param>
    public sealed record ComposeControl(WorldPrincipal Principal, int EntityIndex, GrantSubject Target, bool Exclusive, WorldPrincipal TargetPrincipal) : WorldCommand(
        Principal,
        EntityIndex
    );
    /// <summary>Dissolves every non-own-body <see cref="ControlApplication"/> in <see cref="TargetPrincipal"/>'s set,
    /// restoring the default single own-body application — the <c>body.disengage</c> wire path. The server checks
    /// <see cref="Principal"/> holds <see cref="WorldCapability.Control"/> over each dissolved application's target;
    /// dissolving a set that is already the default is a friendly no-op (see <see cref="ControlOutcome"/>).</summary>
    /// <param name="Principal">The acting identity — the submitter, checked for Control over each dissolved target.</param>
    /// <param name="EntityIndex">The 0-based entity index whose applications are dissolved.</param>
    /// <param name="TargetPrincipal">The identity whose application set is dissolved — see
    /// <see cref="ComposeControl.TargetPrincipal"/>.</param>
    public sealed record DissolveControl(WorldPrincipal Principal, int EntityIndex, WorldPrincipal TargetPrincipal) : WorldCommand(
        Principal,
        EntityIndex
    );
    /// <summary>Stages player-owned durable action state for one explicit simulation tick.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Tick">The simulation tick at whose boundary the values enter.</param>
    /// <param name="Values">Named fixed-point counter or exact timer values.</param>
    public sealed record LoadDurableState(WorldPrincipal Principal, int EntityIndex, ulong Tick, IReadOnlyList<DurableStateValue> Values) : WorldCommand(
        Principal,
        EntityIndex
    );
}
/// <summary>One named value crossing the durable action-state input boundary.</summary>
/// <param name="Name">The authored slot name.</param>
/// <param name="Value">The counter value; zero for a timer.</param>
/// <param name="TimerTicks">The timer remainder; zero for a counter.</param>
public readonly record struct DurableStateValue(string Name, FixedQ4816 Value, ulong TimerTicks);
/// <summary>One player-keyed durable value emitted after a simulation tick.</summary>
/// <param name="Tick">The tick that produced the write.</param>
/// <param name="PlayerId">The stable player identity.</param>
/// <param name="EntityIndex">The body carrying the player during the write.</param>
/// <param name="Value">The named value.</param>
/// <param name="Kind">Whether the body replaced or added to the value.</param>
/// <param name="StorageKind">The durable slot's numeric representation.</param>
public readonly record struct DurableStateOutput(ulong Tick, string PlayerId, int EntityIndex, DurableStateValue Value, WorldDocumentWriteKind Kind, ActionStateKind StorageKind);
/// <summary>The outcome of a <see cref="WorldCommand.DissolveControl"/> — shared protocol vocabulary so the client's
/// pre-submission read (the console echo's source of truth) and the server's actual apply agree. A denial is never
/// confused with the friendly "held nothing to dissolve" no-op.</summary>
public enum ControlOutcome : byte {
    /// <summary>The participant's application set was already the default (its own body alone) — a friendly no-op.
    /// Nothing changed.</summary>
    NotApplied,

    /// <summary>The acting principal lacks <see cref="WorldCapability.Control"/> over at least one applied target —
    /// refused loudly. Nothing changed.</summary>
    Denied,

    /// <summary>Every non-own-body application was dissolved and the own-body application restored; the source
    /// avatar resumes driving itself. The caller drops the entity's held device state.</summary>
    Dissolved,
}
/// <summary>Identifies which pose components a <see cref="WorldCommand.SnapPose"/> replaces. One shape today, and the
/// enum stays because the wire tag it carries is what lets a second one arrive without re-versioning the leaf.</summary>
public enum SnapPoseMode : byte {
    /// <summary>A full 6DOF pose (<c>body.pose</c>): position and yaw/pitch/roll are all written. A caller holding
    /// an axis current spells that with <c>-</c> at the verb, which resolves before the command is built — never a
    /// mode of its own.</summary>
    Pose,
}
