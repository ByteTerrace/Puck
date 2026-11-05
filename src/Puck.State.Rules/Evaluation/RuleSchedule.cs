using System.Text;

namespace Puck.State.Rules;

/// <summary>Why a schedule cannot prove an evaluation's reads unchanged from versions alone.</summary>
[Flags]
public enum RuleVolatility : byte {
    /// <summary>Every read is named and versioned.</summary>
    None = 0,

    /// <summary>The reads include the tick pair, which moves every tick with no state write.</summary>
    ReadsTick = 1,

    /// <summary>The reads include the search ply, which a search host moves with no state write.</summary>
    ReadsSearchPly = 2,

    /// <summary>The reads include a host facet the host advertises no version for.</summary>
    HostFacet = 4,

    /// <summary>The reads include a row the host's own storage serves instead of the arena.</summary>
    HostOwnedRow = 8,

    /// <summary>The reads include a row carrying a value-over-time trait, whose live value moves with the tick.</summary>
    TraitRow = 16,

    /// <summary>The reads include a row the layout does not carry.</summary>
    UnknownRow = 32,
}
/// <summary>A rule's or a binding's memoized read schedule: the distinct row ordinals its reads touch, and why a
/// row version cannot prove those reads unchanged when it cannot. Built once against the fully constructed rule, so
/// a document project's own <see cref="CompiledRule.CollectReads"/> override is included, and cached for the rule's
/// lifetime.</summary>
/// <param name="Rows">The distinct catalog row ordinals the reads touch, ascending.</param>
/// <param name="Volatility">Why row versions alone cannot prove the reads unchanged; <see cref="RuleVolatility.None"/>
/// when they can.</param>
/// <param name="UnprovenRows">The rows that made the schedule volatile — a trait row, a host-owned row, or an
/// ordinal the layout does not carry — ascending, for the read-back.</param>
/// <param name="Facets">The host facets the reads name, in the needs' walk order, for the read-back.</param>
public sealed record RuleSchedule(int[] Rows, RuleVolatility Volatility, int[] UnprovenRows, IReadOnlyList<FacetRef> Facets) {
    /// <summary>Gets a value indicating whether row versions alone cannot prove the reads unchanged.</summary>
    public bool Volatile => (Volatility != RuleVolatility.None);

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

        var volatility = RuleVolatility.None;

        if (needs.ReadsTick) {
            volatility |= RuleVolatility.ReadsTick;
        }
        if (needs.ReadsSearchPly) {
            volatility |= RuleVolatility.ReadsSearchPly;
        }
        if (needs.Facets.Count > 0) {
            volatility |= RuleVolatility.HostFacet;
        }

        var layout = reader.Arena.Layout;
        var seen = new SortedSet<int>();
        var unproven = new SortedSet<int>();

        foreach (var row in needs.HostOwnedRows) {
            volatility |= RuleVolatility.HostOwnedRow;
            _ = unproven.Add(item: row);
        }
        foreach (var access in reads) {
            var ordinal = access.RowOrdinal;

            if (((uint)ordinal) >= ((uint)layout.RowCount)) {
                // A read whose row the layout does not carry is unprovable rather than absent: whatever answers it
                // answers outside the arena's versions.
                volatility |= RuleVolatility.UnknownRow;
                _ = unproven.Add(item: ordinal);

                continue;
            }

            // A row whose slot or any declared cell carries a value-over-time trait computes a different live value
            // every tick from the same stored bits, so an unchanged version proves nothing about a read of it.
            if (layout[ordinal].HasTraits) {
                volatility |= RuleVolatility.TraitRow;
                _ = unproven.Add(item: ordinal);
            }
            _ = seen.Add(item: ordinal);
        }

        return new RuleSchedule(
            Facets: needs.Facets,
            Rows: [.. seen],
            UnprovenRows: [.. unproven],
            Volatility: volatility
        );
    }
    /// <summary>Spells the schedule for a read-back: the rows it depends on by name, and every reason it is volatile.</summary>
    /// <param name="reader">The reader whose catalog names the rows.</param>
    /// <returns>The spelling, such as <c>rows goCell,goMark; volatile: reads search(ply)</c>.</returns>
    public string Describe(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var text = new StringBuilder();

        text.Append(value: "rows ");
        AppendRows(
            reader: reader,
            rows: Rows,
            text: text
        );
        if (!Volatile) {
            return text.Append(value: "; versioned").ToString();
        }

        text.Append(value: "; volatile:");

        var first = true;

        void Reason(string reason) {
            text.Append(value: (first ? " " : ", ")).Append(value: reason);
            first = false;
        }

        if (Volatility.HasFlag(flag: RuleVolatility.ReadsTick)) {
            Reason(reason: $"reads {RuleFacts.Tick}");
        }
        if (Volatility.HasFlag(flag: RuleVolatility.ReadsSearchPly)) {
            Reason(reason: $"reads {RuleFacts.SearchPly}");
        }
        foreach (var facet in Facets) {
            Reason(reason: $"facet {facet.Name} has no version");
        }
        foreach (var row in UnprovenRows) {
            var name = RowName(reader: reader, rowOrdinal: row);

            Reason(reason: ((((uint)row) >= ((uint)reader.Arena.Layout.RowCount))
                ? $"row {name} is not in the layout"
                : (reader.Arena.Layout[row].HasTraits
                    ? $"row {name} carries a trait"
                    : $"row {name} is host-owned")));
        }

        return text.ToString();
    }

    private static void AppendRows(IStateReader reader, int[] rows, StringBuilder text) {
        if (rows.Length == 0) {
            text.Append(value: "none");

            return;
        }
        for (var index = 0; (index < rows.Length); index++) {
            if (index > 0) {
                text.Append(value: ',');
            }
            text.Append(value: RowName(reader: reader, rowOrdinal: rows[index]));
        }
    }
    private static string RowName(IStateReader reader, int rowOrdinal) {
        var descriptors = reader.Catalog.Descriptors;

        return ((((uint)rowOrdinal) < ((uint)descriptors.Count))
            ? descriptors[rowOrdinal].Name
            : $"#{rowOrdinal}");
    }
}
