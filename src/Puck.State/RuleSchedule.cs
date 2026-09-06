namespace Puck.State;

/// <summary>A rule's or a binding's memoized read schedule: the distinct document row handles its reads touch, and
/// whether it depends on a fact a row version cannot prove unchanged — the completed-tick counter, a fact only the
/// document host answers, a read whose row name did not resolve, or a row whose slot or a keyed cell carries a
/// value-over-time trait (<see cref="StateAdvance"/>/<see cref="StateCycle"/>), each of which can change with the
/// tick alone with no write to bump a version. Built once, against the fully constructed rule so a document
/// project's own <see cref="CompiledRule.CollectReads"/> override is included, and cached for the rule's lifetime: a
/// compiled rule's own reads never change.</summary>
/// <param name="Rows">The distinct document row handles the reads touch.</param>
/// <param name="Volatile">Whether row versions alone cannot prove the reads unchanged.</param>
public sealed record RuleSchedule(StateHandle[] Rows, bool Volatile) {
    /// <summary>Builds a schedule from a read set already collected against the fully constructed rule.</summary>
    /// <param name="reads">The reads.</param>
    /// <param name="volatileBase">Whether the reads already carry a host or tick dependency.</param>
    /// <param name="reader">The evaluation in flight, for resolving row names and traits.</param>
    internal static RuleSchedule Build(IReadOnlyList<RuleAccess> reads, bool volatileBase, IRuleReader reader) {
        var rows = new List<StateHandle>(capacity: reads.Count);
        var seen = new HashSet<int>();
        var isVolatile = volatileBase;

        foreach (var access in reads) {
            if (!reader.Catalog.TryResolve(lane: StateLane.Document, name: access.Row, handle: out var handle)) {
                isVolatile = true;

                continue;
            }
            if ((reader.Store.Find(name: access.Row) is { } row) && IsVolatileRow(row: row)) {
                isVolatile = true;
            }
            if (seen.Add(item: handle.Ordinal)) {
                rows.Add(item: handle);
            }
        }

        return new RuleSchedule(Rows: [.. rows], Volatile: isVolatile);
    }

    // A row whose slot or any declared cell carries a value-over-time trait computes a different live value every
    // tick from the same stored bits, so an unchanged version proves nothing about what a read of it would answer.
    private static bool IsVolatileRow(StateRow row) {
        if (row.IsAdvancing || row.IsCycling) {
            return true;
        }

        foreach (var cell in (row.Cells ?? [])) {
            if ((cell.Advance is not null) || (cell.Cycle is not null)) {
                return true;
            }
        }

        return false;
    }
}
