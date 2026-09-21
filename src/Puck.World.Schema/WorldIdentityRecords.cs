namespace Puck.World;

/// <summary>Selects the explicitly identity-owned records that travel across authority boundaries.</summary>
public static class WorldIdentityRecords {
    /// <summary>Copies only selected pools and their record, enum, and vector-space declarations.</summary>
    public static WorldStateSection? Select(WorldDefinition document) {
        ArgumentNullException.ThrowIfNull(argument: document);
        if (document.Identity?.Records is not { Count: > 0 } names) {
            return null;
        }
        var selected = new HashSet<CellName>();
        var pools = new List<StatePool>(capacity: names.Count);

        foreach (var name in names) {
            if (!selected.Add(item: name)) {
                throw new InvalidOperationException(message: $"identity record '{name}' is selected twice");
            }
            var pool = (document.StateRaw?.Pools?.FirstOrDefault(predicate: pool => (pool.Name == name))
                ?? throw new InvalidOperationException(message: $"identity record '{name}' names no declared pool"));

            pools.Add(item: pool);
        }
        var recordNames = pools.Select(selector: pool => pool.Record).ToHashSet();
        var records = (document.StateRaw?.Records ?? []).Where(predicate: record => recordNames.Contains(item: record.Name)).ToArray();
        var fields = records.SelectMany(selector: record => (record.Fields ?? [])).ToArray();
        var enums = fields.Where(predicate: field => field.Enum.HasValue).Select(selector: field => field.Enum!.Value).ToHashSet();
        var spaces = fields.Where(predicate: field => field.Space.HasValue).Select(selector: field => field.Space!.Value).ToHashSet();
        var section = new WorldStateSection(
            Records: records,
            Pools: pools,
            Enums: (document.StateRaw?.Enums ?? []).Where(predicate: value => enums.Contains(item: value.Name)).ToArray(),
            Spaces: (document.StateRaw?.Spaces ?? []).Where(predicate: value => spaces.Contains(item: value.Name)).ToArray()
        );

        Validate(section: section);
        return section;
    }
    /// <summary>Refuses any payload other than complete, live capacity-one record instances.</summary>
    public static void Validate(WorldStateSection? section) {
        if (section is null) {
            return;
        }
        if ((section.World is { Count: > 0 }) || (section.Body is { Count: > 0 }) ||
            (section.Identity is { Count: > 0 }) || (section.Lattices is { Count: > 0 }) || (section.Families is { Count: > 0 }) || (section.PairPools is { Count: > 0 })) {
            throw new InvalidOperationException(message: "identity records cannot carry ordinary world rows or host lanes");
        }
        foreach (var pool in (section.Pools ?? [])) {
            var live = (pool.Snapshot?.Live ?? pool.Initial);

            if ((pool.Capacity != 1) || (live is not { Count: 1 }) || (live[0].Slot != 0)) {
                throw new InvalidOperationException(message: $"identity record '{pool.Name}' must be a capacity-one pool with slot zero live");
            }
        }
        var catalog = StateCatalog.Compile(section: section);
        var time = ArenaTime.Origin;

        if (!StateArena.TryCreate(arena: out _, catalog: catalog, options: null, reason: out var reason, section: section, time: in time)) {
            throw new InvalidOperationException(message: $"identity record payload refuses: {reason}");
        }
    }
}
