namespace Puck.World;

public sealed partial class WorldDisclosureImages {
    // One audience's placement projection and what it was read against. The projection (WorldStateDisclosure's one
    // reading, DisclosedPlacements) is a function of the source document, the audience's policy answers, the arena's
    // declarations, memberships and policies, and the contents of the rows it reads restricted (its reads). Each has a
    // witness here: the source by reference, the answers by the audience key the table is looked up by, every
    // declaration, membership, order and policy write by the arena's DisclosureRevision (StateArena moves it on every
    // write that is not a value or clock write), and each read row's contents by its version and its dependencies'
    // versions (its containing zones and the rows its policies read). A value write to a row the audience reads whole
    // never reaches the projection, which asks only whether such a row reads whole. While every witness stands the
    // reading is the projection a fresh composition would return, so a recipient that lost a cell is never answered
    // from a reading taken before the loss: the loss moved a witness.
    private sealed class PlacementReading(WorldDefinition source, StateArena arena, long revision, IReadOnlyList<WorldPlacement> placements,
        int[] rows, ulong[] versions, ulong[] dependencies) {
        public WorldDefinition Source { get; } = source;
        public StateArena Arena { get; } = arena;
        public long Revision { get; } = revision;
        public IReadOnlyList<WorldPlacement> Placements { get; } = placements;
        public int[] Rows { get; } = rows;
        public ulong[] Versions { get; } = versions;
        public ulong[] Dependencies { get; } = dependencies;
        private HashSet<string>? m_ids;

        // Whether the projection shows a placement id; the ids are indexed at the first question.
        public bool Shows(string? id) {
            if (id is null) { return false; }
            if (m_ids is null) {
                var ids = new HashSet<string>(capacity: Placements.Count, comparer: StringComparer.Ordinal);
                for (var index = 0; index < Placements.Count; index++) { ids.Add(Placements[index].Id); }
                m_ids = ids;
            }
            return m_ids.Contains(id);
        }
    }
    private readonly Dictionary<string, PlacementReading> m_placementReadings = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_placementReads = new(comparer: StringComparer.Ordinal);

    internal IReadOnlyList<WorldPlacement> Placements(WorldRecipientView view) => PlacementsOf(view).Placements;
    internal bool ShowsPlacement(WorldRecipientView view, string? id) => PlacementsOf(view).Shows(id);

    private PlacementReading PlacementsOf(WorldRecipientView view) {
        var audience = Audience(view);
        var source = view.Source;
        var arena = view.Arena;
        if (m_placementReadings.TryGetValue(audience, out var held) && Stands(held, source, arena)) { return held; }
        return ComposePlacements(view, audience, source, arena);
    }
    private bool Stands(PlacementReading reading, WorldDefinition source, StateArena arena) {
        if (!ReferenceEquals(reading.Source, source) || !ReferenceEquals(reading.Arena, arena) || (reading.Revision != arena.DisclosureRevision)) { return false; }
        var rows = reading.Rows;
        for (var index = 0; index < rows.Length; index++) {
            var row = rows[index];
            if ((arena.RowVersion(row) != reading.Versions[index]) || (DependencyVersions(arena, row) != reading.Dependencies[index])) { return false; }
        }
        return true;
    }
    private PlacementReading ComposePlacements(WorldRecipientView view, string audience, WorldDefinition source, StateArena arena) {
        var reads = m_placementReads;
        reads.Clear();
        var revision = arena.DisclosureRevision;
        var placements = WorldStateDisclosure.DisclosedPlacements(definition: source, arena: arena, observer: view.Observer, reads: reads);
        var rows = new int[reads.Count];
        var count = 0;
        foreach (var name in reads) {
            if (arena.Catalog.TryResolve(StateLane.Document, name, out var handle)) { rows[count++] = handle.Ordinal; }
        }
        Array.Resize(ref rows, count);
        var versions = new ulong[count];
        var dependencies = new ulong[count];
        for (var index = 0; index < count; index++) {
            versions[index] = arena.RowVersion(rows[index]);
            dependencies[index] = DependencyVersions(arena, rows[index]);
        }
        m_work.Count(PlacementReadings);
        return m_placementReadings[audience] = new PlacementReading(source, arena, revision, placements, rows, versions, dependencies);
    }
}
