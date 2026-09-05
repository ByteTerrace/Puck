using System.Diagnostics.CodeAnalysis;
using Puck.Maths;

namespace Puck.State;

/// <summary>Which aggregate a <see cref="RuleFacts.ReducePrefix"/> operand computes over a row's cells, or which
/// extremum an arg-reduction searches for (only <see cref="Max"/>/<see cref="Min"/> are meaningful there — an
/// arg-reduction never sums or counts).</summary>
public enum StateReduceOp : byte {
    /// <summary>Not a reduction (the default for every operand kind this field does not apply to).</summary>
    None,

    /// <summary>The largest cell value.</summary>
    Max,

    /// <summary>The smallest cell value.</summary>
    Min,

    /// <summary>The sum of every cell value.</summary>
    Sum,

    /// <summary>The number of cells the row declares.</summary>
    Count,
    /// <summary>The Lehmer rank, over k! for a zone of k tokens, of an ordered zone's order relative to its token
    /// domain's own cell order — the arrangement a shuffle or a sort left the pile in, as one integer; -1 when the
    /// zone holds more than 20 tokens or a token its domain does not declare.</summary>
    ArrangementRank,
}
/// <summary>
/// The one (row, key) → raw-value implementation over a state section's rows, exposed through a name-resolving
/// entrance and a compiled-handle entrance. Every live numeric read of a declared cell resolves here — a rule's gate
/// comparand and its live copy operand, a rule effect's read-modify-write, the console read-backs, a presentation
/// binding, and the cell-write compose arm — so no two of them can drift in how a pair addresses a cell, or in what
/// that cell currently holds.
/// </summary>
/// <remarks>
/// <para><b>The pair rule, once.</b> A null <c>key</c> means the row's slot cell (<see cref="StateRow.SlotKey"/>);
/// a non-null one names a cell inside the row. Which pairs are admissible is a separate, author-time question
/// decided by <see cref="StateRow.IsKeyed"/> at the doors that accept a pair (the rule compiler, the whole-document
/// validator, a generate compose arm) — this reader only resolves an already-admitted pair, and a pair naming a row
/// or cell the section does not declare reads as "absent" rather than refusing: a mid-tick row removal is the only
/// way to get there, and the next install's recompile refuses the rule outright if it can no longer resolve.</para>
/// <para><b>Why a null key can just be the slot key here.</b> Resolving a null key to <see cref="StateRow.SlotKey"/>
/// and scanning is equivalent to asking <see cref="StateRow.IsSlot"/> first and taking the row's single cell, and
/// the equivalence is what lets one rule serve both the callers that pass an already-resolved key and a binding
/// that passes null. It holds because a <c>$value</c> cell can only exist on a slot-shaped row in the first place:
/// the validator refuses the reserved slot key as an authored cell key on any row that declares a capacity or
/// carries a cell count other than one. So an installed section that has a <c>$value</c> cell is slot-shaped, and
/// one that does not resolves to nothing under either reading.</para>
/// <para><b>The value is computed, not fetched.</b> A row declaring <see cref="StateRow.Advance"/> stores a base
/// in its slot cell and advances from it with elapsed ticks; a keyed row's own cell may independently declare
/// <see cref="StateCell.Advance"/> the same way over its own base — the two never both name the same cell (the
/// slot cell may carry only the row's own trait), so this reader checks the row's trait first and only then the
/// cell's own, never both. Either way it returns <see cref="StateAdvance.ComputeCurrentValue"/> at <c>tick</c> for
/// the advancing cell and the stored value for every other. This is the trait's only application site, which is
/// what makes a reader and a writer unable to disagree: an <c>add</c> composes against what a reader sees, a rule
/// gates on it, a gauge draws it, and a read-back echoes it, all from this one computation — and because
/// <see cref="Reduce"/>/<see cref="ArgExtremum"/> resolve the row once and each candidate cell once through the
/// same known-cell computation rather than reading <see cref="StateCell.Value"/> directly, a reduction or
/// arg-reduction over a table of independently advancing cells sees every cell's live value for free.</para>
/// <para><b>Allocation-free.</b> A per-frame binding path runs this once per bound element per frame, so this is
/// an ordinal linear scan over the row's own cells with no LINQ, no closure, and no intermediate collection —
/// matching <see cref="StateRows"/>' own idiom — and it hands back a raw value rather than a cell record, because
/// an advancing row's computed value has no stored <see cref="StateCell"/> to hand back and minting one per read
/// would allocate on that per-frame path. An ordinary row's read allocates nothing at all; an advancing row's pays
/// only what <see cref="Puck.Maths.DiscreteMeasure"/>'s exact rational allocation costs for its magnitude.</para>
/// </remarks>
public static class StateReader {
    /// <summary>Resolves one document-owned row by its compiled typed handle without a row-name scan.</summary>
    /// <param name="rows">The section's rows.</param>
    /// <param name="catalog">The section's current state catalog.</param>
    /// <param name="handle">A document-lane handle minted by <paramref name="catalog"/>.</param>
    /// <param name="key">The cell key, or <see langword="null"/> for the slot cell.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="row">The resolved row.</param>
    /// <param name="rawValue">The addressed live raw value, or <see langword="null"/> when absent.</param>
    /// <param name="text">The addressed text payload, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the row resolves. Never returns <see langword="false"/>: a handle either
    /// addresses a row in the current catalog or the call throws, which is the one rule every per-tick handle read
    /// follows. This differs from <see cref="TryRead(IReadOnlyList{StateRow}?, string, string?, ulong, out StateRow?, out long?, out string?)"/>'s name-resolving door, which reads an unknown row as absent:
    /// a compiled handle is only ever minted against a row the rule compiler already proved present in the same
    /// section, and every install revalidates by recompiling every rule against it — so an installed section can
    /// never carry a rule whose compiled row a handle addresses has vanished. A throw here is therefore a caught
    /// invariant violation, never a reachable "the row is gone" case a rule needs to tolerate.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> does not address a document-owned row in
    /// <paramref name="catalog"/>.</exception>
    public static bool TryReadHandle(
        IReadOnlyList<StateRow> rows,
        StateCatalog catalog,
        StateHandle handle,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out StateRow? row,
        out long? rawValue,
        out string? text
    ) {
        ArgumentNullException.ThrowIfNull(argument: rows);
        ArgumentNullException.ThrowIfNull(argument: catalog);

        if (
            !catalog.TryGetDescriptor(descriptor: out var descriptor, handle: handle) ||
            (descriptor.Ownership != StateLane.Document) ||
            (((uint)descriptor.LaneOrdinal) >= ((uint)rows.Count)) ||
            (rows[descriptor.LaneOrdinal] is not { } resolved) ||
            !string.Equals(a: resolved.Name, b: descriptor.Name, comparisonType: StringComparison.Ordinal)
        ) {
            throw new ArgumentException(message: "The state handle does not address a current document-owned row.", paramName: nameof(handle));
        }

        row = resolved;
        ReadCell(key: key, rawValue: out rawValue, row: row, text: out text, tick: tick);

        return true;
    }
    /// <summary>Resolves one document-owned row by its compiled handle and reads a cell through a store, on
    /// <see cref="TryReadHandle(IReadOnlyList{StateRow}, StateCatalog, StateHandle, string?, ulong, out StateRow?, out long?, out string?)"/>'s terms.</summary>
    /// <param name="store">Where the cell's stored value is read.</param>
    /// <param name="catalog">The section's current state catalog.</param>
    /// <param name="handle">A document-lane handle minted by <paramref name="catalog"/>.</param>
    /// <param name="key">The cell key, or <see langword="null"/> for the slot cell.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="row">The resolved row.</param>
    /// <param name="rawValue">The addressed live raw value, or <see langword="null"/> when absent.</param>
    /// <param name="text">The addressed text payload, or <see langword="null"/>.</param>
    public static bool TryReadHandle(
        StateStore store,
        StateCatalog catalog,
        StateHandle handle,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out StateRow? row,
        out long? rawValue,
        out string? text
    ) {
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: catalog);
        var rows = store.Rows;

        if (
            !catalog.TryGetDescriptor(descriptor: out var descriptor, handle: handle) ||
            (descriptor.Ownership != StateLane.Document) ||
            (((uint)descriptor.LaneOrdinal) >= ((uint)rows.Count)) ||
            (rows[descriptor.LaneOrdinal] is not { } resolved) ||
            !string.Equals(a: resolved.Name, b: descriptor.Name, comparisonType: StringComparison.Ordinal)
        ) {
            throw new ArgumentException(message: "The state handle does not address a current document-owned row.", paramName: nameof(handle));
        }

        row = resolved;
        ReadCell(store: store, row: row, key: key, tick: tick, rawValue: out rawValue, text: out text);

        return true;
    }
    /// <summary>Finds the winning cell's key over a keyed row under <paramref name="op"/>
    /// (<see cref="StateReduceOp.Max"/> or <see cref="StateReduceOp.Min"/>), resolving each candidate cell's value
    /// through the same known-cell computation as <see cref="TryRead(IReadOnlyList{StateRow}?, string, string?, ulong, out StateRow?, out long?, out string?)"/> after resolving the row once. A cell key
    /// that does not parse as a non-negative integer, or that <paramref name="isCandidateIndex"/> rejects, is
    /// excluded from the comparison; ties go to the lowest parsed index.</summary>
    /// <param name="rows">The section's rows.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="op">The extremum to find (<see cref="StateReduceOp.Max"/> or <see cref="StateReduceOp.Min"/>).</param>
    /// <param name="tick">The tick this read is answering as of; used by each candidate's value-over-time trait.</param>
    /// <param name="isCandidateIndex">An optional additional filter over a cell key's parsed index (for example, a
    /// caller-side capacity bound). <see langword="null"/> admits every non-negative parsed index.</param>
    /// <returns>The winning cell's key, or <see langword="null"/> when the row is absent, holds no cells, or no cell
    /// key both parses and passes <paramref name="isCandidateIndex"/>.</returns>
    public static string? ArgExtremum(
        IReadOnlyList<StateRow>? rows,
        string rowName,
        StateReduceOp op,
        ulong tick,
        Func<int, bool>? isCandidateIndex = null
    ) => ArgExtremum(
        rows: rows,
        rowName: rowName,
        op: op,
        tick: tick,
        state: isCandidateIndex,
        isCandidateIndex: static (index, predicate) => ((predicate is null) || predicate(arg: index))
    );
    /// <summary>Finds the winning cell like <see cref="ArgExtremum(IReadOnlyList{StateRow}?,string,StateReduceOp,ulong,Func{int,bool}?)"/>,
    /// passing caller state separately to a static candidate predicate so hot callers need not allocate a closure.</summary>
    /// <typeparam name="TState">The caller's filter-state carrier.</typeparam>
    /// <param name="rows">The section's rows.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="op">The extremum to find.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="state">State passed to <paramref name="isCandidateIndex"/>.</param>
    /// <param name="isCandidateIndex">The allocation-free candidate predicate.</param>
    /// <returns>The winning cell key, or <see langword="null"/> when none qualifies.</returns>
    public static string? ArgExtremum<TState>(
        IReadOnlyList<StateRow>? rows,
        string rowName,
        StateReduceOp op,
        ulong tick,
        TState state,
        Func<int, TState, bool> isCandidateIndex
    ) {
        if (!TryRead(
            rows: rows,
            key: null,
            rawValue: out _,
            row: out var declared,
            rowName: rowName,
            text: out _,
            tick: tick
        )) {
            return null;
        }

        return ArgExtremumOverRow(declared: declared, op: op, tick: tick, state: state, isCandidateIndex: isCandidateIndex);
    }
    /// <summary>Finds the winning cell like <see cref="ArgExtremum{TState}(IReadOnlyList{StateRow}?,string,StateReduceOp,ulong,TState,Func{int,TState,bool})"/>,
    /// resolving the row through a compiled <paramref name="handle"/> instead of a name scan.</summary>
    /// <typeparam name="TState">The caller's filter-state carrier.</typeparam>
    /// <param name="rows">The section's rows.</param>
    /// <param name="catalog">The section's current state catalog.</param>
    /// <param name="handle">The compiled handle for the row to search.</param>
    /// <param name="op">The extremum to find.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="state">State passed to <paramref name="isCandidateIndex"/>.</param>
    /// <param name="isCandidateIndex">The allocation-free candidate predicate.</param>
    /// <returns>The winning cell key, or <see langword="null"/> when none qualifies.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> does not address a current document-owned row.</exception>
    public static string? ArgExtremum<TState>(
        IReadOnlyList<StateRow> rows,
        StateCatalog catalog,
        StateHandle handle,
        StateReduceOp op,
        ulong tick,
        TState state,
        Func<int, TState, bool> isCandidateIndex
    ) {
        _ = TryReadHandle(
            catalog: catalog,
            rows: rows,
            handle: handle,
            key: null,
            rawValue: out _,
            row: out var declared,
            text: out _,
            tick: tick
        );

        return ArgExtremumOverRow(declared: declared!, op: op, tick: tick, state: state, isCandidateIndex: isCandidateIndex);
    }
    private static string? ArgExtremumOverRow<TState>(
        StateRow declared,
        StateReduceOp op,
        ulong tick,
        TState state,
        Func<int, TState, bool> isCandidateIndex
    ) {
        ArgumentNullException.ThrowIfNull(argument: isCandidateIndex);

        var bestIndex = -1;
        string? bestKey = null;
        var bestValue = 0L;

        var cells = (declared.Cells ?? []);
        for (var candidateIndex = 0; candidateIndex < cells.Count; candidateIndex++) {
            var candidate = cells[candidateIndex];
            if (
                !TryParseCandidateIndex(
                key: candidate.Key,
                index: out var index
            ) ||
                !isCandidateIndex(arg1: index, arg2: state)
            ) {
                continue;
            }

            ReadKnownCell(row: declared, cell: candidate, tick: tick, rawValue: out var raw, text: out _);
            var value = raw!.Value;
            var better = ((bestIndex < 0) || ((op == StateReduceOp.Max)
                ? (value > bestValue)
                : (value < bestValue)));
            var tieLower = ((bestIndex >= 0) && (value == bestValue) && (index < bestIndex));

            if (
                better ||
                tieLower
            ) {
                bestIndex = index;
                bestKey = candidate.Key;
                bestValue = value;
            }
        }

        return bestKey;
    }
    /// <summary>Parses a keyed row's cell key as a candidate participant index: a non-negative integer in invariant
    /// decimal form. The one definition of which cell keys name a participant, shared by <see cref="ArgExtremum"/>
    /// and a host's own carrier scans.</summary>
    /// <param name="key">The cell key.</param>
    /// <param name="index">The parsed index, or -1 when the key is not a candidate.</param>
    /// <returns><see langword="true"/> when <paramref name="key"/> parses as a non-negative integer.</returns>
    public static bool TryParseCandidateIndex(string key, out int index) {
        if (
            int.TryParse(
            s: key,
            style: System.Globalization.NumberStyles.Integer,
            provider: System.Globalization.CultureInfo.InvariantCulture,
            result: out index
        ) &&
            (index >= 0)
        ) {
            return true;
        }

        index = -1;

        return false;
    }
    /// <summary>Reduces a keyed row's cell values with <paramref name="op"/>, resolving the row once and each cell
    /// once through the same known-cell computation as <see cref="TryRead(IReadOnlyList{StateRow}?, string, string?, ulong, out StateRow?, out long?, out string?)"/> — so a table whose cells independently
    /// advance (<see cref="StateCell.Advance"/>) reduces over every cell's live value for free, never a stale base
    /// read straight off <see cref="StateCell.Value"/>.</summary>
    /// <param name="rows">The section's rows.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="op">The reduction to apply. <see cref="StateReduceOp.Count"/> answers with the row's cell count
    /// as an integer regardless of the row's <see cref="StateRow.Kind"/>; the others preserve that kind.</param>
    /// <param name="tick">The tick this read is answering as of; used by each cell's value-over-time trait.</param>
    /// <returns>The reduced value, or <see cref="FixedQ4816.Zero"/> when the row is absent or holds no cells.</returns>
    public static FixedQ4816 Reduce(IReadOnlyList<StateRow>? rows, string rowName, StateReduceOp op, ulong tick) {
        if (!TryRead(
            rows: rows,
            key: null,
            rawValue: out _,
            row: out var declared,
            rowName: rowName,
            text: out _,
            tick: tick
        )) {
            return FixedQ4816.Zero;
        }

        var raw = ((op == StateReduceOp.ArrangementRank) ? ArrangementRank(rows: rows, zone: declared) : ReduceRaw(row: declared, op: op, tick: tick));
        var kind = ((op is StateReduceOp.Count or StateReduceOp.ArrangementRank) ? CellKind.Int : declared.Kind);

        return ((kind == CellKind.Fixed)
            ? FixedQ4816.FromRawBits(value: raw)
            : LiftSaturating(raw: raw));
    }
    /// <summary>The most tokens an arrangement rank or an <c>arrange</c> transform covers: 20! is the last factorial
    /// below 2^63.</summary>
    public const int MaxArrangementTokens = 20;

    /// <summary>Returns <see cref="ArrangementRank(IReadOnlyList{StateRow}?, StateRow)"/> over a store's rows — the
    /// rank reads token order alone, which the rows carry.</summary>
    /// <param name="store">The store.</param>
    /// <param name="zone">The ordered zone.</param>
    public static long ArrangementRank(StateStore store, StateRow zone) {
        ArgumentNullException.ThrowIfNull(argument: store);

        return ArrangementRank(rows: store.Rows, zone: zone);
    }
    /// <summary>Returns the Lehmer rank of an ordered zone's token order relative to its token domain's cell order —
    /// over k! for k tokens — or -1 when the zone is not an ordered zone, holds more than
    /// <see cref="MaxArrangementTokens"/> tokens, or holds a token its domain does not declare.</summary>
    /// <param name="rows">The section's rows.</param>
    /// <param name="zone">The ordered zone.</param>
    public static long ArrangementRank(IReadOnlyList<StateRow>? rows, StateRow zone) {
        ArgumentNullException.ThrowIfNull(argument: zone);
        Span<int> ordinals = stackalloc int[MaxArrangementTokens];
        var count = DomainOrdinals(rows: rows, zone: zone, ordinals: ordinals);
        if (count < 0) {
            return -1L;
        }
        Span<int> relative = stackalloc int[MaxArrangementTokens];
        RelativeOrder(ordinals: ordinals[..count], relative: relative[..count]);
        return unchecked((long)Puck.Maths.Combinatorics.PermutationRank(permutation: relative[..count]));
    }

    /// <summary>Fills each zone token's ordinal in its token domain's cell order, in zone order. Returns the token
    /// count, or -1 when the zone is not an ordered zone, exceeds <see cref="MaxArrangementTokens"/>, or holds a
    /// token the domain does not declare.</summary>
    /// <param name="rows">The section's rows.</param>
    /// <param name="zone">The ordered zone.</param>
    /// <param name="ordinals">Scratch of at least <see cref="MaxArrangementTokens"/>.</param>
    public static int DomainOrdinals(IReadOnlyList<StateRow>? rows, StateRow zone, Span<int> ordinals) {
        var cells = (zone.Cells ?? []);
        if (zone.EffectiveDomain is not StateDomain.KeysOf { Ordered: true } keysOf || cells.Count > MaxArrangementTokens ||
            StateRows.FindStateRow(rows: rows, name: keysOf.Row.Value) is not { Cells: { } domain }) {
            return -1;
        }
        for (var index = 0; index < cells.Count; index++) {
            var ordinal = -1;
            for (var candidate = 0; candidate < domain.Count; candidate++) {
                if (domain[candidate].Key == cells[index].Key) {
                    ordinal = candidate;
                    break;
                }
            }
            if (ordinal < 0) {
                return -1;
            }
            ordinals[index] = ordinal;
        }
        return cells.Count;
    }

    /// <summary>Maps distinct ordinals to their ranks among themselves — 0 for the smallest — so a subset's order is
    /// a permutation of 0..k-1.</summary>
    /// <param name="ordinals">The ordinals, distinct.</param>
    /// <param name="relative">Receives each ordinal's rank.</param>
    public static void RelativeOrder(ReadOnlySpan<int> ordinals, Span<int> relative) {
        for (var index = 0; index < ordinals.Length; index++) {
            var rank = 0;
            for (var other = 0; other < ordinals.Length; other++) {
                if (ordinals[other] < ordinals[index]) {
                    rank++;
                }
            }
            relative[index] = rank;
        }
    }
    /// <summary>Lifts a whole-number cell to fixed point, saturating at <see cref="FixedQ4816"/>'s integer band.
    /// An int cell spans the whole <see cref="long"/>; the few readers that need a continuous quantity (a symmetry
    /// node, a dynamics target, a participant-reference key) clamp rather than throw, so no authored value can fault
    /// a tick.</summary>
    /// <param name="raw">The int cell's whole value.</param>
    /// <returns>The value as fixed point, clamped to the representable integer band.</returns>
    public static FixedQ4816 LiftSaturating(long raw) => FixedQ4816.FromInteger(value: Math.Clamp(
        value: raw,
        min: (long.MinValue >> FixedQ4816.FractionBitCount),
        max: (long.MaxValue >> FixedQ4816.FractionBitCount)
    ));

    /// <summary>Reduces a resolved row directly in its native raw encoding. Each declared cell is read once, so the
    /// walk is linear in cell count.</summary>
    /// <param name="row">The already-resolved row.</param>
    /// <param name="op">The reduction to apply.</param>
    /// <param name="tick">The tick at which value-over-time traits are evaluated.</param>
    /// <returns>The native raw reduction, or zero for an empty row.</returns>
    public static long ReduceRaw(StateRow row, StateReduceOp op, ulong tick) => ReduceRaw(store: null, row: row, op: op, tick: tick);
    /// <summary>Reduces a resolved row through a store, on <see cref="ReduceRaw(StateRow, StateReduceOp, ulong)"/>'s terms.</summary>
    /// <param name="store">Where each cell's stored value is read, or <see langword="null"/> for the row's own cells.</param>
    /// <param name="row">The already-resolved row.</param>
    /// <param name="op">The reduction to apply.</param>
    /// <param name="tick">The tick at which value-over-time traits are evaluated.</param>
    public static long ReduceRaw(StateStore? store, StateRow row, StateReduceOp op, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: row);
        var cells = (row.Cells ?? []);

        if (op == StateReduceOp.Count) {
            return cells.Count;
        }
        if (op == StateReduceOp.ArrangementRank) {
            // The rank needs the token domain's order, which only the section holds (ArrangementRank).
            return -1L;
        }

        var hasAcc = false;
        var acc = 0L;

        for (var index = 0; index < cells.Count; index++) {
            var cell = cells[index];
            var stored = (((store is not null) && store.TryStoredAt(row: row, index: index, value: out var framed)) ? framed : cell.Value);
            var value = Live(row: row, cell: cell, baseValue: stored, tick: tick);
            acc = (!hasAcc
                ? value
                : (op switch {
                    StateReduceOp.Sum => unchecked(acc + value),
                    StateReduceOp.Max => ((value > acc) ? value : acc),
                    _ => ((value < acc) ? value : acc),
                }));
            hasAcc = true;
        }

        return acc;
    }
    /// <summary>Resolves one (row, key) pair against a store, on <see cref="TryRead(IReadOnlyList{StateRow}?, string, string?, ulong, out StateRow?, out long?, out string?)"/>'s terms.</summary>
    /// <param name="store">Where the cell's stored value is read.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="key">The cell key, or <see langword="null"/> for the slot cell.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="row">The named row, or <see langword="null"/>.</param>
    /// <param name="rawValue">The addressed live raw value, or <see langword="null"/> when absent.</param>
    /// <param name="text">The addressed text payload, or <see langword="null"/>.</param>
    public static bool TryRead(
        StateStore store,
        string rowName,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out StateRow? row,
        out long? rawValue,
        out string? text
    ) {
        ArgumentNullException.ThrowIfNull(argument: store);
        rawValue = null;
        text = null;
        row = store.Find(name: rowName);

        if (row is null) {
            return false;
        }

        ReadCell(store: store, row: row, key: key, tick: tick, rawValue: out rawValue, text: out text);

        return true;
    }
    /// <summary>Reads one cell of a resolved row through a store: the stored value beneath the key under the row's
    /// value-over-time trait, or <see langword="null"/> when the store holds no cell under that key.</summary>
    /// <param name="store">Where the cell's stored value is read.</param>
    /// <param name="row">The resolved row.</param>
    /// <param name="key">The cell key, or <see langword="null"/> for the slot cell.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="rawValue">The addressed live raw value, or <see langword="null"/> when absent.</param>
    /// <param name="text">The addressed text payload, or <see langword="null"/>.</param>
    public static void ReadCell(StateStore store, StateRow row, string? key, ulong tick, out long? rawValue, out string? text) {
        ArgumentNullException.ThrowIfNull(argument: store);
        rawValue = null;
        text = null;
        var target = (key ?? StateRow.SlotKey.Value);

        if (!CellName.TryParse(candidate: target, name: out var targetKey, reason: out _) || !store.TryStored(row: row, key: targetKey, value: out var stored, text: out text)) {
            return;
        }

        rawValue = Live(row: row, cell: StateRows.FindCell(cells: row.Cells, key: targetKey), baseValue: stored, tick: tick);
    }
    /// <summary>Resolves one (row, key) pair against a section's live rows.</summary>
    /// <param name="rows">The section's rows, or <see langword="null"/> for none.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="key">The cell key inside the row, or <see langword="null"/> for the row's slot cell
    /// (<see cref="StateRow.SlotKey"/>).</param>
    /// <param name="tick">The tick this read is answering as of — what an advancing row's value is computed at.
    /// Callers pass the tick their frame already knows: the authority's completed tick on the authoritative side,
    /// the last delivered snapshot's tick on a client (which is an authority tick, so it is comparable to an epoch;
    /// it lags by delivery, never by a different clock).</param>
    /// <param name="row">The named row, or <see langword="null"/> when the section declares none by that name.</param>
    /// <param name="rawValue">The addressed cell's live raw value, or <see langword="null"/> when the row declares no
    /// cell under that key — a distinct outcome from an unknown row, because a rule effect's read-modify-write treats
    /// an absent cell as zero but an absent row as nothing to write.</param>
    /// <param name="text">The addressed cell's text payload (<see cref="CellKind.Text"/> rows only), or
    /// <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the row resolved (whether or not it holds the addressed cell).</returns>
    public static bool TryRead(
        IReadOnlyList<StateRow>? rows,
        string rowName,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out StateRow? row,
        out long? rawValue,
        out string? text
    ) {
        rawValue = null;
        text = null;
        row = StateRows.FindStateRow(
            rows: rows,
            name: rowName
        );

        if (row is null) {
            return false;
        }

        ReadCell(key: key, rawValue: out rawValue, row: row, text: out text, tick: tick);

        return true;
    }

    /// <summary>Reads one cell of an already resolved row: the live raw value as of <paramref name="tick"/> under the
    /// row's value-over-time trait, or <see langword="null"/> when the key names no cell.</summary>
    /// <param name="row">The resolved row.</param>
    /// <param name="key">The cell key, or <see langword="null"/> for the slot cell.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="rawValue">The addressed live raw value, or <see langword="null"/> when absent.</param>
    /// <param name="text">The addressed text payload, or <see langword="null"/>.</param>
    public static void ReadCell(StateRow row, string? key, ulong tick, out long? rawValue, out string? text) {
        rawValue = null;
        text = null;
        var target = (key ?? StateRow.SlotKey.Value);

        if (
            !CellName.TryParse(
            candidate: target,
            name: out var targetKey,
            reason: out _
        ) ||
            (StateRows.FindCell(
            cells: row.Cells,
            key: targetKey
        ) is not { } cell)
        ) {
            return;
        }

        ReadKnownCell(row: row, cell: cell, tick: tick, rawValue: out rawValue, text: out text);
    }

    private static void ReadKnownCell(StateRow row, StateCell cell, ulong tick, out long? rawValue, out string? text) {
        rawValue = Live(row: row, cell: cell, baseValue: cell.Value, tick: tick);
        text = cell.Text;
    }

    // The live value of a stored base under the row's and the cell's own value-over-time traits; a cell the row
    // does not list (a frame's dense board cell) carries no trait of its own.
    private static long Live(StateRow row, StateCell? cell, long baseValue, ulong tick) {
        var isSlot = ((cell is null) ? false : (cell.Key == StateRow.SlotKey));

        if ((row.Advance is { } advance) && isSlot) {
            return advance.ComputeCurrentValue(row: row, baseValue: baseValue, currentTick: tick);
        }
        if ((row.Cycle is { } cycle) && isSlot) {
            return cycle.ComputeCurrentValue(row: row, baseValue: baseValue, currentTick: tick);
        }
        if (cell?.Advance is { } cellAdvance) {
            return cellAdvance.ComputeCurrentValue(row: row, baseValue: baseValue, currentTick: tick);
        }
        if (cell?.Cycle is { } cellCycle) {
            return cellCycle.ComputeCurrentValue(row: row, baseValue: baseValue, currentTick: tick);
        }

        return baseValue;
    }

    /// <summary>Resolves one (row, key) pair the same way <see cref="TryRead(IReadOnlyList{StateRow}?, string, string?, ulong, out StateRow?, out long?, out string?)"/> does, except a cell carrying a
    /// <see cref="StateDynamics"/> easing trait reads its EASED value at <paramref name="tick"/>
    /// (<see cref="TryEvaluateDynamics"/>) rather than its stored truth — the read a plain presentation binding takes;
    /// a rule gate, an arithmetic write's operand, and a target facet all keep reading <see cref="TryRead(IReadOnlyList{StateRow}?, string, string?, ulong, out StateRow?, out long?, out string?)"/>'s truth
    /// instead. A cell with no trait, or one whose trait names a <c>dynamics</c> row the section no longer declares
    /// (live only mid-tick — every other door refuses a dangling reference at author time), reads bit-identically to
    /// <see cref="TryRead(IReadOnlyList{StateRow}?, string, string?, ulong, out StateRow?, out long?, out string?)"/>.</summary>
    /// <param name="rows">The section's rows.</param>
    /// <param name="dynamics">The declared dynamics rows the trait's reference resolves against.</param>
    /// <param name="ticksPerSecond">The simulation rate the follower is stepped at; a rate of zero or below reads
    /// the stored truth.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="key">The cell key inside the row, or <see langword="null"/> for the row's slot cell.</param>
    /// <param name="tick">The tick this read is answering as of.</param>
    /// <param name="row">The named row, or <see langword="null"/> when the section declares none by that name.</param>
    /// <param name="rawValue">The addressed cell's live eased raw value, or <see langword="null"/> when the row
    /// declares no cell under that key.</param>
    /// <param name="text">The addressed cell's text payload (<see cref="CellKind.Text"/> rows only, never eased), or
    /// <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the row resolved.</returns>
    public static bool TryReadEased(
        IReadOnlyList<StateRow>? rows,
        IReadOnlyList<DynamicsRow>? dynamics,
        int ticksPerSecond,
        string rowName,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out StateRow? row,
        out long? rawValue,
        out string? text
    ) {
        if (!TryRead(
            rows: rows,
            key: key,
            rawValue: out rawValue,
            row: out row,
            rowName: rowName,
            text: out text,
            tick: tick
        )) {
            return false;
        }

        if (rawValue is not long) {
            return true;
        }

        var target = (key ?? StateRow.SlotKey.Value);
        var cell = (CellName.TryParse(
            candidate: target,
            name: out var targetKey,
            reason: out _
        )
            ? StateRows.FindCell(
                cells: row.Cells,
                key: targetKey
            )
            : null
        );

        if (
            (cell is { } resolvedCell) &&
            TryEvaluateDynamics(
            cell: resolvedCell,
            dynamics: dynamics,
            ticksPerSecond: ticksPerSecond,
            row: row,
            sample: out var sample,
            tick: tick,
            trait: out _
        )
        ) {
            rawValue = row.ClampToEnvelope(value: DynamicsFixedToRowRaw(
                row: row,
                value: sample.Value
            ));
        }

        return true;
    }
    /// <summary>Evaluates <paramref name="cell"/>'s <see cref="StateDynamics"/> easing trait — the row's own
    /// <see cref="StateRow.Dynamics"/> for the slot cell, else <paramref name="cell"/>'s own
    /// <see cref="StateCell.Dynamics"/> — at <paramref name="tick"/>, chasing the cell's own stored value
    /// (<see cref="StateCell.Value"/>) as the follower's target. The one evaluation site every reader
    /// (<see cref="TryReadEased"/>) and the mutation compose rebase share, so a rebased trait and an eased read are
    /// always computed the identical way.</summary>
    /// <param name="dynamics">The declared dynamics rows the trait's reference resolves against.</param>
    /// <param name="ticksPerSecond">The simulation rate the follower is stepped at.</param>
    /// <param name="row">The carrying row (for its <see cref="CellKind"/>).</param>
    /// <param name="cell">The addressed cell.</param>
    /// <param name="tick">The tick to evaluate at.</param>
    /// <param name="trait">The resolved trait, or <see langword="null"/> when this cell carries none, the rate is
    /// not positive, or the trait names a <c>dynamics</c> row the section no longer declares.</param>
    /// <param name="sample">The evaluated value/velocity, or <see langword="default"/> when <paramref name="trait"/>
    /// is <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a trait resolved and was evaluated.</returns>
    public static bool TryEvaluateDynamics(
        IReadOnlyList<DynamicsRow>? dynamics,
        int ticksPerSecond,
        StateRow row,
        StateCell cell,
        ulong tick,
        [NotNullWhen(true)] out StateDynamics? trait,
        out SecondOrderSample sample
    ) {
        ArgumentNullException.ThrowIfNull(argument: row);
        ArgumentNullException.ThrowIfNull(argument: cell);

        trait = (((cell.Key == StateRow.SlotKey)
            ? row.Dynamics
            : null) ?? cell.Dynamics);
        sample = default;

        if (trait is null) {
            return false;
        }

        if (
            (ticksPerSecond <= 0) ||
            (StateRows.FindDynamics(
            dynamics: dynamics,
            name: trait.Row
        ) is not { } dynamicsRow)
        ) {
            trait = null;

            return false;
        }

        var epoch = ((trait.EpochTick < 0L)
            ? 0UL
            : (ulong)trait.EpochTick);
        var elapsed = ((tick > epoch)
            ? (tick - epoch)
            : 0UL);

        sample = dynamicsRow.Compiled.Evaluate(
            elapsedTicks: elapsed,
            initialValue: DynamicsTraitRawToFixed(raw: trait.Y0),
            initialVelocity: DynamicsTraitRawToFixed(raw: trait.V0),
            target: DynamicsRowRawToFixed(
                row: row,
                raw: cell.Value
            ),
            ticksPerSecond: ((ulong)ticksPerSecond)
        );

        return true;
    }
    /// <summary>Converts a row-stored target into the continuous quantity a follower computes in.</summary>
    /// <param name="row">The carrying row, for its <see cref="CellKind"/>.</param>
    /// <param name="raw">The raw value.</param>
    /// <returns>Raw <c>FixedQ4816</c> bits reinterpreted for <see cref="CellKind.Fixed"/>; <paramref name="raw"/>
    /// lifted as a whole number for every other kind.</returns>
    public static FixedQ4816 DynamicsRowRawToFixed(StateRow row, long raw) => ((row.Kind == CellKind.Fixed)
        ? FixedQ4816.FromRawBits(value: raw)
        : LiftSaturating(raw: raw));
    /// <summary>Reads fixed-native dynamics state from its raw carrier.</summary>
    /// <param name="raw">The raw carrier.</param>
    public static FixedQ4816 DynamicsTraitRawToFixed(long raw) => FixedQ4816.FromRawBits(value: raw);
    /// <summary>Writes continuous dynamics state to its fixed-native raw carrier without narrowing.</summary>
    /// <param name="value">The continuous value.</param>
    public static long DynamicsFixedToTraitRaw(FixedQ4816 value) => value.Value;
    /// <summary>The inverse of <see cref="DynamicsRowRawToFixed"/> — narrows a continuous value back to the row's own
    /// raw encoding: exact for <see cref="CellKind.Fixed"/>, nearest whole number (ties to even) for every other
    /// kind.</summary>
    /// <param name="row">The carrying row, for its <see cref="CellKind"/>.</param>
    /// <param name="value">The continuous value.</param>
    /// <returns>The row-kind-encoded raw value.</returns>
    public static long DynamicsFixedToRowRaw(StateRow row, FixedQ4816 value) => ((row.Kind == CellKind.Fixed)
        ? value.Value
        : (FixedQ4816.Round(value: value).Value >> FixedQ4816.FractionBitCount));
}
