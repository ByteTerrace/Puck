namespace Puck.State.Rules;

/// <summary>A rule's or a binding's memoized read schedule: the distinct row ordinals its reads touch, and whether
/// a row version cannot prove those reads unchanged. Built once against the fully constructed rule, so a document
/// project's own <see cref="CompiledRule.CollectReads"/> override is included, and cached for the rule's
/// lifetime.</summary>
/// <param name="Rows">The distinct catalog row ordinals the reads touch, ascending.</param>
/// <param name="Volatile">Whether row versions alone cannot prove the reads unchanged.</param>
public sealed record RuleSchedule(int[] Rows, bool Volatile) {
    /// <summary>Builds a schedule from a read set collected against a fully constructed rule.</summary>
    /// <param name="reads">The reads.</param>
    /// <param name="needs">What the rule needs of its host: the tick, a facet, or a host-owned row, each of which a
    /// row version cannot see through.</param>
    /// <param name="reader">The evaluation in flight, for the layout the trait test reads.</param>
    /// <returns>The schedule.</returns>
    public static RuleSchedule Build(IReadOnlyList<CellAccess> reads, RuleNeeds needs, IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: needs);
        ArgumentNullException.ThrowIfNull(argument: reader);
        ArgumentNullException.ThrowIfNull(argument: reads);

        var isVolatile = needs.Volatile;
        var layout = reader.Arena.Layout;
        var seen = new SortedSet<int>();

        foreach (var access in reads) {
            var ordinal = access.RowOrdinal;

            if (((uint)ordinal) >= ((uint)layout.RowCount)) {
                // A read whose row the layout does not carry is unprovable rather than absent: whatever answers it
                // answers outside the arena's versions.
                isVolatile = true;

                continue;
            }

            // A row whose slot or any declared cell carries a value-over-time trait computes a different live value
            // every tick from the same stored bits, so an unchanged version proves nothing about a read of it.
            isVolatile |= layout[ordinal].HasTraits;
            _ = seen.Add(item: ordinal);
        }

        return new RuleSchedule(
            Rows: [.. seen],
            Volatile: isVolatile
        );
    }
}
