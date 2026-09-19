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
    /// <inheritdoc/>
    public override CellKey Resolve(IStateReader reader, out bool named) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        named = false;

        var ordinal = m_rowOrdinal;

        if (
            (m_rowFrom is { } live) &&
            !live.TryResolve(
            reader: reader,
            rowOrdinal: out ordinal
        )
        ) {
            return default;
        }
        if (ordinal < 0) {
            return default;
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
            return default;
        }

        named = true;

        return key;
    }
}
