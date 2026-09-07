using Puck.World.Protocol;
using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Composes a batch as one edit. Members compose in order against what the members before them built, each
    // re-basing its own cell traits as it would alone, so a batch installs exactly the document its members would
    // have reached one by one — a member that reads a row an earlier member wrote reads the written value, and a
    // member that fails refuses the whole batch with the document unchanged.
    //
    // A cell write or removal lands in a workspace: one copy of the row list, held by one definition built over it,
    // that every such member writes into in place. The workspace is private to this call, and the definition it
    // hands back holds the list nobody writes again, so the sharing a record `with` relies on is never violated. A
    // member of any other kind composes through its own arm against the workspace's definition and its result
    // becomes the running candidate; the next cell write opens a fresh workspace over it.
    //
    // The document-value refresh never depended on the member: whether anything in the document is bound to the row
    // a write names is a fact of the sections around the state table, which a cell write cannot change. The batch
    // walks the graph once for the set of referenced rows and asks each member's row against it; a member whose
    // row is in the set rehydrates exactly where the one-by-one compose would have. A member that can add or drop a
    // reference — a whole-row write, or an edit to any other section — drops the set, so the next state member
    // collects it again from the document that member produced.
    private static bool TryComposeBatch(WorldDefinition current, WorldMutation.Batch batch, ulong tick, string instanceIdentity, out WorldDefinition candidate, out string reason, out CellName? evictedKey, CompiledPatterns? patterns) {
        var working = current;
        List<WorldStateRow>? workspace = null;
        var workspaceDerivesBoards = false;
        HashSet<string>? referencedRows = null;

        reason = string.Empty;
        evictedKey = null;
        if (batch.ExpectedDefinition is { } expected && expected != WorldDefinitionFingerprint.Compute(current, batch.ExpectedStateRows)) {
            candidate = current;
            reason = "the proposal is stale: its base definition changed; preview again";
            return false;
        }
        if (batch.ExpectedInputs is { } inputs && inputs.Fingerprint != WorldDefinitionFingerprint.ComputeInputs(current, inputs.PlacementIds, inputs.StateRows)) {
            candidate = current;
            reason = "the proposal is stale: its named inputs changed; preview again";
            return false;
        }
        foreach (var spatial in batch.ExpectedSpatialReads ?? []) {
            var region = spatial.Region;
            if (spatial.Fingerprint != WorldDefinitionFingerprint.ComputeSpatial(current, region)) {
                candidate = current;
                reason = "the proposal is stale: its spatial read changed; preview again";
                return false;
            }
        }
        foreach (var cell in batch.ExpectedCells ?? []) {
            if (!WorldStateReader.TryRead(current, cell.Row, cell.Key, tick, out var row, out var raw, out _) || raw is not { } value ||
                (cell.Kind is { } kind && row.Kind != kind) || !cell.Comparison.Holds(FixedQ4816.FromRawBits(value), FixedQ4816.FromRawBits(cell.Value))) {
                candidate = current;
                reason = $"the proposal is stale: state '{cell.Row}' changed; preview again";
                return false;
            }
        }

        for (var index = 0; index < batch.Mutations.Count; index++) {
            var member = batch.Mutations[index];
            string? writtenRow = null;

            switch (member) {
                case WorldMutation.UpsertStateCell upsert: {
                    workspace ??= OpenWorkspace(working: ref working, derivesBoards: out workspaceDerivesBoards);

                    if (!TryComposeCellUpsert(
                        composed: out var composed,
                        current: working,
                        evictedKey: out evictedKey,
                        mutation: upsert,
                        reason: out reason,
                        tick: tick
                    )) {
                        candidate = current;

                        return false;
                    }

                    PlaceRow(working: working, workspace: workspace, row: composed, rebaseCellKey: upsert.Key, tick: tick, derivesBoards: workspaceDerivesBoards);
                    writtenRow = upsert.Row;

                    break;
                }
                case WorldMutation.RemoveStateCell remove: {
                    workspace ??= OpenWorkspace(working: ref working, derivesBoards: out workspaceDerivesBoards);

                    if (!TryComposeCellRemove(
                        composed: out var composed,
                        current: working,
                        mutation: remove,
                        reason: out reason
                    )) {
                        candidate = current;

                        return false;
                    }

                    PlaceRow(working: working, workspace: workspace, row: composed, rebaseCellKey: null, tick: tick, derivesBoards: workspaceDerivesBoards);
                    writtenRow = remove.Row;

                    break;
                }
                default: {
                    var previous = working;

                    if (!TryComposeCore(
                        candidate: out var next,
                        current: working,
                        evictedKey: out evictedKey,
                        instanceIdentity: instanceIdentity,
                        mutation: member,
                        reason: out reason,
                        tick: tick,
                        patterns: patterns
                    )) {
                        candidate = current;

                        return false;
                    }

                    workspace = null;

                    if (!PreservesReferencedRows(mutation: member)) {
                        referencedRows = null;
                    }

                    working = next;
                    writtenRow = StateRowOf(mutation: member);

                    if (writtenRow is not null) {
                        if (!TryRefreshReferenced(working: ref working, referencedRows: ref referencedRows, rowName: writtenRow, reason: out reason)) {
                            candidate = current;

                            return false;
                        }

                        writtenRow = null;
                    }

                    working = RecomposeDerivedBoards(definition: working, previous: previous);
                    working = RebaseCellTraits(candidate: working, mutation: member, original: previous, tick: tick);

                    break;
                }
            }

            if (writtenRow is null) {
                continue;
            }

            if (!TryRefreshReferenced(working: ref working, referencedRows: ref referencedRows, rowName: writtenRow, reason: out reason)) {
                candidate = current;

                return false;
            }

            if (!ReferenceEquals(objA: working.State, objB: workspace)) {
                workspace = null;
            }
        }

        foreach (var cell in batch.ExpectedCells ?? []) {
            if (cell.Change is not { } change) { continue; }
            if (!WorldStateReader.TryRead(current, cell.Row, cell.Key, tick, out var beforeRow, out var before, out _) ||
                !WorldStateReader.TryRead(working, cell.Row, cell.Key, tick, out var afterRow, out var after, out _) ||
                before is null || after is null || beforeRow.Kind != afterRow.Kind || (Int128)after.Value - before.Value != change ||
                (afterRow.Min is { } minimum && after.Value < minimum) || (afterRow.Max is { } maximum && after.Value > maximum)) {
                candidate = current;
                reason = $"state '{cell.Row}' constraints prevent the exact required change";
                return false;
            }
        }
        candidate = working;

        return true;
    }
    // Copies the running candidate's rows once and re-seats the candidate over the copy, which cell members then
    // write in place.
    private static List<WorldStateRow> OpenWorkspace(ref WorldDefinition working, out bool derivesBoards) {
        var rows = working.State;
        var workspace = new List<WorldStateRow>(collection: rows);

        derivesBoards = false;

        for (var index = 0; index < rows.Count; index++) {
            if (rows[index].Inverse is not null) {
                derivesBoards = true;

                break;
            }
        }

        working = working.WithWorldState(rows: workspace);

        return workspace;
    }
    // Places a composed row into the workspace under the row it replaces, re-basing the written cell's traits
    // against the row as it stood and recomposing every derived board the row feeds — the same two steps the
    // one-by-one compose takes after each member, over the workspace instead of a fresh document.
    private static void PlaceRow(WorldDefinition working, List<WorldStateRow> workspace, WorldStateRow row, string? rebaseCellKey, ulong tick, bool derivesBoards) {
        var index = IndexOfStateRow(rows: workspace, name: row.Name);
        var originalRow = workspace[index];

        workspace[index] = ((rebaseCellKey is null)
            ? row
            : RebaseCellTraits(cellKey: rebaseCellKey, original: working, originalRow: originalRow, row: row, tick: tick)
        );

        if (derivesBoards) {
            RecomposeDerivedBoardsFedBy(definition: working, rows: workspace, sourceName: row.Name.Value);
        }
    }
    // Rehydrates the running candidate when a document value is bound to the written row, collecting the
    // referenced-row set on the first state member that asks and reusing it until a member that can change it
    // drops it.
    private static bool TryRefreshReferenced(ref WorldDefinition working, ref HashSet<string>? referencedRows, string rowName, out string reason) {
        reason = string.Empty;

        if (referencedRows is null) {
            referencedRows = new HashSet<string>(comparer: StringComparer.Ordinal);
            WorldStateDocumentValues.CollectReferencedRows(definition: working, rows: referencedRows);
        }

        return (
            !referencedRows.Contains(item: rowName) ||
            WorldStateDocumentValues.TryRehydrate(
                definition: working,
                refreshed: out working,
                reason: out reason
            )
        );
    }
    // The workspace form of RecomposeDerivedBoards: only a board whose tokens or codes row is the one just
    // written can have changed, so only those recompute.
    private static void RecomposeDerivedBoardsFedBy(WorldDefinition definition, List<WorldStateRow> rows, string sourceName) {
        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];

            if (
                (row.Inverse is not { } inverse) ||
                (!string.Equals(a: inverse.Tokens.Value, b: sourceName, comparisonType: StringComparison.Ordinal) &&
                 !string.Equals(a: inverse.Codes.Value, b: sourceName, comparisonType: StringComparison.Ordinal)) ||
                (row.EffectiveDomain is not StateDomain.CellsOf board)
            ) {
                continue;
            }

            if (WorldTopologyCompilation.Find(definition, board.Topology) is not { } topology) {
                continue;
            }

            var derived = DerivedBoards.Compose(rows: rows, inverse: inverse, topology: topology);

            if (!SameCells(left: row.Cells, right: derived)) {
                rows[index] = (row with { Cells = derived });
            }
        }
    }
    private static int IndexOfStateRow(List<WorldStateRow> rows, CellName name) {
        for (var index = 0; index < rows.Count; index++) {
            if (rows[index].Name == name) {
                return index;
            }
        }

        throw new InvalidOperationException(message: $"the composed row '{name}' is not in the batch's workspace.");
    }
    // The members that change cell values, cursors, or masks and nothing else: none can introduce or remove a
    // document-value reference, so the referenced-row set collected before one still holds after it.
    private static bool PreservesReferencedRows(WorldMutation mutation) => (mutation is
        WorldMutation.UpsertStateCell or WorldMutation.RemoveStateCell or
        WorldMutation.TransformState or WorldMutation.Generate);
}
