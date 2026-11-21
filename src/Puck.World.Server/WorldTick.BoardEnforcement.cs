using Puck.Maths;

namespace Puck.World.Server;

/// <summary>The board-enforcement latch's checkpointed image — one remembered verdict per
/// <see cref="WorldBoardEnforcement.Return"/>-bound board binding, keyed by its carrying placement's id.</summary>
/// <param name="Entries">The remembered (placement id, verdict) pairs.</param>
public sealed record WorldBoardEnforcementCheckpoint(IReadOnlyList<(string PlacementId, long Verdict)> Entries) {
    /// <summary>Gets an empty image, for a checkpoint captured before this section existed.</summary>
    public static WorldBoardEnforcementCheckpoint Empty { get; } = new(Entries: []);
}
public sealed partial class WorldTick : IWorldPersistedSection<WorldBoardEnforcementCheckpoint> {
    // The last verdict seen at each Return-bound board placement, keyed by placement id — kept outside the
    // document because a candidate replaces state.<verdict> wholesale every tick and the edge needs last tick's
    // value to compare against. Simulation state: it hashes and checkpoints through
    // IWorldPersistedSection<WorldBoardEnforcementCheckpoint>.
    private readonly Dictionary<string, long> m_boardVerdictLatch = new(comparer: StringComparer.Ordinal);

    // A Return-bound board binding as the installed document resolves it: its placement, its binding and the catalog
    // ordinals of its verdict and move rows, -1 for a row the catalog does not hold. Resolved once per placement list
    // and catalog, which is sound because a document's rows are replaced wholesale, never changed in place.
    private readonly record struct BoardReturn(string PlacementId, WorldPlacementBoard Board, int VerdictRow, int MoveRow);

    private static readonly CellName MoveFromKey = CellName.Parse(candidate: "from");
    private static readonly CellName MoveToKey = CellName.Parse(candidate: "to");

    private readonly List<BoardReturn> m_boardReturns = [];
    private IReadOnlyList<WorldPlacement>? m_boardReturnsOf;
    private StateCatalog? m_boardReturnsCatalog;

    private void IndexBoardReturns(IReadOnlyList<WorldPlacement> placements, StateCatalog catalog) {
        if (ReferenceEquals(objA: m_boardReturnsOf, objB: placements) && ReferenceEquals(objA: m_boardReturnsCatalog, objB: catalog)) { return; }
        m_boardReturnsOf = placements;
        m_boardReturnsCatalog = catalog;
        m_boardReturns.Clear();
        foreach (var ordinal in WorldPlacementIndex.Of(placements: placements).ReturnBoards) {
            if (placements[ordinal] is not { Board: { Verdict: { } verdict, Move: { } move } board } placement) { continue; }
            m_boardReturns.Add(item: new BoardReturn(PlacementId: placement.Id, Board: board, VerdictRow: DocumentRowOrdinal(name: verdict), MoveRow: DocumentRowOrdinal(name: move)));
        }
    }
    private int DocumentRowOrdinal(string name) => ((Host.RuleHost.TryResolveDocumentRow(handle: out var handle, name: name, row: out var row) && (row is not null)) ? handle.Ordinal : -1);
    private bool TryReadKeyedCell(int rowOrdinal, CellName key, out long value) {
        value = 0;
        return (rowOrdinal >= 0) && Host.Arena.Keys.TryResolve(key, out var cell) &&
            Host.Arena.TryReadLiveNumber(rowOrdinal, cell, Host.RuleHost.Time, out value);
    }
    // Poses the body sitting on the move's 'to' cell back onto its 'from' cell — the same WorldBody.Pose door
    // FirePoseEffect (WorldServer.RuleHost.cs) already uses for a rule-authored pose. Height rides the body's own
    // current Y and yaw rides its own current heading; pitch/roll reset upright, the resting attitude a settled
    // board piece already carries.
    private void ReturnBoardMove(WorldPlacementBoard board, int moveRow) {
        if (
            !TryReadKeyedCell(rowOrdinal: moveRow, key: MoveFromKey, value: out var fromCell) ||
            !TryReadKeyedCell(rowOrdinal: moveRow, key: MoveToKey, value: out var toCell) ||
            (fromCell < 0L) ||
            (toCell < 0L)
        ) {
            return;
        }

        if (WorldTopologyCompilation.Find(
            definition: Host.Document.Definition,
            name: board.Topology
        ) is not { } topology) {
            return;
        }

        if (
            (fromCell >= topology.CellCount) ||
            (toCell >= topology.CellCount)
        ) {
            return;
        }

        var to = ((int)toCell);

        for (var index = 0; (index < Host.Population.Capacity); index++) {
            if (
                !Host.Population.IsActive(index: index) ||
                (Host.Body(index: index) is not { } body)
            ) {
                continue;
            }

            if (
                !topology.TryCellOf(
                position: body.FixedPosition,
                cell: out var cell
            ) ||
                (cell != to)
            ) {
                continue;
            }

            var centre = topology.CellCentre(cell: ((int)fromCell));
            var current = body.FixedPosition;

            body.Pose(
                position: new FixedVector3(
                    X: centre.X,
                    Y: current.Y,
                    Z: centre.Z
                ),
                yawRadians: body.FixedYaw,
                pitchRadians: FixedQ4816.Zero,
                rollRadians: FixedQ4816.Zero
            );

            return;
        }
    }
    // Runs right after EvaluateWorldRules, so a Return binding acts on the verdict THIS tick's own rules just
    // settled. Acts on the refuse EDGE — the latch's remembered previous verdict compared against this tick's —
    // rather than the level, so a verdict left sitting at its refusing value returns the piece exactly once.
    private void StepBoardEnforcement(ulong tick) {
        _ = tick;
        IndexBoardReturns(placements: Host.Document.Definition.Placements, catalog: Host.Arena.Catalog);

        foreach (var (placementId, board, verdictRow, moveRow) in m_boardReturns) {
            if (!TryReadKeyedCell(rowOrdinal: verdictRow, key: StateRow.SlotKey, value: out var verdict)) { continue; }

            var previous = (m_boardVerdictLatch.TryGetValue(key: placementId, value: out var seen) ? seen : board.Accept);

            m_boardVerdictLatch[placementId] = verdict;
            if ((verdict != board.Accept) && (previous == board.Accept) && (moveRow >= 0)) {
                ReturnBoardMove(board: board, moveRow: moveRow);
            }
        }
    }

    // Drops every remembered verdict whose placement no longer carries a Return-enforced board binding — the same
    // surviving-name reasoning m_ruleGateHeld.Prune applies to compiled rule names.
    internal void PruneBoardEnforcement(WorldDefinition definition) {
        if (m_boardVerdictLatch.Count == 0) {
            return;
        }

        var live = new HashSet<string>(comparer: StringComparer.Ordinal);
        var placements = definition.Placements;

        foreach (var ordinal in WorldPlacementIndex.Of(placements: placements).ReturnBoards) {
            _ = live.Add(item: placements[ordinal].Id);
        }

        List<string>? stale = null;

        foreach (var placementId in m_boardVerdictLatch.Keys) {
            if (!live.Contains(item: placementId)) {
                (stale ??= []).Add(item: placementId);
            }
        }

        if (stale is not null) {
            foreach (var placementId in stale) {
                _ = m_boardVerdictLatch.Remove(key: placementId);
            }
        }
    }

    // Folds the latch into the state hash, sorted by placement id, so two servers holding the same latch hash the
    // same regardless of insertion order.
    void IWorldPersistedSection<WorldBoardEnforcementCheckpoint>.AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(value: ((uint)m_boardVerdictLatch.Count));

        if (m_boardVerdictLatch.Count == 0) {
            return;
        }

        var entries = new List<KeyValuePair<string, long>>(capacity: m_boardVerdictLatch.Count);

        foreach (var entry in m_boardVerdictLatch) {
            entries.Add(item: entry);
        }

        entries.Sort(comparison: static (left, right) => string.CompareOrdinal(
            strA: left.Key,
            strB: right.Key
        ));

        foreach (var (placementId, verdict) in entries) {
            hash.Add(value: Fnv1aHash.Compute(values: placementId.AsSpan()));
            hash.Add(value: verdict);
        }
    }
    // Sorted by placement id, so the checkpoint's bytes are the same on every server holding the same latch.
    WorldBoardEnforcementCheckpoint IWorldPersistedSection<WorldBoardEnforcementCheckpoint>.Capture() {
        var entries = new List<(string PlacementId, long Verdict)>(capacity: m_boardVerdictLatch.Count);

        foreach (var (placementId, verdict) in m_boardVerdictLatch) {
            entries.Add(item: (placementId, verdict));
        }

        entries.Sort(comparison: static (left, right) => string.CompareOrdinal(
            strA: left.PlacementId,
            strB: right.PlacementId
        ));

        return new WorldBoardEnforcementCheckpoint(Entries: entries);
    }
    void IWorldPersistedSection<WorldBoardEnforcementCheckpoint>.Restore(WorldBoardEnforcementCheckpoint checkpoint) {
        m_boardVerdictLatch.Clear();

        foreach (var (placementId, verdict) in checkpoint.Entries) {
            m_boardVerdictLatch[placementId] = verdict;
        }
    }
}
