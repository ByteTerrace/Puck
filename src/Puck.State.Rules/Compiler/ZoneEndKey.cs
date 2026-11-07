namespace Puck.State.Rules;

/// <summary>The first or last member's key in an ordered zone, read from the arena. An empty zone — or a live row
/// whose index selects none — resolves to the invalid key, which reads absent and addresses no write.</summary>
public sealed class ZoneEndKey : RuleKeyFact {
    private readonly bool m_last;
    private readonly int m_rowOrdinal;
    private readonly LiveRow? m_rowFrom;

    /// <summary>Initializes the key.</summary>
    /// <param name="rowOrdinal">The zone's catalog ordinal, or <c>-1</c> for a live row.</param>
    /// <param name="rowFrom">The live row, or <see langword="null"/> for a fixed zone.</param>
    /// <param name="last">Whether to read the last member rather than the first.</param>
    public ZoneEndKey(int rowOrdinal, LiveRow? rowFrom, bool last) {
        m_last = last;
        m_rowFrom = rowFrom;
        m_rowOrdinal = rowOrdinal;
    }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (m_rowFrom is { } live) {
            live.CollectReads(into: into);
        } else {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: m_rowOrdinal
            ));
        }
    }

    /// <summary>Gets the program the live row's index resolves through, or <see langword="null"/>.</summary>
    public CompiledExpressionToken[]? FeedProgram => m_rowFrom?.FeedProgram;

    /// <inheritdoc/>
    public override Resolved<CellKey> Resolve(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return ((m_rowFrom is { } live)
            ? EndOf(
                reader: reader,
                selected: live.Resolve(reader: reader)
            )
            : EndOf(
                reader: reader,
                selected: Resolved<int>.Named(value: m_rowOrdinal)
            ));
    }
    /// <inheritdoc/>
    public override Resolved<CellKey> Resolve(IStateReader reader, in AddressFeed feed) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return ((m_rowFrom is { } live)
            ? EndOf(
                reader: reader,
                selected: live.Resolve(
                    feed: in feed,
                    reader: reader
                )
            )
            : EndOf(
                reader: reader,
                selected: Resolved<int>.Named(value: m_rowOrdinal)
            ));
    }

    private Resolved<CellKey> EndOf(IStateReader reader, Resolved<int> selected) {
        if (!selected.TryNamed(value: out var ordinal)) {
            return ((selected.Case == FaultCase.Faulted)
                ? Resolved<CellKey>.Faulted
                : Resolved<CellKey>.Nothing);
        }
        if (ordinal < 0) {
            return Resolved<CellKey>.Nothing;
        }

        var arena = reader.Arena;
        var count = arena.CellCount(rowOrdinal: ordinal);

        if (
            (count == 0) ||
            !arena.TryKeyAt(
            key: out var key,
            position: (m_last
            ? (count - 1)
            : 0),
            rowOrdinal: ordinal
        )
        ) {
            return Resolved<CellKey>.Nothing;
        }

        return Resolved<CellKey>.Named(value: key);
    }
}
