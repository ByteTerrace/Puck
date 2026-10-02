namespace Puck.World;

public sealed partial class WorldIdentity {
    private WorldStateSection? m_travelRecords;
    private WorldDefinition? m_recordSource;
    private WorldStateSection? m_selectedRecords;
    private WorldStateSection? m_recordArenaSource;
    private StateArena? m_recordArena;

    private readonly StateInstanceHandle[] m_recordHandles = new StateInstanceHandle[1];

    /// <summary>Gets the explicitly owned typed record state, excluding private document state.</summary>
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

    private bool TryRecordArena(out StateArena arena, out string reason) {
        var section = RecordState;

        if (section is null) {
            arena = null!;
            reason = $"identity '{Id}' carries no records";
            return false;
        }
        if (ReferenceEquals(objA: section, objB: m_recordArenaSource) && (m_recordArena is not null)) {
            arena = m_recordArena;
            reason = string.Empty;
            return true;
        }
        var time = ArenaTime.Origin;

        if (!StateArena.TryCreate(catalog: StateCatalog.Compile(section: section), section: section, options: null,
            time: in time, arena: out var created, reason: out reason)) {
            arena = null!;
            return false;
        }
        arena = created;
        m_recordArenaSource = section;
        m_recordArena = arena;
        return true;
    }

    /// <summary>Reads a typed field from an explicitly owned record pool, including a traveler's records.</summary>
    public bool TryReadRecord(CellName record, CellName field, out CellValue value) {
        value = default;
        if (!TryRecordArena(arena: out var arena, reason: out _) || !arena.Catalog.TryGetPool(name: record, pool: out var pool) || (pool is null)) {
            return false;
        }
        if (arena.CopyPoolSnapshot(poolOrdinal: pool.Ordinal, destination: m_recordHandles) != 1) {
            return false;
        }
        for (var index = 0; (index < pool.Fields.Count); index++) {
            if (pool.Fields[index].Name == field) {
                return arena.TryRead(m_recordHandles[0], pool.Fields[index].Ordinal, out value);
            }
        }
        return false;
    }
    /// <summary>Writes one owned record field after validating its kind and bounds. The updated document is
    /// available through <see cref="Document"/> for the owning persistence service to save.</summary>
    public bool TryWriteRecord(CellName record, CellName field, CellValue value, out string reason) {
        var section = RecordState;

        if (section is null) {
            reason = $"identity '{Id}' carries no record '{record}'";
            return false;
        }
        if (!TryRecordArena(arena: out var arena, reason: out reason)) {
            return false;
        }
        if (!arena.Catalog.TryGetPool(name: record, pool: out var pool) || (pool is null)) {
            reason = $"identity '{Id}' cannot resolve record '{record}'";
            return false;
        }
        var member = pool.Fields.FirstOrDefault(predicate: candidate => (candidate.Name == field));

        if ((member.Name != field) || (arena.CopyPoolSnapshot(poolOrdinal: pool.Ordinal, destination: m_recordHandles) != 1)) {
            reason = $"identity record '{record}' has no live field '{field}'";
            return false;
        }
        if (!arena.TryWrite(handle: m_recordHandles[0], fieldOrdinal: member.Ordinal, value: value, reason: out reason)) {
            return false;
        }
        var updated = section with { Pools = arena.ToPools() };

        if (Document is { } document) {
            var replacements = updated.Pools!.ToDictionary(keySelector: pool => pool.Name);

            Document = document with {
                StateRaw = document.StateRaw! with {
                    Pools = document.StateRaw!.Pools!.Select(selector: pool => replacements.GetValueOrDefault(pool.Name, pool)).ToArray(),
                },
            };
        } else {
            m_travelRecords = updated;
        }
        m_factsRevision++;
        reason = string.Empty;
        return true;
    }
    /// <summary>Adopts what this identity's own traveler carried home: every fact on the traveler's travelling row and
    /// every field of the record pools this identity owns. Nothing else of <paramref name="carried"/> is read, so this
    /// identity keeps its own name, color, rates, panel, bindings and seat look. Each value passes the write door a
    /// local write passes, so a value this identity's own declarations refuse is skipped and named. An identity with an
    /// owned document adopts into it; one rebuilt from a projection (a replay's detached copy of an owned identity)
    /// adopts into its travelling rows.</summary>
    /// <param name="carried">The identity the traveler arrived as, rebuilt from its projection.</param>
    /// <param name="reason">The first value refused, or empty when every value was adopted.</param>
    /// <returns><see langword="true"/> when every carried value was adopted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carried"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="carried"/> names another identity.</exception>
    public bool TryAdopt(WorldIdentity carried, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: carried);
        if (!string.Equals(a: carried.Id, b: Id, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidOperationException(message: $"identity '{Id}' cannot adopt what '{carried.Id}' carried");
        }

        var refused = string.Empty;

        foreach (var cell in (carried.Facts?.Cells ?? [])) {
            if (!TrySetFact(key: cell.Key, value: cell.Value.AsInt, changed: out _, reason: out var factReason) && (refused.Length == 0)) {
                refused = $"fact '{cell.Key}': {factReason}";
            }
        }
        if (RecordState is { } owned) {
            foreach (var pool in (owned.Pools ?? [])) {
                var record = (owned.Records ?? []).FirstOrDefault(predicate: candidate => (candidate.Name == pool.Record));

                foreach (var field in (record?.Fields ?? [])) {
                    if (
                        carried.TryReadRecord(record: pool.Name, field: field.Name, value: out var value) &&
                        !TryWriteRecord(record: pool.Name, field: field.Name, value: value, reason: out var recordReason) &&
                        (refused.Length == 0)
                    ) {
                        refused = $"record '{pool.Name}.{field.Name}': {recordReason}";
                    }
                }
            }
        }

        reason = refused;
        return (refused.Length == 0);
    }
}
