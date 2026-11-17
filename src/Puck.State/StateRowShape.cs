using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>The declaration-only rules shared by stored rows and detached declaration descriptors.
/// No cell, cursor, reservation, key table or live reader is part of this shape.</summary>
/// <param name="Name">The stable row name.</param>
/// <param name="Kind">The kind of values the row accepts.</param>
/// <param name="Min">The lower bound in the kind's raw encoding, when declared.</param>
/// <param name="Max">The upper bound in the kind's raw encoding, when declared.</param>
/// <param name="Capacity">The declared cell limit, when present.</param>
/// <param name="Overflow">Whether an out-of-envelope write refuses or saturates.</param>
/// <param name="Evicts">Whether growth at capacity evicts the oldest member.</param>
/// <param name="Advance">The declared continuous accumulation rate.</param>
/// <param name="Dynamics">The symbolic dynamics binding.</param>
/// <param name="Cycle">The declared periodic value program.</param>
/// <param name="Domain">The declared cell domain; detached descriptors resolve source shorthand before copying.</param>
/// <param name="ValuesFrom">The topology whose cell ordinals the values name.</param>
/// <param name="Inverse">The token and code rows deriving this board.</param>
/// <param name="Visibility">The row's declared audience policy.</param>
/// <param name="Knowledge">The source and mask bindings of a remembered layer.</param>
/// <param name="PhaseOf">The required phase protocol's row name.</param>
/// <param name="Space">The vector space name.</param>
/// <param name="Enum">The integer values' symbolic domain.</param>
/// <param name="HostOwned">Whether a host facet serves the row.</param>
/// <param name="CellVisibility">Whether the declaration permits per-cell audience policies.</param>
public abstract record StateRowShape(
    CellName Name,
    CellKind Kind,
    long? Min = null,
    long? Max = null,
    int? Capacity = null,
    StateOverflow Overflow = StateOverflow.Refuse,
    bool Evicts = false,
    StateAdvance? Advance = null,
    StateDynamics? Dynamics = null,
    StateCycle? Cycle = null,
    StateDomain? Domain = null,
    string? ValuesFrom = null,
    StateInverse? Inverse = null,
    StateVisibility? Visibility = null,
    StateKnowledge? Knowledge = null,
    string? PhaseOf = null,
    string? Space = null,
    CellName? Enum = null,
    bool HostOwned = false,
    bool CellVisibility = false
) {
    /// <summary>Checks that every value a code row can admit is also a value a derived board can store.</summary>
    /// <param name="board">The derived board.</param>
    /// <param name="boardSymbols">The board's enum, or <see langword="null"/>.</param>
    /// <param name="codes">The inverse codes row.</param>
    /// <param name="codeSymbols">The codes row's enum, or <see langword="null"/>.</param>
    /// <param name="reason">Why the inverse domain is not safe, or empty.</param>
    /// <returns><see langword="true"/> when the code domain is a subset of the board domain and the board empty
    /// value is admitted.</returns>
    public static bool TryProveDerivedDomain(StateRowShape board, StateEnum? boardSymbols, StateRowShape codes, StateEnum? codeSymbols, out string reason) {
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

    private static bool TryAdmitDerivedValue(StateRowShape row, StateEnum? symbols, long value, out string reason) {
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
    /// row name starting with it is refused on every row a document declares (only the catalog mints rows under it,
    /// a pool's <c>$pool$…</c> rows); a cell key starting with it is refused
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
        StateDomain.Slot => 1,
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
        : StateCapacity.MaxCellsPerRow),
    };
    /// <summary>The declared domain, with any source shorthand resolved at the declaration boundary.</summary>
    [JsonIgnore]
    public abstract StateDomain EffectiveDomain { get; }
    /// <summary>Gets whether the row accumulates continuously.</summary>
    public bool IsAdvancing => (Advance is not null);
    /// <summary>Gets a value indicating whether the row's slot cell turns with the tick through a <see cref="StateCycle"/> trait.</summary>
    public bool IsCycling => (Cycle is not null);
    /// <summary>Gets a value indicating whether this row declares a <see cref="Draw"/> — whether it is a draw site.</summary>
    public abstract bool IsDraw { get; }
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
    /// slot: its one cell holds the drawn value, and its own bookkeeping (<see cref="StateRow.DrawCursor"/>/
    /// <see cref="StateRow.DrawnMasks"/>) lives in row fields rather than in cells.</summary>
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
    /// <summary>Determines whether this row's own shape mints a cell under a
    /// <see cref="ReservedNamePrefix"/>-prefixed key — the one question <see cref="StateReservedCells"/> asks before
    /// refusing such a cell.</summary>
    /// <param name="key">The reserved-prefix cell key.</param>
    /// <returns><see langword="true"/> when the engine mints a cell by that key on a row of this shape.</returns>
    /// <remarks>The engine row mints exactly one: <see cref="SlotKey"/>. A document project's row adds the reserved
    /// keys its own traits mint, so the reserved-cell rule stays one rule asked at one door rather than a list the
    /// validator and the store each keep.</remarks>
    public virtual bool MintsReservedCell(CellName key) => (key == SlotKey);
}
