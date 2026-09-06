using Puck.World.Protocol;

namespace Puck.World.Server;

internal sealed partial class WorldSearchRuntime {
    private bool Land(Job job, Func<WorldMutation, bool> apply) {
        var plan = job.Plan;
        var rows = m_live();
        var tokenCells = StateRows.FindStateRow(rows: rows, name: plan.Row.Tokens)?.Cells;

        m_outputs.Clear();

        if ((plan.Row.Legal is { } legal) && (tokenCells is not null)) {
            for (var index = 0; (index < tokenCells.Count) && (index < job.Legal.Length); index++) {
                m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: legal, Key: tokenCells[index].Key.Value, Value: job.Legal[index], Kind: WorldDocumentWriteKind.Set));
            }
        }
        if ((plan.Row.Counts is { } counts) && (tokenCells is not null)) {
            for (var index = 0; (index < tokenCells.Count) && (index < job.Counts.Length); index++) {
                m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: counts, Key: tokenCells[index].Key.Value, Value: job.Counts[index], Kind: WorldDocumentWriteKind.Set));
            }
        }
        if (plan.Row.Count is { } count) {
            m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: count, Key: StateRow.SlotKey.Value, Value: job.Count, Kind: WorldDocumentWriteKind.Set));
        }
        if (plan.Row.Best is { } best) {
            m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: best, Key: "token", Value: job.BestToken, Kind: WorldDocumentWriteKind.Set));
            m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: best, Key: "to", Value: job.BestTarget, Kind: WorldDocumentWriteKind.Set));
            m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: best, Key: "score", Value: job.Best, Kind: WorldDocumentWriteKind.Set));
        }
        if ((plan.Row.Reach is { } reach) && (job.Wide is { } wide)) {
            // Every reach cell resets through the same clear-then-paint door a boardCombine authors — the ordinary
            // mutation path already writes a sparse board of any size, so a wide board needs no dedicated kind.
            m_outputs.Add(item: new WorldMutation.TransformState(Principal: WorldPrincipal.World, Transform: new StateTransform.BoardCombine(Row: reach, Operation: BoardCombineOp.Clear)));

            var held = ((plan.Row.Held is { } heldName) ? Slot(store: m_store, name: heldName) : -1L);

            if ((held >= 0L) && (held < job.Legal.Length)) {
                var token = (int)held;
                var words = WideWordsPerToken(cellCount: plan.Topology.CellCount);

                for (var cell = 0; cell < plan.Topology.CellCount; cell++) {
                    if (WideBit(wide: wide, token: token, cell: cell, words: words)) {
                        m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: reach, Key: plan.Topology.Key(cell), Value: 1L, Kind: WorldDocumentWriteKind.Set));
                    }
                }
            }
        }
        if (m_outputs.Count == 0) {
            return false;
        }

        var mutation = ((m_outputs.Count == 1) ? m_outputs[0] : new WorldMutation.Batch(Principal: WorldPrincipal.World, Mutations: [.. m_outputs]));

        if (apply(mutation)) {
            return true;
        }

        Console.Error.WriteLine(value: $"[world.search: job '{plan.Row.Name}' finished but its outputs were refused by the mutation door; world.search shows the count it found]");

        return false;
    }
}
