using Puck.Maths;

namespace Puck.World.Server;

/// <summary>The board-enforcement latch's checkpointed image — one remembered verdict per
/// <see cref="WorldBoardEnforcement.Return"/>-bound board binding, keyed by its carrying placement's id.</summary>
/// <param name="Entries">The remembered (placement id, verdict) pairs.</param>
public sealed record WorldBoardEnforcementCheckpoint(IReadOnlyList<(string PlacementId, long Verdict)> Entries) {
    /// <summary>Gets an empty image, for a checkpoint captured before this section existed.</summary>
    public static WorldBoardEnforcementCheckpoint Empty { get; } = new(Entries: []);
}

public sealed partial class WorldServer {
    // The last verdict seen at each Return-bound board placement, keyed by placement id — kept outside the
    // document because a candidate replaces state.<verdict> wholesale every tick and the edge needs last tick's
    // value to compare against. Simulation state: it hashes (AppendBoardEnforcementStateHash) and checkpoints
    // (CaptureBoardEnforcement/RestoreBoardEnforcement).
    private readonly Dictionary<string, long> m_boardVerdictLatch = new(comparer: StringComparer.Ordinal);

    private static bool TryReadSlotValue(WorldStateRow? row, out long value) {
        value = 0;

        if (row?.Cells is not { } cells) {
            return false;
        }

        for (var index = 0; (index < cells.Count); index++) {
            if (cells[index].Key == StateRow.SlotKey) {
                value = cells[index].Value;

                return true;
            }
        }

        return false;
    }
    private static bool TryReadKeyedCell(WorldStateRow? row, string key, out long value) {
        value = 0;

        if (row?.Cells is not { } cells) {
            return false;
        }

        for (var index = 0; (index < cells.Count); index++) {
            if (string.Equals(a: cells[index].Key.Value, b: key, comparisonType: StringComparison.Ordinal)) {
                value = cells[index].Value;

                return true;
            }
        }

        return false;
    }
    // Poses the body sitting on the move's 'to' cell back onto its 'from' cell — the same WorldBody.Pose door
    // FirePoseEffect (WorldServer.RuleHost.cs) already uses for a rule-authored pose. Height rides the body's own
    // current Y and yaw rides its own current heading; pitch/roll reset upright, the resting attitude a settled
    // board piece already carries.
    private void ReturnBoardMove(WorldPlacementBoard board, WorldStateRow moveRow) {
        if (
            !TryReadKeyedCell(row: moveRow, key: "from", out var fromCell) ||
            !TryReadKeyedCell(row: moveRow, key: "to", out var toCell) ||
            (fromCell < 0L) ||
            (toCell < 0L)
        ) {
            return;
        }

        if (WorldTopologyCompilation.Find(m_definition, board.Topology) is not { } topology) {
            return;
        }

        if (
            (fromCell >= topology.CellCount) ||
            (toCell >= topology.CellCount)
        ) {
            return;
        }

        var to = ((int)toCell);

        for (var index = 0; (index < m_population.Capacity); index++) {
            if (!m_population.IsActive(index: index) || (Body(index: index) is not { } body)) {
                continue;
            }

            if (!topology.TryCellOf(position: body.FixedPosition, cell: out var cell) || (cell != to)) {
                continue;
            }

            var centre = topology.CellCentre(cell: ((int)fromCell));
            var current = body.FixedPosition;

            body.Pose(
                position: new FixedVector3(X: centre.X, Y: current.Y, Z: centre.Z),
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

        var placements = m_definition.Placements;

        for (var index = 0; (index < placements.Count); index++) {
            var placement = placements[index];

            if (placement.Board is not { Enforcement: WorldBoardEnforcement.Return } board) {
                continue;
            }

            if (
                (board.Verdict is not { } verdictName) ||
                (board.Move is not { } moveName)
            ) {
                continue;
            }

            if (!TryReadSlotValue(row: WorldDefinitionRows.FindStateRow(m_definition.State, verdictName), value: out var verdict)) {
                continue;
            }

            var previous = (m_boardVerdictLatch.TryGetValue(key: placement.Id, value: out var seen) ? seen : board.Accept);

            m_boardVerdictLatch[placement.Id] = verdict;

            if ((verdict == board.Accept) || (previous != board.Accept)) {
                continue;
            }

            if (WorldDefinitionRows.FindStateRow(m_definition.State, moveName) is { } moveRow) {
                ReturnBoardMove(board: board, moveRow: moveRow);
            }
        }
    }
    // Drops every remembered verdict whose placement no longer carries a Return-enforced board binding — the same
    // surviving-name reasoning m_ruleGateHeld.Prune applies to compiled rule names.
    private void PruneBoardEnforcement(WorldDefinition definition) {
        if (m_boardVerdictLatch.Count == 0) {
            return;
        }

        var live = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var placement in definition.Placements) {
            if (placement.Board is { Enforcement: WorldBoardEnforcement.Return }) {
                _ = live.Add(item: placement.Id);
            }
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
    private void AppendBoardEnforcementStateHash(ref Fnv1aHash hash) {
        hash.Add(value: ((uint)m_boardVerdictLatch.Count));

        if (m_boardVerdictLatch.Count == 0) {
            return;
        }

        var entries = new List<KeyValuePair<string, long>>(capacity: m_boardVerdictLatch.Count);

        foreach (var entry in m_boardVerdictLatch) {
            entries.Add(item: entry);
        }

        entries.Sort(comparison: static (left, right) => string.CompareOrdinal(strA: left.Key, strB: right.Key));

        foreach (var (placementId, verdict) in entries) {
            hash.Add(value: Fnv1aHash.Compute(values: placementId.AsSpan()));
            hash.Add(value: verdict);
        }
    }
    // Sorted by placement id, so the checkpoint's bytes are the same on every server holding the same latch.
    private WorldBoardEnforcementCheckpoint CaptureBoardEnforcement() {
        var entries = new List<(string PlacementId, long Verdict)>(capacity: m_boardVerdictLatch.Count);

        foreach (var (placementId, verdict) in m_boardVerdictLatch) {
            entries.Add(item: (placementId, verdict));
        }

        entries.Sort(comparison: static (left, right) => string.CompareOrdinal(strA: left.PlacementId, strB: right.PlacementId));

        return new WorldBoardEnforcementCheckpoint(Entries: entries);
    }
    private void RestoreBoardEnforcement(WorldBoardEnforcementCheckpoint checkpoint) {
        m_boardVerdictLatch.Clear();

        foreach (var (placementId, verdict) in checkpoint.Entries) {
            m_boardVerdictLatch[placementId] = verdict;
        }
    }
}
