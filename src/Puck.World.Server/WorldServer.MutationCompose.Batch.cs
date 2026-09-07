using Puck.World.Protocol;
using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Composes a batch as one edit. Members compose in order against what the members before them built, each
    // re-basing its own cell traits as it would alone, so a batch installs exactly the document its members would
    // have reached one by one — a member that reads a row an earlier member wrote reads the written value, and a
    // member that fails refuses the whole batch with the document unchanged.
    //
    // A cell write or removal reads and writes one shared workspace list — a private copy of the running document's
    // rows, owned by this call alone and never handed to a live WorldStateSection until it must be: WorldStateSection
    // owns an immutable copy of its own row list (construction and `with` alike), so a batch that froze one on every
    // member would pay that copy once per member instead of once per call. `working` is re-seated over the workspace
    // (SyncWorkspace) only where something other than a cell write needs it — before a non-cell member composes,
    // before a document-value rehydration actually runs, and once more at the end — so a run of independent cell
    // writes costs one row-list copy for the whole call, not one per write.
    //
    // The document-value refresh never depended on the member: whether anything in the document is bound to the row
    // a write names is a fact of the sections around the state table, which a cell write cannot change, so collecting
    // the referenced-row set (a structural read, never a value read) never itself needs a workspace sync — only the
    // rehydration a hit triggers does. A member that can add or drop a reference — a whole-row write, or an edit to
    // any other section — drops the set, so the next state member collects it again from the document that member
    // produced.
    private static bool TryComposeBatch(WorldDefinition current, WorldMutation.Batch batch, ulong tick, string instanceIdentity, out WorldDefinition candidate, out string reason, out CellName? evictedKey, CompiledPatterns? patterns) {
        var working = current;
        List<WorldStateRow>? workspace = null;
        var workspaceDirty = false;
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
                    workspace ??= OpenWorkspace(working: working, derivesBoards: out workspaceDerivesBoards);

                    if (!TryComposeCellUpsert(
                        composed: out var composed,
                        rows: workspace,
                        evictedKey: out evictedKey,
                        mutation: upsert,
                        reason: out reason,
                        tick: tick
                    )) {
                        candidate = current;

                        return false;
                    }

                    PlaceRow(working: working, workspace: workspace, row: composed, rebaseCellKey: upsert.Key, tick: tick, derivesBoards: workspaceDerivesBoards);
                    workspaceDirty = true;
                    writtenRow = upsert.Row;

                    break;
                }
                case WorldMutation.RemoveStateCell remove: {
                    workspace ??= OpenWorkspace(working: working, derivesBoards: out workspaceDerivesBoards);

                    if (!TryComposeCellRemove(
                        composed: out var composed,
                        rows: workspace,
                        mutation: remove,
                        reason: out reason
                    )) {
                        candidate = current;

                        return false;
                    }

                    PlaceRow(working: working, workspace: workspace, row: composed, rebaseCellKey: null, tick: tick, derivesBoards: workspaceDerivesBoards);
                    workspaceDirty = true;
                    writtenRow = remove.Row;

                    break;
                }
                default: {
                    SyncWorkspace(working: ref working, workspace: workspace, workspaceDirty: ref workspaceDirty);
                    workspace = null;

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

                    if (!PreservesReferencedRows(mutation: member)) {
                        referencedRows = null;
                    }

                    working = next;
                    writtenRow = StateRowOf(mutation: member);

                    if (writtenRow is not null) {
                        if (!TryRefreshReferenced(working: ref working, referencedRows: ref referencedRows, rowName: writtenRow, workspace: null, workspaceDirty: ref workspaceDirty, reason: out reason)) {
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

            var dirtyBeforeRefresh = workspaceDirty;

            if (!TryRefreshReferenced(working: ref working, referencedRows: ref referencedRows, rowName: writtenRow, workspace: workspace, workspaceDirty: ref workspaceDirty, reason: out reason)) {
                candidate = current;

                return false;
            }

            // A rehydration hit re-seated `working` over the workspace's content and then rebuilt other sections
            // from it — the workspace list is no longer what `working.State` holds, so the next cell write opens a
            // fresh one over the document that rehydration produced rather than continuing to write into a list
            // nothing downstream still reads from.
            if (dirtyBeforeRefresh && !workspaceDirty) {
                workspace = null;
            }
        }

        SyncWorkspace(working: ref working, workspace: workspace, workspaceDirty: ref workspaceDirty);

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
    // Copies the running candidate's rows once and reports whether any row is a derived board — read once per
    // workspace rather than re-scanned per placement, since a cell write can never add or drop the Inverse trait
    // that makes a row one. The copy is this call's own private scratch: it is written into in place across every
    // cell-write member that follows, and is never itself handed to WithWorldState until SyncWorkspace commits it.
    private static List<WorldStateRow> OpenWorkspace(WorldDefinition working, out bool derivesBoards) {
        var rows = working.State;
        var workspace = new List<WorldStateRow>(collection: rows);

        derivesBoards = false;

        for (var index = 0; index < rows.Count; index++) {
            if (rows[index].Inverse is not null) {
                derivesBoards = true;

                break;
            }
        }

        return workspace;
    }
    // Places a composed row into the workspace under the row it replaces, re-basing the written cell's traits
    // against the row as it stood and recomposing every derived board the row feeds — the same two steps the
    // one-by-one compose takes after each member, over the workspace instead of a fresh document. `working` need
    // not reflect this call's own or an earlier placement's row values: RebaseCellTraits and
    // RecomposeDerivedBoardsFedBy read the row being replaced from the workspace itself, and every other section
    // they touch (dynamics, topology) is unchanged by a cell write.
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
    // Re-seats `working` over the workspace's current content — the one point a batch's private scratch list is
    // handed to WithWorldState, so WorldStateSection's own copy is made once for however many placements preceded
    // this call, not once per placement.
    private static void SyncWorkspace(ref WorldDefinition working, List<WorldStateRow>? workspace, ref bool workspaceDirty) {
        if (!workspaceDirty) {
            return;
        }

        working = working.WithWorldState(rows: workspace!);
        workspaceDirty = false;
    }
    // Rehydrates the running candidate when a document value is bound to the written row, collecting the
    // referenced-row set on the first state member that asks and reusing it until a member that can change it
    // drops it. Collecting the set is a structural read (which rows a document VALUE names, never their live
    // content), so it never needs a workspace sync; only an actual hit — the rehydration below — does, since that
    // reads the written row's current value back out of `working`.
    private static bool TryRefreshReferenced(ref WorldDefinition working, ref HashSet<string>? referencedRows, string rowName, List<WorldStateRow>? workspace, ref bool workspaceDirty, out string reason) {
        reason = string.Empty;

        if (referencedRows is null) {
            referencedRows = new HashSet<string>(comparer: StringComparer.Ordinal);
            WorldStateDocumentValues.CollectReferencedRows(definition: working, rows: referencedRows);
        }

        if (!referencedRows.Contains(item: rowName)) {
            return true;
        }

        SyncWorkspace(working: ref working, workspace: workspace, workspaceDirty: ref workspaceDirty);

        return WorldStateDocumentValues.TryRehydrate(
            definition: working,
            refreshed: out working,
            reason: out reason
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
