using Puck.SdfVm;
using Puck.World.Server;

namespace Puck.World.Client;

public sealed partial class WorldAdjacencySceneEmitter {
    private sealed class StaticProjection {
        public readonly WorldRigidAssets Assets = new();
        public readonly WorldPickMapBuilder Picks = new();
        public WorldDefinition? Source;
        public WorldDefinition? Image;
        public IReadOnlyList<WorldPrototype>? Creations;
        public WorldPlacement[] Original = [];
        public WorldPlacement[] Rows = [];
        public WorldAdjacencyFramePair[] Path = [];
        public ulong Seed;
    }
    private readonly Dictionary<string, StaticProjection> m_staticProjections = new(comparer: StringComparer.Ordinal);
    private readonly List<StaticProjection> m_preparedStatic = [];
    private readonly HashSet<string> m_staticNames = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_staticRetired = [];
    private readonly List<SdfMeshInstance> m_staticInstances = [];

    private void PrepareStaticProjections(IReadOnlyList<WorldAdjacencyProjection> projections) {
        m_preparedStatic.Clear();
        m_staticNames.Clear();
        for (var band = 0; band < Math.Min(projections.Count, m_bandCount); band++) {
            var projection = projections[band];
            var image = projection.Neighbour.Definition;
            var selected = WorldAdjacencyGeometry.Select(image, projection.Path[0].Neighbour, projection.OverlapDepth).Placements;
            if (!m_staticProjections.TryGetValue(projection.Name, out var prepared)) {
                prepared = new StaticProjection();
                m_staticProjections.Add(projection.Name, prepared);
            }
            m_staticNames.Add(projection.Name);
            var seed = (image.Generation?.WorldSeed ?? 0UL);
            if (!ReferenceEquals(prepared.Source, image) || !ReferenceEquals(prepared.Creations, image.Creations) || !prepared.Original.SequenceEqual(selected) ||
                !prepared.Path.SequenceEqual(projection.Path) || (prepared.Seed != seed)) {
                prepared.Source = image;
                prepared.Creations = image.Creations;
                prepared.Original = selected.ToArray();
                prepared.Path = projection.Path.ToArray();
                prepared.Seed = seed;
                prepared.Rows = selected.Select(placement => MapIntoSource(placement, projection.Path, image)).ToArray();
                prepared.Image = (image with { PlacementRowsRaw = prepared.Rows });
                prepared.Assets.ReconcileStatic(prepared.Image, prepared.Rows, projection.Name);
                prepared.Assets.Reconcile(image.Creations, [], null, prepared.Rows);
            }
            m_preparedStatic.Add(prepared);
        }
        m_staticRetired.Clear();
        foreach (var name in m_staticProjections.Keys) { if (!m_staticNames.Contains(name)) { m_staticRetired.Add(name); } }
        foreach (var name in m_staticRetired) { m_staticProjections.Remove(name); }
        PublishStaticCopies();
    }
    private void PublishStaticCopies() {
        m_staticInstances.Clear();
        foreach (var prepared in m_preparedStatic) {
            m_staticInstances.AddRange(prepared.Assets.Copies);
        }
    }
}
