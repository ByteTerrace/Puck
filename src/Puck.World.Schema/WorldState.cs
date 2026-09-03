using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>The closed set of cell value kinds a <see cref="WorldStateRow"/> declares, shared by every cell the row
/// carries. Carries no float kind: simulation state is float-free by the determinism contract (see
/// <see cref="Fixed"/> for how a fractional value still rides here). A counter is represented as
/// <see cref="Fixed"/>; a timer is <see cref="Int"/> with <see cref="WorldStateRow.NonNegative"/> set.</summary>
[JsonConverter(typeof(StrictEnumConverter<CellKind>))]
public enum CellKind : byte {
    /// <summary>A whole 64-bit signed integer cell (a score, a round counter, an inventory count, or — with
    /// <see cref="WorldStateRow.NonNegative"/> set — a tick-count timer).</summary>
    Int,

    /// <summary>A fixed-point cell holding raw <c>FixedQ4816</c> bits — the deterministic replacement for a float in
    /// simulation state. Human-authored surfaces (document JSON, console verb arguments, validator refusal text,
    /// read-back echoes) use the decimal representation via <c>FixedQ4816.TryParse</c>/<c>ToString</c>; only the
    /// addon ABI channel wire and the per-cell mutation payload carry the raw bit pattern.</summary>
    Fixed,

    /// <summary>A boolean cell (a win flag, a toggle). Carries no range — a gauge cannot bind to it.</summary>
    Bool,

    /// <summary>A short-text cell (a status label, a player name slot), bounded to
    /// <see cref="WorldStateCapacity.MaxTextValueLength"/> UTF-16 code units. Carries no range — a gauge cannot bind
    /// to it. The only kind whose value is carried in <see cref="WorldStateCell.Text"/> rather than
    /// <see cref="WorldStateCell.Value"/>.</summary>
    Text,
}
/// <summary>The root <c>state</c> declaration. It is the document's abstract state inventory; compilation through
/// <see cref="WorldStateCatalog"/> describes each lane's typed ownership and storage contract before runtime lowers
/// values into the storage appropriate to their access pattern.</summary>
/// <param name="World">Document-owned cell rows. These remain mutation-addressable through <c>state:&lt;name&gt;</c>.</param>
/// <param name="Body">Per-body ephemeral counters and timers, compiled into each body's bounded ordinal arrays.</param>
/// <param name="Identity">Per-body counters and timers synchronized through the durable identity-document seam.</param>
/// <param name="Lattices">The lattice topologies the section's lattice-shaped rows lie over (see
/// <see cref="WorldStateLatticeTopology"/>).</param>
/// <param name="Social">Optional bounded social-memory policy; learned runtime impressions are checkpoint state, not public cell rows.</param>
public sealed record WorldStateSection(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldStateRow>? World = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ActionStateSlot>? Body = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ActionStateSlot>? Identity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldStateLatticeTopology>? Lattices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSocialPolicy? Social = null
);
/// <summary>
/// One cell of the <c>state</c> section's substrate — a typed value addressed by a stable string <see cref="Key"/>
/// within its carrying <see cref="WorldStateRow"/>. A row whose cells hold exactly one entry keyed
/// <see cref="WorldStateRow.SlotKey"/> is a slot; a row with author-chosen keys is a table.
/// </summary>
/// <param name="Key">The cell's stable string key, unique within its carrying row.</param>
/// <param name="Value">The cell's numeric value for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/> (raw
/// <c>FixedQ4816</c> bits for <see cref="CellKind.Fixed"/>), or its 0/1 encoding for <see cref="CellKind.Bool"/>;
/// ignored for <see cref="CellKind.Text"/>. Always raw-encoded at this layer, never decimal — a human-facing
/// ingress converts before writing here.</param>
/// <param name="Text">The cell's text for <see cref="CellKind.Text"/>; <see langword="null"/> for every other
/// kind.</param>
/// <param name="Advance">This cell's own continuous accumulation trait, or <see langword="null"/> if the value only
/// changes through an explicit write — the keyed counterpart of <see cref="WorldStateRow.Advance"/>, which governs a
/// slot's own cell instead. <see cref="Value"/> is this cell's stored base when present; see
/// <see cref="WorldStateAdvance"/> for the read-side computation. Legitimate only on a cell whose <see cref="Key"/>
/// is not <see cref="WorldStateRow.SlotKey"/> — a scalar row's own accumulation is authored at the row level
/// instead.</param>
/// <param name="Provenance">The identity that minted this cell's current value. A keyed <see cref="CellKind.Int"/>
/// row addressed by a holder's 0-based entity index is an item or currency fact: <see cref="Value"/> is the
/// quantity or balance, and this field names who minted it. <see langword="null"/> means locally self-minted, the
/// only value a single-authority world produces today; a federated authority is expected to populate a real issuer
/// id.</param>
/// <param name="Dynamics">This cell's own second-order easing trait, or <see langword="null"/> for an ordinary cell
/// whose value only changes through an explicit write — the keyed counterpart of
/// <see cref="WorldStateRow.Dynamics"/>, which governs a slot's own cell instead. Legitimate only on a cell whose
/// <see cref="Key"/> is not <see cref="WorldStateRow.SlotKey"/>.</param>
/// <param name="Cycle">This cell's own tick-indexed rotation trait, or <see langword="null"/> for an ordinary cell —
/// the keyed counterpart of <see cref="WorldStateRow.Cycle"/>, which governs a slot's own cell instead. See
/// <see cref="WorldStateCycle"/>; <see cref="Value"/> is the phase (or the lattice node) the trait turns from.
/// Legitimate only on a cell whose <see cref="Key"/> is not <see cref="WorldStateRow.SlotKey"/>.</param>
/// <param name="Visibility">An additional cell-level audience restriction; slot policies belong on the row.</param>
/// <param name="Observation">The persisted last-seen stamp of a knowledge cell.</param>
public sealed record WorldStateCell(WorldCellName Key, long Value = 0, string? Text = null, WorldStateAdvance? Advance = null, string? Provenance = null, WorldStateDynamics? Dynamics = null, WorldStateCycle? Cycle = null, WorldStateVisibility? Visibility = null, WorldStateObservation? Observation = null);
/// <summary>
/// One row of the <c>state</c> section — a named cell or a named collection of cells, addressed by its stable
/// <see cref="Name"/>. <see cref="Name"/> is the <c>UpsertStateRow</c>/<c>RemoveStateRow</c> key, the
/// <c>state:&lt;name&gt;</c> grant subject, and — for a slot-shaped row — the <c>state.&lt;name&gt;</c> HUD binding
/// token. The engine never interprets a row's name, key, or value.
/// </summary>
/// <remarks>
/// A row declares either a bare <c>value</c> — sugar for one cell keyed <see cref="SlotKey"/> — or a <c>cells</c>
/// array of author-keyed cells, never both; carrying both, or a <c>value</c> beside a declared
/// <see cref="Capacity"/>, is refused by name. A row whose <see cref="Cells"/> holds exactly one cell keyed
/// <see cref="SlotKey"/> and declares no <see cref="Capacity"/> is a slot (<see cref="IsSlot"/>).
/// <para><see cref="NonNegative"/> enforces a floor of zero regardless of any authored <see cref="Min"/>. Every
/// consumer that reads this row's cells — including the cross-document write-back channel in
/// <c>Server.WorldOwnedWorlds.Decide</c> — must read this trait off the row rather than assume a floor of its
/// own.</para>
/// </remarks>
/// <param name="Name">The row's stable string name (unique within the section).</param>
/// <param name="Kind">Which cell kind every cell in this row carries.</param>
/// <param name="Min">The row-wide declared lower bound every cell's <see cref="WorldStateCell.Value"/> must satisfy,
/// raw-encoded per <see cref="Kind"/> (raw <c>FixedQ4816</c> bits for <see cref="CellKind.Fixed"/>), or
/// <see langword="null"/> for none. Present only together with <see cref="Max"/> — a range is authored as a pair or
/// not at all. Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>. Omitted from the wire
/// when null.</param>
/// <param name="Max">The row-wide declared upper bound, raw-encoded per <see cref="Kind"/>, or <see langword="null"/>
/// for none. Present only together with <see cref="Min"/>. Omitted from the wire when null.</param>
/// <param name="Capacity">The row's own cell-count ceiling (<c>1..</c><see cref="WorldStateCapacity.MaxCellsPerRow"/>),
/// or <see langword="null"/> to fall back to the implicit ceiling. A row declaring <see cref="Capacity"/> can never
/// be a slot (<see cref="IsSlot"/>), even if it happens to carry exactly one cell — declaring a capacity is
/// declaring table intent. Omitted from the wire when null.</param>
/// <param name="NonNegative">Whether every cell's value must be non-negative, enforced regardless of any authored
/// <see cref="Min"/>. Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>. A timer is
/// represented as <see cref="CellKind.Int"/> with this set.</param>
/// <param name="GatesDrive">Whether this row is a drive-admission gate. When set, this must be a keyed row
/// (<see cref="IsKeyed"/>) whose per-body cell — keyed by the body's 0-based entity index — is consulted before
/// admitting a drive or action intent for that body: a nonzero cell refuses the body's intents until the cell reads
/// zero again, checked fresh every tick. The engine does not interpret the row's name; several independently named
/// gate rows may exist at once, any one of which can refuse. Legitimate only on a row declaring
/// <see cref="Capacity"/>, and only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>/
/// <see cref="CellKind.Bool"/>. Default <see langword="false"/>.</param>
/// <param name="Evicts">Whether this row is a bounded, FIFO-evicting table. Ordinarily a <see cref="Capacity"/> is a
/// hard ceiling and a write that would exceed it is refused by name; with this set, such a write instead succeeds
/// and, if it added a new key past capacity, evicts the row's oldest surviving cell. Eviction runs as a pure
/// function of the candidate cells inside the compose step, so replay reproduces the identical victim, and the
/// dropped key is named on the mutation's apply echo.
/// <para>Eviction is by insertion position, not recency of touch: a new key is appended to the end of
/// <see cref="Cells"/> and eviction always drops index 0. Re-writing an existing key updates it in place without
/// moving it — true FIFO, never LRU.</para>
/// Legitimate only together with a declared <see cref="Capacity"/>. Default <see langword="false"/>.</param>
/// <param name="Cells">The row's current cells (default empty). Refused past its effective capacity, and on a
/// duplicate key, by name — unless <see cref="Evicts"/> is set, in which case a write that would grow past capacity
/// evicts the oldest cell instead of refusing (see <see cref="Evicts"/>). A slot-shaped row (see
/// <see cref="IsSlot"/>) holds exactly one cell keyed <see cref="SlotKey"/>; a keyed row may hold any author-chosen
/// keys except <see cref="SlotKey"/> itself, which is reserved for the <c>value</c> sugar and refused as an
/// authored cell key.</param>
/// <param name="Advance">The row's own (slot-cell) continuous accumulation trait, or <see langword="null"/> for an
/// ordinary row whose slot value only changes through an explicit write. See <see cref="WorldStateAdvance"/>.
/// Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>, only on a scalar (slot-eligible)
/// row, and never together with <see cref="Draw"/> — a row is an authored-randomness draw site or a continuous
/// accumulator, never both. A keyed row's own cells accumulate independently through
/// <see cref="WorldStateCell.Advance"/> instead.</param>
/// <param name="Draw">The row's authored-randomness facet, or <see langword="null"/> for an ordinary row. A row
/// carrying one is a draw site (see <see cref="WorldDraw"/> and <see cref="IsDraw"/>): its slot cell's value is
/// drawn at first fill and at every later <c>generate</c> its <see cref="WorldDraw.Timing"/> admits, from the
/// source the facet either names (<see cref="WorldDraw.Source"/>, a row of the document's <c>generators</c>
/// section) or inlines (<see cref="WorldDraw.Generator"/>). The site's <see cref="Kind"/> must be one the source can
/// write (<see cref="CellKind.Text"/> for a Markov source, <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>
/// for a numeric one); it may declare no <see cref="Capacity"/>, and is mutually exclusive with
/// <see cref="Advance"/>.</param>
/// <param name="DrawCursor">How many samples this site's <see cref="Draw"/> has ever consumed — engine-minted
/// bookkeeping and the position the engine re-seeks to (<c>WorldGeneratorEngine.AdvancesPerSample</c> scales it into
/// <c>Pcg32XshRr</c> advances, so resuming is an exact O(1) advance rather than a replay of the earlier draws).
/// Stored in the document, so <c>world.undo</c>, <c>world.save</c>, and replay rewind a site's draw position with
/// the same whole-document restore that rewinds an ordinary counter. Zero when <see cref="Draw"/> is
/// <see langword="null"/>; refused negative.</param>
/// <param name="DrawDecks">This site's dealt masks — engine-minted bookkeeping a source under a deck
/// <see cref="WorldGeneratorMode"/> carries: one mask per context, by declaration ordinal, for a Markov source; exactly
/// one for a weighted numeric source. Bit <c>i</c> is set when entry <c>i</c> has been dealt. Lives at the site rather
/// than on the source row, which lets two sites reference one declared source and deal independently. Null or empty
/// for a site whose source never deals.</param>
/// <param name="Dynamics">The row's own (slot-cell) second-order easing trait, or <see langword="null"/> for an
/// ordinary row whose slot value only changes through an explicit write. See <see cref="WorldStateDynamics"/>.
/// Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>, only on a scalar (slot-eligible)
/// row, and never together with <see cref="Advance"/> or <see cref="Draw"/>. A keyed row's own cells ease
/// independently through <see cref="WorldStateCell.Dynamics"/> instead.</param>
/// <param name="Lattice">The lattice trait — the row holds one scalar per cell of a named topology (see
/// <see cref="WorldStateLatticeTrait"/>); <see langword="null"/> for a slot/keyed row.</param>
/// <param name="Cycle">The row's own (slot-cell) tick-indexed rotation trait, or <see langword="null"/> for an
/// ordinary row. See <see cref="WorldStateCycle"/>. Legitimate only for <see cref="CellKind.Int"/>/
/// <see cref="CellKind.Fixed"/>, only on a scalar (slot-eligible) row, and never together with
/// <see cref="Advance"/>, <see cref="Dynamics"/>, <see cref="Draw"/> or <see cref="Lattice"/>. A keyed row's own
/// cells turn independently through <see cref="WorldStateCell.Cycle"/> instead.</param>
/// <param name="Board">Discrete topology addressing for this keyed row; absent for ordinary rows.</param>
/// <param name="Tokens">A stable token identity domain.</param>
/// <param name="Zone">Membership and pile order over a token domain.</param>
/// <param name="KeysFrom">The token domain whose keys this attribute row may address.</param>
/// <param name="ValuesFrom">The discrete topology whose cell ordinals this integer attribute row stores.</param>
/// <param name="Phase">A finite participant phase protocol and its persisted progression.</param>
/// <param name="Visibility">An opt-in observation policy; empty readers retains the row at the authority.</param>
/// <param name="Knowledge">The source and visibility mask of a remembered board layer.</param>
/// <param name="PhaseOf">The phase row required on external gameplay transforms that write this row.</param>
public sealed record WorldStateRow(
    WorldCellName Name,
    CellKind Kind,
    long? Min = null,
    long? Max = null,
    int? Capacity = null,
    bool NonNegative = false,
    bool GatesDrive = false,
    bool Evicts = false,
    IReadOnlyList<WorldStateCell>? Cells = null,
    WorldStateAdvance? Advance = null,
    WorldDraw? Draw = null,
    long DrawCursor = 0,
    IReadOnlyList<ClosedBitset256>? DrawDecks = null,
    WorldStateDynamics? Dynamics = null,
    WorldStateLatticeTrait? Lattice = null,
    WorldStateCycle? Cycle = null,
    WorldStateBoard? Board = null,
    WorldStateTokens? Tokens = null,
    WorldStateZone? Zone = null,
    string? KeysFrom = null,
    string? ValuesFrom = null,
    WorldStatePhase? Phase = null, WorldStateVisibility? Visibility = null, WorldStateKnowledge? Knowledge = null, string? PhaseOf = null
) {
    /// <summary>The prefix every engine-minted row or cell name carries, and the one an author may never spell. A
    /// row name starting with it is refused outright (nothing mints a row); a cell key starting with it is refused
    /// unless it is exactly the engine-minted key legitimate for that row's shape — <see cref="SlotKey"/> on a slot.
    /// Enforced by <c>WorldDefinitionValidator</c> at boot, at every live mutation, and on undo-replay.</summary>
    public const string ReservedNamePrefix = "$";

    /// <summary>The reserved cell key a slot-shaped row's one implicit cell carries — the address the authored
    /// <c>value</c> sugar writes to, and never a legal author-chosen cell key (see <see cref="IsSlot"/>). Chosen to
    /// be visually distinct from any string a game would plausibly choose as its own key — legal as a
    /// <see cref="WorldCellName"/> like any other.</summary>
    public static readonly WorldCellName SlotKey = WorldCellName.Parse(candidate: "$value");

    /// <summary>Gets the storage ceiling admitted by the row's shape. Ordinary rows retain the 128-cell ceiling.</summary>
    public int CellCeiling => (Tokens is { } tokens ? Math.Clamp(tokens.Capacity, 1, WorldTopologyCompilation.MaxCells) : (int?)null) ?? ((Board is not null || Zone is not null || KeysFrom is not null) ? WorldTopologyCompilation.MaxCells : WorldStateCapacity.MaxCellsPerRow);
    /// <summary>Gets whether the row accumulates continuously.</summary>
    public bool IsAdvancing => (Advance is not null);
    /// <summary>Gets a value indicating whether this row declares a <see cref="WorldStateDynamics"/> easing trait.</summary>
    public bool IsEasing => (Dynamics is not null);
    /// <summary>Gets a value indicating whether the row's slot cell turns with the tick through a <see cref="WorldStateCycle"/> trait.</summary>
    public bool IsCycling => (Cycle is not null);
    /// <summary>Gets a value indicating whether this row declares a <see cref="WorldDraw"/> — whether it is a draw site.</summary>
    public bool IsDraw => (Draw is not null);
    /// <summary>Gets a value indicating whether this row is keyed — it declares a <see cref="Capacity"/>, carries
    /// more than one cell, or carries its single cell under an author-chosen key. Such a row has no single cell, so
    /// an omitted key beside it addresses nothing: a world rule's <c>compareState</c>/<c>setState</c>/
    /// <c>addState</c>, a <c>generate</c> effect's destination at either scope, and the <c>Generate</c> mutation's
    /// own target all refuse by name here rather than reading the row's first cell.</summary>
    /// <remarks>Not the negation of <see cref="IsSlot"/>: a row carrying no cells at all is not a slot yet is still
    /// slot-addressable, since the first write mints its slot cell exactly as <c>world.state.cell.set</c> does.
    /// <see cref="IsSlot"/> asks whether a single value exists to read; this asks whether an omitted key can address
    /// one.</remarks>
    public bool IsKeyed => ((Board is not null || Tokens is not null || Zone is not null || KeysFrom is not null || Phase is not null) || (Capacity is not null) || (Cells is { Count: > 1 }) || ((Cells is { Count: 1 } cells) && (cells[0].Key != SlotKey)));
    /// <summary>Gets a value indicating whether this row is shaped as a scalar slot — no declared
    /// <see cref="Capacity"/> and exactly one cell keyed <see cref="SlotKey"/>. Drives whether
    /// <c>Puck.World.WorldStateRowJsonConverter</c> writes the row's one cell back as the bare <c>value</c> sugar or
    /// as a <c>cells</c> array, and which read-backs (HUD <c>state.&lt;name&gt;</c> binding, <c>world.state</c>'s
    /// value column) resolve a live value for — a keyed row has no single value to show. A draw site is an ordinary
    /// slot: its one cell holds the drawn value, and its own bookkeeping (<see cref="DrawCursor"/>/
    /// <see cref="DrawDecks"/>) lives in row fields rather than in cells.</summary>
    public bool IsSlot => ((Board is null && Tokens is null && Zone is null && KeysFrom is null && Phase is null) && (Capacity is null) && (Cells is { Count: 1 } cells) && (cells[0].Key == SlotKey));

    /// <summary>Clamps <paramref name="value"/> into this row's declared numeric envelope: the
    /// <see cref="NonNegative"/> floor first, then an authored <see cref="Min"/>/<see cref="Max"/> pair.</summary>
    /// <remarks>Used for reads, never for writes: a computed value clamps through this method, but an explicit write
    /// that falls outside the envelope is refused by <c>WorldDefinitionValidator</c> rather than clamped.
    /// <see cref="WorldStateAdvance.ComputeCurrentValue"/> uses this for its read clamp; <c>WorldServer.FireWorldRuleEffect</c>
    /// uses it only to test whether a rule's write could move the destination, never to alter the value the write
    /// submits.</remarks>
    /// <param name="value">The raw value to clamp, encoded per this row's <see cref="Kind"/>.</param>
    /// <returns>The clamped raw value; <paramref name="value"/> unchanged when this row declares no envelope.</returns>
    public long ClampToEnvelope(long value) {
        var clamped = ((NonNegative && (value < 0L))
            ? 0L
            : value
        );

        if (
            (Min is { } lo) &&
            (Max is { } hi)
        ) {
            clamped = ((clamped < lo)
                ? lo
                : ((clamped > hi)
                    ? hi
                    : clamped
            ));
        }

        return clamped;
    }
    /// <summary>Determines whether this row declares a cell under <paramref name="key"/> — the (row, key) existence
    /// check used by the rule compiler's operand walk, the HUD binding validator, and the <c>world.hud</c> read-back
    /// alike, so an undeclared cell refuses the same way at every door.</summary>
    /// <remarks>Allocation-free and ordinal, like <see cref="WorldDefinitionRows.FindStateRow"/> its callers reach it
    /// through: the HUD path runs this per frame.</remarks>
    /// <param name="key">The cell key to look for.</param>
    /// <returns><see langword="true"/> when the row declares a cell under that key.</returns>
    public bool HasCell(string key) => (WorldCellName.TryParse(
        candidate: key,
        name: out var cellKey,
        reason: out _
    ) && (WorldDefinitionRows.FindCell(
        cells: Cells,
        key: cellKey
    ) is not null));
}
/// <summary>
/// A <see cref="WorldStateRow"/>'s continuous accumulation trait: the row's stored cell is a base value, and the
/// read value advances with elapsed ticks at an exact per-tick rational rate from the tick it was last explicitly
/// set (<see cref="EpochTick"/>). Used for regen, fractional accumulation, a day/night clock — anything that should
/// move on its own between observations.
/// </summary>
/// <remarks>
/// <para>Nothing per-tick materializes or journals: the computed value (<see cref="ComputeCurrentValue"/>) is a pure
/// function of the base, <see cref="EpochTick"/>, the rate, and the tick asked about. An explicit write —
/// <c>UpsertStateRow</c> re-authoring the row, or a slot-cell <c>UpsertStateCell</c> — rebases: the written value
/// becomes the new base and <see cref="EpochTick"/> becomes the tick the write applied at.</para>
/// <para><see cref="ComputeCurrentValue"/> is applied only by <see cref="WorldStateReader"/>'s central known-cell
/// computation, so both its name and compiled-handle entrances, every aggregate, read-back, rule gate, HUD binding,
/// and arithmetic write resolve an advancing row through the same code. An
/// <c>add</c> against an advancing row adds to what a reader sees, never to the stored base.</para>
/// <para>A rule's own <c>compareState</c> reads an advancing row's live computed value like any other row. A rule's
/// <c>setState</c>/<c>addState</c> effect against an advancing row's slot cell is an explicit write, so it rebases —
/// a rule that writes the same row every tick overrides this trait's accumulation with its own.</para>
/// <para><see cref="RateNumerator"/>/<see cref="RateDenominator"/> is an exact fraction of the row's own displayed
/// unit per tick — the unit its <c>value</c>, <see cref="WorldStateRow.Min"/>, and <see cref="WorldStateRow.Max"/>
/// are authored in, not raw storage. For <see cref="CellKind.Int"/> the two coincide. For
/// <see cref="CellKind.Fixed"/> they do not: <see cref="ComputeCurrentValue"/> scales the numerator by
/// <c>2^FixedQ4816.FractionBitCount</c> before allocating via <see cref="Puck.Maths.DiscreteMeasure"/>'s exact
/// rational allocation, so a rate accumulates without rounding drift.</para>
/// <para>The rate may be negative (decay/drain); a negative rate is the exact mirror of its positive twin, not a
/// floor of the signed affine function — <see cref="Puck.Maths.DiscreteMeasure"/> accepts only a non-negative rate,
/// so this type floors the magnitude and negates it. Decay and regen at equal magnitude stay symmetric.</para>
/// <para>A declared <see cref="WorldStateRow.Min"/>/<see cref="WorldStateRow.Max"/> or
/// <see cref="WorldStateRow.NonNegative"/> floor clamps the computed value on every read; it never rewrites the
/// stored base or epoch. A value that must wrap is a <see cref="WorldStateCycle"/>, not an advance.</para>
/// </remarks>
/// <param name="RateNumerator">The per-tick rate's signed numerator, in the row's own displayed unit (see this
/// type's remarks). Negative accumulates downward (decay); zero is declared but inert.</param>
/// <param name="RateDenominator">The per-tick rate's denominator. Refused at zero or below.</param>
/// <param name="EpochTick">The server tick the rate starts accumulating from — the tick the row's base value was
/// last explicitly set, or the loaded document's own authored value for a row never set since. A negative value is
/// refused; in practice this can only be violated by an authored boot document, since every live write rebases to
/// the applying tick before validation sees it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateAdvance(long RateNumerator, long RateDenominator, long EpochTick = 0) {
    /// <summary>Computes <paramref name="row"/>'s current value: <paramref name="baseValue"/> plus the exact
    /// accumulation between <see cref="EpochTick"/> and <paramref name="currentTick"/>, clamped into the row's
    /// declared envelope. A <paramref name="currentTick"/> preceding <see cref="EpochTick"/> reads as zero elapsed
    /// rather than a negative accumulation.</summary>
    /// <param name="row">The carrying row (for its <see cref="CellKind"/> and envelope).</param>
    /// <param name="baseValue">The row's stored raw cell value.</param>
    /// <param name="currentTick">The tick to compute the value as of.</param>
    /// <returns>The computed, envelope-clamped raw value.</returns>
    public long ComputeCurrentValue(WorldStateRow row, long baseValue, ulong currentTick) {
        ArgumentNullException.ThrowIfNull(argument: row);

        var delta = 0L;

        if (
            (RateNumerator != 0) &&
            (currentTick > ((ulong)Math.Max(val1: EpochTick, val2: 0L)))
        ) {
            var scale = ((row.Kind == CellKind.Fixed)
                ? (1L << FixedQ4816.FractionBitCount)
                : 1L
            );
            var elapsed = (currentTick - ((ulong)Math.Max(val1: EpochTick, val2: 0L)));

            if (TryAccumulate(elapsed: elapsed, scale: scale, magnitude: out var magnitude)) {
                delta = ((RateNumerator < 0) ? -magnitude : magnitude);
            }
            else {
                // A magnitude past long.MaxValue can still land inside long once the base is added (a drain from a
                // positive base), so the sum is formed exactly and saturated as a whole rather than the magnitude alone.
                var wide = AccumulatedMagnitude(elapsed: elapsed, scale: scale);
                var exact = (baseValue + ((RateNumerator < 0) ? -wide : wide));

                return row.ClampToEnvelope(value: ((exact > long.MaxValue) ? long.MaxValue : ((exact < long.MinValue) ? long.MinValue : ((long)exact))));
            }
        }

        // A saturating add into long and a clamp after is the same answer as clamping the exact sum: every envelope
        // bound is itself a long, so saturation can only move a value that is already outside every bound, and it
        // moves it to the same side. Stating the envelope once (WorldStateRow.ClampToEnvelope) is what keeps this read
        // clamp and the rule-effect write's "could this move the cell" test from drifting apart.
        var raw = ((delta >= 0L)
            ? ((baseValue > (long.MaxValue - delta)) ? long.MaxValue : (baseValue + delta))
            : ((baseValue < (long.MinValue - delta)) ? long.MinValue : (baseValue + delta)));

        return row.ClampToEnvelope(value: raw);
    }

    // |rate| · scale allocated over the elapsed ticks, as ⌊elapsed · |rate| · scale / denominator⌋ — the exact
    // rational allocation of DiscreteMeasure. The compiled signed-64-bit form answers every read the tick can produce
    // in long arithmetic; the exact form remains behind it for a rate or an elapsed span the bounded representation
    // cannot hold, so the two never disagree on a value, only on cost.
    private bool TryAccumulate(ulong elapsed, long scale, out long magnitude) {
        var compiled = CompiledFor(scale: scale);

        if (
            compiled.IsValid &&
            (elapsed <= long.MaxValue) &&
            compiled.TryAmountBetween(
                amount: out magnitude,
                end: ((long)elapsed),
                start: 0L
            )
        ) {
            return true;
        }

        magnitude = 0L;

        return false;
    }
    private BigInteger AccumulatedMagnitude(ulong elapsed, long scale) =>
        (TryAccumulate(elapsed: elapsed, scale: scale, magnitude: out var magnitude)
            ? magnitude
            : ExactMeasure(scale: scale).AmountBetween(
                end: elapsed,
                start: BigInteger.Zero
            ));
    private CompiledDiscreteMeasure64 CompiledFor(long scale) {
        var cache = m_compiled;

        // The rate fields are the cache key, so a `with` copy that changes the rate recompiles on its first read
        // rather than answering from the copied cache; the holder is one immutable reference, so a concurrent reader
        // sees either the old cache or the new one. Each scale compiles on its first read only.
        if ((cache is null) || (cache.RateNumerator != RateNumerator) || (cache.RateDenominator != RateDenominator)) {
            cache = new CompiledMeasureCache(RateNumerator: RateNumerator, RateDenominator: RateDenominator);
            m_compiled = cache;
        }

        return cache.For(advance: this, scale: scale);
    }
    private DiscreteMeasure ExactMeasure(long scale) =>
        DiscreteMeasure.Rational(
            denominator: RateDenominator,
            numerator: (BigInteger.Abs(value: ((BigInteger)RateNumerator)) * scale)
        );

    /// <summary>Tests equality over the authored members alone; the compiled-measure cache is runtime acceleration
    /// and never part of the record's identity.</summary>
    public bool Equals(WorldStateAdvance? other) =>
        ((other is not null) && (RateNumerator == other.RateNumerator) && (RateDenominator == other.RateDenominator) && (EpochTick == other.EpochTick));
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(value1: RateNumerator, value2: RateDenominator, value3: EpochTick);

    // Runtime acceleration beside the immutable record, excluded from its equality above. Invalid compiled values are
    // cached too, so an exact-only rate does not retry compilation on every read.
    private CompiledMeasureCache? m_compiled;

    private sealed class CompiledMeasureCache(long RateNumerator, long RateDenominator) {
        private CompiledDiscreteMeasure64? m_fixed;
        private CompiledDiscreteMeasure64? m_integer;

        public long RateNumerator { get; } = RateNumerator;
        public long RateDenominator { get; } = RateDenominator;

        public CompiledDiscreteMeasure64 For(WorldStateAdvance advance, long scale) {
            if (scale == FixedQ4816.One.Value) {
                return (m_fixed ??= Compile(advance: advance, scale: scale));
            }

            return (m_integer ??= Compile(advance: advance, scale: scale));
        }
        private static CompiledDiscreteMeasure64 Compile(WorldStateAdvance advance, long scale) {
            _ = advance.ExactMeasure(scale: scale).TryCompileInt64(compiled: out var compiled);

            return compiled;
        }
    }
}
/// <summary>
/// A <see cref="WorldStateCell"/>/<see cref="WorldStateRow"/>'s second-order easing trait: the STORED value stays the
/// TRUTH (what rules, gates, and comparands read), while a read through <c>Puck.World.WorldStateReader.TryReadEased</c>
/// computes a second-order follower's current sample from <see cref="Y0"/>/<see cref="V0"/> at <see cref="EpochTick"/>,
/// chasing the stored value as its target — the closed-form counterpart to <see cref="WorldStateAdvance"/>'s linear
/// accumulation, no per-tick work either. An explicit write REBASES: the trait's <see cref="Y0"/>/<see cref="V0"/>
/// become the eased value and velocity computed AT the write's own tick, and <see cref="EpochTick"/> becomes that tick
/// — the same rebase discipline <see cref="WorldStateAdvance"/>'s own write rule follows, so a retune never jumps.
/// </summary>
/// <param name="Row">The referenced <c>dynamics</c> row name; must resolve.</param>
/// <param name="Y0">The follower's position at <see cref="EpochTick"/> as raw <c>FixedQ4816</c> bits, independent
/// of the carrying row's stored-value kind. Keeping the continuous state fixed-native preserves sub-unit phase when
/// an integer target is rebased.</param>
/// <param name="V0">The follower's velocity at <see cref="EpochTick"/>, per second, as raw
/// <c>FixedQ4816</c> bits. A sub-unit response kick therefore survives an integer-row rebase.</param>
/// <param name="EpochTick">The server tick <see cref="Y0"/>/<see cref="V0"/> were captured at.</param>
public sealed record WorldStateDynamics(string Row, long Y0, long V0, long EpochTick = 0);
/// <summary>What a <see cref="WorldStateCycle"/> cell reads: the rotation as a step count, a fraction of a turn or a
/// unit-rotation component, or the rotation carried along a symmetry-lattice orbit as a node index, its ring or a
/// projected coordinate. The integer outputs belong to <see cref="CellKind.Int"/> cells and the fixed outputs to
/// <see cref="CellKind.Fixed"/> cells.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldCycleOutput>))]
public enum WorldCycleOutput : byte {
    /// <summary>The rotation's step count in <c>[0, order)</c>, one full turn per <see cref="WorldStateCycle.Order"/>
    /// steps — an int cell.</summary>
    Step,
    /// <summary>The rotation as a fraction of one turn, <c>⌊step · 2^16 / order⌋</c> in raw <c>FixedQ4816</c> bits, so
    /// the value wraps once per loop the way <c>render.cycle</c> keys read a row — a fixed cell.</summary>
    Turns,
    /// <summary>The unit rotation's cosine, the real part of the order's root of unity at the step — a fixed cell.</summary>
    Cos,
    /// <summary>The unit rotation's sine, the imaginary part of that root of unity — a fixed cell.</summary>
    Sin,
    /// <summary>The symmetry-lattice node the phase node has been carried to along its orbit under the generator, in
    /// <c>[0, 240)</c> — an int cell whose stored value is the node the walk starts from.</summary>
    Node,
    /// <summary>The current node's projected X coordinate on the plane of eight concentric rings — a fixed cell.</summary>
    ProjectionX,
    /// <summary>The current node's projected Y coordinate on that plane — a fixed cell.</summary>
    ProjectionY,
    /// <summary>The ring, 0..7, the current node lies on — an int cell; constant along the lattice's own cycle, and a
    /// value that moves under a word whose orbits cross rings.</summary>
    Ring,
}
/// <summary>
/// A row's or cell's tick-indexed rotation trait: the value is a pure function of the server tick through a
/// generator of the symmetry lattice's reflection group — <c>Puck.Maths.SymmetryWord</c>, the lattice's own
/// thirty-step cycle when no <see cref="Word"/> is authored — raised to <see cref="Power"/> once per step. The
/// generator's order is the loop's period, derived from the word rather than authored: a word of order twelve is a
/// twelve-position dial, one of order twenty-four a day. Nothing accumulates and nothing is rebased: the mapping is
/// tick-absolute, so a replay, a reconnect, or a fresh read at any tick lands on the same bits.
/// </summary>
/// <remarks>
/// <para>The trait advances one step every <see cref="TicksPerStep"/> ticks from <see cref="EpochTick"/>, so a loop
/// lasts <c>Order · TicksPerStep</c> ticks. The stored cell value is the phase: for the rotation outputs a whole
/// number of steps added to the rotation's own step count, for the lattice outputs the node the orbit walk starts
/// from (reduced into <c>[0, 240)</c>) — both read in the row's own displayed unit, so a <see cref="CellKind.Fixed"/>
/// row's phase is the whole part of its value. An explicit write therefore sets the phase — no epoch moves — and a
/// rule's <c>addState</c> against a cycling cell turns it by whole steps or nodes.</para>
/// <para>The rotation index is <c>(Power · steps + phase) mod Order</c>; <see cref="WorldCycleOutput.Node"/> applies
/// the generator <c>Power · steps</c> times to the phase node. With no word, powers 1, 7, 11 and 13 are the lattice
/// cycle's four rotation planes. A declared envelope clamps the computed value on every read, exactly as it does an
/// advancing row's.</para>
/// <para><c>world.save</c> settles a cycling cell in the serialized projection only: the stored value becomes the
/// current rotation index (or node), the epoch returns to zero, and <see cref="SubstepTicks"/> carries the elapsed
/// portion of the current step, so both the first value and the next transition remain continuous after reload.</para>
/// </remarks>
/// <param name="Word">The generator as a word of reflections — mirror nodes, one to eight, applied first to last —
/// or <see langword="null"/> for the lattice's own cycle. A word that moves no node is refused: it loops nothing.</param>
/// <param name="Power">How many applications of the generator one step is; nonzero, and smaller in magnitude than
/// the generator's order, since a power reduces modulo the order and a multiple of it would be the identity.</param>
/// <param name="Output">What the cell reads; must suit the carrying row's <see cref="CellKind"/>.</param>
/// <param name="TicksPerStep">The server ticks one step lasts; refused at zero or below.</param>
/// <param name="EpochTick">The server tick the step count is measured from; a tick before it reads as step zero. A
/// negative value is refused.</param>
/// <param name="SubstepTicks">Elapsed ticks already accumulated toward the next step at <see cref="EpochTick"/>;
/// must be non-negative and less than <see cref="TicksPerStep"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateCycle(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? Word = null,
    int Power = 1,
    WorldCycleOutput Output = WorldCycleOutput.Step,
    long TicksPerStep = 1,
    long EpochTick = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long SubstepTicks = 0
) {
    // The baked generator and the word it was baked from, resolved on first use; a pure function of Word, so it
    // never enters equality, and a `with` copy that swaps the word re-bakes rather than answering from the copy.
    private SymmetryWord? m_generator;
    private IReadOnlyList<int>? m_generatorWord;

    /// <summary>Gets the baked generator: the authored <see cref="Word"/>, or the lattice's own cycle when none is
    /// authored.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The word is empty, longer than eight letters, or names a letter
    /// outside the node range — shapes the validator refuses first.</exception>
    [JsonIgnore]
    public SymmetryWord Generator {
        get {
            var word = Word;

            if ((m_generator is not { } baked) || !ReferenceEquals(objA: m_generatorWord, objB: word)) {
                baked = Bake(word: word);
                m_generator = baked;
                m_generatorWord = word;
            }

            return baked;
        }
    }
    /// <summary>Gets the loop's period in steps: the generator's order.</summary>
    [JsonIgnore]
    public int Order => Generator.Order;

    /// <summary>Gets a value indicating whether <paramref name="output"/> reads through the symmetry lattice rather
    /// than the bare rotation.</summary>
    public static bool IsLatticeOutput(WorldCycleOutput output) => (output is WorldCycleOutput.Node or WorldCycleOutput.ProjectionX or WorldCycleOutput.ProjectionY or WorldCycleOutput.Ring);
    /// <summary>Gets a value indicating whether <paramref name="output"/> is read by an <see cref="CellKind.Int"/>
    /// cell; every other output is read by a <see cref="CellKind.Fixed"/> cell.</summary>
    public static bool IsIntegerOutput(WorldCycleOutput output) => (output is WorldCycleOutput.Step or WorldCycleOutput.Node or WorldCycleOutput.Ring);

    private static SymmetryWord Bake(IReadOnlyList<int>? word) =>
        ((word is null)
            ? SymmetryWord.Coxeter
            : SymmetryWord.Create(mirrors: [.. word]));

    /// <summary>Returns the stored phase of a cell in whole units — the raw value itself for an <see cref="CellKind.Int"/>
    /// row, the whole part of a <see cref="CellKind.Fixed"/> row's raw value.</summary>
    /// <param name="kind">The carrying row's kind.</param>
    /// <param name="baseValue">The stored raw cell value.</param>
    public static long Phase(CellKind kind, long baseValue) =>
        ((kind == CellKind.Fixed) ? (baseValue >> FixedQ4816.FractionBitCount) : baseValue);
    /// <summary>Resolves the generator without throwing, naming the authoring defect when the word cannot bake.</summary>
    /// <param name="generator">The baked generator, on success.</param>
    /// <param name="reason">Why the word was refused, on failure.</param>
    /// <returns><see langword="true"/> when the word bakes.</returns>
    public bool TryResolveGenerator([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SymmetryWord? generator, out string reason) {
        if (Word is { } letters) {
            if ((letters.Count < 1) || (letters.Count > SymmetryWord.MaximumLength)) {
                generator = null;
                reason = $"word holds {letters.Count} letters — a word is one to {SymmetryWord.MaximumLength} mirror nodes, or omitted for the lattice's own cycle";

                return false;
            }

            for (var index = 0; (index < letters.Count); index++) {
                if ((letters[index] < 0) || (letters[index] >= SymmetryLattice.NodeCount)) {
                    generator = null;
                    reason = $"word[{index}] {letters[index]} is not a symmetry-lattice node 0..{SymmetryLattice.NodeCount - 1}";

                    return false;
                }
            }
        }

        generator = Generator;
        reason = string.Empty;

        return true;
    }
    /// <summary>Returns the rotation index in <c>[0, <see cref="Order"/>)</c> the trait has reached at a tick from a
    /// stored phase — the step count the rotation outputs read.</summary>
    /// <param name="phase">The stored phase, in whole steps (see <see cref="Phase"/>).</param>
    /// <param name="currentTick">The tick to compute as of.</param>
    public int RotationIndex(long phase, ulong currentTick) =>
        ((int)(Rotation(currentTick: currentTick) + phase).FloorModulo(modulus: ((long)Order)));
    /// <summary>Returns the lattice node the trait has reached at a tick from a stored phase node.</summary>
    /// <param name="phaseNode">The stored phase (see <see cref="Phase"/>), a node index reduced into <c>[0, 240)</c>.</param>
    /// <param name="currentTick">The tick to compute as of.</param>
    public int CurrentNode(long phaseNode, ulong currentTick) =>
        Generator.Apply(
            node: ((int)phaseNode.FloorModulo(modulus: ((long)SymmetryLattice.NodeCount))),
            steps: Rotation(currentTick: currentTick)
        );
    /// <summary>Computes the cell's current raw value: the selected <see cref="Output"/> at <paramref name="currentTick"/>
    /// from the stored phase, clamped into the row's declared envelope.</summary>
    /// <param name="row">The carrying row (for its <see cref="CellKind"/> and envelope).</param>
    /// <param name="baseValue">The row's stored raw cell value — the phase.</param>
    /// <param name="currentTick">The tick to compute the value as of.</param>
    /// <returns>The computed, envelope-clamped raw value.</returns>
    public long ComputeCurrentValue(WorldStateRow row, long baseValue, ulong currentTick) {
        ArgumentNullException.ThrowIfNull(argument: row);

        long value;
        var phase = Phase(baseValue: baseValue, kind: row.Kind);

        if (IsLatticeOutput(output: Output)) {
            var node = CurrentNode(currentTick: currentTick, phaseNode: phase);

            value = Output switch {
                WorldCycleOutput.Node => node,
                WorldCycleOutput.Ring => SymmetryLattice.Ring(node: node),
                WorldCycleOutput.ProjectionX => SymmetryLattice.Project(node: node).X.Value,
                _ => SymmetryLattice.Project(node: node).Y.Value,
            };
        }
        else {
            var order = Order;
            var index = RotationIndex(currentTick: currentTick, phase: phase);

            value = Output switch {
                WorldCycleOutput.Step => index,
                WorldCycleOutput.Turns => ((((long)index) << FixedQ4816.FractionBitCount) / order),
                WorldCycleOutput.Cos => CyclicRotation.Rotor(step: index, order: order).Real.Value,
                _ => CyclicRotation.Rotor(step: index, order: order).Imaginary.Value,
            };
        }

        return row.ClampToEnvelope(value: value);
    }
    /// <summary>Returns the raw cell value a settled projection stores so a boot from it reads the current value at
    /// its first tick: the current rotation index, or the current node for a lattice output, in the row's own
    /// encoding.</summary>
    /// <param name="row">The carrying row (for its <see cref="CellKind"/>).</param>
    /// <param name="baseValue">The stored raw cell value.</param>
    /// <param name="currentTick">The tick being settled at.</param>
    public long SettledPhase(WorldStateRow row, long baseValue, ulong currentTick) {
        ArgumentNullException.ThrowIfNull(argument: row);

        var phase = Phase(baseValue: baseValue, kind: row.Kind);
        long settled = (IsLatticeOutput(output: Output)
            ? CurrentNode(currentTick: currentTick, phaseNode: phase)
            : RotationIndex(currentTick: currentTick, phase: phase));

        return ((row.Kind == CellKind.Fixed) ? (settled << FixedQ4816.FractionBitCount) : settled);
    }

    /// <summary>Returns the elapsed remainder within the current step when settling at a tick.</summary>
    public long SettledSubstep(ulong currentTick) {
        var duration = ((ulong)Math.Max(val1: TicksPerStep, val2: 1L));
        var elapsedRemainder = (Elapsed(currentTick: currentTick) % duration);
        var carried = Math.Min(val1: ((ulong)Math.Max(val1: SubstepTicks, val2: 0L)), val2: (duration - 1UL));

        return ((long)((carried + elapsedRemainder) % duration));
    }
    /// <summary>Determines whether another trait spells the same rotation: the same word letter for letter, power,
    /// output, step length, epoch and substep.</summary>
    /// <param name="other">The trait to compare with.</param>
    public bool Equals(WorldStateCycle? other) =>
        (other is not null) &&
        (Power == other.Power) &&
        (Output == other.Output) &&
        (TicksPerStep == other.TicksPerStep) &&
        (EpochTick == other.EpochTick) &&
        (SubstepTicks == other.SubstepTicks) &&
        SameWord(left: Word, right: other.Word);
    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();

        hash.Add(value: Power);
        hash.Add(value: Output);
        hash.Add(value: TicksPerStep);
        hash.Add(value: EpochTick);
        hash.Add(value: SubstepTicks);

        if (Word is { } letters) {
            foreach (var letter in letters) { hash.Add(value: letter); }
        }

        return hash.ToHashCode();
    }

    private static bool SameWord(IReadOnlyList<int>? left, IReadOnlyList<int>? right) {
        if (ReferenceEquals(objA: left, objB: right)) { return true; }
        if ((left is null) || (right is null) || (left.Count != right.Count)) { return false; }

        for (var index = 0; (index < left.Count); index++) {
            if (left[index] != right[index]) { return false; }
        }

        return true;
    }
    // The generator applications reached at a tick: whole steps since the epoch (with the carried substep), reduced
    // modulo the order before the power multiplies them, so no tick count can overflow the arithmetic.
    private long Rotation(ulong currentTick) {
        var duration = ((ulong)Math.Max(val1: TicksPerStep, val2: 1L));
        var elapsed = Elapsed(currentTick: currentTick);
        var carried = Math.Min(val1: ((ulong)Math.Max(val1: SubstepTicks, val2: 0L)), val2: (duration - 1UL));
        var steps = ((elapsed / duration) + (((elapsed % duration) + carried) / duration));

        return (((long)(steps % ((ulong)Order))) * Power);
    }
    private ulong Elapsed(ulong currentTick) {
        var epoch = ((ulong)Math.Max(val1: EpochTick, val2: 0L));
        return ((currentTick <= epoch) ? 0UL : (currentTick - epoch));
    }
}
/// <summary>How a <see cref="WorldGenerator"/>'s entries — a Markov context's alternatives, or a weighted numeric
/// source's outcomes — are consumed: the deck vocabulary. Authored, never inferred: exhaustion behaviour is a
/// declaration, not a fallback the engine picks.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldGeneratorMode>))]
public enum WorldGeneratorMode : byte {
    /// <summary>Every sample leaves the entry set unchanged — the ordinary weighted draw.</summary>
    WithReplacement,

    /// <summary>Each entry may be dealt at most once per pass; a set whose entries are all dealt refuses the whole
    /// emission by name (never a silent stall, never a re-deal).</summary>
    WithoutReplacement,

    /// <summary>Each entry may be dealt at most once per pass; a set whose entries are all dealt clears its deck and
    /// deals again from the full set, deterministically, in the same emission — the shuffle bag.</summary>
    ReshuffleOnExhaustion,
}
/// <summary>One weighted alternative of a <see cref="WorldGeneratorContext"/>: the token it emits, its relative
/// weight, and the context the walk moves into after it is picked. The authored <see cref="Next"/> is what makes this
/// a real Markov process rather than a bag of independent draws — the context key is the process state, so an author
/// folds exactly as much history into it as the chain needs.</summary>
/// <param name="Token">The opaque game-authored token this alternative emits. The engine never interprets it; it is
/// space-joined with the emission's other tokens and written into the target text cell. Bounded by
/// <see cref="WorldGeneratorCapacity.MaxTokenLength"/>.</param>
/// <param name="Weight">The alternative's positive relative weight. At least one alternative in a context must carry
/// a non-zero weight.</param>
/// <param name="Next">The context the walk moves into after this alternative is picked. Must name a declared
/// <see cref="WorldGeneratorContext.Key"/>; naming a context that declares NO alternatives ends the emission (a
/// terminal is a context with nothing to say, never a reserved token spelling).</param>
/// <param name="Count">How many cards of this alternative one deck pass holds, at least one; <see langword="null"/>
/// is one. Under <see cref="WorldGeneratorMode.WithReplacement"/> a count only scales the weight; under a deck mode
/// each card is dealt once per pass. A context's cards total at most <see cref="WorldGeneratorCapacity.MaxCardsPerSet"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGeneratorAlternative(string Token, ulong Weight, WorldCellName Next, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null);
/// <summary>One named context of a <see cref="WorldGenerator"/> — the state the walk may be sitting in and the
/// weighted alternatives it may pick while there. A context declaring NO alternatives is TERMINAL: reaching it ends
/// the emission.</summary>
/// <param name="Key">The stable context key, unique within the generator.</param>
/// <param name="Alternatives">The weighted alternatives out of this context, or empty for a terminal context.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGeneratorContext(WorldCellName Key, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGeneratorAlternative>? Alternatives = null);
/// <summary>The closed vocabulary of a <see cref="WorldGenerator"/>'s draw shape — which of its fields are read, and
/// what one emission produces: a Markov text walk, a deck deal, a uniform range, a weighted numeric table, and a raw
/// stream draw are sources of one family, never parallel primitives with their own seeding, cursoring, and refusal
/// stories.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldGeneratorSource>))]
public enum WorldGeneratorSource : byte {
    /// <summary>The weighted-transition walk over <see cref="WorldGenerator.Contexts"/> — reads
    /// <see cref="WorldGenerator.Start"/>/<see cref="WorldGenerator.Bound"/>/<see cref="WorldGenerator.Contexts"/>/
    /// <see cref="WorldGenerator.Mode"/>, writes text. The only source that deals (see
    /// <see cref="WorldGeneratorMode"/>) and the only one whose emission costs more than one sample.</summary>
    Markov,

    /// <summary>One draw over the closed integer range
    /// <c>[<see cref="WorldGenerator.RangeMin"/>, <see cref="WorldGenerator.RangeMax"/>]</c> — reads only those two
    /// fields, writes an int or fixed value. One fixed-cost advance per draw: a multiply-high map of a
    /// <c>UnitFraction32</c>, rather than the rejection-sampled <c>Pcg32XshRr.NextUInt32(min, max)</c>, which keeps
    /// the cursor seekable. The map is uniform rather than exactly uniform: with <c>n</c> the range's value count,
    /// each outcome claims either <c>⌊2³²/n⌋</c> or <c>⌈2³²/n⌉</c> of the 2³² fractions — a relative deviation of at
    /// most <c>n/2³²</c>, and zero whenever <c>n</c> divides 2³².</summary>
    UniformRange,

    /// <summary>One alias-table draw over <see cref="WorldGenerator.Weighted"/>'s numeric outcomes — reads that field
    /// and <see cref="WorldGenerator.Mode"/>, writes an int or fixed value. Exactly two advances per draw, the same
    /// fixed alias-table cost the Markov walk pays per token; under a deck mode the outcomes are dealt through the
    /// site's one <see cref="WorldStateRow.DrawDecks"/> mask, which is the numeric shuffle bag.</summary>
    WeightedNumeric,

    /// <summary>One raw, unshaped 32-bit draw off the site's own stream — no range, no weights — widened into the
    /// target's raw value as-is. One fixed-cost advance per draw. The unshaped-entropy primitive: no distribution is
    /// applied.</summary>
    StreamDraw,

    /// <summary>One uniform draw over an orbit of the symmetry lattice: the thirty nodes of <see cref="WorldGenerator.Ring"/>,
    /// or the nodes <see cref="WorldGenerator.Node"/> visits under <see cref="WorldGenerator.Word"/> (the lattice's
    /// own cycle when none is authored, so the node's ring) — reads those fields and <see cref="WorldGenerator.Mode"/>,
    /// writes the node index in the site's displayed unit (a fixed site stores <c>node.0</c>, the phase a cycle trait
    /// reads). The cards are the orbit's nodes in walk order, equally weighted, dealt under
    /// a deck mode through the site's one <see cref="WorldStateRow.DrawDecks"/> mask exactly as a weighted numeric
    /// source is; exactly two advances per draw.</summary>
    SymmetryOrbit,
}
/// <summary>One numeric outcome of a <see cref="WorldGeneratorSource.WeightedNumeric"/> source: the raw value it
/// writes and its relative weight — the numeric twin of <see cref="WorldGeneratorAlternative"/>, minus
/// <c>Token</c> (nothing to join into text) and <c>Next</c> (a numeric draw is one terminal pick, never a
/// walk).</summary>
/// <param name="Value">The raw value this outcome writes on selection (a plain integer for
/// <see cref="CellKind.Int"/>, raw <c>FixedQ4816</c> bits for <see cref="CellKind.Fixed"/>).</param>
/// <param name="Weight">The outcome's relative weight, fed straight to <c>Puck.Maths.WeightedSampler</c>'s exact
/// <c>ulong</c> overload. At least one outcome must carry a non-zero weight.</param>
/// <param name="Count">How many cards of this outcome one deck pass holds, at least one; <see langword="null"/> is
/// one. Under <see cref="WorldGeneratorMode.WithReplacement"/> a count only scales the weight; under a deck mode each
/// card is dealt once per pass, so an outcome that should come out twice per pass declares <c>2</c> rather than being
/// authored twice. A source's cards total at most <see cref="WorldGeneratorCapacity.MaxCardsPerSet"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGeneratorWeightedNumeric(long Value, ulong Weight, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null);
/// <summary>
/// An authored stochastic source — the vocabulary for every randomness declaration in the document: a name
/// generator, a dialogue line, a loot roll, a flat weighted draw, a card deal, a random census, and a drawn host
/// backend all reduce to a source of this family, sampled at an authored moment into an authored site (see
/// <see cref="WorldDraw"/>).
/// </summary>
/// <remarks>
/// <para>A source is a pure declaration; it holds no position. The cursor and the dealt decks live on the site that
/// draws (<see cref="WorldStateRow.DrawCursor"/>/<see cref="WorldStateRow.DrawDecks"/>), which is what lets two
/// sites reference one declared source and draw independent sequences from it. A source may be declared once in the
/// document's <c>generators</c> section (see <see cref="WorldGeneratorRow"/>) and referenced by name, or inlined at
/// a single site as sugar — the two spellings compile to the identical record.</para>
/// <para>A Markov emission is one walk: it begins at <see cref="Start"/> and repeats — sample the current context's
/// alternatives, append the picked token, move to its <see cref="WorldGeneratorAlternative.Next"/> — until it
/// reaches a terminal context (one declaring no alternatives). A walk that has emitted <see cref="Bound"/> tokens
/// without terminating refuses the whole emission by name rather than truncating it. A single self-terminating
/// context with <see cref="Bound"/> 1 is the degenerate flat weighted text draw.</para>
/// <para>The other sources are numeric and always exactly one draw — <see cref="Bound"/> is meaningless beside
/// them and must be left at its default. Each source's fields are both-or-neither against the fields the others
/// own: declaring <see cref="Contexts"/> beside <see cref="RangeMin"/> is refused by name rather than silently
/// ignored.</para>
/// <para><see cref="Mode"/> belongs to the alias-table shapes — per context for Markov, over the outcome set for
/// weighted numeric, over the orbit for a symmetry orbit — and persists across emissions in the drawing site's own <see cref="WorldStateRow.DrawDecks"/>
/// masks. <see cref="WorldGeneratorSource.UniformRange"/> and <see cref="WorldGeneratorSource.StreamDraw"/> have no
/// entry set and never deal.</para>
/// </remarks>
/// <param name="Source">Which draw shape this source fires.</param>
/// <param name="Start">Markov only: the context every emission begins from. Must name a declared context.</param>
/// <param name="Bound">Markov only: the maximum tokens one emission may draw before refusing by name,
/// <c>1..</c><see cref="WorldGeneratorCapacity.MaxEmissionBound"/>. Left at <see cref="DefaultBound"/> by a numeric
/// source.</param>
/// <param name="Contexts">Markov only: the declared contexts, at least one, uniquely keyed.</param>
/// <param name="Mode">Markov, weighted numeric and symmetry orbit: how the entries are consumed (see <see cref="WorldGeneratorMode"/>).</param>
/// <param name="RangeMin"><see cref="WorldGeneratorSource.UniformRange"/> only: the closed range's inclusive lower
/// bound — both bounds present or neither. Raw-encoded per the destination site's <see cref="CellKind"/> (raw
/// <c>FixedQ4816</c> bits for a <c>fixed</c> site) — unlike a site row's own <c>min</c>/<c>max</c>, which a
/// <c>fixed</c> row authors as decimal text, since a source is not bound to one site and cannot know the kind it
/// will write.</param>
/// <param name="RangeMax"><see cref="WorldGeneratorSource.UniformRange"/> only: the inclusive upper bound, same
/// encoding as <see cref="RangeMin"/>.</param>
/// <param name="Weighted"><see cref="WorldGeneratorSource.WeightedNumeric"/> only: the weighted numeric outcomes, at
/// least one, at least one carrying a non-zero weight; under a deck <see cref="Mode"/> each outcome contributes
/// <see cref="WorldGeneratorWeightedNumeric.Count"/> cards to the pass.</param>
/// <param name="Ring"><see cref="WorldGeneratorSource.SymmetryOrbit"/> only: the ring, 0..7, whose thirty nodes are
/// the cards — exactly one of <see cref="Ring"/> and <see cref="Node"/>.</param>
/// <param name="Node"><see cref="WorldGeneratorSource.SymmetryOrbit"/> only: the node, 0..239, whose orbit under
/// <see cref="Word"/> is the cards.</param>
/// <param name="Word"><see cref="WorldGeneratorSource.SymmetryOrbit"/> beside <see cref="Node"/> only: the word of
/// reflections (one to eight mirror nodes, applied first to last) the orbit is taken under, or <see langword="null"/>
/// for the lattice's own cycle — the same generator vocabulary a <see cref="WorldStateCycle"/> authors.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGenerator(
    WorldGeneratorSource Source = WorldGeneratorSource.Markov,
    // Each source reads a disjoint field set, so the canonical writer omits the ones this source does not own rather
    // than emitting a wall of nulls a reader has to discount.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCellName? Start = null,
    int Bound = WorldGenerator.DefaultBound,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGeneratorContext>? Contexts = null,
    WorldGeneratorMode Mode = WorldGeneratorMode.WithReplacement,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? RangeMin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? RangeMax = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGeneratorWeightedNumeric>? Weighted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Ring = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Node = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? Word = null
) {
    /// <summary>The <see cref="Bound"/> an undeclared source carries — one emitted token. <see cref="Bound"/> is
    /// Markov-only and not nullable, so "left at its default" is the only reading of "not declared" available to it; a
    /// numeric source carrying anything else is refused against this constant rather than left to parse and then be
    /// ignored.</summary>
    public const int DefaultBound = 1;
}
/// <summary>One row of the document's <c>generators</c> section: a stochastic source declared under a name, so that
/// any number of <see cref="WorldDraw"/> sites may reference it (<see cref="WorldDraw.Source"/>). Declaring a source
/// once and referencing it is what makes an NPC-bark site and a loot site able to share one authored table while
/// still drawing independent sequences — the source carries the shape, each site carries its own position.</summary>
/// <param name="Name">The source's name, unique within the section, and the spelling a site's
/// <see cref="WorldDraw.Source"/> resolves against.</param>
/// <param name="Generator">The source itself.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGeneratorRow(WorldCellName Name, WorldGenerator Generator);
/// <summary>The <see cref="WorldGenerator"/> caps enforced by <see cref="WorldDefinitionValidator"/>.</summary>
/// <remarks>The context cap and the alternative cap are load-bearing together, not decorative: a deck mode records
/// one <see cref="WorldStateRow.DrawDecks"/> mask per context, and each such mask is a 256-bit dealt set (so a
/// context can hold no more alternatives than the membership set has bits).</remarks>
public static class WorldGeneratorCapacity {
    /// <summary>A context's alternative-count ceiling — one bit per alternative in its deck mask.</summary>
    public const int MaxAlternativesPerContext = 256;
    /// <summary>A source's context-count ceiling.</summary>
    public const int MaxContexts = 32;
    /// <summary>The document's declared-source count ceiling.</summary>
    public const int MaxDeclaredSources = 64;
    /// <summary>The declared <see cref="WorldGenerator.Bound"/> ceiling.</summary>
    public const int MaxEmissionBound = 64;
    /// <summary>The greatest value a <see cref="WorldGeneratorSource.UniformRange"/> bound may hold. The draw is a
    /// single fixed-cost multiply-high map whose span must fit a <c>uint</c> without truncation; this bound is what
    /// keeps it there.</summary>
    public const long MaxRangeBound = int.MaxValue;
    /// <summary>One emitted token's length ceiling, in UTF-16 code units (the JOINED emission is separately bounded
    /// by <see cref="WorldStateCapacity.MaxTextValueLength"/>).</summary>
    public const int MaxTokenLength = 64;
    /// <summary>A <see cref="WorldGeneratorSource.WeightedNumeric"/> source's outcome-count ceiling — matches
    /// <see cref="MaxAlternativesPerContext"/> since both build an alias table over an authored entry list.</summary>
    public const int MaxWeightedOutcomes = 256;
    /// <summary>The most cards one dealt set may hold — a context's alternatives or a weighted source's outcomes,
    /// each counted <c>Count</c> times — since a deck mask is one 256-bit set with one bit per card.</summary>
    public const int MaxCardsPerSet = 256;
    /// <summary>The least value a <see cref="WorldGeneratorSource.UniformRange"/> bound may hold — see
    /// <see cref="MaxRangeBound"/>.</summary>
    public const long MinRangeBound = int.MinValue;
}
/// <summary>
/// The rule for a <see cref="WorldStateRow.ReservedNamePrefix"/>-prefixed cell: which reserved keys a row's shape
/// legitimately mints.
/// </summary>
/// <remarks>
/// Stated once here because two doors ask it: the whole-document walk in <c>WorldDefinitionValidator</c> (which runs
/// at boot, at every live mutation, and on every undo-replay entry) and the <c>WorldMutation.UpsertStateCell</c>
/// compose arm, which refuses the same shape by name at the verb rather than letting the operator read a
/// whole-document validation error for a cell they just typed.
/// </remarks>
public static class WorldStateReservedCells {
    /// <summary>Validates one <see cref="WorldStateRow.ReservedNamePrefix"/>-prefixed cell against the row that
    /// carries it.</summary>
    /// <param name="row">The row the cell lives on.</param>
    /// <param name="key">The cell's key (assumed to carry the reserved prefix — an ordinary key is always admitted).</param>
    /// <param name="reason">Why the cell was refused, in the author's own vocabulary, or empty on success.</param>
    /// <returns><see langword="true"/> when the row mints a cell by that key.</returns>
    public static bool TryValidateReservedCell(WorldStateRow row, WorldCellName key, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: row);
        reason = string.Empty;

        if (
            !key.Value.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldStateRow.ReservedNamePrefix
        ) ||
            (key == WorldStateRow.SlotKey)
        ) {
            return true;
        }

        reason = $"carries the reserved prefix '{WorldStateRow.ReservedNamePrefix}' — reserved cell keys are engine-minted, and this row mints none by that name";

        return false;
    }
}
/// <summary>The <c>state</c> section schema caps enforced by <see cref="WorldDefinitionValidator"/>.</summary>
/// <remarks>
/// A HUD gauge element (see <c>WorldHudElementKind.Gauge</c>) may bind to <c>state.&lt;name&gt;</c>, legitimate only
/// for a slot-shaped row (see <see cref="WorldStateRow.IsSlot"/>). A <see cref="CellKind.Int"/>/
/// <see cref="CellKind.Fixed"/> row either carries no <see cref="WorldStateRow.Min"/>/<see cref="WorldStateRow.Max"/>
/// at all, or carries both together with <c>Min &lt; Max</c> and every cell's own value inside <c>[Min, Max]</c> — a
/// half-declared range (one bound present, the other absent) is refused rather than guessed. A gauge bound to a row
/// with no declared range, to a <see cref="CellKind.Bool"/>/<see cref="CellKind.Text"/> row (which carry no range at
/// all), or to a keyed row (no single value to show) draws empty at render time rather than failing validation.
/// </remarks>
public static class WorldStateCapacity {
    /// <summary>The combined body- and identity-state slot ceiling. Compilation allocates fixed parallel arrays of
    /// this authored length per body, so the document gate bounds both memory and checkpoint width before runtime.</summary>
    public const int MaxBodySlots = 128;
    /// <summary>The implicit per-row cell-count ceiling — applies to every <see cref="WorldStateRow.Cells"/>,
    /// slot-shaped or keyed alike (a slot never approaches it: exactly one cell), even when the author omits
    /// <see cref="WorldStateRow.Capacity"/>, so a row can never state no bound at all (unbounded growth is refused by
    /// construction, never by author diligence). An authored <see cref="WorldStateRow.Capacity"/> may only narrow
    /// this, never widen it.</summary>
    public const int MaxCellsPerRow = 128;
    /// <summary>The greatest value a <see cref="CellKind.Int"/> cell may carry — <see cref="FixedQ4816"/>'s own
    /// integer ceiling, because every engine read of an int cell lifts it to fixed point through
    /// <see cref="FixedQ4816.FromInteger(long)"/>, which throws outside this band. Enforced at every ingress — boot
    /// file, console verb, addon decode, rule write-back, undo replay — so an unrepresentable cell is refused by
    /// name at the one validator rather than admitting a value that kills the process on the first tick a rule reads
    /// it. A <see cref="CellKind.Fixed"/> cell carries raw <see cref="FixedQ4816"/> bits and legitimately spans the
    /// whole <see cref="long"/>, so this band is an int-cell rule alone.</summary>
    public const long MaxIntCellValue = (long.MaxValue >> FixedQ4816.FractionBitCount);
    /// <summary>A cell's <see cref="WorldStateCell.Provenance"/> length ceiling, in UTF-16 code units — bounded like
    /// <see cref="MaxTextValueLength"/> since it is likewise a free-form issuer label, never a validated-identifier
    /// type.</summary>
    public const int MaxProvenanceLength = 256;
    /// <summary>The section's row-count ceiling.</summary>
    public const int MaxRows = 128;
    /// <summary>A <see cref="CellKind.Text"/> cell's value-length ceiling, in UTF-16 code units.</summary>
    public const int MaxTextValueLength = 256;
    /// <summary>The least value a <see cref="CellKind.Int"/> cell may carry — see <see cref="MaxIntCellValue"/>.</summary>
    public const long MinIntCellValue = (long.MinValue >> FixedQ4816.FractionBitCount);
}
