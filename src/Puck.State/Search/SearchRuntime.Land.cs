namespace Puck.State;

public sealed partial class SearchRuntime {
    private bool Land(Job job, Func<IReadOnlyList<SearchWrite>, bool> apply) {
        var plan = job.Plan;
        var rows = m_live();
        var tokenCells = StateRows.FindStateRow(rows: rows, name: plan.Tokens)?.Cells;

        m_outputs.Clear();

        if ((plan.Legal is { } legal) && (tokenCells is not null)) {
            for (var index = 0; (index < tokenCells.Count) && (index < job.Legal.Length); index++) {
                m_outputs.Add(item: new SearchWrite.Cell(Row: legal, Key: tokenCells[index].Key.Value, Value: job.Legal[index]));
            }
        }
        if ((plan.Counts is { } counts) && (tokenCells is not null)) {
            for (var index = 0; (index < tokenCells.Count) && (index < job.Counts.Length); index++) {
                m_outputs.Add(item: new SearchWrite.Cell(Row: counts, Key: tokenCells[index].Key.Value, Value: job.Counts[index]));
            }
        }
        if (plan.Best is { } best) {
            m_outputs.Add(item: new SearchWrite.Cell(Row: best, Key: "token", Value: job.BestToken));
            m_outputs.Add(item: new SearchWrite.Cell(Row: best, Key: "to", Value: job.BestTarget));
            m_outputs.Add(item: new SearchWrite.Cell(Row: best, Key: "score", Value: job.Best));
        }
        if ((plan.Reach is { } reach) && (job.Wide is { } wide) && (plan.Topology is { } topology)) {
            // Every reach cell resets through the same clear-then-paint door a document project's own boardCombine
            // authors — a wide board needs no dedicated write kind.
            m_outputs.Add(item: new SearchWrite.ClearBoard(Row: reach));

            var held = ((plan.Held is { } heldName) ? Slot(store: m_store, name: heldName) : -1L);

            if ((held >= 0L) && (held < job.Legal.Length)) {
                var token = (int)held;
                var words = WideWordsPerToken(cellCount: plan.CellCount);

                for (var cell = 0; cell < plan.CellCount; cell++) {
                    if (WideBit(wide: wide, token: token, cell: cell, words: words)) {
                        m_outputs.Add(item: new SearchWrite.Cell(Row: reach, Key: topology.Key(cell), Value: 1L));
                    }
                }
            }
        }
        if (m_outputs.Count == 0) {
            return false;
        }

        if (apply(m_outputs)) {
            return true;
        }

        m_narrate?.Invoke(
            "world.search",
            $"[world.search: job '{plan.Name}' finished but its outputs were refused by the mutation door; world.search shows the count it found]"
        );

        return false;
    }
}
