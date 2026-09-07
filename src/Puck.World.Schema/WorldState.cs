using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>The root <c>state</c> declaration. It is the document's abstract state inventory; compilation through
/// <see cref="StateCatalog"/> describes each lane's typed ownership and storage contract before runtime lowers
/// values into the storage appropriate to their access pattern. The engine reads it as an <see cref="IStateSection"/>:
/// <see cref="World"/> is the document lane, <see cref="Body"/>/<see cref="Identity"/> the per-participant slot lanes.
/// The section owns an immutable snapshot of every list it carries, taken at construction and again on every
/// <c>with</c>: a caller's array or list is copied once and never aliased, so an edit the caller makes to its own
/// collection afterward — in place, by index — can never reach an already-built section. A writer that means to
/// change a row composes a new list and hands it to <c>with</c> (or <see cref="WorldDefinition.WithWorldState"/>);
/// it may never keep the old list and mutate it through the section's own reference.</summary>
/// <param name="World">Document-owned cell rows. These remain mutation-addressable through <c>state:&lt;name&gt;</c>.</param>
/// <param name="Body">Per-body ephemeral counters and timers, compiled into each body's bounded ordinal arrays.</param>
/// <param name="Identity">Per-body counters and timers synchronized through the durable identity-document seam.</param>
/// <param name="Lattices">The lattice topologies the section's lattice-shaped rows lie over (see
/// <see cref="LatticeTopology"/>; the document adds the physical <see cref="WorldFieldTopology"/> case).</param>
public sealed record WorldStateSection(
    IReadOnlyList<WorldStateRow>? World = null,
    IReadOnlyList<ActionStateSlot>? Body = null,
    IReadOnlyList<ActionStateSlot>? Identity = null,
    IReadOnlyList<LatticeTopology>? Lattices = null
) : IStateSection {
    /// <inheritdoc cref="World"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WorldStateRow>? World { get => field; init => field = Freeze(value); } = Freeze(World);
    /// <inheritdoc cref="Body"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ActionStateSlot>? Body { get => field; init => field = Freeze(value); } = Freeze(Body);
    /// <inheritdoc cref="Identity"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ActionStateSlot>? Identity { get => field; init => field = Freeze(value); } = Freeze(Identity);
    /// <inheritdoc cref="Lattices"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LatticeTopology>? Lattices { get => field; init => field = Freeze(value); } = Freeze(Lattices);

    IReadOnlyList<StateRow>? IStateSection.Rows => World;
    IReadOnlyList<IStateSlot>? IStateSection.ParticipantSlots => Body;
    IReadOnlyList<IStateSlot>? IStateSection.IdentitySlots => Identity;

    // The one freeze site every construction and every `with` routes through — a section can never expose a list
    // the caller still holds a live, writable reference to.
    private static IReadOnlyList<T>? Freeze<T>(IReadOnlyList<T>? items) => ((items is null) ? null : items.ToImmutableArray());
}
/// <summary>
/// One row of the document's <c>state</c> section: a <see cref="StateRow"/> plus the two traits only a world reads —
/// the drive-admission gate and the physical-field storage. <see cref="StateRow.Name"/> is the
/// <c>UpsertStateRow</c>/<c>RemoveStateRow</c> key, the <c>state:&lt;name&gt;</c> grant subject, and — for a
/// slot-shaped row — the <c>state.&lt;name&gt;</c> HUD binding token.
/// </summary>
/// <param name="Name">The row's stable string name (unique within the section).</param>
/// <param name="Kind">Which cell kind every cell in this row carries.</param>
/// <param name="Min">See <see cref="StateRow.Min"/>.</param>
/// <param name="Max">See <see cref="StateRow.Max"/>.</param>
/// <param name="Capacity">See <see cref="StateRow.Capacity"/>.</param>
/// <param name="NonNegative">See <see cref="StateRow.NonNegative"/>.</param>
/// <param name="GatesDrive">Whether this row is a drive-admission gate. When set, this must be a keyed row
/// (<see cref="StateRow.IsKeyed"/>) whose per-body cell — keyed by the body's 0-based entity index — is consulted
/// before admitting a drive or action intent for that body: a nonzero cell refuses the body's intents until the cell
/// reads zero again, checked fresh every tick. The engine does not interpret the row's name; several independently
/// named gate rows may exist at once, any one of which can refuse. Legitimate only on a row declaring
/// <see cref="StateRow.Capacity"/>, and only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>/
/// <see cref="CellKind.Bool"/>. Default <see langword="false"/>.</param>
/// <param name="Evicts">See <see cref="StateRow.Evicts"/>.</param>
/// <param name="Cells">See <see cref="StateRow.Cells"/>.</param>
/// <param name="Advance">See <see cref="StateRow.Advance"/>.</param>
/// <param name="Draw">See <see cref="StateRow.Draw"/>.</param>
/// <param name="DrawCursor">See <see cref="StateRow.DrawCursor"/>.</param>
/// <param name="DrawnMasks">See <see cref="StateRow.DrawnMasks"/>.</param>
/// <param name="Dynamics">See <see cref="StateRow.Dynamics"/>.</param>
/// <param name="Field">The physical-field trait — carried only by a <see cref="StateDomain.CellsOf"/> row over a
/// <c>Field</c>-kind topology (see <see cref="WorldStateFieldTrait"/>); <see langword="null"/> for every other row,
/// including a discrete (board) <see cref="StateDomain.CellsOf"/> row. Never together with
/// <see cref="StateRow.Cycle"/>.</param>
/// <param name="Cycle">See <see cref="StateRow.Cycle"/>.</param>
/// <param name="Domain">See <see cref="StateRow.Domain"/>.</param>
/// <param name="ValuesFrom">See <see cref="StateRow.ValuesFrom"/>.</param>
/// <param name="Inverse">See <see cref="StateRow.Inverse"/>.</param>
/// <param name="Phase">See <see cref="StateRow.Phase"/>.</param>
/// <param name="Visibility">See <see cref="StateRow.Visibility"/>.</param>
/// <param name="Knowledge">See <see cref="StateRow.Knowledge"/>.</param>
/// <param name="PhaseOf">See <see cref="StateRow.PhaseOf"/>.</param>
/// <param name="HistoryCursor">See <see cref="StateRow.HistoryCursor"/>.</param>
public sealed record WorldStateRow(
    CellName Name,
    CellKind Kind,
    long? Min = null,
    long? Max = null,
    int? Capacity = null,
    bool NonNegative = false,
    bool GatesDrive = false,
    bool Evicts = false,
    IReadOnlyList<StateCell>? Cells = null,
    StateAdvance? Advance = null,
    Draw? Draw = null,
    long DrawCursor = 0,
    IReadOnlyList<ClosedBitset256>? DrawnMasks = null,
    StateDynamics? Dynamics = null,
    WorldStateFieldTrait? Field = null,
    StateCycle? Cycle = null,
    StateDomain? Domain = null,
    string? ValuesFrom = null,
    StateInverse? Inverse = null,
    StatePhase? Phase = null, StateVisibility? Visibility = null, StateKnowledge? Knowledge = null, string? PhaseOf = null,
    long HistoryCursor = 0
) : StateRow(Name, Kind, Min, Max, Capacity, NonNegative, Evicts, Cells, Advance, Draw, DrawCursor, DrawnMasks, Dynamics, Cycle, Domain, ValuesFrom, Inverse, Phase, Visibility, Knowledge, PhaseOf, HistoryCursor) {
    /// <summary>Initializes a document row over an engine row, adding the two world-only traits.</summary>
    /// <param name="row">The engine row.</param>
    /// <param name="gatesDrive">Whether the row is a drive-admission gate.</param>
    /// <param name="field">The physical-field trait, or <see langword="null"/>.</param>
    public WorldStateRow(StateRow row, bool gatesDrive, WorldStateFieldTrait? field) : this(
        Name: row.Name,
        Kind: row.Kind,
        Min: row.Min,
        Max: row.Max,
        Capacity: row.Capacity,
        NonNegative: row.NonNegative,
        GatesDrive: gatesDrive,
        Evicts: row.Evicts,
        Cells: row.Cells,
        Advance: row.Advance,
        Draw: row.Draw,
        DrawCursor: row.DrawCursor,
        DrawnMasks: row.DrawnMasks,
        Dynamics: row.Dynamics,
        Field: field,
        Cycle: row.Cycle,
        Domain: row.Domain,
        ValuesFrom: row.ValuesFrom,
        Inverse: row.Inverse,
        Phase: row.Phase,
        Visibility: row.Visibility,
        Knowledge: row.Knowledge,
        PhaseOf: row.PhaseOf,
        HistoryCursor: row.HistoryCursor
    ) {
    }
}
