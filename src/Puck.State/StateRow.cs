using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>
/// One cell of the <c>state</c> section's substrate — a typed value addressed by a stable string <see cref="Key"/>
/// within its carrying <see cref="StateRow"/>. A row whose cells hold exactly one entry keyed
/// <see cref="StateRow.SlotKey"/> is a slot; a row with author-chosen keys is a table.
/// </summary>
/// <param name="Key">The cell's stable string key, unique within its carrying row.</param>
/// <param name="Value">The cell's numeric value for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/> (raw
/// <c>FixedQ4816</c> bits for <see cref="CellKind.Fixed"/>), or its 0/1 encoding for <see cref="CellKind.Bool"/>;
/// ignored for <see cref="CellKind.Text"/>. Always raw-encoded at this layer, never decimal — a human-facing
/// ingress converts before writing here.</param>
/// <param name="Text">The cell's text for <see cref="CellKind.Text"/>; <see langword="null"/> for every other
/// kind.</param>
/// <param name="Advance">This cell's own continuous accumulation trait, or <see langword="null"/> if the value only
/// changes through an explicit write — the keyed counterpart of <see cref="StateRow.Advance"/>, which governs a
/// slot's own cell instead. <see cref="Value"/> is this cell's stored base when present; see
/// <see cref="StateAdvance"/> for the read-side computation. Legitimate only on a cell whose <see cref="Key"/>
/// is not <see cref="StateRow.SlotKey"/> — a scalar row's own accumulation is authored at the row level
/// instead.</param>
/// <param name="Provenance">The identity that minted this cell's current value. A keyed <see cref="CellKind.Int"/>
/// row addressed by a holder's 0-based entity index is an item or currency fact: <see cref="Value"/> is the
/// quantity or balance, and this field names who minted it. <see langword="null"/> means locally self-minted, the
/// only value a single-authority world produces today; a federated authority is expected to populate a real issuer
/// id.</param>
/// <param name="Dynamics">This cell's own second-order easing trait, or <see langword="null"/> for an ordinary cell
/// whose value only changes through an explicit write — the keyed counterpart of
/// <see cref="StateRow.Dynamics"/>, which governs a slot's own cell instead. Legitimate only on a cell whose
/// <see cref="Key"/> is not <see cref="StateRow.SlotKey"/>.</param>
/// <param name="Cycle">This cell's own tick-indexed rotation trait, or <see langword="null"/> for an ordinary cell —
/// the keyed counterpart of <see cref="StateRow.Cycle"/>, which governs a slot's own cell instead. See
/// <see cref="StateCycle"/>; <see cref="Value"/> is the phase (or the lattice node) the trait turns from.
/// Legitimate only on a cell whose <see cref="Key"/> is not <see cref="StateRow.SlotKey"/>.</param>
/// <param name="Visibility">An additional cell-level audience restriction; slot policies belong on the row.</param>
/// <param name="Observation">The persisted last-seen stamp of a knowledge cell.</param>
public sealed record StateCell(CellName Key, long Value = 0, string? Text = null, StateAdvance? Advance = null, string? Provenance = null, StateDynamics? Dynamics = null, StateCycle? Cycle = null, StateVisibility? Visibility = null, StateObservation? Observation = null);
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
/// <param name="Min">The row-wide declared lower bound every cell's <see cref="StateCell.Value"/> must satisfy,
/// raw-encoded per <see cref="Kind"/> (raw <c>FixedQ4816</c> bits for <see cref="CellKind.Fixed"/>), or
/// <see langword="null"/> for none. Present only together with <see cref="Max"/> — a range is authored as a pair or
/// not at all. Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>. Omitted from the wire
/// when null.</param>
/// <param name="Max">The row-wide declared upper bound, raw-encoded per <see cref="Kind"/>, or <see langword="null"/>
/// for none. Present only together with <see cref="Min"/>. Omitted from the wire when null.</param>
/// <param name="Capacity">The row's own cell-count ceiling (<c>1..</c><see cref="StateCapacity.MaxCellsPerRow"/>),
/// or <see langword="null"/> to fall back to the implicit ceiling. A row declaring <see cref="Capacity"/> can never
/// be a slot (<see cref="IsSlot"/>), even if it happens to carry exactly one cell — declaring a capacity is
/// declaring table intent. Omitted from the wire when null.</param>
/// <param name="NonNegative">Whether every cell's value must be non-negative, enforced regardless of any authored
/// <see cref="Min"/>. Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>. A timer is
/// represented as <see cref="CellKind.Int"/> with this set.</param>
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
/// ordinary row whose slot value only changes through an explicit write. See <see cref="StateAdvance"/>.
/// Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>, only on a scalar (slot-eligible)
/// row, and never together with <see cref="Draw"/> — a row is an authored-randomness draw site or a continuous
/// accumulator, never both. A keyed row's own cells accumulate independently through
/// <see cref="StateCell.Advance"/> instead.</param>
/// <param name="Draw">The row's authored-randomness facet, or <see langword="null"/> for an ordinary row. A row
/// carrying one is a draw site (see <see cref="Draw"/> and <see cref="IsDraw"/>): its slot cell's value is
/// drawn at first fill and at every later <c>generate</c> its <see cref="Draw.Timing"/> admits, from the
/// source the facet either names (<see cref="Draw.Source"/>, a row of the document's <c>generators</c>
/// section) or inlines (<see cref="Draw.Generator"/>). The site's <see cref="Kind"/> must be one the source can
/// write (<see cref="CellKind.Text"/> for a Markov source, <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>
/// for a numeric one); it may declare no <see cref="Capacity"/>, and is mutually exclusive with
/// <see cref="Advance"/>.</param>
/// <param name="DrawCursor">How many samples this site's <see cref="Draw"/> has ever consumed — engine-minted
/// bookkeeping and the position the engine re-seeks to (<c>GeneratorEngine.AdvancesPerSample</c> scales it into
/// <c>Pcg32XshRr</c> advances, so resuming is an exact O(1) advance rather than a replay of the earlier draws).
/// Stored in the document, so <c>world.undo</c>, <c>world.save</c>, and replay rewind a site's draw position with
/// the same whole-document restore that rewinds an ordinary counter. Zero when <see cref="Draw"/> is
/// <see langword="null"/>; refused negative.</param>
/// <param name="DrawnMasks">This site's drawn masks — engine-minted bookkeeping a source under an exhausting
/// <see cref="GeneratorMode"/> carries: one mask per context, by declaration ordinal, for a Markov source; exactly
/// one for a weighted numeric source. Bit <c>i</c> is set when entry <c>i</c> has been drawn. Lives at the site rather
/// than on the source row, which lets two sites reference one declared source and draw independently. Null or empty
/// for a site whose source never exhausts.</param>
/// <param name="Dynamics">The row's own (slot-cell) second-order easing trait, or <see langword="null"/> for an
/// ordinary row whose slot value only changes through an explicit write. See <see cref="StateDynamics"/>.
/// Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>, only on a scalar (slot-eligible)
/// row, and never together with <see cref="Advance"/> or <see cref="Draw"/>. A keyed row's own cells ease
/// independently through <see cref="StateCell.Dynamics"/> instead.</param>
/// <param name="Cycle">The row's own (slot-cell) tick-indexed rotation trait, or <see langword="null"/> for an
/// ordinary row. See <see cref="StateCycle"/>. Legitimate only for <see cref="CellKind.Int"/>/
/// <see cref="CellKind.Fixed"/>, only on a scalar (slot-eligible) row, and never together with
/// <see cref="Advance"/>, <see cref="Dynamics"/> or <see cref="Draw"/>. A keyed row's own
/// cells turn independently through <see cref="StateCell.Cycle"/> instead.</param>
/// <param name="Domain">The row's declared cell domain (see <see cref="StateDomain"/>) —
/// <see langword="null"/> when unauthored, in which case <see cref="InferDomain"/> derives one from
/// <see cref="Cells"/>/<see cref="Capacity"/> exactly as an unauthored row always has.</param>
/// <param name="ValuesFrom">The discrete topology whose cell ordinals this row's integer values name — a
/// value-typing trait, independent of <see cref="Domain"/> (legitimate only alongside a
/// <see cref="StateDomain.KeysOf"/> domain over <see cref="CellKind.Int"/> cells).</param>
/// <param name="Phase">A finite participant phase protocol and its persisted progression.</param>
/// <param name="Visibility">An opt-in observation policy; empty readers retains the row at the authority.</param>
/// <param name="Knowledge">The source and visibility mask of a remembered board layer.</param>
/// <param name="PhaseOf">The phase row required on external gameplay transforms that write this row.</param>
/// <param name="HistoryCursor">How many values have ever been pushed into a <see cref="StateDomain.Ring"/> row —
/// engine bookkeeping that names the next slot (<c>cursor mod capacity</c>) and how much of the ring is filled. Zero
/// without the trait; refused negative.</param>
public record StateRow(
    CellName Name,
    CellKind Kind,
    long? Min = null,
    long? Max = null,
    int? Capacity = null,
    bool NonNegative = false,
    bool Evicts = false,
    IReadOnlyList<StateCell>? Cells = null,
    StateAdvance? Advance = null,
    Draw? Draw = null,
    long DrawCursor = 0,
    IReadOnlyList<ClosedBitset256>? DrawnMasks = null,
    StateDynamics? Dynamics = null,
    StateCycle? Cycle = null,
    StateDomain? Domain = null,
    string? ValuesFrom = null,
    StatePhase? Phase = null, StateVisibility? Visibility = null, StateKnowledge? Knowledge = null, string? PhaseOf = null,
    long HistoryCursor = 0
) {
    /// <summary>The prefix every engine-minted row or cell name carries, and the one an author may never spell. A
    /// row name starting with it is refused outright (nothing mints a row); a cell key starting with it is refused
    /// unless it is exactly the engine-minted key legitimate for that row's shape — <see cref="SlotKey"/> on a slot.
    /// Enforced by <c>WorldDefinitionValidator</c> at boot, at every live mutation, and on undo-replay.</summary>
    public const string ReservedNamePrefix = "$";

    /// <summary>The reserved cell key a slot-shaped row's one implicit cell carries — the address the authored
    /// <c>value</c> sugar writes to, and never a legal author-chosen cell key (see <see cref="IsSlot"/>). Chosen to
    /// be visually distinct from any string a game would plausibly choose as its own key — legal as a
    /// <see cref="CellName"/> like any other.</summary>
    public static readonly CellName SlotKey = CellName.Parse(candidate: "$value");

    /// <summary>Gets the effective domain: the authored <see cref="Domain"/>, or <see cref="InferDomain"/>'s answer
    /// when unauthored.</summary>
    [JsonIgnore]
    public StateDomain EffectiveDomain => (Domain ?? InferDomain());
    /// <summary>Infers the domain an unauthored row carries from its <see cref="Cells"/>/<see cref="Capacity"/>/
    /// <see cref="Phase"/> alone — the same shape a plain row (no <see cref="Domain"/> member at all) has always had,
    /// restated as a case rather than a pair of booleans: a declared <see cref="Capacity"/>, more than one cell, a
    /// single cell under an author-chosen key, or a declared <see cref="Phase"/> trait is <see cref="StateDomain.Keys"/>
    /// — a phase row has no single value to read even before its first participant is admitted; anything else (no
    /// cells yet, or exactly one cell keyed <see cref="SlotKey"/>) is <see cref="StateDomain.Slot"/>. A plain row
    /// therefore authors nothing new by omitting <see cref="Domain"/>.</summary>
    public StateDomain InferDomain() =>
        ((Phase is not null) || (Capacity is not null) || (Cells is { Count: > 1 }) || ((Cells is { Count: 1 } cells) && (cells[0].Key != SlotKey))
            ? StateDomain.Keys.Instance
            : StateDomain.Slot.Instance);
    /// <summary>Gets the storage ceiling admitted by the row's shape: the declared capacity when there is one, else
    /// <see cref="StateCapacity.MaxCellsPerRow"/>, the one cell bound every domain shares.</summary>
    public int CellCeiling => EffectiveDomain switch {
        StateDomain.Ring ring => Math.Clamp(ring.Capacity, 1, StateCapacity.MaxCellsPerRow),
        StateDomain.KeysOf or StateDomain.CellsOf => (Capacity is { } linked ? Math.Clamp(linked, 1, StateCapacity.MaxCellsPerRow) : StateCapacity.MaxCellsPerRow),
        _ => (Capacity is { } capacity ? Math.Clamp(capacity, 1, StateCapacity.MaxCellsPerRow) : StateCapacity.DefaultCellRoom),
    };
    /// <summary>Gets whether the row accumulates continuously.</summary>
    public bool IsAdvancing => (Advance is not null);
    /// <summary>Gets a value indicating whether this row declares a <see cref="StateDynamics"/> easing trait.</summary>
    public bool IsEasing => (Dynamics is not null);
    /// <summary>Gets a value indicating whether the row's slot cell turns with the tick through a <see cref="StateCycle"/> trait.</summary>
    public bool IsCycling => (Cycle is not null);
    /// <summary>Gets a value indicating whether this row declares a <see cref="Draw"/> — whether it is a draw site.</summary>
    public bool IsDraw => (Draw is not null);
    /// <summary>Gets a value indicating whether this row is keyed — its domain is anything but
    /// <see cref="StateDomain.Slot"/>. Such a row has no single cell, so an omitted key beside it addresses
    /// nothing: a world rule's <c>compareState</c>/<c>setState</c>/<c>addState</c>, a <c>generate</c> effect's
    /// destination at either scope, and the <c>Generate</c> mutation's own target all refuse by name here rather
    /// than reading the row's first cell.</summary>
    /// <remarks>Not the negation of <see cref="IsSlot"/> in general, but the domain switch makes the two exhaustive
    /// and complementary by construction: every case that is not <see cref="StateDomain.Slot"/> addresses no
    /// omitted key, and slot addresses exactly one — including a row carrying no cells at all yet, since the first
    /// write mints its slot cell exactly as <c>world.state.cell.set</c> does.</remarks>
    public bool IsKeyed => (EffectiveDomain is not StateDomain.Slot);
    /// <summary>Gets a value indicating whether this row is shaped as a scalar slot. Drives whether
    /// <c>Puck.World.WorldStateRowJsonConverter</c> writes the row's one cell back as the bare <c>value</c> sugar or
    /// as a <c>cells</c> array, and which read-backs (HUD <c>state.&lt;name&gt;</c> binding, <c>world.state</c>'s
    /// value column) resolve a live value for — a keyed row has no single value to show. A draw site is an ordinary
    /// slot: its one cell holds the drawn value, and its own bookkeeping (<see cref="DrawCursor"/>/
    /// <see cref="DrawnMasks"/>) lives in row fields rather than in cells.</summary>
    public bool IsSlot => (EffectiveDomain is StateDomain.Slot);

    /// <summary>Clamps <paramref name="value"/> into this row's declared numeric envelope: the
    /// <see cref="NonNegative"/> floor first, then an authored <see cref="Min"/>/<see cref="Max"/> pair.</summary>
    /// <remarks>Used for reads, never for writes: a computed value clamps through this method, but an explicit write
    /// that falls outside the envelope is refused by <c>WorldDefinitionValidator</c> rather than clamped.
    /// <see cref="StateAdvance.ComputeCurrentValue"/> uses this for its read clamp; <c>WorldServer.FireWorldRuleEffect</c>
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
    /// <remarks>Allocation-free and ordinal, like <see cref="StateRows.FindStateRow"/> its callers reach it
    /// through: the HUD path runs this per frame.</remarks>
    /// <param name="key">The cell key to look for.</param>
    /// <returns><see langword="true"/> when the row declares a cell under that key.</returns>
    public bool HasCell(string key) => (CellName.TryParse(
        candidate: key,
        name: out var cellKey,
        reason: out _
    ) && (StateRows.FindCell(
        cells: Cells,
        key: cellKey
    ) is not null));
}
/// <summary>
/// The rule for a <see cref="StateRow.ReservedNamePrefix"/>-prefixed cell: which reserved keys a row's shape
/// legitimately mints.
/// </summary>
/// <remarks>
/// Stated once here because two doors ask it: the whole-document walk in <c>WorldDefinitionValidator</c> (which runs
/// at boot, at every live mutation, and on every undo-replay entry) and the <c>WorldMutation.UpsertStateCell</c>
/// compose arm, which refuses the same shape by name at the verb rather than letting the operator read a
/// whole-document validation error for a cell they just typed.
/// </remarks>
public static class StateReservedCells {
    /// <summary>Validates one <see cref="StateRow.ReservedNamePrefix"/>-prefixed cell against the row that
    /// carries it.</summary>
    /// <param name="row">The row the cell lives on.</param>
    /// <param name="key">The cell's key (assumed to carry the reserved prefix — an ordinary key is always admitted).</param>
    /// <param name="reason">Why the cell was refused, in the author's own vocabulary, or empty on success.</param>
    /// <returns><see langword="true"/> when the row mints a cell by that key.</returns>
    public static bool TryValidateReservedCell(StateRow row, CellName key, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: row);
        reason = string.Empty;

        if (
            !key.Value.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: StateRow.ReservedNamePrefix
        ) ||
            (key == StateRow.SlotKey)
        ) {
            return true;
        }

        reason = $"carries the reserved prefix '{StateRow.ReservedNamePrefix}' — reserved cell keys are engine-minted, and this row mints none by that name";

        return false;
    }
}
/// <summary>The <c>state</c> section schema caps the document validator enforces.</summary>
/// <remarks>
/// A HUD gauge element (see <c>WorldHudElementKind.Gauge</c>) may bind to <c>state.&lt;name&gt;</c>, legitimate only
/// for a slot-shaped row (see <see cref="StateRow.IsSlot"/>). A <see cref="CellKind.Int"/>/
/// <see cref="CellKind.Fixed"/> row either carries no <see cref="StateRow.Min"/>/<see cref="StateRow.Max"/>
/// at all, or carries both together with <c>Min &lt; Max</c> and every cell's own value inside <c>[Min, Max]</c> — a
/// half-declared range (one bound present, the other absent) is refused rather than guessed. A gauge bound to a row
/// with no declared range, to a <see cref="CellKind.Bool"/>/<see cref="CellKind.Text"/> row (which carry no range at
/// all), or to a keyed row (no single value to show) draws empty at render time rather than failing validation.
/// </remarks>
public static class StateCapacity {
    /// <summary>The combined body- and identity-state slot ceiling. Compilation allocates fixed parallel arrays of
    /// this authored length per body, so the document gate bounds both memory and checkpoint width before runtime.</summary>
    public const int MaxBodySlots = 128;
    /// <summary>The most attribute keys one zone sort orders by — each key names a declared state row, so a sort
    /// can never carry more keys than <see cref="MaxRows"/> the section holds.</summary>
    public const int MaxSortKeys = MaxRows;
    /// <summary>The implicit per-row cell-count ceiling — applies to every <see cref="StateRow.Cells"/>,
    /// slot-shaped or keyed alike (a slot never approaches it: exactly one cell), even when the author omits
    /// <see cref="StateRow.Capacity"/>, so a row can never state no bound at all (unbounded growth is refused by
    /// construction, never by author diligence). An authored <see cref="StateRow.Capacity"/> may only narrow
    /// this, never widen it.</summary>
    public const int MaxCellsPerRow = TopologyCompilation.MaxCells;
    /// <summary>The growth room a slot- or keys-domain row gets when it authors no <see cref="StateRow.Capacity"/>;
    /// a registry-sized row authors its capacity, up to <see cref="MaxCellsPerRow"/>.</summary>
    public const int DefaultCellRoom = 128;
    /// <summary>A cell's <see cref="StateCell.Provenance"/> length ceiling, in UTF-16 code units — bounded like
    /// <see cref="MaxTextValueLength"/> since it is likewise a free-form issuer label, never a validated-identifier
    /// type.</summary>
    public const int MaxProvenanceLength = 256;
    /// <summary>The section's row-count ceiling — a pure capacity bound on document size and per-tick iteration
    /// cost, never a fixed-size stack buffer or a per-world tunable.</summary>
    public const int MaxRows = 256;
    /// <summary>A <see cref="CellKind.Text"/> cell's value-length ceiling, in UTF-16 code units.</summary>
    public const int MaxTextValueLength = 256;
}
