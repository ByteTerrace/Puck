namespace Puck.World;

public sealed partial class WorldIdentity {
    private WorldStateSection? m_travelRecords;
    private WorldDefinition? m_recordSource;
    private WorldStateSection? m_selectedRecords;
    private WorldStateSection? m_storeSource;
    private WorldStateSection? m_store;
    private WorldStateSection? m_storeArenaSource;
    private StateArena? m_storeArena;

    /// <summary>Gets the record pools this identity's projection carries across a seam: its document's crossing pools
    /// (<see cref="WorldIdentityTravel.Crossing"/>), or a traveller's travelling records.</summary>
    public WorldStateSection? RecordState {
        get {
            if (Document is not { } document) {
                return m_travelRecords;
            }
            if (!ReferenceEquals(objA: m_recordSource, objB: document)) {
                m_selectedRecords = WorldIdentityRecords.Select(document: document);
                m_recordSource = document;
            }
            return m_selectedRecords;
        }
    }
    /// <summary>Gets the whole record store this identity holds: every pool its owned document declares, home and
    /// crossing, or a traveller's travelling records.</summary>
    public WorldStateSection? RecordStore {
        get {
            if (Document is not { } document) {
                return m_travelRecords;
            }
            if (!ReferenceEquals(objA: m_storeSource, objB: document.StateRaw)) {
                m_store = WorldIdentityRecords.Store(document: document);
                m_storeSource = document.StateRaw;
            }
            return m_store;
        }
    }

    // A read-only arena over the store as it stands, rebuilt when the store's section moves. Reads only: every write
    // goes through a stage, which owns its own arena.
    private bool TryStoreArena(out StateArena arena) {
        if (RecordStore is not { } section) {
            arena = null!;
            return false;
        }
        if (ReferenceEquals(objA: section, objB: m_storeArenaSource) && (m_storeArena is { } held)) {
            arena = held;
            return true;
        }
        var time = ArenaTime.Origin;

        if (!StateArena.TryCreate(catalog: StateCatalog.Compile(section: section), section: section, options: null, time: in time, arena: out var created, reason: out _)) {
            arena = null!;
            return false;
        }
        m_storeArenaSource = section;
        m_storeArena = created;
        arena = created;
        return true;
    }

    /// <summary>Reads one field of the live instance in a pool slot, a traveller's records included.</summary>
    /// <param name="pool">The pool's name on the identity.</param>
    /// <param name="slot">The 0-based slot.</param>
    /// <param name="field">The record field.</param>
    /// <param name="value">The field's value, on success.</param>
    /// <returns><see langword="true"/> when the slot holds a live instance with that field.</returns>
    public bool TryReadRecord(CellName pool, int slot, CellName field, out CellValue value) {
        value = default;

        return (TryStoreArena(arena: out var arena) && WorldIdentityStage.TryReadField(arena: arena, field: field, pool: pool, slot: slot, value: out value));
    }
    /// <summary>Returns the live instances in a pool and the generation of one slot.</summary>
    /// <param name="pool">The pool's name on the identity.</param>
    /// <returns>The number of live instances, or 0 when the identity holds no such pool.</returns>
    public int CountRecords(CellName pool) => ((TryStoreArena(arena: out var arena) && arena.Catalog.TryGetPool(name: pool, pool: out var descriptor))
        ? arena.CellCount(rowOrdinal: descriptor.DomainRowOrdinal)
        : 0);
    /// <summary>Returns the generation of the live instance in a pool slot.</summary>
    /// <param name="pool">The pool's name on the identity.</param>
    /// <param name="slot">The 0-based slot.</param>
    /// <param name="generation">The live instance's generation, on success.</param>
    /// <returns><see langword="true"/> when the slot holds a live instance.</returns>
    public bool TryRecordGeneration(CellName pool, int slot, out long generation) {
        generation = 0L;
        if (!TryStoreArena(arena: out var arena) || !arena.Catalog.TryGetPool(name: pool, pool: out var descriptor) || !arena.TryResolvePoolSlot(handle: out var handle, poolOrdinal: descriptor.Ordinal, slot: slot)) {
            return false;
        }
        generation = handle.Generation;
        return true;
    }
    /// <summary>Opens a stage over this identity's record store: a private copy every claim, release and field write
    /// lands in, admitted by the writer that asks, until <see cref="Apply"/> installs it whole or the stage is
    /// dropped.</summary>
    /// <param name="stage">The stage, on success.</param>
    /// <param name="reason">Why no stage opened, or empty on success.</param>
    /// <returns><see langword="true"/> when the identity holds a record store.</returns>
    public bool TryOpenStage(out WorldIdentityStage stage, out string reason) {
        stage = null!;
        if (RecordStore is not { } section) {
            reason = $"identity '{Id}' holds no record store";
            return false;
        }
        var time = ArenaTime.Origin;

        if (!StateArena.TryCreate(catalog: StateCatalog.Compile(section: section), section: section, options: null, time: in time, arena: out var arena, reason: out reason)) {
            reason = $"identity '{Id}' record store does not load: {reason}";
            return false;
        }
        stage = new WorldIdentityStage(identity: this, source: section, arena: arena);
        reason = string.Empty;
        return true;
    }
    /// <summary>Installs a stage's store whole: its pools and pair pools replace the ones the identity holds, in its
    /// owned document or its travelling records. A stage opened over an earlier store than the one the identity now
    /// holds is refused, so two stages can never interleave.</summary>
    /// <param name="stage">The stage this identity opened.</param>
    /// <param name="reason">Why the stage was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the stage was installed or held no write.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stage"/> is <see langword="null"/>.</exception>
    public bool Apply(WorldIdentityStage stage, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: stage);
        if (!ReferenceEquals(objA: stage.Identity, objB: this)) {
            reason = $"the stage belongs to identity '{stage.Identity.Id}', not '{Id}'";
            return false;
        }
        if (!stage.Dirty) {
            reason = string.Empty;
            return true;
        }
        if (!ReferenceEquals(objA: stage.Source, objB: RecordStore) && !SameStore(left: stage.Source, right: RecordStore)) {
            reason = $"identity '{Id}' record store moved after the stage opened";
            return false;
        }
        var pools = stage.Arena.ToPools();
        var pairs = stage.Arena.ToPairPools();

        if (Document is { } document) {
            var poolsByName = pools.ToDictionary(keySelector: static pool => pool.Name);
            var pairsByName = pairs.ToDictionary(keySelector: static pool => pool.Name);
            var state = document.StateRaw!;

            Document = document with {
                StateRaw = state with {
                    Pools = (state.Pools ?? []).Select(selector: pool => poolsByName.GetValueOrDefault(pool.Name, pool)).ToArray(),
                    PairPools = ((state.PairPools is { } declared) ? declared.Select(selector: pool => pairsByName.GetValueOrDefault(pool.Name, pool)).ToArray() : null),
                },
            };
        } else {
            m_travelRecords = stage.Source with { Pools = pools, PairPools = ((pairs.Count > 0) ? pairs : null) };
        }
        m_factsRevision++;
        stage.MarkApplied();
        reason = string.Empty;
        return true;
    }

    // A document replaced by an edit that kept every pool (a fact write, a rate) holds a new store section with the same
    // pools; the stage still describes it.
    private static bool SameStore(WorldStateSection? left, WorldStateSection? right) => (
        (left is not null) &&
        (right is not null) &&
        (left.Pools ?? []).SequenceEqual(second: (right.Pools ?? [])) &&
        (left.PairPools ?? []).SequenceEqual(second: (right.PairPools ?? []))
    );

    /// <summary>Adopts what this identity's own traveller carried home: every fact on the traveller's travelling row and
    /// every crossing pool it carried, through the doors a local write passes. A fact or pool this identity marks
    /// home-scoped is never adopted, since no world writes one. Nothing else of <paramref name="carried"/> is read, so
    /// this identity keeps its own name, color, rates, panel, bindings, seat look and home pools.</summary>
    /// <param name="carried">The identity the traveller arrived as, rebuilt from its projection.</param>
    /// <param name="reason">The first value refused, or empty when every value was adopted.</param>
    /// <returns><see langword="true"/> when every carried value was adopted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carried"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="carried"/> names another identity.</exception>
    /// <remarks>The carried values are unsigned, so which package wrote each is not known here: the home-scoped classes
    /// are what a carried projection cannot reach. Binding a carried write to the package that wrote it is the signed
    /// write-back of remote home adoption.</remarks>
    public bool TryAdopt(WorldIdentity carried, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: carried);
        if (!string.Equals(a: carried.Id, b: Id, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidOperationException(message: $"identity '{Id}' cannot adopt what '{carried.Id}' carried");
        }
        if (ReferenceEquals(objA: carried, objB: this)) {
            reason = string.Empty;
            return true;
        }

        var definition = Document?.Identity;
        var refused = string.Empty;

        foreach (var cell in (carried.Facts?.Cells ?? [])) {
            if (definition?.IsHomeScoped(name: cell.Key) == true) {
                continue;
            }
            if (!TrySetFact(key: cell.Key, value: cell.Value.AsInt, writer: WorldIdentityWriter.Home, changed: out _, reason: out var factReason) && (refused.Length == 0)) {
                refused = $"fact '{cell.Key}': {factReason}";
            }
        }
        if ((carried.RecordState is { Pools: { Count: > 0 } carriedPools } carriedSection) && TryOpenStage(stage: out var stage, reason: out _)) {
            foreach (var pool in carriedPools) {
                if ((definition?.IsHomeScoped(name: pool.Name) == true) || (definition is not null && (definition.TravelOf(pool: pool.Name) != WorldIdentityTravel.Crossing))) {
                    continue;
                }
                if (!stage.TryReplacePool(carried: carriedSection, pool: pool.Name, reason: out var poolReason) && (refused.Length == 0)) {
                    refused = $"pool '{pool.Name}': {poolReason}";
                }
            }
            if (!Apply(stage: stage, reason: out var applyReason) && (refused.Length == 0)) {
                refused = applyReason;
            }
        }

        reason = refused;
        return (refused.Length == 0);
    }
}
