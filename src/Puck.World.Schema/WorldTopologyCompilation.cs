
namespace Puck.World;

/// <summary>The document-anchored entrance to <see cref="TopologyCompilation"/>: a topology a placement's
/// <c>board</c> facet names is placed in that placement's resolved frame, its position and its yaw.</summary>
public static class WorldTopologyCompilation {
    private static readonly Puck.Abstractions.CodeDerivedTable<LatticeTopology, AnchoredCompile> Cache = new();

    // Anchor is the resolving placement instance, not merely its id — see Find's remarks on why identity (not name)
    // is the correct staleness test.
    private sealed class AnchoredCompile {
        public WorldPlacement? Anchor;
        public CompiledTopology? Compiled;
    }

    private static WorldPlacement? FindAnchor(WorldDefinition definition, string topologyName) =>
        WorldPlacementIndex.Of(placements: definition.Placements).BoardOf(topology: topologyName);

    /// <summary>Finds and compiles a discrete topology by name, anchored: a topology a placement's <c>board</c> facet
    /// names (<see cref="WorldPlacementBoard.Topology"/>) is placed in that placement's own
    /// <see cref="WorldDefinitionRows.ResolvedFrame"/>: its authored <c>origin</c> is a point of that frame and its own
    /// axes turn with the frame's yaw (<see cref="CompiledTopology.Heading"/>), so a board, its cells and every
    /// position resolved against it turn with the placement that carries it. The frame's scale is not applied: a
    /// cell keeps its authored size. An unanchored topology (no placement's <c>board</c> facet names it) resolves exactly as
    /// authored, identical to <see cref="TopologyCompilation.Find(IReadOnlyList{LatticeTopology}?, string)"/>. Cached
    /// per (topology, anchor placement instance) pair, so a mutation that moves the anchor — a new
    /// <see cref="WorldPlacement"/> instance, since rows are replaced wholesale, never mutated in place — recompiles
    /// rather than serving a stale origin; the topology's own object identity alone is not enough once its resolved
    /// origin depends on another row.</summary>
    /// <param name="definition">The document — resolves the anchor, if any.</param>
    /// <param name="name">The topology name.</param>
    /// <returns>The compiled topology, or null if absent or malformed.</returns>
    public static CompiledTopology? Find(WorldDefinition definition, string name) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var topologies = definition.StateRaw?.Lattices;

        for (var index = 0; (index < (topologies?.Count ?? 0)); index++) {
            var topology = topologies![index];

            if (
                (topology is null) ||
                (topology.Name != name) ||
                !TopologyCompilation.Validates(topology: topology)
            ) {
                continue;
            }

            var anchor = FindAnchor(
                definition: definition,
                topologyName: name
            );

            if (anchor is null) {
                return TopologyCompilation.Find(
                    lattices: topologies,
                    name: name
                );
            }

            var cache = Cache.GetValue(
                topology,
                static _ => new AnchoredCompile()
            );

            lock (cache) {
                if (
                    !ReferenceEquals(
                    objA: cache.Anchor,
                    objB: anchor
                ) ||
                    (cache.Compiled is null)
                ) {
                    var frame = WorldDefinitionRows.ResolvedFrame(
                        definition: definition,
                        placement: anchor
                    );

                    cache.Anchor = anchor;
                    cache.Compiled = TopologyCompilation.Compile(
                        topology: topology,
                        anchorPosition: frame.FixedPosition,
                        anchorYawRadians: frame.FixedYawRadians
                    );
                }

                return cache.Compiled;
            }
        }

        return null;
    }
    /// <summary>Finds a named physical topology, if any. Discrete boards never allocate a fluid field.</summary>
    /// <param name="state">The state section.</param>
    /// <param name="name">The region identity.</param>
    /// <returns>The named physical topology or null.</returns>
    public static WorldFieldTopology? FindPhysical(IStateSection? state, string name) {
        var topologies = state?.Lattices;

        for (var index = 0; (index < (topologies?.Count ?? 0)); index++) {
            if ((topologies![index] is WorldFieldTopology field) && (field.Name == name)) {
                return field;
            }
        }
        return null;
    }
}
