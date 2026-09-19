using Puck.Maths;

namespace Puck.World.Server;

public static partial class WorldStateHashComposition {
    /// <summary>Folds the lattice topologies a state section declares its board and field rows over.</summary>
    /// <param name="hash">The running hash.</param>
    /// <param name="state">The state section, or <see langword="null"/>.</param>
    public static void AppendTopologies(ref Fnv1aHash hash, WorldStateSection? state) {
        var rows = state?.Lattices;

        hash.Add(value: (rows?.Count ?? 0));
        for (var index = 0; (index < (rows?.Count ?? 0)); index++) {
            var row = rows![index];
            // A physical field normalizes on the same flat terms as the discrete kinds: its footprint, no wrap, no
            // radius.
            var (width, depth, layers, wrap, radius) = ((row is WorldFieldTopology field)
                ? (field.Width, field.Depth, field.Layers, TopologyWrap.None, 0)
                : Discrete(topology: row)
            );

            AppendString(
                hash: ref hash,
                value: row.Name
            );
            hash.Add(value: ((byte)row.Kind));
            hash.Add(value: ((byte)wrap));
            hash.Add(value: radius);
            hash.Add(value: width);
            hash.Add(value: depth);
            hash.Add(value: layers);
        }
    }

    private static (int Width, int Depth, int Layers, TopologyWrap Wrap, int Radius) Discrete(LatticeTopology topology) {
        var normalized = TopologyCompilation.Normalize(topology: topology);

        return (normalized.Width, normalized.Depth, normalized.Layers, normalized.Wrap, normalized.Radius);
    }
}
