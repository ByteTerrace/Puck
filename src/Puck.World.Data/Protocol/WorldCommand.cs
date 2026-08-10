using System.Numerics;
using Puck.Maths;

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
    public sealed record SnapPose(WorldPrincipal Principal, int EntityIndex, Vector3 Position, float YawRadians, float PitchRadians, float RollRadians, SnapPoseMode Mode) : WorldCommand(Principal, EntityIndex);

    /// <summary>Enqueues a timed scripted segment on an entity's tape (run = planar channels, fly = all six) — while live
    /// it overrides that entity's device/wander for <see cref="Seconds"/> of advance time.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Intent">The intent the segment holds while live.</param>
    /// <param name="Seconds">How long (advance seconds) the segment drives before it expires.</param>
    public sealed record EnqueueSegment(WorldPrincipal Principal, int EntityIndex, PlayerIntent Intent, float Seconds) : WorldCommand(Principal, EntityIndex);

    /// <summary>Presses a channel for a timed auto-release (the wire <c>player.press</c> path) — independent of the
    /// movement tape, reaching any declared channel (movement roles included).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="ChannelOrdinal">The channel ordinal to hold.</param>
    /// <param name="Value">The raw fixed-point value to hold the channel at.</param>
    /// <param name="HoldSeconds">How long (sim seconds) the channel reads held before auto-releasing, or
    /// <see langword="null"/> for the default host-step-derived tap.</param>
    public sealed record PressChannel(WorldPrincipal Principal, int EntityIndex, int ChannelOrdinal, FixedQ4816 Value, float? HoldSeconds) : WorldCommand(Principal, EntityIndex);

    /// <summary>Sets an entity's named body motion program — an authoritative switch (does not glide).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="BodyMotionProgram">The body motion program to execute.</param>
    public sealed record SetBodyMotion(WorldPrincipal Principal, int EntityIndex, string BodyMotionProgram) : WorldCommand(Principal, EntityIndex);

    /// <summary>Sets an entity's <see cref="IntentSource"/> — what fills its intent gaps between tape segments.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Source">The intent source to latch.</param>
    public sealed record SetControl(WorldPrincipal Principal, int EntityIndex, IntentSource Source) : WorldCommand(Principal, EntityIndex);

    /// <summary>A smoothed server correction: the sim pose snaps to the target while the snapshot carries
    /// <see cref="EntityContinuityKind.Correction"/> so the client eases the pre-snap render error to zero.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="X">The authoritative world X coordinate.</param>
    /// <param name="Z">The authoritative world Z coordinate.</param>
    /// <param name="YawRadians">The authoritative heading in radians (0 = facing -Z).</param>
    /// <param name="Seconds">The smoothing window over which the client eases the render error to zero.</param>
    public sealed record Reconcile(WorldPrincipal Principal, int EntityIndex, float X, float Z, float YawRadians, float Seconds) : WorldCommand(Principal, EntityIndex);

    /// <summary>Stops an entity dead — clears its whole tape and releases every held key/lane.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    public sealed record Stop(WorldPrincipal Principal, int EntityIndex) : WorldCommand(Principal, EntityIndex);

    /// <summary>Routes an entity's intent onto a target — a diegetic screen (today's <c>player.engage</c> UX,
    /// unchanged) or another body (possession) — the context-routes widening of the old screen-only engage path,
    /// dissolved off <c>WorldEngagement</c>'s old loopback-only surface into a principal-carrying command (headless
    /// design §1.8). The server checks <see cref="Principal"/> — the submitter, never <paramref name="TargetPrincipal"/>
    /// — holds <see cref="WorldCapability.Control"/> over <see cref="Target"/> before any mutation (never the generic
    /// <see cref="WorldCapability.Drive"/>-over-body gate every other command passes through — see
    /// <c>Server.WorldServer.ApplyCommand</c>'s own remarks). The route's channel mask is resolved server-side from
    /// document data (a screen's authored <c>WorldScreenRoute.Channels</c>, or every ordinal for a body target) —
    /// never carried on this command, since it is a deterministic function of already-replayed state.</summary>
    /// <param name="Principal">The acting identity — the submitter, checked for Control over <see cref="Target"/>.</param>
    /// <param name="EntityIndex">The 0-based entity index being routed.</param>
    /// <param name="Target">The route's target subject — a screen or a body.</param>
    /// <param name="Capture">Whether the route captures the source body (idles it, today's behavior) or mirrors it
    /// (the source keeps integrating its own pose while the same resolved intent also reaches the target).</param>
    /// <param name="TargetPrincipal">The identity the route is recorded under — the resolved identity of
    /// the entity being routed (a local seat's own claimed identity, or a population entry's <see cref="PrincipalKind.Peer"/>
    /// identity), distinct from <see cref="Principal"/> whenever an actor routes an entity that is not itself.</param>
    public sealed record Engage(WorldPrincipal Principal, int EntityIndex, GrantSubject Target, bool Capture, WorldPrincipal TargetPrincipal) : WorldCommand(Principal, EntityIndex);

    /// <summary>Disengages an entity from whichever target it is routed to (a screen or a body) — the
    /// <c>player.disengage</c> wire path, dissolved off <c>WorldEngagement</c> the same way <see cref="Engage"/> was.
    /// The server reads the entity's capture latch and its <see cref="TargetPrincipal"/>'s Control route together and
    /// decides among four outcomes (see <c>Server.WorldEngagement.Disengage</c>'s own remarks): a stuck latch (latch
    /// set, no route) self-heals unconditionally; a route with no latch requires <see cref="Principal"/> to hold
    /// Control over that route's target before it is cleared; an ordinary engaged disengage requires the identical
    /// check over the currently-routed target; and disengaging an entity that was never routed is a friendly no-op.</summary>
    /// <param name="Principal">The acting identity — the submitter, checked for Control wherever the decision needs it.</param>
    /// <param name="EntityIndex">The 0-based entity index being disengaged.</param>
    /// <param name="TargetPrincipal">The identity the engagement route is recorded under — see <see cref="Engage.TargetPrincipal"/>.</param>
    public sealed record Disengage(WorldPrincipal Principal, int EntityIndex, WorldPrincipal TargetPrincipal) : WorldCommand(Principal, EntityIndex);

    /// <summary>Stages player-owned durable action state for one explicit simulation tick.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="EntityIndex">The 0-based entity index.</param>
    /// <param name="Tick">The simulation tick at whose boundary the values enter.</param>
    /// <param name="Values">Named fixed-point counter or exact timer values.</param>
    public sealed record LoadDurableState(WorldPrincipal Principal, int EntityIndex, ulong Tick, IReadOnlyList<DurableStateValue> Values) : WorldCommand(Principal, EntityIndex);
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

/// <summary>The outcome of a <c>player.disengage</c> — dissolved off <c>WorldEngagement</c>'s old internal enum into
/// shared protocol vocabulary so both the client's pre-submission read (the console echo's source of truth) and the
/// server's actual apply agree on the same four states. A denial is never confused with the friendly "was not engaged"
/// no-op, and a latch/route inconsistency is never silently swallowed by either. The two repaired cases are kept
/// distinct (rather than one shared value) because only one of them warrants dropping the entity's held device
/// state — see <c>Server.WorldEngagement.Disengage</c>'s own remarks for the full decision.</summary>
public enum DisengageOutcome : byte {
    /// <summary>The entity was not engaged on any screen (the latch was clear and no route existed) — a friendly
    /// no-op. Nothing changed.</summary>
    NotEngaged,

    /// <summary>The entity was truly engaged (the latch and the route agreed), or a route existed with no latch, and
    /// the acting principal lacks Control over the relevant screen — refused loudly. Nothing changed.</summary>
    Denied,

    /// <summary>Either the entity was truly captured (the latch and the route agreed) and the actor held Control over
    /// the target, or the route was a deliberate mirror (capture:false — the latch never sets by design) and the
    /// actor held Control over the target — both are an ordinary successful disengage, and the route was cleared
    /// (plus the latch, when it was set).</summary>
    Disengaged,

    /// <summary>The capture latch was set with no matching Control route (an admin revoke stripped the route out
    /// from under a genuinely captured entity) — self-heals unconditionally, since nothing here touches the grant
    /// table. A held device state is dropped, exactly as an ordinary <see cref="Disengaged"/> outcome does.</summary>
    RepairedLatch,

    /// <summary>A Control route existed with no matching capture latch, and the route was never established as a
    /// deliberate mirror (a bare <c>world.grant … control screen:N</c>/<c>control body:N</c> with no matching
    /// engage) — cleared only after the same Control check an ordinary disengage requires. No held device state is
    /// dropped: the entity was never actually captured, so there is nothing to release.</summary>
    RepairedRoute,
}

/// <summary>Identifies which pose components a <see cref="WorldCommand.SnapPose"/> replaces. One shape today, and the
/// enum stays because the wire tag it carries is what lets a second one arrive without re-versioning the leaf.</summary>
public enum SnapPoseMode : byte {
    /// <summary>A full 6DOF pose (<c>player.pose</c>): position and yaw/pitch/roll are all written. A caller holding
    /// an axis current spells that with <c>-</c> at the verb, which resolves before the command is built — never a
    /// mode of its own.</summary>
    Pose,
}
