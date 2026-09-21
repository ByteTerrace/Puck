using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>What <see cref="StateRow.Overflow"/> does with a write <see cref="StateRow.TryAdmitWrite"/> finds
/// outside the row's declared envelope, or that overflows raw 64-bit arithmetic.</summary>
[JsonConverter(typeof(StrictEnumConverter<StateOverflow>))]
public enum StateOverflow : byte {
    /// <summary>Refuse the write by name.</summary>
    Refuse = 0,
    /// <summary>Clamp the write to the crossed bound, or to <see cref="long.MinValue"/>/<see cref="long.MaxValue"/>
    /// on a side with no declared bound.</summary>
    Saturate,
}
/// <summary>Whether a cell's effective value-over-time behavior is its carrying row's own default, or an explicit
/// opt-out — see <see cref="StateCell.Behavior"/> and <see cref="EffectiveBehavior.Resolve"/>.</summary>
[JsonConverter(typeof(StrictEnumConverter<StateCellBehavior>))]
public enum StateCellBehavior : byte {
    /// <summary>The cell carries no override of its own: its effective behavior is whichever of
    /// <see cref="StateRow.Advance"/>/<see cref="StateRow.Dynamics"/>/<see cref="StateRow.Cycle"/> the row
    /// declares, or none at all. The default, and the only legitimate value for a row's own slot cell.</summary>
    Inherit = 0,
    /// <summary>The cell explicitly opts out of its row's default behavior — its effective behavior is none,
    /// regardless of what the row declares. Refused together with the cell's own <see cref="StateCell.Advance"/>/
    /// <see cref="StateCell.Dynamics"/>/<see cref="StateCell.Cycle"/> (a cell that opts out declares no trait of
    /// its own either) and on the reserved slot key (see <see cref="StateRow.SlotKey"/>) — nothing for a slot's
    /// one cell to opt out of beyond simply not declaring a row trait.</summary>
    None,
}
/// <summary>
/// A cell's own value-over-time timing state — the epoch(s), the second-order follower's sampled position and
/// velocity, and a rotation's carried substep remainder — moved off the trait records
/// (<see cref="StateAdvance"/>/<see cref="StateDynamics"/>/<see cref="StateCycle"/>, which keep only their
/// authored parameters) onto the cell they time, so a key a write mints later starts its own clock from the tick
/// it was created rather than sharing one baked into a trait every cell of the row would otherwise repeat.
/// </summary>
/// <remarks>
/// For a slot-shaped row written with the <c>value</c> sugar, the slot cell's clock is authored as a row-level
/// <c>clock</c> member beside <c>value</c> — see <see cref="StateRowJsonConverter{TRow}"/>. Every field defaults
/// to zero, the settled-at-epoch-zero shape <c>world.save</c> and undo/checkpoint restore leave behind; the
/// fields a particular effective behavior does not read are simply ignored (an <see cref="StateAdvance"/> cell
/// never reads <see cref="Y0"/>/<see cref="V0"/>/<see cref="SubstepTicks"/>/<see cref="EpochTick"/>, a
/// <see cref="StateCycle"/> cell never reads <see cref="Y0"/>/<see cref="V0"/>/<see cref="EpochEngineTick"/>).
/// <see cref="EpochTick"/> and <see cref="EpochEngineTick"/> are two independent clocks, never convertible from one
/// another at a world's current simulation rate: <see cref="EpochTick"/> is a simulation-tick coordinate (read by
/// <see cref="StateCycle"/>), and <see cref="EpochEngineTick"/> is an engine-tick coordinate (read by
/// <see cref="StateAdvance"/>) — see <c>WorldServer.CompletedEngineTicks</c> for where the engine-tick coordinate
/// comes from live.
/// </remarks>
/// <param name="EpochTick">The simulation tick this clock's rotation is measured from — the tick the cell's base
/// value was last explicitly set, or its own behavior last changed. Read by <see cref="StateCycle"/>. A negative
/// value is refused; in practice this can only be violated by an authored boot document, since every live settle
/// rebases to the applying tick before validation sees it.</param>
/// <param name="EpochEngineTick">The engine tick (<see cref="Puck.Maths.FixedTickConversion.TicksPerSecond"/> per
/// second) this clock's accumulation is measured from — the engine tick the cell's base value was last explicitly
/// set, or its own behavior last changed. Read by <see cref="StateAdvance"/> alone; never derived from
/// <see cref="EpochTick"/> at a simulation rate, since a live rate change must move neither. A negative value is
/// refused, for the same reason <see cref="EpochTick"/> is.</param>
/// <param name="Y0">A <see cref="StateDynamics"/> follower's position at <see cref="EpochTick"/>, as raw
/// <c>FixedQ4816</c> bits, independent of the carrying row's stored-value kind.</param>
/// <param name="V0">A <see cref="StateDynamics"/> follower's velocity at <see cref="EpochTick"/>, per second, as
/// raw <c>FixedQ4816</c> bits.</param>
/// <param name="SubstepTicks">Elapsed ticks a <see cref="StateCycle"/> has already accumulated toward its next
/// step at <see cref="EpochTick"/>; must be non-negative and less than the cycle's own <c>ticksPerStep</c>.</param>
public sealed record StateCellClock(long EpochTick = 0, long EpochEngineTick = 0, long Y0 = 0, long V0 = 0, long SubstepTicks = 0);
/// <summary>
/// One cell of the <c>state</c> section's substrate — a typed value addressed by a stable string <see cref="Key"/>
/// within its carrying <see cref="StateRow"/>. A row whose cells hold exactly one entry keyed
/// <see cref="StateRow.SlotKey"/> is a slot; a row with author-chosen keys is a table.
/// </summary>
/// <param name="Key">The cell's stable string key, unique within its carrying row.</param>
/// <param name="Value">The cell's carried value, one case for every <see cref="CellKind"/> — see
/// <see cref="CellValue"/>. Its own <see cref="CellValue.Kind"/> must agree with the carrying row's
/// <see cref="StateRow.Kind"/> (see <see cref="StateRow.TryAdmitKind"/>); there is no implicit conversion between
/// cases, since a raw number means <see cref="CellKind.Int"/> in one row and Q48.16 bits in another.</param>
/// <param name="Advance">This cell's own continuous accumulation trait, replacing its row's <see cref="StateRow.Advance"/>
/// default wholesale, or <see langword="null"/> to inherit that default (see <see cref="EffectiveBehavior.Resolve"/>).
/// <see cref="Value"/> is this cell's stored base when the effective behavior is advancing; see
/// <see cref="StateAdvance"/> for the read-side computation and <see cref="StateCellClock"/> for where its epoch
/// lives. Legitimate only on a cell whose <see cref="Key"/> is not <see cref="StateRow.SlotKey"/> — a slot's one
/// cell has no separate override to declare; its behavior is entirely its row's.</param>
/// <param name="Provenance">The identity that minted this cell's current value. A keyed <see cref="CellKind.Int"/>
/// row addressed by a holder's 0-based entity index is an item or currency fact: <see cref="Value"/> is the
/// quantity or balance, and this field names who minted it. <see langword="null"/> means locally self-minted, the
/// only value a single-authority world produces today; a federated authority is expected to populate a real issuer
/// id.</param>
/// <param name="Dynamics">This cell's own second-order easing trait, replacing its row's <see cref="StateRow.Dynamics"/>
/// default wholesale, or <see langword="null"/> to inherit that default. Legitimate only on a cell whose
/// <see cref="Key"/> is not <see cref="StateRow.SlotKey"/>.</param>
/// <param name="Cycle">This cell's own tick-indexed rotation trait, replacing its row's <see cref="StateRow.Cycle"/>
/// default wholesale, or <see langword="null"/> to inherit that default. See <see cref="StateCycle"/>;
/// <see cref="Value"/> is the phase (or the lattice node) the trait turns from. Legitimate only on a cell whose
/// <see cref="Key"/> is not <see cref="StateRow.SlotKey"/>.</param>
/// <param name="Behavior"><see cref="StateCellBehavior.None"/> opts this cell out of its row's default behavior
/// entirely (effective behavior none, regardless of what the row declares); the default, <see cref="StateCellBehavior.Inherit"/>,
/// leaves the row's default — or this cell's own <see cref="Advance"/>/<see cref="Dynamics"/>/<see cref="Cycle"/>,
/// when declared — in effect. Refused together with any of those three, and on the reserved slot key.</param>
/// <param name="Clock">This cell's own value-over-time timing state (epoch, a follower's sampled position/velocity,
/// a rotation's substep remainder), or <see langword="null"/> for a cell whose effective behavior has never
/// settled anywhere but tick zero. See <see cref="StateCellClock"/>.</param>
/// <param name="Visibility">An additional cell-level audience restriction; slot policies belong on the row.</param>
/// <param name="Observation">The persisted last-seen stamp of a knowledge cell.</param>
public sealed record StateCell(CellName Key, CellValue Value, StateAdvance? Advance = null, string? Provenance = null, StateDynamics? Dynamics = null, StateCycle? Cycle = null, StateCellBehavior Behavior = StateCellBehavior.Inherit, StateCellClock? Clock = null, StateVisibility? Visibility = null, StateObservation? Observation = null);
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
/// <para>A timer is a <see cref="CellKind.Int"/> row declaring <see cref="Min"/> zero. Every consumer that reads
/// this row's cells — including the cross-document write-back channel in the document
/// project's identity-write door — must read <see cref="TryAdmitWrite"/>'s decision rather than assume an envelope
/// of its own.</para>
/// </remarks>
/// <param name="Name">The row's stable string name (unique within the section).</param>
/// <param name="Kind">Which cell kind every cell in this row carries.</param>
/// <param name="Min">The row-wide declared lower bound every cell's <see cref="StateCell.Value"/> must satisfy,
/// raw-encoded per <see cref="Kind"/> (raw <c>FixedQ4816</c> bits for <see cref="CellKind.Fixed"/>), or
/// <see langword="null"/> for none. Independent of <see cref="Max"/> — a one-sided range (a floor with no ceiling,
/// or the reverse) is legal; when both are present <see cref="Min"/> must be less than <see cref="Max"/>.
/// Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>. Omitted from the wire when null. A
/// timer is represented as <see cref="CellKind.Int"/> with this at zero.</param>
/// <param name="Max">The row-wide declared upper bound, raw-encoded per <see cref="Kind"/>, or <see langword="null"/>
/// for none. Independent of <see cref="Min"/>; see its remarks. Omitted from the wire when null.</param>
/// <param name="Capacity">The row's own cell-count ceiling (<c>1..</c><see cref="StateCapacity.MaxCellsPerRow"/>),
/// or <see langword="null"/> to fall back to the implicit ceiling. A row declaring <see cref="Capacity"/> can never
/// be a slot (<see cref="IsSlot"/>), even if it happens to carry exactly one cell — declaring a capacity is
/// declaring table intent. Omitted from the wire when null.</param>
/// <param name="Overflow">What a write that would leave <see cref="Min"/>/<see cref="Max"/>, or that overflows raw
/// 64-bit arithmetic, does: refuse by name (<see cref="StateOverflow.Refuse"/>, the default — including a row that
/// declares no envelope at all, which still refuses a genuine arithmetic overflow rather than wrapping), or clamp
/// to the crossed bound, or to <see cref="long.MinValue"/>/<see cref="long.MaxValue"/> on a side with no declared
/// bound (<see cref="StateOverflow.Saturate"/>). Legitimate only for <see cref="CellKind.Int"/>/
/// <see cref="CellKind.Fixed"/>. See <see cref="TryAdmitWrite"/>, the one door every write path decides through.</param>
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
/// <param name="Advance">The row's default continuous accumulation trait — the effective behavior of every cell
/// that does not declare its own (see <see cref="EffectiveBehavior.Resolve"/>), including a key a write mints
/// later. See <see cref="StateAdvance"/>. Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>,
/// and never together with <see cref="Draw"/> — a row is an authored-randomness draw site or a continuous
/// accumulator, never both. A cell replaces this default wholesale with its own <see cref="StateCell.Advance"/>,
/// or opts out with <see cref="StateCell.Behavior"/>.</param>
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
/// <param name="Dynamics">The row's default second-order easing trait — the effective behavior of every cell that
/// does not declare its own. See <see cref="StateDynamics"/>. Legitimate only for <see cref="CellKind.Int"/>/
/// <see cref="CellKind.Fixed"/>, and never together with <see cref="Advance"/> or <see cref="Draw"/>. A cell
/// replaces this default wholesale with its own <see cref="StateCell.Dynamics"/>, or opts out with
/// <see cref="StateCell.Behavior"/>.</param>
/// <param name="Cycle">The row's default tick-indexed rotation trait — the effective behavior of every cell that
/// does not declare its own. See <see cref="StateCycle"/>. Legitimate only for <see cref="CellKind.Int"/>/
/// <see cref="CellKind.Fixed"/>, and never together with <see cref="Advance"/>, <see cref="Dynamics"/> or
/// <see cref="Draw"/>. A cell replaces this default wholesale with its own <see cref="StateCell.Cycle"/>, or
/// opts out with <see cref="StateCell.Behavior"/>.</param>
/// <param name="Domain">The row's declared cell domain (see <see cref="StateDomain"/>) —
/// <see langword="null"/> when unauthored, in which case <see cref="InferDomain"/> derives one from
/// <see cref="Cells"/>/<see cref="Capacity"/> exactly as an unauthored row always has.</param>
/// <param name="ValuesFrom">The discrete topology whose cell ordinals this row's integer values name — a
/// value-typing trait, independent of <see cref="Domain"/> (legitimate only alongside a
/// <see cref="StateDomain.KeysOf"/> domain over <see cref="CellKind.Int"/> cells).</param>
/// <param name="Inverse">Declares this row a board derived from a token row's current cells rather than authored
/// directly — see <see cref="StateInverse"/>. Legitimate only alongside a <see cref="StateDomain.CellsOf"/> domain
/// over <see cref="CellKind.Int"/> cells.</param>
/// <param name="Phase">A finite participant phase protocol and its persisted progression.</param>
/// <param name="Visibility">An opt-in observation policy; empty readers retains the row at the authority.</param>
/// <param name="Knowledge">The source and visibility mask of a remembered board layer.</param>
/// <param name="PhaseOf">The phase row required on external gameplay transforms that write this row.</param>
/// <param name="HistoryCursor">How many values have ever been pushed into a <see cref="StateDomain.Ring"/> row —
/// engine bookkeeping that names the next slot (<c>cursor mod capacity</c>) and how much of the ring is filled. Zero
/// without the trait; refused negative.</param>
/// <param name="Space">The vector space this row belongs to; required for <see cref="CellKind.Vector"/>, refused for every other kind.</param>
/// <param name="Enum">The <see cref="StateEnum"/> this row's integer values name, or <see langword="null"/> for a
/// row whose values carry no symbolic domain. Legitimate only for <see cref="CellKind.Int"/> cells, and only
/// naming an enum the section declares.</param>
/// <param name="HostOwned">Whether a host facet serves this row instead of the store. A host-owned row has a
/// descriptor and no storage: rules read it through the facet, no rule writes it, and its owner — not the store's
/// hash — covers it. Legitimate only for <see cref="RowShape.Slot"/> and <see cref="RowShape.Lattice"/> rows, which
/// a host can serve without an ordering contract. It has no wire form: the document project derives it.</param>
public record StateRow(
    CellName Name,
    CellKind Kind,
    long? Min = null,
    long? Max = null,
    int? Capacity = null,
    StateOverflow Overflow = StateOverflow.Refuse,
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
    StateInverse? Inverse = null,
    StatePhase? Phase = null, StateVisibility? Visibility = null, StateKnowledge? Knowledge = null, string? PhaseOf = null,
    long HistoryCursor = 0,
    string? Space = null,
    CellName? Enum = null,
    bool HostOwned = false
) {
    /// <summary>Checks that every value a code row can admit is also a value a derived board can store.</summary>
    /// <param name="board">The derived board.</param>
    /// <param name="boardSymbols">The board's enum, or <see langword="null"/>.</param>
    /// <param name="codes">The inverse codes row.</param>
    /// <param name="codeSymbols">The codes row's enum, or <see langword="null"/>.</param>
    /// <param name="reason">Why the inverse domain is not safe, or empty.</param>
    /// <returns><see langword="true"/> when the code domain is a subset of the board domain and the board empty
    /// value is admitted.</returns>
    public static bool TryProveDerivedDomain(StateRow board, StateEnum? boardSymbols, StateRow codes, StateEnum? codeSymbols, out string reason) {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(codes);

        if (
            ((board.Kind != CellKind.Int) && (board.Kind != CellKind.Bool)) ||
            (codes.Kind != CellKind.Int)
        ) {
            reason = $"derived board '{board.Name.Value}' must be an integer or bool row and inverse codes '{codes.Name.Value}' must be an integer row";

            return false;
        }
        if (board.EffectiveDomain is not StateDomain.CellsOf boardDomain) {
            reason = $"derived board '{board.Name.Value}' does not declare a cellsOf domain";

            return false;
        }
        if (!TryAdmitDerivedValue(
            row: board,
            symbols: boardSymbols,
            value: boardDomain.Empty,
            out reason
        )) {
            reason = $"derived board '{board.Name.Value}' empty value {boardDomain.Empty} {reason}";

            return false;
        }

        var boardLower = ((board.Kind == CellKind.Bool) ? 0L : (board.Min ?? long.MinValue));
        var boardUpper = ((board.Kind == CellKind.Bool) ? 1L : (board.Max ?? long.MaxValue));
        var codeLower = (codes.Min ?? long.MinValue);
        var codeUpper = (codes.Max ?? long.MaxValue);

        if (boardSymbols is { } boardEnum) {
            boardLower = Math.Max(val1: boardLower, val2: 0L);
            boardUpper = Math.Min(val1: boardUpper, val2: (boardEnum.Count - 1L));
        }
        if (codeSymbols is { } codeEnum) {
            codeLower = Math.Max(val1: codeLower, val2: 0L);
            codeUpper = Math.Min(val1: codeUpper, val2: (codeEnum.Count - 1L));
        }

        if ((boardLower > boardUpper) || (codeLower > codeUpper)) {
            reason = $"derived board '{board.Name.Value}' or inverse codes '{codes.Name.Value}' has an empty admitted value domain";

            return false;
        }

        if (
            (codeLower < boardLower) ||
            (codeUpper > boardUpper)
        ) {
            reason = $"derived board '{board.Name.Value}' admits {DescribeRange(lower: boardLower, upper: boardUpper)}, but inverse codes '{codes.Name.Value}' can admit {DescribeRange(lower: codeLower, upper: codeUpper)}";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    private static bool TryAdmitDerivedValue(StateRow row, StateEnum? symbols, long value, out string reason) {
        if (
            (row.ClampToEnvelope(value: value) != value) ||
            ((symbols is not null) && !symbols.Admits(value: value))
        ) {
            reason = $"is outside its declared value domain";

            return false;
        }

        reason = string.Empty;

        return true;
    }
    private static string DescribeRange(long lower, long upper) => $"{lower}..{upper}";

    /// <summary>Gets a value indicating whether the runtime or a lowering synthesized this row rather than an
    /// author declaring it. A console listing, a HUD binding, the decompiler, and the schema treat a generated row
    /// as implementation detail; the hash and the checkpoint still cover it.</summary>
    public bool Generated { get; init; }

    /// <summary>The prefix every engine-minted row or cell name carries, and the one an author may never spell. A
    /// row name starting with it is refused outright (nothing mints a row); a cell key starting with it is refused
    /// unless the row's own shape mints one by that key (<see cref="MintsReservedCell"/>).
    /// Enforced by the document project's validator at boot, at every live mutation, and on undo-replay.</summary>
    public const string ReservedNamePrefix = "$";

    /// <summary>The reserved cell key a slot-shaped row's one implicit cell carries — the address the authored
    /// <c>value</c> sugar writes to, and never a legal author-chosen cell key (see <see cref="IsSlot"/>). Chosen to
    /// be visually distinct from any string a game would plausibly choose as its own key — legal as a
    /// <see cref="CellName"/> like any other.</summary>
    public static readonly CellName SlotKey = CellName.Parse(candidate: "$value");

    /// <summary>Gets the storage ceiling admitted by the row's shape: the declared capacity when there is one, else
    /// <see cref="StateCapacity.MaxCellsPerRow"/>, the one cell bound every domain shares.</summary>
    public int CellCeiling => EffectiveDomain switch {
        StateDomain.Ring ring => Math.Clamp(
        ring.Capacity,
        1,
        StateCapacity.MaxCellsPerRow
    ),
        StateDomain.KeysOf or StateDomain.CellsOf => ((Capacity is { } linked)
        ? Math.Clamp(
            max: StateCapacity.MaxCellsPerRow,
            min: 1,
            value: linked
        )
        : StateCapacity.MaxCellsPerRow),
        _ => ((Capacity is { } capacity)
        ? Math.Clamp(
            max: StateCapacity.MaxCellsPerRow,
            min: 1,
            value: capacity
        )
        : StateCapacity.DefaultCellRoom),
    };
    /// <summary>Gets the effective domain: the authored <see cref="Domain"/>, or <see cref="InferDomain"/>'s answer
    /// when unauthored.</summary>
    [JsonIgnore]
    public StateDomain EffectiveDomain => (Domain ?? InferDomain());
    /// <summary>Gets whether the row accumulates continuously.</summary>
    public bool IsAdvancing => (Advance is not null);
    /// <summary>Gets a value indicating whether the row's slot cell turns with the tick through a <see cref="StateCycle"/> trait.</summary>
    public bool IsCycling => (Cycle is not null);
    /// <summary>Gets a value indicating whether this row declares a <see cref="Draw"/> — whether it is a draw site.</summary>
    public bool IsDraw => (Draw is not null);
    /// <summary>Gets a value indicating whether this row declares a <see cref="StateDynamics"/> easing trait.</summary>
    public bool IsEasing => (Dynamics is not null);
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
    /// <see cref="StateRowJsonConverter{TRow}"/> writes the row's one cell back as the bare <c>value</c> sugar or
    /// as a <c>cells</c> array, and which read-backs (HUD <c>state.&lt;name&gt;</c> binding, <c>world.state</c>'s
    /// value column) resolve a live value for — a keyed row has no single value to show. A draw site is an ordinary
    /// slot: its one cell holds the drawn value, and its own bookkeeping (<see cref="DrawCursor"/>/
    /// <see cref="DrawnMasks"/>) lives in row fields rather than in cells.</summary>
    public bool IsSlot => (EffectiveDomain is StateDomain.Slot);
    /// <summary>Gets the storage shape this row's <see cref="EffectiveDomain"/> derives — the one shape axis every
    /// store and compiler switches over.</summary>
    [JsonIgnore]
    public RowShape Shape => RowShapes.FromDomain(domain: EffectiveDomain);

    /// <summary>Clamps <paramref name="value"/> into this row's declared <see cref="Min"/>/<see cref="Max"/>
    /// envelope, each bound applied independently when present.</summary>
    /// <remarks>Used for computed reads alone — an advancing, easing, or cycling row's live value — never for
    /// writes: <see cref="TryAdmitWrite"/> is the one door every write path decides through, and it never calls
    /// this method, since a write's admission also depends on <see cref="Overflow"/> and on detecting arithmetic
    /// overflow the way a plain clamp cannot.</remarks>
    /// <param name="value">The raw value to clamp, encoded per this row's <see cref="Kind"/>.</param>
    /// <returns>The clamped raw value; <paramref name="value"/> unchanged on the side(s) this row declares no
    /// bound for.</returns>
    public long ClampToEnvelope(long value) {
        var clamped = value;

        if (
            (Min is { } lo) &&
            (clamped < lo)
        ) {
            clamped = lo;
        }
        if (
            (Max is { } hi) &&
            (clamped > hi)
        ) {
            clamped = hi;
        }

        return clamped;
    }
    /// <summary>Decides one write against this row's declared <see cref="Min"/>/<see cref="Max"/>/<see cref="Overflow"/>
    /// envelope — the one method every write path (the rule frame, mutation compose, ring push, board combine,
    /// write sets, the rule evaluator's no-op test, and the rule compiler's constant checks) calls instead of
    /// composing its own range or overflow test.</summary>
    /// <remarks>The candidate result is computed as a 128-bit intermediate, so a genuine 64-bit arithmetic overflow
    /// is detected rather than silently wrapping. A row declaring no envelope at all still refuses such an
    /// overflow under <see cref="StateOverflow.Refuse"/> (the default); it never reaches
    /// <see cref="StateOverflow.Saturate"/>'s per-side <see cref="long.MinValue"/>/<see cref="long.MaxValue"/>
    /// clamp unless authored to.
    /// <para>The envelope decides first: a saturating row clamps and the clamped value is then admitted against
    /// <paramref name="symbols"/>, so a clamp landing outside the enum refuses rather than storing a value the
    /// enum does not name.</para></remarks>
    /// <param name="current">The cell's stored value before this write.</param>
    /// <param name="operand">The write's operand: the replacement for <see cref="StateWriteKind.Set"/>, or the
    /// addend for <see cref="StateWriteKind.Add"/>.</param>
    /// <param name="write">Set or add.</param>
    /// <param name="stored">The value to store: the exact result when admitted, or the clamped bound under
    /// <see cref="StateOverflow.Saturate"/>; zero when refused.</param>
    /// <param name="reason">Why the write was refused, in the author's own vocabulary, or empty on success.</param>
    /// <param name="symbols">The enum this row names (see <see cref="Enum"/>), or <see langword="null"/> when it
    /// names none.</param>
    /// <returns><see langword="true"/> when the write is admitted, whether stored exactly or saturated.</returns>
    public bool TryAdmitWrite(long current, long operand, StateWriteKind write, out long stored, out string reason, StateEnum? symbols = null) {
        var exact = ((write == StateWriteKind.Add)
            ? (((Int128)current) + operand)
            : ((Int128)operand)
        );
        var lowerBound = ((Int128)(Min ?? long.MinValue));
        var upperBound = ((Int128)(Max ?? long.MaxValue));

        if (
            (exact < lowerBound) ||
            (exact > upperBound)
        ) {
            if (Overflow == StateOverflow.Saturate) {
                var saturated = ((long)((exact < lowerBound)
                    ? lowerBound
                    : upperBound
                ));

                if (
                    (symbols is not null) &&
                    !symbols.TryAdmit(
                    reason: out reason,
                    value: saturated
                )
                ) {
                    stored = 0L;

                    return false;
                }

                stored = saturated;
                reason = string.Empty;

                return true;
            }

            var overflowed = (
                (exact < long.MinValue) ||
                (exact > long.MaxValue)
            );

            stored = 0L;
            reason = (overflowed
                ? "would overflow 64-bit storage"
                : "would leave the row's declared envelope"
            );

            return false;
        }

        var candidate = ((long)exact);

        if (
            (symbols is not null) &&
            !symbols.TryAdmit(
            reason: out reason,
            value: candidate
        )
        ) {
            stored = 0L;

            return false;
        }

        stored = candidate;
        reason = string.Empty;

        return true;
    }
    /// <summary>Decides whether <paramref name="value"/>'s own case is this row's declared <see cref="Kind"/> — the
    /// door every cell-admitting caller (the arena's cell import, a live mint, insert, or write) decides a carried
    /// <see cref="CellValue"/> through before ever touching what it stores, beside <see cref="TryAdmitWrite"/>'s
    /// numeric envelope. A carrier that holds no case at all is refused exactly like one holding the wrong case:
    /// neither is this row's <see cref="Kind"/>.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="reason">Why the value was refused, in the author's own vocabulary, or empty on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> holds this row's own <see cref="Kind"/>.</returns>
    public bool TryAdmitKind(CellValue value, out string reason) {
        if (!value.HasValue) {
            reason = $"row '{Name.Value}' declares kind {Kind}, which a value carrying no case does not satisfy";

            return false;
        }
        if (value.Kind != Kind) {
            reason = $"row '{Name.Value}' declares kind {Kind}, which a {value.Kind} value does not satisfy";

            return false;
        }

        reason = string.Empty;

        return true;
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
    /// <summary>Determines whether this row's own shape mints a cell under a
    /// <see cref="ReservedNamePrefix"/>-prefixed key — the one question <see cref="StateReservedCells"/> asks before
    /// refusing such a cell.</summary>
    /// <param name="key">The reserved-prefix cell key.</param>
    /// <returns><see langword="true"/> when the engine mints a cell by that key on a row of this shape.</returns>
    /// <remarks>The engine row mints exactly one: <see cref="SlotKey"/>. A document project's row adds the reserved
    /// keys its own traits mint, so the reserved-cell rule stays one rule asked at one door rather than a list the
    /// validator and the store each keep.</remarks>
    public virtual bool MintsReservedCell(CellName key) => (key == SlotKey);
    /// <summary>Infers the domain an unauthored row carries from its <see cref="Cells"/>/<see cref="Capacity"/>/
    /// <see cref="Phase"/> alone — the same shape a plain row (no <see cref="Domain"/> member at all) has always had,
    /// restated as a case rather than a pair of booleans: a declared <see cref="Capacity"/>, more than one cell, a
    /// single cell under an author-chosen key, or a declared <see cref="Phase"/> trait is <see cref="StateDomain.Keys"/>
    /// — a phase row has no single value to read even before its first participant is admitted; anything else (no
    /// cells yet, or exactly one cell keyed <see cref="SlotKey"/>) is <see cref="StateDomain.Slot"/>. A plain row
    /// therefore authors nothing new by omitting <see cref="Domain"/>.</summary>
    public StateDomain InferDomain() =>
        (((Phase is not null) || (Capacity is not null) || (Cells is { Count: > 1 }) || ((Cells is { Count: 1 } cells) && (cells[0].Key != SlotKey)))
            ? StateDomain.Keys.Instance
            : StateDomain.Slot.Instance
        );
}
/// <summary>
/// The rule for a <see cref="StateRow.ReservedNamePrefix"/>-prefixed cell: which reserved keys a row's shape
/// legitimately mints.
/// </summary>
/// <remarks>
/// Stated once here because two doors ask it: the whole-document walk in the document project's validator (which runs
/// at boot, at every live mutation, and on every undo-replay entry) and the cell-upsert mutation
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
            row.MintsReservedCell(key: key)
        ) {
            return true;
        }

        reason = $"carries the reserved prefix '{StateRow.ReservedNamePrefix}' — reserved cell keys are engine-minted, and this row mints none by that name";

        return false;
    }
}
/// <summary>The <c>state</c> section schema caps the document validator enforces.</summary>
/// <remarks>
/// A document project's gauge element may bind to <c>state.&lt;name&gt;</c>, legitimate only
/// for a slot-shaped row (see <see cref="StateRow.IsSlot"/>). A <see cref="CellKind.Int"/>/
/// <see cref="CellKind.Fixed"/> row either carries no <see cref="StateRow.Min"/>/<see cref="StateRow.Max"/>
/// at all, or carries both together with <c>Min &lt; Max</c> and every cell's own value inside <c>[Min, Max]</c> — a
/// half-declared range (one bound present, the other absent) is refused rather than guessed. A gauge bound to a row
/// with no declared range, to a <see cref="CellKind.Bool"/>/<see cref="CellKind.Text"/> row (which carry no range at
/// all), or to a keyed row (no single value to show) draws empty at render time rather than failing validation.
/// </remarks>
public static class StateCapacity {
    /// <summary>The most simultaneously live lexical pool-instance bindings in one rule evaluation. Each host
    /// holds a compact handle for every slot, so nested claims and pool sweeps remain bounded without reusing a
    /// global binding such as <c>$each</c>.</summary>
    public const int MaxInstanceBindings = 32;
    /// <summary>The growth room a slot- or keys-domain row gets when it authors no <see cref="StateRow.Capacity"/>;
    /// a registry-sized row authors its capacity, up to <see cref="MaxCellsPerRow"/>.</summary>
    public const int DefaultCellRoom = 128;
    /// <summary>The combined body- and identity-state slot ceiling. Compilation allocates fixed parallel arrays of
    /// this authored length per body, so the document gate bounds both memory and checkpoint width before runtime.</summary>
    public const int MaxBodySlots = 128;
    /// <summary>The implicit per-row cell-count ceiling — applies to every <see cref="StateRow.Cells"/>,
    /// slot-shaped or keyed alike (a slot never approaches it: exactly one cell), even when the author omits
    /// <see cref="StateRow.Capacity"/>, so a row can never state no bound at all (unbounded growth is refused by
    /// construction, never by author diligence). An authored <see cref="StateRow.Capacity"/> may only narrow
    /// this, never widen it.</summary>
    public const int MaxCellsPerRow = TopologyCompilation.MaxCells;
    /// <summary>The per-table ceiling on distinct interned <see cref="CellKey"/> names. Each arena owns its runtime
    /// additions and releases speculative names on rewind. Interning collapses one
    /// name used by many rows to one entry, so this admits sixteen fully disjoint
    /// <see cref="MaxCellsPerRow"/>-wide key sets before a mint refuses by name.</summary>
    public const int MaxCellKeys = (16 * MaxCellsPerRow);
    /// <summary>A <see cref="StateEnum"/>'s member-count ceiling, the widest symbolic domain a row's integer
    /// cells may name. A member is one name the catalog holds; nothing per tick is sized by it.</summary>
    public const int MaxEnumMembers = 1024;
    /// <summary>The section's enum-count ceiling: one name table of at most <see cref="MaxEnumMembers"/> names
    /// each, held once by the catalog.</summary>
    public const int MaxEnums = 256;
    /// <summary>The section's family-count ceiling: one member-ordinal table per family in the catalog. A family's
    /// own size is bounded by <see cref="MaxRows"/>, since its members are rows.</summary>
    public const int MaxFamilies = 256;
    /// <summary>A cell's <see cref="StateCell.Provenance"/> length ceiling, in UTF-16 code units — bounded like
    /// <see cref="MaxTextValueLength"/> since it is likewise a free-form issuer label, never a validated-identifier
    /// type.</summary>
    public const int MaxProvenanceLength = 256;
    /// <summary>The most explicit readers one row or cell visibility may retain.</summary>
    public const int MaxVisibilityReaders = 32;
    /// <summary>The most UTF-16 code units one explicit visibility reader token may retain.</summary>
    public const int MaxVisibilityReaderLength = 256;
    /// <summary>The section's row-count ceiling. A row is one layout record, one key-to-slot map, and one version,
    /// generation and change stamp in every arena over the catalog; its cells are counted by the arena's byte
    /// ceiling (<see cref="ArenaCapacity.MaxBytes"/>), and what a rule does across rows is priced on the work
    /// sheet.</summary>
    public const int MaxRows = 1024;
    /// <summary>A <see cref="CellKind.Text"/> cell's value-length ceiling, in UTF-16 code units: two bytes a unit,
    /// so one text cell holds at most two kibibytes.</summary>
    public const int MaxTextValueLength = 1024;
    /// <summary>The minimum allowed dimensions for a vector embedding space.</summary>
    public const int MinVectorDimensions = 8;
    /// <summary>The maximum allowed dimensions for a vector embedding space.</summary>
    public const int MaxVectorDimensions = 1024;
    /// <summary>The maximum number of vector embedding spaces a world may declare. A space is a name and a
    /// dimension count; the components its rows hold are bounded by <see cref="MaxVectorSectionBytes"/>.</summary>
    public const int MaxVectorSpaces = 64;
    /// <summary>The maximum byte capacity of a single vector row (capacity * dimensions).</summary>
    public const int MaxVectorRowBytes = 65536;
    /// <summary>The maximum total byte capacity of all vector rows across a state section (4 MiB).</summary>
    public const int MaxVectorSectionBytes = 4194304;
    /// <summary>The maximum number of results returned by a nearest vector transform. The transform leases one
    /// candidate per cell of the row it scans, so this bounds the writes one firing makes rather than its
    /// scratch.</summary>
    public const int MaxNearestResults = 256;
    /// <summary>The maximum number of terms in a mix vector transform. The sum is one leased word per dimension
    /// however many terms feed it; each term is one pass over the dimensions, priced on the work sheet.</summary>
    public const int MaxMixTerms = 32;
    /// <summary>The maximum absolute weight magnitude of a term in a mix vector transform.</summary>
    public const int MaxMixWeight = 1000;
}
