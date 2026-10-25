using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // The clocks a compose applies its members at: the mutation's own tick pair, plus the document's dynamics rows
    // and simulation rate, so an eased cell's write kicks the follower it is actually chasing rather than reading a
    // rate of zero and resting on its stored target.
    private static ArenaTime ComposeTime(WorldDefinition definition, ulong tick, ulong engineTick) => new(
        Dynamics: definition.Dynamics,
        EngineTick: engineTick,
        Tick: tick,
        TicksPerSecond: definition.SimulationRateHz
    );

    // One returned scratch store per catalog. A concurrent or recursive compose takes another store, never an
    // arena with an open scope. Weak catalog keys release the storage when its compiled declarations leave use.
    private sealed class ComposeArenaPool {
        public ComposeArenaEntry? Available;
    }
    private sealed record ComposeArenaEntry(StateArena Arena, WorldStateSection Source, ComposeArenaPool Pool);

    private static readonly ConditionalWeakTable<StateCatalog, ComposeArenaPool> ComposeArenas = new();
    private static readonly ConditionalWeakTable<StateArena, ComposeArenaEntry> ComposeEntries = new();
    private static readonly HashSet<string> NoSeedRows = new(comparer: StringComparer.Ordinal);

    private static bool CanReuseComposeArena(ComposeArenaEntry entry, WorldDefinition definition) {
        var source = entry.Source;
        var before = source;
        var after = definition.StateRaw!;

        if ((entry.Arena.Layout.Options != WorldSlotLanes.Options(definition: definition)) ||
            !ReferenceEquals(objA: before.Body, objB: after.Body) || !ReferenceEquals(objA: before.Identity, objB: after.Identity) ||
            !ReferenceEquals(objA: before.Lattices, objB: after.Lattices) || !ReferenceEquals(objA: before.Spaces, objB: after.Spaces) ||
            !ReferenceEquals(objA: before.Enums, objB: after.Enums) || !ReferenceEquals(objA: before.Families, objB: after.Families) ||
            !ReferenceEquals(objA: before.Records, objB: after.Records)) {
            return false;
        }
        var rows = definition.AuthoredState;
        var previous = (source.World ?? []);

        if (rows.Count != previous.Count) { return false; }
        for (var index = 0; (index < rows.Count); index++) {
            if (!ReferenceEquals(objA: rows[index], objB: previous[index]) && !SameComposeDeclaration(rows[index], previous[index])) {
                return false;
            }
        }
        return true;
    }
    private static bool SameComposeDeclaration(WorldStateRow row, WorldStateRow previous) {
        if ((row.Name != previous.Name) || (row.Kind != previous.Kind) || (row.Shape != previous.Shape) ||
            (row.Min != previous.Min) || (row.Max != previous.Max) || (row.Capacity != previous.Capacity) ||
            (row.Overflow != previous.Overflow) || (row.Evicts != previous.Evicts) || (row.GatesDrive != previous.GatesDrive) ||
            (row.Advance != previous.Advance) || (row.Draw != previous.Draw) || (row.Dynamics != previous.Dynamics) ||
            (row.Field != previous.Field) || (row.Cycle != previous.Cycle) || (row.Domain != previous.Domain) ||
            (row.ValuesFrom != previous.ValuesFrom) || (row.Inverse != previous.Inverse) || (row.Phase != previous.Phase) ||
            (row.Visibility != previous.Visibility) || (row.Knowledge != previous.Knowledge) || (row.PhaseOf != previous.PhaseOf) ||
            (row.Space != previous.Space) || (row.Enum != previous.Enum) || (row.HostOwned != previous.HostOwned) ||
            (row.Generated != previous.Generated) || (row.Verdict != previous.Verdict) || (row.Witness != previous.Witness)) { return false; }
        // Only explicit per-cell time traits are declaration. All other cell fields live in columns.
        foreach (var cell in (row.Cells ?? [])) {
            if ((cell.Advance is null) && (cell.Cycle is null) && (cell.Dynamics is null)) { continue; }
            StateCell? before = null;

            foreach (var candidate in (previous.Cells ?? [])) {
                if (candidate.Key == cell.Key) { before = candidate; break; }
            }
            if ((before is null) || (cell.Advance != before.Advance) || (cell.Cycle != before.Cycle) || (cell.Dynamics != before.Dynamics)) { return false; }
        }
        foreach (var cell in (previous.Cells ?? [])) {
            if ((cell.Advance is null) && (cell.Cycle is null) && (cell.Dynamics is null)) { continue; }
            var found = false;

            foreach (var candidate in (row.Cells ?? [])) {
                if (candidate.Key == cell.Key) {
                    found = ((candidate.Advance == cell.Advance) && (candidate.Cycle == cell.Cycle) && (candidate.Dynamics == cell.Dynamics));
                    break;
                }
            }
            if (!found) { return false; }
        }
        return true;
    }
    private static void CloseComposeArena(StateArena arena, int mark) {
        arena.Rewind(mark: mark);
        if (ComposeEntries.TryGetValue(key: arena, value: out var entry)) {
            Interlocked.Exchange(location1: ref entry.Pool.Available, value: entry);
        }
    }
    private static bool TryOpenComposeArena(WorldDefinition definition, in ArenaTime time, [NotNullWhen(true)] out StateArena? arena, out int mark, out string reason, string? row = null, IReadOnlySet<string>? seeded = null) {
        arena = null;
        mark = 0;
        try {
            if (((row is not null) || (seeded is not null)) && ExportsWrittenRows(definition: definition)) {
                var pool = ComposeArenas.GetValue(definition.StateCatalog, static _ => new ComposeArenaPool());
                var entry = Interlocked.Exchange(location1: ref pool.Available, value: null);

                if ((entry is null) || !CanReuseComposeArena(definition: definition, entry: entry)) {
                    if (!StateArena.TryCreateSeeding(
                        catalog: definition.StateCatalog, section: definition.StateRaw,
                        options: WorldSlotLanes.Options(definition: definition), time: in time,
                        seeded: NoSeedRows, arena: out arena, reason: out reason)) { return false; }
                    entry = new ComposeArenaEntry(Arena: arena, Pool: pool, Source: definition.StateRaw!);
                    ComposeEntries.Add(key: arena, value: entry);
                }
                arena = entry.Arena;
                mark = arena.BeginScope();
                if (!arena.TryLoadSeeding(definition.AuthoredState, (seeded ?? new HashSet<string>(comparer: StringComparer.Ordinal) { row! }), in time, out reason)) {
                    CloseComposeArena(arena: arena, mark: mark);
                    arena = null;
                    return false;
                }
                return true;
            }

            if (!StateArena.TryCreate(
                arena: out arena,
                catalog: definition.StateCatalog,
                options: WorldSlotLanes.Options(definition: definition),
                reason: out reason,
                section: definition.StateRaw,
                time: in time
            )) { return false; }
            mark = arena.BeginScope();
            return true;
        } catch (InvalidOperationException layout) {
            if ((arena is not null) && (arena.Journal.Scopes > 0)) { CloseComposeArena(arena: arena, mark: mark); }
            arena = null;
            reason = layout.Message;

            return false;
        }
    }
    // The candidate's state section is the arena's export: every exported row is the row the arena was built over
    // with its stored columns written back, so a WorldStateRow's own traits ride across and the cast is total. A row
    // the open compose scope did not write, other than `declaredOrdinal` (a row the compose loaded from a submitted
    // declaration), exports as the row it was loaded from, so it keeps the definition's own instance: an install
    // reads an unchanged instance as a row the mutation left alone.
    private static WorldDefinition ExportComposeArena(WorldDefinition definition, StateArena arena, int declaredOrdinal = -1) {
        if (TryExportWrittenRows(
            arena: arena,
            declaredOrdinal: declaredOrdinal,
            definition: definition,
            exported: out var written
        )) {
            return written;
        }

        var exported = arena.ToRows();
        var rows = new WorldStateRow[exported.Count];

        for (var index = 0; (index < exported.Count); index++) {
            rows[index] = ((WorldStateRow)exported[index]);
        }

        return definition.WithWorldState(rows: rows, pools: ((definition.StateRaw?.Pools is { Count: > 0 }) ? arena.ToPools() : null), pairPools: ((definition.StateRaw?.PairPools is { Count: > 0 }) ? arena.ToPairPools() : null));
    }
    // Whether the definition's authored rows are its catalog's document rows, one for one in catalog order, with no
    // pool storage expanding them: the condition under which a compose exports only the rows it wrote.
    private static bool ExportsWrittenRows(WorldDefinition definition) {
        var installed = definition.AuthoredState;
        var descriptors = definition.StateCatalog.Descriptors;
        var aligned = (definition.StateRaw is { Pools: not { Count: > 0 }, PairPools: not { Count: > 0 } });
        var index = 0;

        for (var ordinal = 0; (aligned && (ordinal < descriptors.Count)); ordinal++) {
            if (descriptors[ordinal].Lane == StateLane.Document) {
                aligned = (
                    (index < installed.Count) &&
                    string.Equals(
                        a: installed[index].Name.Value,
                        b: descriptors[ordinal].Name,
                        comparisonType: StringComparison.Ordinal
                    )
                );
                index++;
            }
        }

        return (aligned && (index == installed.Count));
    }
    // Exports only the rows the open compose scope wrote, and `declaredOrdinal`; false sends the caller to the whole
    // export.
    private static bool TryExportWrittenRows(WorldDefinition definition, StateArena arena, int declaredOrdinal, out WorldDefinition exported) {
        exported = definition;

        if (
            (arena.Journal.Scopes == 0) ||
            !ReferenceEquals(objA: arena.Catalog, objB: definition.StateCatalog) ||
            !ExportsWrittenRows(definition: definition)
        ) {
            return false;
        }

        var installed = definition.AuthoredState;
        var descriptors = arena.Catalog.Descriptors;
        var count = arena.Layout.RowCount;
        var open = new bool[count];

        arena.FlagOpenRows(rows: open);

        WorldStateRow[]? rows = null;
        var index = 0;

        for (var ordinal = 0; (ordinal < count); ordinal++) {
            if (descriptors[ordinal].Lane != StateLane.Document) {
                continue;
            }
            if (open[ordinal] || (ordinal == declaredOrdinal)) {
                rows ??= [.. installed];
                rows[index] = ((WorldStateRow)arena.ToRow(rowOrdinal: ordinal));
            }

            index++;
        }

        exported = ((rows is null)
            ? definition
            : definition.WithWorldState(rows: rows)
        );

        return true;
    }
    // Applies one cell write through the arena's own kernels. `working` supplies the row RECORD — its kind, its
    // cycle and eviction declarations — so a same-batch row declaration ahead of this write is what the write sees;
    // every VALUE the write reads (a cycle's current token, an add's addend) comes from the arena, live at
    // `time`, and the store itself is the arena's, so envelope, capacity, eviction and clock settling are decided
    // once, where a rule firing decides them.
    private static bool TryApplyCellUpsert(StateArena arena, WorldDefinition working, WorldMutation.UpsertStateCell mutation, in ArenaTime time, out string reason, out CellName? evictedKey) {
        reason = string.Empty;
        evictedKey = null;
        // The one door: every row-existence and row-kind decision this write depends on is asked here, against
        // the candidate this compose has built so far, never at the console verb, which cannot know whether a
        // same-batch UpsertStateRow ahead of this one has already declared (or redeclared the kind of) the row
        // it names.
        if (WorldDefinitionRows.FindStateRow(
            rows: working.State,
            name: mutation.Row
        ) is not { } row) {
            reason = $"no state row named '{mutation.Row}' — declare it first with world.row.set state <json>";

            return false;
        }

        if (row.Inverse is { } inverse) {
            reason = $"state row '{mutation.Row}' is a derived board (inverse names '{inverse.Tokens}'/'{inverse.Codes}') — write those rows instead; the engine recomputes '{mutation.Row}' on install";

            return false;
        }

        if (row.IsRuleWritten) {
            reason = WorldVerdict.RefuseWrite(row: row);

            return false;
        }

        if (!CellName.TryParse(
            candidate: mutation.Key,
            name: out var cellKey,
            reason: out var keyReason
        )) {
            reason = $"state row '{mutation.Row}' cell key '{mutation.Key}' {keyReason}";

            return false;
        }

        if (!arena.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: mutation.Row
        )) {
            reason = $"no state row named '{mutation.Row}' — declare it first with world.row.set state <json>";

            return false;
        }

        var rowOrdinal = handle.Ordinal;

        if (
            (mutation.Vector is not null) &&
            (row.Kind != CellKind.Vector)
        ) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' is not vector-kind and takes a {StateSpelling.Kind(kind: row.Kind)} operand, never a vector one";

            return false;
        }

        if (row.Kind == CellKind.Vector) {
            if (mutation.Kind != WorldDocumentWriteKind.Set) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' — 'add' is refused on a vector-kind row";

                return false;
            }

            if (mutation.Text is not null) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' is vector-kind and takes a vector operand, never a text one";

                return false;
            }

            if (mutation.CycleTokens is not null) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' cycle is refused on a vector-kind row";

                return false;
            }

            if (!WorldStateSpaces.TryResolveSpace(
                spaces: working.Spaces,
                row: row,
                space: out var space,
                reason: out var spaceReason
            )) {
                reason = spaceReason;

                return false;
            }

            StateVector vectorToWrite;

            if (mutation.Vector is not null) {
                vectorToWrite = mutation.Vector;
            } else if (mutation.RawToken is { } rawToken) {
                if (!StateVector.TryParseBase64Url(
                    text: rawToken,
                    dimensions: space.Identity.Dimensions,
                    vector: out var parsedVector,
                    error: out var parseError
                )) {
                    reason = $"state row '{mutation.Row}' cell '{mutation.Key}' {parseError}";

                    return false;
                }

                vectorToWrite = parsedVector;
            } else {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' requires a vector operand";

                return false;
            }

            if (vectorToWrite.Dimensions != space.Identity.Dimensions) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' vector dimensions {vectorToWrite.Dimensions} do not match space '{space.Name}' dimensions {space.Identity.Dimensions}";

                return false;
            }

            return TryStoreCarried(
                arena: arena,
                cellKey: cellKey,
                evictedKey: out evictedKey,
                keyText: mutation.Key,
                reason: out reason,
                rowName: mutation.Row,
                rowOrdinal: rowOrdinal,
                value: CellValue.Vector(components: vectorToWrite.Memory)
            );
        }

        // Whether this write is a text write is a fact of the write, not the row — a text write always
        // carries a non-null Text (even ""), a numeric one never does. Asking it this way
        // (rather than switching on row.Kind) is what lets a kind-mismatched write refuse by name instead of
        // silently composing against the wrong field: a numeric write against a text row would
        // otherwise fall into this arm with Text null and overwrite the cell with an empty string.
        var isTextWrite = (mutation.Text is not null);
        // A cycle on a text row: the next token after the one the live cell reads, wrapping; the write
        // is then an ordinary text set of that token.
        var isTextCycle = ((row.Kind == CellKind.Text) && (mutation.CycleTokens is { Count: >= 2 }));

        if (
            isTextCycle &&
            (mutation.Kind != WorldDocumentWriteKind.Set)
        ) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' cycle needs a set write";

            return false;
        }

        if (
            isTextWrite ||
            isTextCycle
        ) {
            if (row.Kind != CellKind.Text) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' is not a text row";

                return false;
            }

            var textToWrite = mutation.Text!;

            if (isTextCycle) {
                var currentText = ((arena.Keys.TryResolve(
                    key: out var currentKey,
                    name: cellKey
                ) &&
                    arena.TryReadLive(
                    key: currentKey,
                    rowOrdinal: rowOrdinal,
                    time: in time,
                    value: out var carried
                ) &&
                    (carried.Kind == CellKind.Text))
                    ? carried.AsText
                    : null
                );

                textToWrite = NextInCycle(
                    tokens: mutation.CycleTokens!,
                    matches: token => string.Equals(
                        a: token,
                        b: currentText,
                        comparisonType: StringComparison.Ordinal
                    )
                );
            }

            return TryStoreCarried(
                arena: arena,
                cellKey: cellKey,
                evictedKey: out evictedKey,
                keyText: mutation.Key,
                reason: out reason,
                rowName: mutation.Row,
                rowOrdinal: rowOrdinal,
                value: CellValue.Text(value: textToWrite)
            );
        }

        // The reverse kind mismatch: a numeric operand against a Text-kind row. This, and the bool+add
        // refusal below, are the two kind-dependent refusals the console verb used to ask before submitting —
        // asked here so they see the same candidate row the existence check above just resolved, rather than
        // whatever the live definition happened to hold at text-submit time.
        if (row.Kind == CellKind.Text) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' is text-kind and takes a text operand, never a numeric one";

            return false;
        }

        if (
            (mutation.Kind == WorldDocumentWriteKind.Add) &&
            (row.Kind == CellKind.Bool)
        ) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' — 'add' is refused on a bool-kind row";

            return false;
        }

        if (
            (mutation.CycleTokens is not null) &&
            ((mutation.CycleTokens.Count < 2) || (mutation.Kind != WorldDocumentWriteKind.Set))
        ) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' cycle needs at least two tokens and a set write";

            return false;
        }

        // The honest encoding for a payload whose shape depends on the row's kind: a console write carries
        // the un-interpreted wire token (RawToken) because it cannot know Fixed-vs-Int-vs-Bool before this
        // row's kind resolves against the candidate; a caller that already knows the kind (the rule-effect
        // engine, which reads the destination row itself before submitting) carries the resolved Value
        // directly. See WorldMutation.UpsertStateCell.RawToken's remarks.
        long operand;

        if (mutation.CycleTokens is { Count: >= 2 } cycleTokens) {
            // A cycle on a numeric row: every token must parse against this row's kind; the operand is the
            // token after the one the live value equals (wrapping), else the first. The live value is the
            // same read every gate and binding runs, so an advancing row cycles from what a reader sees.
            var parsed = new long[cycleTokens.Count];

            for (var index = 0; (index < parsed.Length); index++) {
                if (!CellValue.TryParse(
                    kind: row.Kind,
                    reason: out var cycleReason,
                    symbols: working.EnumOf(row: row),
                    token: cycleTokens[index],
                    value: out var carried
                )) {
                    reason = $"state row '{mutation.Row}' cell '{mutation.Key}' {cycleReason}";

                    return false;
                }

                parsed[index] = carried.Raw;
            }

            var live = (ReadLiveNumber(
                arena: arena,
                cellKey: cellKey,
                rowOrdinal: rowOrdinal,
                time: in time
            ) ?? long.MinValue);
            var at = Array.IndexOf(
                array: parsed,
                value: live
            );

            operand = parsed[((at < 0)
                ? 0
                : ((at + 1) % parsed.Length))];
        } else if (mutation.RawToken is { } rawToken) {
            if (!CellValue.TryParse(
                kind: row.Kind,
                reason: out var tokenReason,
                symbols: working.EnumOf(row: row),
                token: rawToken,
                value: out var parsedToken
            )) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' {tokenReason}";

                return false;
            }

            operand = parsedToken.Raw;
        } else {
            operand = mutation.Value;
        }

        return TryStoreNumber(
            arena: arena,
            cellKey: cellKey,
            evictedKey: out evictedKey,
            keyText: mutation.Key,
            operand: operand,
            reason: out reason,
            rowName: mutation.Row,
            rowOrdinal: rowOrdinal,
            time: in time,
            write: ((mutation.Kind == WorldDocumentWriteKind.Add)
                ? StateWriteKind.Add
                : StateWriteKind.Set
            )
        );
    }
    // Composes one cell removal onto the row it names, on the same terms as TryApplyCellUpsert.
    private static bool TryApplyCellRemove(StateArena arena, WorldDefinition working, WorldMutation.RemoveStateCell mutation, out string reason) {
        reason = string.Empty;

        if (WorldDefinitionRows.FindStateRow(
            rows: working.State,
            name: mutation.Row
        ) is not { } row) {
            reason = $"no state row named '{mutation.Row}'";

            return false;
        }

        if (row.Inverse is { } inverse) {
            reason = $"state row '{mutation.Row}' is a derived board (inverse names '{inverse.Tokens}'/'{inverse.Codes}') — write those rows instead; the engine recomputes '{mutation.Row}' on install";

            return false;
        }

        if (row.IsRuleWritten) {
            reason = WorldVerdict.RefuseWrite(row: row);

            return false;
        }

        if (
            !arena.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: mutation.Row
        ) ||
            !CellName.TryParse(
            candidate: mutation.Key,
            name: out var cellKey,
            reason: out _
        ) ||
            !arena.Keys.TryResolve(
            key: out var key,
            name: cellKey
        )
        ) {
            reason = $"state row '{mutation.Row}' has no cell keyed '{mutation.Key}'";

            return false;
        }

        if (!arena.TryRemove(
            key: key,
            reason: out var removeReason,
            rowOrdinal: handle.Ordinal
        )) {
            reason = removeReason;

            return false;
        }

        return true;
    }
    // The live number a cell reads at `time`, or null when the row holds no cell under the key.
    private static long? ReadLiveNumber(StateArena arena, int rowOrdinal, CellName cellKey, in ArenaTime time) => ((arena.Keys.TryResolve(
        key: out var key,
        name: cellKey
    ) &&
        arena.TryReadLiveNumber(
        key: key,
        rowOrdinal: rowOrdinal,
        time: in time,
        value: out var value
    ))
        ? value
        : null
    );
    // The one door a composed numeric operand reaches the arena through: the live write — which rebases an advancing
    // cell, kicks an easing one, and leaves a rotating cell's clock where it is — or a mint when the row holds no
    // cell under the key yet.
    private static bool TryStoreNumber(StateArena arena, int rowOrdinal, CellName cellKey, string rowName, string keyText, long operand, StateWriteKind write, in ArenaTime time, out string reason, out CellName? evictedKey) {
        if (!TryInternCell(
            arena: arena,
            cellKey: cellKey,
            key: out var key,
            keyText: keyText,
            reason: out reason,
            rowName: rowName
        )) {
            evictedKey = null;

            return false;
        }

        return arena.TryWriteLiveOrMint(
            evicted: out evictedKey,
            key: key,
            operand: operand,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            time: in time,
            write: write
        );
    }
    // The text and vector counterpart of TryStoreNumber: neither kind carries a value-over-time trait, so the
    // carried value is stored as it stands.
    private static bool TryStoreCarried(StateArena arena, int rowOrdinal, CellName cellKey, string rowName, string keyText, CellValue value, out string reason, out CellName? evictedKey) {
        if (!TryInternCell(
            arena: arena,
            cellKey: cellKey,
            key: out var key,
            keyText: keyText,
            reason: out reason,
            rowName: rowName
        )) {
            evictedKey = null;

            return false;
        }

        return arena.TryWriteOrMint(
            evicted: out evictedKey,
            key: key,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            value: value
        );
    }
    // The key is interned before the row is addressed: a lattice or ring row resolves a cell's position from the
    // interned NAME, so a write naming one of its own addresses reaches it rather than reading as absent. Every
    // admission the write itself needs — the envelope, the reserved-cell rule, the capacity and its eviction —
    // belongs to the arena door, not here.
    private static bool TryInternCell(StateArena arena, CellName cellKey, string rowName, string keyText, out CellKey key, out string reason) {
        if (!arena.Keys.TryIntern(
            key: out key,
            name: cellKey,
            reason: out var internReason
        )) {
            reason = $"state row '{rowName}' cell '{keyText}' {internReason}";

            return false;
        }

        reason = string.Empty;

        return true;
    }
}
