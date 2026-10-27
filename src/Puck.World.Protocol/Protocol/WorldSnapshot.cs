using Puck.Commands;
using System.Numerics;
using Puck.Physics.Motion;

namespace Puck.World.Protocol;

/// <summary>How an entity's pose changed across the tick a <see cref="WorldSnapshot"/> reports — the presentation hint
/// the client reads to interpolate, snap, or ease the on-screen pose toward the new authoritative one.</summary>
public enum EntityContinuityKind : byte {
    /// <summary>The pose advanced by ordinary integration — interpolate from the previous snapshot pose.</summary>
    Continuous,

    /// <summary>The pose was hard-teleported (warp/face/pose/model switch) — snap the render pose, never interpolate the jump.</summary>
    Teleport,

    /// <summary>A server correction snapped the sim pose — snap authority but ease the pre-snap render error to zero over
    /// <see cref="EntityContinuity.Seconds"/>.</summary>
    Correction,

    /// <summary>The body crossed a portal pair standing in its own world, moving through the rigid
    /// <see cref="EntityContinuity.Carried"/> motion and keeping its momentum — carry the previous pose and every
    /// latched presentation state through it, then interpolate as for <see cref="Continuous"/>.</summary>
    Carry,
}
/// <summary>The per-entity continuity hint a <see cref="EntitySnapshot"/> carries: whether the client interpolates
/// (<see cref="EntityContinuityKind.Continuous"/>), snaps (<see cref="EntityContinuityKind.Teleport"/>), or eases across
/// the tick's pose change (<see cref="EntityContinuityKind.Correction"/> with a smoothing window). It replaces the
/// per-body teleport/reconcile presentation bookkeeping — the render-error easer lives client-side, driven by this
/// flag.</summary>
/// <param name="Kind">The kind of pose change.</param>
/// <param name="Seconds">The smoothing window for <see cref="EntityContinuityKind.Correction"/>; zero otherwise.</param>
/// <param name="Carried">The rigid motion for <see cref="EntityContinuityKind.Carry"/>; <see langword="null"/>
/// otherwise.</param>
public readonly record struct EntityContinuity(EntityContinuityKind Kind, float Seconds = 0f, WorldPoseCarry? Carried = null) {
    /// <summary>Gets the ordinary-integration hint — the client interpolates from the previous snapshot pose.</summary>
    public static EntityContinuity Continuous => new(Kind: EntityContinuityKind.Continuous);
    /// <summary>Gets the hard-teleport hint — the client snaps the render pose without interpolating the jump.</summary>
    public static EntityContinuity Teleport => new(Kind: EntityContinuityKind.Teleport);

    /// <summary>Creates a server-correction hint — the client snaps authority but eases the pre-snap render error to zero over
    /// <paramref name="seconds"/>.</summary>
    /// <param name="seconds">The smoothing window.</param>
    public static EntityContinuity Correction(float seconds) => new(
        Kind: EntityContinuityKind.Correction,
        Seconds: seconds
    );
    /// <summary>Creates an in-place crossing hint — the client carries its previous pose through
    /// <paramref name="carried"/> and interpolates on.</summary>
    /// <param name="carried">The rigid motion the crossing moved the body by.</param>
    public static EntityContinuity Carry(WorldPoseCarry carried) => new(
        Kind: EntityContinuityKind.Carry,
        Carried: carried
    );
    /// <summary>Returns the hint for this pose change followed by <paramref name="later"/> before any client saw
    /// either: two carries compose, a carry keeps an earlier snap or correction, and anything else is
    /// <paramref name="later"/> unless it is <see cref="EntityContinuityKind.Continuous"/>.</summary>
    /// <param name="later">The later pose change's hint.</param>
    /// <returns>The combined hint.</returns>
    public EntityContinuity Then(EntityContinuity later) => (later.Kind, Kind) switch {
        (EntityContinuityKind.Continuous, _) => this,
        (EntityContinuityKind.Carry, EntityContinuityKind.Carry) => Carry(carried: Carried!.Value.Then(later: later.Carried!.Value)),
        (EntityContinuityKind.Carry, EntityContinuityKind.Teleport or EntityContinuityKind.Correction) => this,
        _ => later,
    };
}
/// <summary>One entity's authoritative render state for a tick — the server's outbound currency for a single body. The
/// client draws from a run of these, interpolating (or snapping/easing per <see cref="Continuity"/>) between consecutive
/// snapshots. Poses flow OUT only: this is the sole channel a body's pose leaves the server on.</summary>
/// <param name="Index">The 0-based entity index (0..3 local seats, 4..4095 peers at the engine ceiling).</param>
/// <param name="Position">The authoritative world-space position.</param>
/// <param name="Orientation">The authoritative full 6DOF attitude.</param>
/// <param name="BodyColor">The avatar's material albedo (a pending seat's is already gray-lerped).</param>
/// <param name="Active">Whether the entity is drawn this tick.</param>
/// <param name="Kit">The entity's kit row index into the server-delivered definition (drives render selection, never
/// who is driving it).</param>
/// <param name="Look">The entity's LOOK row index into the server-delivered definition's look table (drives the
/// client's appearance resolution — catalog rig vs. creation stamp — PRESENTATION-ONLY, never who is driving it).</param>
/// <param name="CatalogRig">The entity-owned procedural catalog rig. An explicit authored catalog look overrides it;
/// an implicit catalog look reads it, so changing authority slots cannot silently change the entity's shape.</param>
/// <param name="Continuity">How the pose changed across this tick — the client's interpolate/snap/ease hint.</param>
/// <param name="Generation">The slot activation generation, paired with the snapshot authority and
/// <paramref name="Index"/> to form a durable <see cref="WorldEntityAddress"/>.</param>
/// <param name="PlacementId">The placement row this entity INHABITS, or <see langword="null"/> for a seat/peer. The
/// client renders an inhabitant's creation geometry (a body-rooted stamp riding this pose) instead of a catalog avatar —
/// PRESENTATION-ONLY, never who is driving it. No transport change (the loopback is a pass-through).</param>
/// <param name="Heading">The body's authoritative HEADING in radians about world up — the Turn role's integral, which
/// way "forward" is — as distinct from <paramref name="Orientation"/>, the attitude it is drawn in (under a World-frame
/// kit with facing snap the attitude turns to face the commanded travel while the heading holds). A seat moving in a
/// heading-framed channel pair composes against this, never against the drawn attitude.</param>
/// <param name="Facts">The body's authoritative per-tick fact mask — the same predicates the simulation's action
/// gates read, published so a client can key animation (a walk, a climb reach, a swim stroke) on the authority's own
/// answer. Derived, never authored.</param>
/// <param name="State">The body's action-state register file as it presents (raw <see cref="Puck.Maths.FixedQ4816"/>
/// bits per <see cref="WorldBodyStateReads.Presented"/>), one value per slot in register-file order (<c>state.body</c>,
/// then <c>state.identity</c>), so a render lane reads a slot the way a rule does. Like <paramref name="Facts"/> it
/// rides the entry, so an observer is delivered it only for a body it is disclosed. The memory is borrowed scratch
/// the next snapshot overwrites: a consumer copies what it keeps.</param>
public readonly record struct EntitySnapshot(
    int Index,
    Vector3 Position,
    Quaternion Orientation,
    Vector3 BodyColor,
    bool Active,
    byte Kit,
    byte Look,
    byte CatalogRig,
    EntityContinuity Continuity,
    int Generation = 0,
    string? PlacementId = null,
    float Heading = 0f,
    BodyFacts Facts = BodyFacts.None,
    ReadOnlyMemory<long> State = default
);
/// <summary>The server's outbound tick image — the whole entity table's authoritative render state plus a revision the
/// client watches to rebuild its avatar program (as the population/roster revisions drive it today). The client
/// consumes this and produces intents, commands, and session requests in return.</summary>
/// <param name="Tick">The simulation tick this snapshot reports.</param>
/// <param name="Revision">The declared-set/palette revision; a change drives the client program rebuild.</param>
/// <param name="StepTicks">The engine ticks the reported step advanced by — the client's easer-decay delta.</param>
/// <param name="Entries">The active entries this tick (one <see cref="EntitySnapshot"/> per drawn body).</param>
/// <param name="Authority">The authority identity every entry belongs to.</param>
/// <param name="FieldCells">Field-lattice writes and typed document-cell values since the previous snapshot.
/// A primer carries every streamed document cell, and every lattice cell when <paramref name="FieldsFull"/> is set.</param>
/// <param name="FieldsFull"><see langword="true"/> when <paramref name="FieldCells"/> carries every cell (a primer or a
/// resync), so a mirror replaces rather than patches.</param>
/// <param name="EngineTick">The engine-tick coordinate <see cref="Tick"/> completed at — what a presentation-side
/// reader (a HUD binding, a gait driver) must pass to evaluate a <c>StateAdvance</c> row's live value; never derived
/// from <see cref="Tick"/> at a simulation rate.</param>
/// <param name="Inputs">Authority-issued input epochs and continuation decisions, disclosed only to their owner.</param>
/// <remarks>Machine engagement pads do not ride this snapshot: <c>Server.WorldEngagement.FoldTick</c>'s per-screen
/// pad fold is read directly by <c>Server.WorldMachineHost.Advance</c> (in <c>Puck.World.Machines</c>) inside <c>WorldServer.Step</c>, in-process,
/// since machine stepping runs server-side and needs no wire lane to a presentation-side consumer.</remarks>
public readonly record struct WorldSnapshot(
    ulong Tick,
    int Revision,
    ulong StepTicks,
    ReadOnlyMemory<EntitySnapshot> Entries,
    string Authority = "",
    ReadOnlyMemory<FieldCellDelta> FieldCells = default,
    bool FieldsFull = false,
    ulong EngineTick = 0UL,
    ReadOnlyMemory<WorldInputPublication> Inputs = default
);
/// <summary>One lattice or typed document-cell write carried on a snapshot.</summary>
/// <param name="Cell">The lattice cell index (z, then layer, then x), or declared document-cell ordinal.</param>
/// <param name="Field">The field's index in the declared <c>fields.fields</c> list.</param>
/// <param name="Raw">The cell's value, raw Q48.16 bits.</param>
/// <param name="Row">A document row ordinal for a typed state cell, or -1 for a field-lattice cell.</param>
/// <param name="Image">The stored cell. A cell index of -1 starts a replacement row with Raw cells.</param>
public readonly record struct FieldCellDelta(int Cell, byte Field, long Raw, int Row = -1, ArenaCellImage Image = default);
/// <summary>One entity's submitted intent for a tick — the client's inbound movement currency for a body it drives (a
/// connection carries up to four per tick, one per local seat). The server resolves the intent against the body's live
/// tape and server-side producer in <c>NextIntent</c> precedence; <paramref name="HeldChannels"/> is the always-overlay
/// device-channel image (a held jump button rides a tape-driven runner).</summary>
/// <param name="Tick">The tick this intent is submitted for.</param>
/// <param name="EntityIndex">The 0-based entity index the intent drives.</param>
/// <param name="Intent">The merged movement and action image for the tick.</param>
/// <param name="Principal">The acting identity the submission is checked against — the server drops it (loud, once per
/// denial episode) unless the principal holds <see cref="WorldCapability.Drive"/> over the target body.</param>
/// <param name="HeldChannels">The live-held device channel image overlaid regardless of tape/producer precedence —
/// only ordinals the world's channel table does not resolve to a claimed movement role are meaningful (channels take
/// consecutive ordinals in document order); movement-role ordinals ride <paramref name="Intent"/>
/// directly. Replaces the old <c>ActionLanes HeldLanes</c> bitmask.</param>
/// <param name="MeasuredHoldTicks">An externally conditioned input-hold measurement in simulation ticks. No
/// simulation code samples a clock; this tick-stamped value is recorded with the intent.</param>
/// <param name="Input">The owner-scoped semantic batch, admitted at the simulation interval boundary.</param>
/// <param name="InputOwner">A local producer's admitted history session when distinct from its acting principal.
/// Its authority must resolve back to <paramref name="Principal"/> and this exact target; it grants no capability.</param>
public readonly record struct IntentSubmission(
    ulong Tick,
    int EntityIndex,
    PlayerIntent Intent,
    Principal Principal,
    PlayerIntent HeldChannels = default,
    int MeasuredHoldTicks = 0,
    WorldInputBatch? Input = null,
    Principal? InputOwner = null
);
/// <summary>Evaluates a <see cref="WorldObserverDisclosure"/> policy against an <see cref="EntitySnapshot"/> — kept
/// beside <see cref="EntitySnapshot"/> rather than on the policy record itself, since the policy is document data
/// (<c>Puck.World.Schema</c>) and this evaluation is a wire-delivery operation over this project's own snapshot
/// shape.</summary>
public static class WorldObserverDisclosureEvaluation {
    /// <summary>Returns whether an observer at <paramref name="observerIndex"/> is delivered <paramref name="entry"/>
    /// under <paramref name="disclosure"/>.</summary>
    /// <param name="disclosure">The disclosure policy.</param>
    /// <param name="entry">The candidate entity.</param>
    /// <param name="observerIndex">The observer's own 0-based body index, or a negative value when the observer has
    /// no body in this world.</param>
    /// <param name="observerPosition">The observer's own position; ignored unless
    /// <see cref="WorldObserverDisclosure.Mode"/> is <see cref="WorldObserverDisclosureMode.Radius"/>.</param>
    /// <returns><see langword="true"/> when the entity is delivered.</returns>
    public static bool Discloses(this WorldObserverDisclosure disclosure, in EntitySnapshot entry, int observerIndex, Vector3 observerPosition) {
        if (disclosure.Mode == WorldObserverDisclosureMode.All) {
            return true;
        }

        if (observerIndex < 0) {
            return false;
        }

        if (entry.Index == observerIndex) {
            return true;
        }

        return (
            (disclosure.Mode == WorldObserverDisclosureMode.Radius) &&
            (disclosure.Radius is { } radius) &&
            (Vector3.DistanceSquared(
            value1: entry.Position,
            value2: observerPosition
        ) <= (radius * radius))
        );
    }
}
