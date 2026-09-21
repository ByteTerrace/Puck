using System.Runtime.InteropServices;

namespace Puck.World.Server;

/// <summary>The identity facts the world carries on its reserved <see cref="WorldIdentityFactLane"/> row: a body's
/// lane loads from its identity's persisted facts row when a seat binds one, zeroes when the seat unbinds, and a
/// <c>setIdentityFact</c> effect writes the lane and the identity's row together, persisting through the owned-world
/// catalog's ordinary save.</summary>
/// <remarks>The lane row is an arena row, so a lane write is rewound by the firing's own scope; only the persisted
/// identity document waits for the commit.</remarks>
public sealed partial class WorldRuleHost {
    private readonly List<PendingIdentityFact> m_pendingIdentityFacts = [];
    private readonly Dictionary<(int Body, string Fact), CellKey> m_identityLaneKeys = [];

    private WorldIdentity?[] m_identityLaneBound = [];
    private int[] m_identityLaneRevision = [];
    private StateCatalog? m_cachedIdentityLaneCatalog;
    private CellKeyTable? m_identityLaneKeyTable;
    private int m_cachedIdentityLaneOrdinal = -1;

    // The lane row's catalog ordinal, or -1 when the document declares none.
    private int IdentityLaneOrdinal {
        get {
            var catalog = Host.Arena.Catalog;

            if (ReferenceEquals(
                objA: m_cachedIdentityLaneCatalog,
                objB: catalog
            )) {
                return m_cachedIdentityLaneOrdinal;
            }

            m_cachedIdentityLaneCatalog = catalog;
            m_cachedIdentityLaneOrdinal = (catalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: WorldIdentityFactLane.RowName
            )
                ? handle.Ordinal
                : -1
            );

            return m_cachedIdentityLaneOrdinal;
        }
    }

    // A fact a firing wrote to the lane, persisted outward once the firing's scope commits.
    private readonly record struct PendingIdentityFact(WorldIdentity Identity, CellName Key, int LaneOrdinal, CellKey LaneKey, long Value);

    // The interned lane key of one (body, fact) pair. Spelling it allocates, so each pair is spelled once; the
    // cache is dropped with the arena table whose runtime addresses it holds.
    private CellKey IdentityLaneKey(int bodyIndex, string fact) {
        var keys = Host.Arena.Keys;

        if (!ReferenceEquals(
            objA: m_identityLaneKeyTable,
            objB: keys
        )) {
            m_identityLaneKeyTable = keys;

            m_identityLaneKeys.Clear();
        }

        ref var key = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: m_identityLaneKeys,
            exists: out var exists,
            key: (bodyIndex, fact)
        );

        if (!exists || !keys.TryGetName(key: key, name: out _)) {
            key = keys.Intern(name: CellName.Parse(candidate: WorldIdentityFactLane.Key(
                bodyIndex: bodyIndex,
                fact: fact
            )));
        }

        return key;
    }
    // Every body whose identity binding, or whose identity's own facts row, moved since the last sync reloads its
    // lane: a body's cells the identity does not carry read 0, every fact it carries lands as a lane write.
    private void SyncIdentityFactLanes() {
        var ordinal = IdentityLaneOrdinal;

        if (ordinal < 0) {
            return;
        }

        var capacity = Host.Population.Capacity;

        if (m_identityLaneBound.Length != capacity) {
            m_identityLaneBound = new WorldIdentity?[capacity];
            m_identityLaneRevision = new int[capacity];
        }

        for (var index = 0; (index < capacity); index++) {
            var profile = Host.Body(index: index)?.Profile;
            var revision = (profile?.FactsRevision ?? 0);

            if (
                ReferenceEquals(
                objA: m_identityLaneBound[index],
                objB: profile
            ) &&
                (m_identityLaneRevision[index] == revision)
            ) {
                continue;
            }

            ReloadIdentityFactLane(
                bodyIndex: index,
                profile: profile,
                rowOrdinal: ordinal
            );
            m_identityLaneBound[index] = profile;
            m_identityLaneRevision[index] = revision;
        }
    }
    private void ReloadIdentityFactLane(int rowOrdinal, int bodyIndex, WorldIdentity? profile) {
        var facts = profile?.Facts;
        var cursor = 0;

        while (Host.Arena.TryNextCell(
            cursor: ref cursor,
            key: out var key,
            rowOrdinal: rowOrdinal
        )) {
            if (
                !WorldIdentityFactLane.TryParse(
                bodyIndex: out var owner,
                fact: out var fact,
                key: Host.Arena.Keys[key].Value
            ) ||
                (owner != bodyIndex) ||
                ((facts is not null) && (StateRows.FindCell(
                cells: facts.Cells,
                key: CellName.Parse(candidate: fact.ToString())
            ) is not null))
            ) {
                continue;
            }

            WriteIdentityLane(
                key: key,
                rowOrdinal: rowOrdinal,
                value: 0L
            );
        }

        if (facts?.Cells is not { } carried) {
            return;
        }

        foreach (var carriedCell in carried) {
            // A profile arrives from another authority, so a fact that is not an integer is skipped rather than
            // read: the lane stores integers, and a foreign document is not trusted to agree.
            if (carriedCell.Value.Kind != CellKind.Int) {
                continue;
            }

            WriteIdentityLane(
                key: IdentityLaneKey(
                    bodyIndex: bodyIndex,
                    fact: carriedCell.Key.Value
                ),
                rowOrdinal: rowOrdinal,
                value: carriedCell.Value.AsInt
            );
        }
    }
    // A lane write that would leave the cell as it is moves no row version, so a reload after a persist that
    // already mirrored the lane installs nothing.
    private void WriteIdentityLane(int rowOrdinal, CellKey key, long value) {
        if (
            Host.Arena.TryRead(
            key: key,
            rowOrdinal: rowOrdinal,
            value: out var stored
        ) &&
            (stored.Kind == CellKind.Int) &&
            (stored.AsInt == value)
        ) {
            return;
        }
        if (Host.Arena.TryWrite(
            key: key,
            operand: value,
            reason: out var reason,
            rowOrdinal: rowOrdinal,
            write: StateWriteKind.Set
        )) {
            return;
        }
        if (Host.Arena.TryMint(
            key: out _,
            name: Host.Arena.Keys[key],
            reason: out reason,
            rowOrdinal: rowOrdinal,
            value: CellValue.Int(value: value)
        )) {
            return;
        }
        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(
                channel: "world.identity",
                text: $"[world.identity: lane cell '{Host.Arena.Keys[key]}' refused — {reason}]"
            );
        }
    }
    private void PersistIdentityFact(WorldIdentity identity, CellName key, long value) {
        if (
            !Host.Profiles.TrySetFact(
            changed: out _,
            identity: identity,
            key: key,
            reason: out var reason,
            value: value
        ) &&
            Host.Output.HasNarrationSink
        ) {
            Host.Output.Narrate(
                channel: "world.identity",
                text: $"[world.identity: fact '{key}' on world:{identity.Id} not persisted — {reason}]"
            );
        }
    }
}
