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
}
