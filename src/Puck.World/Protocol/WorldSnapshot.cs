using System.Numerics;

namespace Puck.World.Protocol;

/// <summary>How an entity's pose changed across the tick a <see cref="WorldSnapshot"/> reports — the presentation hint
/// the client reads to interpolate, snap, or ease the on-screen pose toward the new authoritative one.</summary>
internal enum EntityContinuityKind : byte {
    /// <summary>The pose advanced by ordinary integration — interpolate from the previous snapshot pose.</summary>
    Continuous,

    /// <summary>The pose was hard-teleported (warp/face/pose/model switch) — snap the render pose, never interpolate the jump.</summary>
    Teleport,

    /// <summary>A server correction snapped the sim pose — snap authority but ease the pre-snap render error to zero over
    /// <see cref="EntityContinuity.Seconds"/>.</summary>
    Correction,
}

/// <summary>The per-entity continuity hint a <see cref="EntitySnapshot"/> carries: whether the client interpolates
/// (<see cref="EntityContinuityKind.Continuous"/>), snaps (<see cref="EntityContinuityKind.Teleport"/>), or eases across
/// the tick's pose change (<see cref="EntityContinuityKind.Correction"/> with a smoothing window). It replaces the
/// per-body teleport/reconcile presentation bookkeeping — the render-error easer lives client-side, driven by this
/// flag.</summary>
/// <param name="Kind">The kind of pose change.</param>
/// <param name="Seconds">The smoothing window for <see cref="EntityContinuityKind.Correction"/>; zero otherwise.</param>
internal readonly record struct EntityContinuity(EntityContinuityKind Kind, float Seconds = 0f) {
    /// <summary>The position-error ceiling (world units) above which a correction pops instead of gliding — shared by
    /// the server's snap-escape check and the client's easer guard, so neither side ever streaks a respawn-scale jump.</summary>
    public const float MaxSmoothError = 3f;

    /// <summary>The ordinary-integration hint — the client interpolates from the previous snapshot pose.</summary>
    public static EntityContinuity Continuous => new(Kind: EntityContinuityKind.Continuous);

    /// <summary>The hard-teleport hint — the client snaps the render pose without interpolating the jump.</summary>
    public static EntityContinuity Teleport => new(Kind: EntityContinuityKind.Teleport);

    /// <summary>A server-correction hint — the client snaps authority but eases the pre-snap render error to zero over
    /// <paramref name="seconds"/>.</summary>
    /// <param name="seconds">The smoothing window.</param>
    public static EntityContinuity Correction(float seconds) => new(Kind: EntityContinuityKind.Correction, Seconds: seconds);
}

/// <summary>One entity's authoritative render state for a tick — the server's outbound currency for a single body. The
/// client draws from a run of these, interpolating (or snapping/easing per <see cref="Continuity"/>) between consecutive
/// snapshots. Poses flow OUT only: this is the sole channel a body's pose leaves the server on.</summary>
/// <param name="Index">The 0-based entity index (0..3 local seats, 4..127 peers).</param>
/// <param name="Position">The authoritative world-space position.</param>
/// <param name="Orientation">The authoritative full 6DOF attitude.</param>
/// <param name="BodyColor">The avatar's material albedo (a pending seat's is already gray-lerped).</param>
/// <param name="Active">Whether the entity is drawn this tick.</param>
/// <param name="Kit">The entity's kit row index into the server-delivered definition (drives render selection, never
/// who is driving it).</param>
/// <param name="Look">The entity's LOOK row index into the server-delivered definition's look table (drives the
/// client's appearance resolution — catalog rig vs. creation stamp — PRESENTATION-ONLY, never who is driving it).</param>
/// <param name="Continuity">How the pose changed across this tick — the client's interpolate/snap/ease hint.</param>
internal readonly record struct EntitySnapshot(
    int Index,
    Vector3 Position,
    Quaternion Orientation,
    Vector3 BodyColor,
    bool Active,
    byte Kit,
    byte Look,
    EntityContinuity Continuity
);

/// <summary>The server's outbound tick image — the whole entity table's authoritative render state plus a revision the
/// client watches to rebuild its avatar program (as the population/roster revisions drive it today). The client
/// consumes this and produces intents, commands, and session requests in return.</summary>
/// <param name="Tick">The simulation tick this snapshot reports.</param>
/// <param name="Revision">The declared-set/palette revision; a change drives the client program rebuild.</param>
/// <param name="StepTicks">The engine ticks the reported step advanced by — the client's easer-decay delta.</param>
/// <param name="Entries">The active entries this tick (one <see cref="EntitySnapshot"/> per drawn body).</param>
internal readonly record struct WorldSnapshot(
    ulong Tick,
    int Revision,
    ulong StepTicks,
    ReadOnlyMemory<EntitySnapshot> Entries
);

/// <summary>One entity's submitted intent for a tick — the client's inbound movement currency for a body it drives (a
/// connection carries up to four per tick, one per local seat). The server resolves the intent against the body's live
/// tape and server-side producer in <c>NextIntent</c> precedence; <paramref name="HeldLanes"/> is the always-overlay
/// device-lane image (a held jump button rides a tape-driven runner).</summary>
/// <param name="Tick">The tick this intent is submitted for.</param>
/// <param name="EntityIndex">The 0-based entity index the intent drives.</param>
/// <param name="Intent">The merged movement and action image for the tick.</param>
/// <param name="Principal">The acting identity the submission is checked against — the server drops it (loud, once per
/// denial episode) unless the principal holds <see cref="WorldCapability.Drive"/> over the target body.</param>
/// <param name="HeldLanes">The live-held device lanes overlaid regardless of tape/producer precedence.</param>
internal readonly record struct IntentSubmission(
    ulong Tick,
    int EntityIndex,
    PlayerIntent Intent,
    WorldPrincipal Principal,
    ActionLanes HeldLanes = ActionLanes.None
);
