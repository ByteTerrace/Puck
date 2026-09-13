namespace Puck.State;

/// <summary>The first or last member's key in an ordered zone, read from the active store. An empty zone — or a live
/// zone whose index selects none — resolves to the empty key, which reads absent and addresses no write.</summary>
/// <param name="row">The zone's name, or the live spelling.</param>
/// <param name="handle">The zone's compiled handle, for a fixed zone.</param>
/// <param name="rowFrom">The live zone, or <see langword="null"/> for a fixed one.</param>
/// <param name="last">Whether to read the last member rather than the first.</param>
public sealed class ZoneEndKey(string row, StateHandle handle, LiveZone? rowFrom, bool last) : KeyFact {
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        if (rowFrom is { } live) {
            live.CollectReads(into: into);
        } else {
            into.Add(item: new RuleAccess(
                Row: row,
                Key: null
            ));
        }
    }
    /// <inheritdoc/>
    public override string Resolve(IRuleReader reader) {
        if (
            !RuleEvaluation.TryResolveRow(
            handle: handle,
            reader: reader,
            resolved: out var zoneHandle,
            rowFrom: rowFrom
        ) ||
            !StateReader.TryReadHandle(
            store: reader.Store,
            catalog: reader.Catalog,
            handle: zoneHandle,
            key: null,
            tick: reader.Tick,
            row: out var zone,
            rawValue: out _,
            text: out _
        )
        ) {
            return string.Empty;
        }
        var count = reader.Store.CellCount(row: zone);

        return (((count > 0) && reader.Store.TryKeyAt(
            index: (last
            ? (count - 1)
            : 0),
            key: out var key,
            row: zone
        ))
            ? key.Value
            : string.Empty
        );
    }

    /// <inheritdoc/>
    public override bool HostOnly => (rowFrom is { HostOnly: true });
}
