using System.Diagnostics.CodeAnalysis;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The one (row, key) → raw-value implementation over a world document's <c>state</c> section, exposed through a
/// name-resolving entrance and a compiled-handle entrance. Every live numeric read of a declared cell resolves here
/// — a world rule's gate comparand and its live copy operand, a rule effect's
/// read-modify-write, the <c>world.state</c> console read-backs, the HUD <c>state.&lt;row&gt;</c>/
/// <c>state.&lt;row&gt;.&lt;key&gt;</c> binding, and the <c>UpsertStateCell</c> Add compose arm — so no two of them
/// can drift in how a pair addresses a cell, or in what that cell currently holds.
/// </summary>
/// <remarks>
/// <para><b>The pair rule, once.</b> A null <c>key</c> means the row's slot cell
/// (<see cref="WorldStateRow.SlotKey"/>); a non-null one names a cell inside the row. Which pairs are admissible is a
/// separate, author-time question decided by <see cref="WorldStateRow.IsKeyed"/> at the doors that accept a pair (the
/// rule compiler, the whole-document validator, the <c>Generate</c> compose arm) — this reader only resolves an
/// already-admitted pair, and a pair naming a row or cell the document does not declare reads as "absent" rather than
/// refusing: a mid-tick <c>RemoveStateRow</c> is the only way to get there, and the next install's recompile refuses
/// the rule outright if it can no longer resolve.</para>
/// <para><b>Why a null key can just be the slot key here.</b> Resolving a null key to
/// <see cref="WorldStateRow.SlotKey"/> and scanning is equivalent to asking <see cref="WorldStateRow.IsSlot"/> first
/// and taking the row's single cell, and the equivalence is what lets one rule serve both the callers that pass an
/// already-resolved key and the HUD binding that passes null. It holds because a <c>$value</c> cell can only exist on
/// a slot-shaped row in the first place: the validator refuses the reserved slot key as an authored cell key on any
/// row that declares a capacity or carries a cell count other than one, and refuses it outright on a generator row,
/// at boot, on every mutation, and on every undo-replay entry. So an installed document that has a <c>$value</c> cell
/// is slot-shaped, and one that does not resolves to nothing under either reading.</para>
/// <para><b>The value is computed, not fetched.</b> A row declaring <see cref="WorldStateRow.Advance"/> stores a
/// base in its slot cell and advances from it with elapsed ticks; a keyed row's own cell may independently declare
/// <see cref="WorldStateCell.Advance"/> the same way over its own base — the two never both name the same cell (the
/// slot cell may carry only the row's own trait), so this reader checks the row's trait first and only then the
/// cell's own, never both. Either way it returns <see cref="WorldStateAdvance.ComputeCurrentValue"/> at <c>tick</c>
/// for the advancing cell and the stored value for every other. This is the trait's only application site, which is
/// what makes a reader and a writer unable to disagree: an <c>add</c> composes against what a reader sees (the
/// compose arm reads here too), a rule gates on it, a HUD gauge draws it, and <c>world.state</c> echoes it, all from
/// this one computation — and because <see cref="Reduce"/>/<see cref="ArgExtremum"/> resolve the row once and each
/// candidate cell once through the same known-cell computation rather than reading
/// <see cref="WorldStateCell.Value"/> directly, a
/// <c>$reduce:</c>/<c>$argmax:</c>/<c>$argmin:</c> operand over a table of independently advancing cells sees every
/// cell's live value for free — no special case anywhere in either method.</para>
/// <para><b>Allocation-free.</b> The HUD path runs this once per bound element per frame, so this is an ordinal
/// linear scan over the row's own cells with no LINQ, no closure, and no intermediate collection — matching
/// <see cref="WorldDefinitionRows"/>'s own idiom — and it hands back a raw value rather than a cell record, because
/// an advancing row's computed value has no stored <see cref="WorldStateCell"/> to hand back and minting one per
/// read would allocate on that per-frame path. An ordinary row's read allocates nothing at all; an advancing row's
/// pays only what <see cref="Puck.Maths.DiscreteMeasure"/>'s exact rational allocation costs for its magnitude.</para>
/// </remarks>
public static class WorldStateReader {
    /// <summary>Resolves one world-owned row by its compiled typed handle without a row-name scan.</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="catalog">The document's current state catalog.</param>
    /// <param name="handle">A world-lane handle minted by <paramref name="catalog"/>.</param>
    /// <param name="key">The cell key, or <see langword="null"/> for the slot cell.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="row">The resolved row.</param>
    /// <param name="rawValue">The addressed live raw value, or <see langword="null"/> when absent.</param>
    /// <param name="text">The addressed text payload, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the row resolves.</returns>
    /// <exception cref="ArgumentException"><paramref name="catalog"/> is not the definition's current catalog, or
    /// <paramref name="handle"/> does not address a world-owned row in it.</exception>
    public static bool TryReadHandle(
        WorldDefinition definition,
        WorldStateCatalog catalog,
        WorldStateHandle handle,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out WorldStateRow? row,
        out long? rawValue,
        out string? text
    ) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: catalog);

        if (!ReferenceEquals(objA: definition.StateCatalog, objB: catalog)) {
            throw new ArgumentException(message: "The state catalog is not current for this definition.", paramName: nameof(catalog));
        }

        if (
            !catalog.TryGetDescriptor(descriptor: out var descriptor, handle: handle) ||
            (descriptor.Ownership != WorldStateOwnershipLane.World) ||
            (((uint)descriptor.LaneOrdinal) >= ((uint)definition.State.Count)) ||
            (definition.State[descriptor.LaneOrdinal] is not { } resolved) ||
            !string.Equals(a: resolved.Name, b: descriptor.Name, comparisonType: StringComparison.Ordinal)
        ) {
            throw new ArgumentException(message: "The state handle does not address a current world-owned row.", paramName: nameof(handle));
        }

        row = resolved;
        ReadCell(key: key, rawValue: out rawValue, row: row, text: out text, tick: tick);

        return true;
    }
    /// <summary>Finds the winning cell's key over a keyed row under <paramref name="op"/>
    /// (<see cref="WorldStateReduceOp.Max"/> or <see cref="WorldStateReduceOp.Min"/>), resolving each candidate
    /// cell's value through the same known-cell computation as <see cref="TryRead"/> after resolving the row once.
    /// A cell key that does not parse as a non-negative integer, or that
    /// <paramref name="isCandidateIndex"/> rejects, is excluded from the comparison; ties go to the lowest parsed
    /// index.</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="op">The extremum to find (<see cref="WorldStateReduceOp.Max"/> or
    /// <see cref="WorldStateReduceOp.Min"/>).</param>
    /// <param name="tick">The tick this read is answering AS OF; used by each candidate's value-over-time trait.</param>
    /// <param name="isCandidateIndex">An optional additional filter over a cell key's parsed index (for example, a
    /// caller-side population-capacity bound). <see langword="null"/> admits every non-negative parsed index.</param>
    /// <returns>The winning cell's key, or <see langword="null"/> when the row is absent, holds no cells, or no cell
    /// key both parses and passes <paramref name="isCandidateIndex"/>.</returns>
    public static string? ArgExtremum(
        WorldDefinition definition,
        string rowName,
        WorldStateReduceOp op,
        ulong tick,
        Func<int, bool>? isCandidateIndex = null
    ) => ArgExtremum(
        definition: definition,
        rowName: rowName,
        op: op,
        tick: tick,
        state: isCandidateIndex,
        isCandidateIndex: static (index, predicate) => ((predicate is null) || predicate(arg: index))
    );
    /// <summary>Finds the winning cell like <see cref="ArgExtremum(WorldDefinition,string,WorldStateReduceOp,ulong,Func{int,bool}?)"/>,
    /// passing caller state separately to a static candidate predicate so hot callers need not allocate a closure.</summary>
    /// <typeparam name="TState">The caller's filter-state carrier.</typeparam>
    /// <param name="definition">The document to read.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="op">The extremum to find.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="state">State passed to <paramref name="isCandidateIndex"/>.</param>
    /// <param name="isCandidateIndex">The allocation-free candidate predicate.</param>
    /// <returns>The winning cell key, or <see langword="null"/> when none qualifies.</returns>
    public static string? ArgExtremum<TState>(
        WorldDefinition definition,
        string rowName,
        WorldStateReduceOp op,
        ulong tick,
        TState state,
        Func<int, TState, bool> isCandidateIndex
    ) {
        ArgumentNullException.ThrowIfNull(argument: isCandidateIndex);

        if (!TryRead(
            definition: definition,
            key: null,
            rawValue: out _,
            row: out var declared,
            rowName: rowName,
            text: out _,
            tick: tick
        )) {
            return null;
        }

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
            var better = ((bestIndex < 0) || ((op == WorldStateReduceOp.Max)
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
    /// <summary>Parses a keyed row's cell key as a candidate body index: a non-negative integer in invariant
    /// decimal form. The one definition of which cell keys name a body, shared by <see cref="ArgExtremum"/> and
    /// the server's carrier scans.</summary>
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
    /// once through the same known-cell computation as <see cref="TryRead"/> — so a
    /// table whose cells independently advance (<see cref="WorldStateCell.Advance"/>) reduces over every cell's live
    /// value for free, never a stale base read straight off <see cref="WorldStateCell.Value"/>.</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="op">The reduction to apply. <see cref="WorldStateReduceOp.Count"/> answers with the row's cell
    /// count as an integer regardless of the row's <see cref="WorldStateRow.Kind"/>; the others preserve that
    /// kind.</param>
    /// <param name="tick">The tick this read is answering as of; used by each cell's value-over-time trait.</param>
    /// <returns>The reduced value, or <see cref="FixedQ4816.Zero"/> when the row is absent or holds no cells.</returns>
    public static FixedQ4816 Reduce(WorldDefinition definition, string rowName, WorldStateReduceOp op, ulong tick) {
        if (!TryRead(
            definition: definition,
            key: null,
            rawValue: out _,
            row: out var declared,
            rowName: rowName,
            text: out _,
            tick: tick
        )) {
            return FixedQ4816.Zero;
        }

        var raw = ReduceRaw(row: declared, op: op, tick: tick);
        var kind = ((op == WorldStateReduceOp.Count) ? CellKind.Int : declared.Kind);

        return ((kind == CellKind.Fixed)
            ? FixedQ4816.FromRawBits(value: raw)
            : LiftSaturating(raw: raw));
    }
    /// <summary>Lifts a whole-number cell to fixed point, saturating at <see cref="FixedQ4816"/>'s integer band.
    /// An int cell spans the whole <see cref="long"/>; the few readers that need a continuous quantity (a symmetry
    /// node, a dynamics target, a body-reference key) clamp rather than throw, so no authored value can fault a
    /// tick.</summary>
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
    public static long ReduceRaw(WorldStateRow row, WorldStateReduceOp op, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: row);
        var cells = (row.Cells ?? []);

        if (op == WorldStateReduceOp.Count) {
            return cells.Count;
        }

        var hasAcc = false;
        var acc = 0L;

        for (var index = 0; index < cells.Count; index++) {
            var cell = cells[index];
            ReadKnownCell(row: row, cell: cell, tick: tick, rawValue: out var raw, text: out _);
            var value = raw!.Value;
            acc = (!hasAcc
                ? value
                : (op switch {
                    WorldStateReduceOp.Sum => unchecked(acc + value),
                    WorldStateReduceOp.Max => ((value > acc) ? value : acc),
                    _ => ((value < acc) ? value : acc),
                }));
            hasAcc = true;
        }

        return acc;
    }
    /// <summary>Resolves one (row, key) pair against a document's live <c>state</c> section.</summary>
    /// <param name="definition">The document to read. The whole document rather than just its rows: a cell's value
    /// is the document's answer, and what the value-over-time trait reads to compute one is a document-scoped
    /// question (see <paramref name="tick"/>).</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="key">The cell key inside the row, or <see langword="null"/> for the row's slot cell
    /// (<see cref="WorldStateRow.SlotKey"/>).</param>
    /// <param name="tick">The tick this read is answering as of — what an advancing row's value is computed at.
    /// Callers pass the tick their frame already knows: the server's completed tick on the authoritative side, the
    /// last delivered snapshot's tick on the client side (which is a server tick, so it is comparable to an epoch;
    /// it lags by delivery, never by a different clock).</param>
    /// <param name="row">The named row, or <see langword="null"/> when the section declares none by that name.</param>
    /// <param name="rawValue">The addressed cell's live raw value, or <see langword="null"/> when the row declares no
    /// cell under that key — a distinct outcome from an unknown row, because a rule effect's read-modify-write treats
    /// an absent cell as zero but an absent row as nothing to write.</param>
    /// <param name="text">The addressed cell's text payload (<see cref="CellKind.Text"/> rows only), or
    /// <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the row resolved (whether or not it holds the addressed cell).</returns>
    public static bool TryRead(
        WorldDefinition definition,
        string rowName,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out WorldStateRow? row,
        out long? rawValue,
        out string? text
    ) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        rawValue = null;
        text = null;
        row = WorldDefinitionRows.FindStateRow(
            rows: definition.State,
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
    public static void ReadCell(WorldStateRow row, string? key, ulong tick, out long? rawValue, out string? text) {
        rawValue = null;
        text = null;
        var target = (key ?? WorldStateRow.SlotKey.Value);

        if (
            !WorldCellName.TryParse(
            candidate: target,
            name: out var targetKey,
            reason: out _
        ) ||
            (WorldDefinitionRows.FindCell(
            cells: row.Cells,
            key: targetKey
        ) is not { } cell)
        ) {
            return;
        }

        ReadKnownCell(row: row, cell: cell, tick: tick, rawValue: out rawValue, text: out text);
    }

    private static void ReadKnownCell(WorldStateRow row, WorldStateCell cell, ulong tick, out long? rawValue, out string? text) {
        rawValue = (((row.Advance is { } advance) && (cell.Key == WorldStateRow.SlotKey))
            ? advance.ComputeCurrentValue(
                row: row,
                baseValue: cell.Value,
                currentTick: tick
            )
            : (((row.Cycle is { } cycle) && (cell.Key == WorldStateRow.SlotKey))
                ? cycle.ComputeCurrentValue(
                    row: row,
                    baseValue: cell.Value,
                    currentTick: tick
                )
                : ((cell.Advance is { } cellAdvance)
                    ? cellAdvance.ComputeCurrentValue(
                        row: row,
                        baseValue: cell.Value,
                        currentTick: tick
                    )
                    : ((cell.Cycle is { } cellCycle)
                        ? cellCycle.ComputeCurrentValue(
                            row: row,
                            baseValue: cell.Value,
                            currentTick: tick
                        )
                        : cell.Value
        ))));
        text = cell.Text;
    }

    /// <summary>Resolves one (row, key) pair the same way <see cref="TryRead"/> does, except a cell carrying a
    /// <see cref="WorldStateDynamics"/> easing trait reads its EASED value at <paramref name="tick"/>
    /// (<see cref="TryEvaluateDynamics"/>) rather than its stored truth — the read the HUD's plain
    /// <c>state.&lt;row&gt;[.&lt;key&gt;]</c> binding takes; a rule gate, an arithmetic write's operand, and the
    /// <c>.$target</c> HUD facet all keep reading <see cref="TryRead"/>'s truth instead. A cell with no trait, or one
    /// whose trait names a <c>dynamics</c> row this document no longer declares (live only mid-tick — every other
    /// door refuses a dangling reference at author time), reads bit-identically to <see cref="TryRead"/>.</summary>
    /// <param name="definition">The document to read.</param>
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
        WorldDefinition definition,
        string rowName,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out WorldStateRow? row,
        out long? rawValue,
        out string? text
    ) {
        if (!TryRead(
            definition: definition,
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

        var target = (key ?? WorldStateRow.SlotKey.Value);
        var cell = (WorldCellName.TryParse(
            candidate: target,
            name: out var targetKey,
            reason: out _
        )
            ? WorldDefinitionRows.FindCell(
                cells: row.Cells,
                key: targetKey
            )
            : null
        );

        if (
            (cell is { } resolvedCell) &&
            TryEvaluateDynamics(
            cell: resolvedCell,
            definition: definition,
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
    /// <summary>Evaluates <paramref name="cell"/>'s <see cref="WorldStateDynamics"/> easing trait — the row's own
    /// <see cref="WorldStateRow.Dynamics"/> for the slot cell, else <paramref name="cell"/>'s own
    /// <see cref="WorldStateCell.Dynamics"/> — at <paramref name="tick"/>, chasing the cell's own stored value
    /// (<see cref="WorldStateCell.Value"/>) as the follower's target. The one evaluation site every reader
    /// (<see cref="TryReadEased"/>) and the mutation compose rebase share, so a rebased trait and an eased read are
    /// always computed the identical way.</summary>
    /// <param name="definition">The document to resolve the trait's referenced <c>dynamics</c> row against.</param>
    /// <param name="row">The carrying row (for its <see cref="CellKind"/> and simulation rate).</param>
    /// <param name="cell">The addressed cell.</param>
    /// <param name="tick">The tick to evaluate at.</param>
    /// <param name="trait">The resolved trait, or <see langword="null"/> when this cell carries none, the document
    /// authors no simulation rate, or the trait names a <c>dynamics</c> row this document no longer declares.</param>
    /// <param name="sample">The evaluated value/velocity, or <see langword="default"/> when <paramref name="trait"/>
    /// is <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a trait resolved and was evaluated.</returns>
    public static bool TryEvaluateDynamics(
        WorldDefinition definition,
        WorldStateRow row,
        WorldStateCell cell,
        ulong tick,
        [NotNullWhen(true)] out WorldStateDynamics? trait,
        out SecondOrderSample sample
    ) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: row);
        ArgumentNullException.ThrowIfNull(argument: cell);

        trait = (((cell.Key == WorldStateRow.SlotKey)
            ? row.Dynamics
            : null) ?? cell.Dynamics);
        sample = default;

        if (trait is null) {
            return false;
        }

        if (
            (definition.SimulationRateHz <= 0) ||
            (WorldDefinitionRows.FindDynamics(
            dynamics: definition.Dynamics,
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
            ticksPerSecond: ((ulong)definition.SimulationRateHz)
        );

        return true;
    }
    /// <summary>Converts a row-stored target into the continuous quantity a follower computes in.</summary>
    /// <param name="row">The carrying row, for its <see cref="CellKind"/>.</param>
    /// <param name="raw">The raw value.</param>
    /// <returns>Raw <c>FixedQ4816</c> bits reinterpreted for <see cref="CellKind.Fixed"/>; <paramref name="raw"/>
    /// lifted as a whole number for every other kind.</returns>
    public static FixedQ4816 DynamicsRowRawToFixed(WorldStateRow row, long raw) => ((row.Kind == CellKind.Fixed)
        ? FixedQ4816.FromRawBits(value: raw)
        : LiftSaturating(raw: raw));
    /// <summary>Reads fixed-native dynamics state from its raw carrier.</summary>
    public static FixedQ4816 DynamicsTraitRawToFixed(long raw) => FixedQ4816.FromRawBits(value: raw);
    /// <summary>Writes continuous dynamics state to its fixed-native raw carrier without narrowing.</summary>
    public static long DynamicsFixedToTraitRaw(FixedQ4816 value) => value.Value;
    /// <summary>The inverse of <see cref="DynamicsRowRawToFixed"/> — narrows a continuous value back to the row's own
    /// raw encoding: exact for <see cref="CellKind.Fixed"/>, nearest whole number (ties to even) for every other
    /// kind.</summary>
    /// <param name="row">The carrying row, for its <see cref="CellKind"/>.</param>
    /// <param name="value">The continuous value.</param>
    /// <returns>The row-kind-encoded raw value.</returns>
    public static long DynamicsFixedToRowRaw(WorldStateRow row, FixedQ4816 value) => ((row.Kind == CellKind.Fixed)
        ? value.Value
        : (FixedQ4816.Round(value: value).Value >> FixedQ4816.FractionBitCount));
}
