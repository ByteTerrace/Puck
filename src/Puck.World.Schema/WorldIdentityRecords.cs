namespace Puck.World;

/// <summary>Selects and admits an identity's record pools: the ones a projection carries across an authority seam
/// (<see cref="WorldIdentityTravel.Crossing"/>), and the whole store the identity's own document holds.</summary>
public static class WorldIdentityRecords {
    /// <summary>Copies the crossing pools of an identity's document, the pair pools joining two of them, and the record,
    /// enum and vector-space declarations they depend on. A home pool, and a pair pool reaching one, is never
    /// copied.</summary>
    /// <param name="document">The identity's owned document.</param>
    /// <returns>The crossing section, or <see langword="null"/> when the document declares no crossing pool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The travel list names a pool twice or a pool the document does not
    /// declare, or the selection does not load as a store.</exception>
    public static WorldStateSection? Select(WorldDefinition document) {
        ArgumentNullException.ThrowIfNull(argument: document);
        if (document.Identity?.Travel is not { Count: > 0 } travel) {
            return null;
        }
        var selected = new HashSet<CellName>();
        var pools = new List<StatePool>(capacity: travel.Count);

        foreach (var entry in travel) {
            if (!selected.Add(item: entry.Pool)) {
                throw new InvalidOperationException(message: $"identity pool '{entry.Pool}' names a travel class twice");
            }
            var pool = (document.StateRaw?.Pools?.FirstOrDefault(predicate: pool => (pool.Name == entry.Pool))
                ?? throw new InvalidOperationException(message: $"identity pool '{entry.Pool}' names no declared pool"));

            if (entry.Class == WorldIdentityTravel.Crossing) {
                pools.Add(item: pool);
            }
        }
        if (pools.Count == 0) {
            return null;
        }
        var crossing = pools.Select(selector: static pool => pool.Name).ToHashSet();
        var pairs = (document.StateRaw?.PairPools ?? []).Where(predicate: pair => (crossing.Contains(item: pair.LeftPool) && crossing.Contains(item: pair.RightPool))).ToArray();
        var section = Declarations(
            pairs: pairs,
            pools: pools,
            source: document.StateRaw
        );

        Validate(section: section);
        return section;
    }
    /// <summary>Returns the record store an identity's document holds: every pool and pair pool it declares and the
    /// declarations they depend on, home and crossing alike — what the account writes through the identity store
    /// door.</summary>
    /// <param name="document">The identity's owned document.</param>
    /// <returns>The store, or <see langword="null"/> when the document declares no pool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static WorldStateSection? Store(WorldDefinition document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return ((document.StateRaw?.Pools is { Count: > 0 } pools)
            ? Declarations(
                pairs: (document.StateRaw.PairPools ?? []),
                pools: pools,
                source: document.StateRaw
            )
            : null);
    }
    /// <summary>Refuses a record payload other than complete record pools and the pair pools between them: no world row,
    /// body or identity lane, lattice or family, and a payload the arena loads whole.</summary>
    /// <param name="section">The payload, or <see langword="null"/> for none.</param>
    /// <exception cref="InvalidOperationException"><paramref name="section"/> carries anything else, or does not load
    /// as a store.</exception>
    public static void Validate(WorldStateSection? section) {
        if (section is null) {
            return;
        }
        if ((section.World is { Count: > 0 }) || (section.Body is { Count: > 0 }) ||
            (section.Identity is { Count: > 0 }) || (section.Lattices is { Count: > 0 }) || (section.Families is { Count: > 0 })) {
            throw new InvalidOperationException(message: "identity records cannot carry ordinary world rows or host lanes");
        }
        var catalog = StateCatalog.Compile(section: section);
        var time = ArenaTime.Origin;

        if (!StateArena.TryCreate(arena: out _, catalog: catalog, options: null, reason: out var reason, section: section, time: in time)) {
            throw new InvalidOperationException(message: $"identity record payload refuses: {reason}");
        }
    }

    // The pools and pair pools given, with the record, enum and vector-space declarations their fields name.
    private static WorldStateSection Declarations(IReadOnlyList<StatePool> pools, IReadOnlyList<StatePairPool> pairs, WorldStateSection? source) {
        var recordNames = pools.Select(selector: static pool => pool.Record).Concat(second: pairs.Select(selector: static pair => pair.Record)).ToHashSet();
        var records = (source?.Records ?? []).Where(predicate: record => recordNames.Contains(item: record.Name)).ToArray();
        var fields = records.SelectMany(selector: static record => (record.Fields ?? [])).ToArray();
        var enums = fields.Where(predicate: static field => field.Enum.HasValue).Select(selector: static field => field.Enum!.Value).ToHashSet();
        var spaces = fields.Where(predicate: static field => field.Space.HasValue).Select(selector: static field => field.Space!.Value).ToHashSet();

        return new WorldStateSection(
            Records: records,
            Pools: [.. pools],
            PairPools: ((pairs.Count > 0) ? [.. pairs] : null),
            Enums: (source?.Enums ?? []).Where(predicate: value => enums.Contains(item: value.Name)).ToArray(),
            Spaces: (source?.Spaces ?? []).Where(predicate: value => spaces.Contains(item: value.Name)).ToArray()
        );
    }
}
