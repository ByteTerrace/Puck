using System.Runtime.CompilerServices;

namespace Puck.World;

/// <summary>The document-anchored entrance to <see cref="TopologyCompilation"/>: a Grid topology a placement's
/// <c>board</c> facet names takes its world origin from that placement's resolved frame.</summary>
public static class WorldTopologyCompilation {
    private static readonly ConditionalWeakTable<LatticeTopology, AnchoredCompile> s_cache = new();

    // Anchor is the resolving placement instance, not merely its id — see Find's remarks on why identity (not name)
    // is the correct staleness test.
    private sealed class AnchoredCompile {
        public WorldPlacement? Anchor;
        public CompiledTopology? Compiled;
    }

    /// <summary>Finds the physical topology, if any. Discrete boards never allocate a fluid field.</summary>
    /// <param name="state">The state section.</param>
    /// <returns>The first physical topology or null.</returns>
    public static WorldFieldTopology? FindPhysical(IStateSection? state) {
        var topologies = state?.Lattices;
        for (var index = 0; index < (topologies?.Count ?? 0); index++) {
            if (topologies![index] is WorldFieldTopology field) {
                return field;
            }
        }
        return null;
    }

    /// <summary>Finds and compiles a discrete topology by name, anchored: a Grid topology a placement's <c>board</c>
    /// facet names (<see cref="WorldPlacementBoard.Topology"/>) takes its world origin from that placement's own
    /// <see cref="WorldDefinitionRows.ResolvedFrame"/> plus its authored (now local) <c>origin</c> — translation only;
    /// the anchor's yaw is not applied to the grid's own axes, which stay world-axis-aligned regardless of the
    /// anchor's heading. An unanchored topology (no placement's <c>board</c> facet names it) resolves exactly as
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

        for (var index = 0; index < (topologies?.Count ?? 0); index++) {
            var topology = topologies![index];

            if ((topology is null) || (topology.Name != name) || !TopologyCompilation.TryValidate(topology, out _)) {
                continue;
            }

            var anchor = FindAnchor(definition: definition, topologyName: name);

            if (anchor is null) {
                return TopologyCompilation.Find(lattices: topologies, name: name);
            }

            var cache = s_cache.GetValue(topology, static _ => new AnchoredCompile());

            lock (cache) {
                if (!ReferenceEquals(objA: cache.Anchor, objB: anchor) || (cache.Compiled is null)) {
                    cache.Anchor = anchor;
                    cache.Compiled = TopologyCompilation.Compile(
                        topology: topology,
                        anchorOffset: WorldDefinitionRows.ResolvedFrame(definition: definition, placement: anchor).Position
                    );
                }

                return cache.Compiled;
            }
        }

        return null;
    }
    // Every real placement carries at most one board facet naming this topology (validated: WorldDefinitionValidator.
    // Board.cs refuses a second placement claiming the same topology) — the first match is the only one there can be.
    private static WorldPlacement? FindAnchor(WorldDefinition definition, string topologyName) {
        foreach (var placement in definition.Placements) {
            if (string.Equals(a: placement.Board?.Topology, b: topologyName, comparisonType: StringComparison.Ordinal)) {
                return placement;
            }
        }

        return null;
    }
}
