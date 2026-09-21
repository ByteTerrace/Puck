namespace Puck.World;

public sealed partial record WorldDefinition {
    // Expanded values belong to the immutable section, not to a catalog reused after a value-only update.
    private IReadOnlyList<WorldStateRow> GetStateRows() {
        if ((StateRaw?.Pools is not { Count: > 0 }) && (StateRaw?.PairPools is not { Count: > 0 })) {
            return (StateRaw?.World ?? []);
        }

        var cache = GetCompilationCache(state: StateRaw);

        if (Volatile.Read(location: ref cache.StateRows) is { } warm) {
            return warm;
        }

        lock (cache.SyncRoot) {
            return (cache.StateRows ??= Array.AsReadOnly(array: StateCatalog.ExpandRows(section: StateRaw)
                .Select(selector: static row => ((row as WorldStateRow) ?? new WorldStateRow(
                    row: row,
                    gatesDrive: false,
                    field: null
                ))).ToArray()));
        }
    }
}
