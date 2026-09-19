using System.Diagnostics.CodeAnalysis;
using Puck.World.Protocol;
using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // Composes a batch as one edit. Members compose in order against what the members before them built, each
    // re-basing its own cell traits as it would alone, so a batch installs exactly the document its members would
    // have reached one by one — a member that reads a row an earlier member wrote reads the written value, and a
    // member that fails refuses the whole batch with the document unchanged.
    //
    // A batch holds ONE arena. Every cell write and removal applies through its kernels, so the batch pays one load
    // and one export for however many cell members it carries rather than one document rebuild per member, and a
    // same-batch row declaration ahead of a write is what the write sees because the declaring member lands in
    // `working` and the next cell member loads its arena from there. `working` is re-seated over the arena's export
    // only where something other than a cell write needs it — before a non-cell member composes, before a
    // document-value rehydration actually runs, and once more at the end. The arena is dropped only when the
    // document it was loaded from is no longer the one `working` holds: a member that rewrites the state section
    // invalidates it, and every other member leaves it standing.
    //
    // The document-value refresh never depended on the member: whether anything in the document is bound to the row
    // a write names is a fact of the sections around the state table, which a cell write cannot change, so collecting
    // the referenced-row set (a structural read, never a value read) never itself needs an export — only the
    // rehydration a hit triggers does. A member that can add or drop a reference — a whole-row write, or an edit to
    // any other section — drops the set, so the next state member collects it again from the document that member
    // produced.
    private static bool TryComposeBatch(WorldDefinition current, WorldMutation.Batch batch, ulong tick, ulong engineTick, string instanceIdentity, out WorldDefinition candidate, out string reason, out CellName? evictedKey) {
        var working = current;
        var time = ComposeTime(
            definition: current,
            engineTick: engineTick,
            tick: tick
        );
        StateArena? arena = null;
        var mark = 0;
        var arenaDirty = false;
        HashSet<string>? referencedRows = null;

        reason = string.Empty;
        evictedKey = null;
        if (
            (batch.ExpectedDefinition is { } expected) &&
            (expected != WorldDefinitionFingerprint.Compute(
            definition: current,
            stateRows: batch.ExpectedStateRows
        ))
        ) {
            candidate = current;
            reason = "the proposal is stale: its base definition changed; preview again";
            return false;
        }
        if (
            (batch.ExpectedInputs is { } inputs) &&
            (inputs.Fingerprint != WorldDefinitionFingerprint.ComputeInputs(
            definition: current,
            placementIds: inputs.PlacementIds,
            stateRows: inputs.StateRows
        ))
        ) {
            candidate = current;
            reason = "the proposal is stale: its named inputs changed; preview again";
            return false;
        }
        foreach (var spatial in (batch.ExpectedSpatialReads ?? [])) {
            var region = spatial.Region;

            if (spatial.Fingerprint != WorldDefinitionFingerprint.ComputeSpatial(
                definition: current,
                region: region
            )) {
                candidate = current;
                reason = "the proposal is stale: its spatial read changed; preview again";
                return false;
            }
        }
        foreach (var cell in (batch.ExpectedCells ?? [])) {
            if (
                !WorldStateReader.TryRead(
                current,
                cell.Row,
                cell.Key,
                tick,
                engineTick,
                out var row,
                out var raw,
                out var text
            ) ||
                (raw is not { } value) ||
                ((cell.Kind is { } kind) && (row.Kind != kind)) ||
                ((cell.Text is not null) && (text != cell.Text)) ||
                ((cell.Kind != CellKind.Text) && !cell.Comparison.Holds(
                FixedQ4816.FromRawBits(value: value),
                FixedQ4816.FromRawBits(value: cell.Value)
            ))
            ) {
                candidate = current;
                reason = $"the proposal is stale: state '{cell.Row}' changed; preview again";
                return false;
            }
        }

        try {
            for (var index = 0; (index < batch.Mutations.Count); index++) {
                var member = batch.Mutations[index];
                string? writtenRow = null;

                switch (member) {
                    case WorldMutation.UpsertStateCell upsert: {
                            if (!TryOpenBatchArena(
                                arena: out var upsertArena,
                                held: arena,
                                mark: ref mark,
                                reason: out reason,
                                time: in time,
                                working: working
                            )) {
                                candidate = current;

                                return false;
                            }

                            arena = upsertArena;

                            if (!TryApplyCellUpsert(
                                arena: upsertArena,
                                evictedKey: out evictedKey,
                                mutation: upsert,
                                reason: out reason,
                                time: in time,
                                working: working
                            )) {
                                candidate = current;

                                return false;
                            }

                            arenaDirty = true;
                            writtenRow = upsert.Row;

                            break;
                        }
                    case WorldMutation.RemoveStateCell remove: {
                            if (!TryOpenBatchArena(
                                arena: out var removeArena,
                                held: arena,
                                mark: ref mark,
                                reason: out reason,
                                time: in time,
                                working: working
                            )) {
                                candidate = current;

                                return false;
                            }

                            arena = removeArena;

                            if (!TryApplyCellRemove(
                                arena: removeArena,
                                mutation: remove,
                                reason: out reason,
                                working: working
                            )) {
                                candidate = current;

                                return false;
                            }

                            arenaDirty = true;
                            writtenRow = remove.Row;

                            break;
                        }
                    default: {
                            SyncArenaExport(
                                arena: arena,
                                arenaDirty: ref arenaDirty,
                                working: ref working
                            );

                            if (!TryComposeCore(
                                candidate: out var next,
                                current: working,
                                engineTick: engineTick,
                                evictedKey: out evictedKey,
                                instanceIdentity: instanceIdentity,
                                mutation: member,
                                reason: out reason,
                                tick: tick
                            )) {
                                candidate = current;

                                return false;
                            }

                            if (!PreservesReferencedRows(mutation: member)) {
                                referencedRows = null;
                            }

                            working = next;
                            writtenRow = StateRowOf(mutation: member);

                            // A member that rewrote the state section left the arena holding a document nothing
                            // downstream still reads from, so the next cell member loads a fresh one over what this
                            // member produced.
                            if (
                                (writtenRow is not null) ||
                                !PreservesStateSection(mutation: member)
                            ) {
                                arena = null;
                            }

                            if (writtenRow is not null) {
                                if (!TryRefreshReferenced(
                                    arena: arena,
                                    arenaDirty: ref arenaDirty,
                                    reason: out reason,
                                    referencedRows: ref referencedRows,
                                    rowName: writtenRow,
                                    working: ref working
                                )) {
                                    candidate = current;

                                    return false;
                                }

                                writtenRow = null;
                            }

                            break;
                        }
                }

                if (writtenRow is null) {
                    continue;
                }

                if (!TryRefreshReferenced(
                    arena: arena,
                    arenaDirty: ref arenaDirty,
                    reason: out reason,
                    referencedRows: ref referencedRows,
                    rowName: writtenRow,
                    working: ref working
                )) {
                    candidate = current;

                    return false;
                }
            }

            SyncArenaExport(
                arena: arena,
                arenaDirty: ref arenaDirty,
                working: ref working
            );

            foreach (var cell in (batch.ExpectedCells ?? [])) {
                if (cell.Change is not { } change) { continue; }
                if (
                    !WorldStateReader.TryRead(
                    current,
                    cell.Row,
                    cell.Key,
                    tick,
                    engineTick,
                    out var beforeRow,
                    out var before,
                    out _
                ) ||
                    !WorldStateReader.TryRead(
                    working,
                    cell.Row,
                    cell.Key,
                    tick,
                    engineTick,
                    out var afterRow,
                    out var after,
                    out _
                ) ||
                    (before is null) ||
                    (after is null) ||
                    (beforeRow.Kind != afterRow.Kind) ||
                    ((((Int128)after.Value) - before.Value) != change) ||
                    ((afterRow.Min is { } minimum) && (after.Value < minimum)) ||
                    ((afterRow.Max is { } maximum) && (after.Value > maximum))
                ) {
                    candidate = current;
                    reason = $"state '{cell.Row}' constraints prevent the exact required change";
                    return false;
                }
            }
            candidate = working;

            return true;
        } finally {
            // The batch's own store is scratch: the candidate it exported is what the ordinary mutation pipeline
            // installs, so the arena is rewound whether the batch composed or refused.
            arena?.Rewind(mark: mark);
        }
    }
    // Loads the batch's arena over the document `working` currently holds, or leaves the one already open — which
    // is the same document, since every member that could have rewritten the state section drops it.
    private static bool TryOpenBatchArena(WorldDefinition working, in ArenaTime time, StateArena? held, ref int mark, [NotNullWhen(true)] out StateArena? arena, out string reason) {
        reason = string.Empty;

        if (held is not null) {
            arena = held;

            return true;
        }

        if (!TryOpenComposeArena(
            arena: out arena,
            reason: out reason,
            definition: working,
            time: in time
        )) {
            return false;
        }

        mark = arena.BeginScope();

        return true;
    }
    // Re-seats `working` over the arena's export — the one point a batch's store becomes a document, so
    // WorldStateSection's own copy is made once for however many writes preceded this call, not once per write.
    private static void SyncArenaExport(ref WorldDefinition working, StateArena? arena, ref bool arenaDirty) {
        if (
            !arenaDirty ||
            (arena is null)
        ) {
            return;
        }

        working = ExportComposeArena(
            arena: arena,
            definition: working
        );
        arenaDirty = false;
    }
    // Rehydrates the running candidate when a document value is bound to the written row, collecting the
    // referenced-row set on the first state member that asks and reusing it until a member that can change it
    // drops it. Collecting the set is a structural read (which rows a document value names, never their live
    // content), so it never needs an export; only an actual hit — the rehydration below — does, since that reads
    // the written row's current value back out of `working`.
    private static bool TryRefreshReferenced(ref WorldDefinition working, ref HashSet<string>? referencedRows, string rowName, StateArena? arena, ref bool arenaDirty, out string reason) {
        reason = string.Empty;

        if (referencedRows is null) {
            referencedRows = new HashSet<string>(comparer: StringComparer.Ordinal);
            WorldStateDocumentValues.CollectReferencedRows(
                definition: working,
                rows: referencedRows
            );
        }

        if (!referencedRows.Contains(item: rowName)) {
            return true;
        }

        SyncArenaExport(
            arena: arena,
            arenaDirty: ref arenaDirty,
            working: ref working
        );

        // A rehydration re-resolves document values bound to state rows; it never rewrites the state section
        // itself, so the arena still holds the rows `working` does and stays open.
        return WorldStateDocumentValues.TryRehydrate(
            definition: working,
            reason: out reason,
            refreshed: out working
        );
    }
    // The members that change cell values, cursors, or masks and nothing else: none can introduce or remove a
    // document-value reference, so the referenced-row set collected before one still holds after it.
    private static bool PreservesReferencedRows(WorldMutation mutation) => (mutation is
        WorldMutation.UpsertStateCell or WorldMutation.RemoveStateCell or
        WorldMutation.TransformState or WorldMutation.Generate);
    // The members that leave the state section as the batch's arena holds it. A nested batch is excluded: what it
    // composed is not known here.
    private static bool PreservesStateSection(WorldMutation mutation) => (mutation is not
        (WorldMutation.TransformState or WorldMutation.Generate or WorldMutation.Batch));
}
