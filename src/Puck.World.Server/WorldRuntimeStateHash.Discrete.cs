using Puck.Maths;

namespace Puck.World.Server;

public static partial class WorldRuntimeStateHash {
    private static void AppendVisibility(ref Fnv1aHash hash, StateVisibility? visibility) {
        hash.Add((byte)(visibility is null ? 0 : 1));
        if (visibility is null) { return; }
        hash.Add(visibility.Readers?.Count ?? -1);
        for (var index = 0; index < (visibility.Readers?.Count ?? 0); index++) {
            AppendString(ref hash, visibility.Readers![index]);
        }
    }

    private static void AppendDiscreteRow(ref Fnv1aHash hash, WorldStateRow row) {
        var domain = row.EffectiveDomain;
        hash.Add((byte)(domain switch {
            StateDomain.Slot => 0,
            StateDomain.Keys => 1,
            StateDomain.KeysOf => 2,
            StateDomain.CellsOf => 3,
            StateDomain.Ring => 4,
            _ => throw new InvalidOperationException($"unknown state domain '{domain.GetType().Name}'"),
        }));
        switch (domain) {
            case StateDomain.KeysOf keysOf:
                AppendString(ref hash, keysOf.Row.Value);
                hash.Add((byte)(keysOf.Ordered ? 1 : 0));
                break;
            case StateDomain.CellsOf cellsOf:
                AppendString(ref hash, cellsOf.Topology);
                hash.Add(cellsOf.Empty);
                break;
            case StateDomain.Ring ring:
                hash.Add(ring.Capacity);
                hash.Add(ring.Empty);
                hash.Add(row.HistoryCursor);
                break;
        }
        AppendString(ref hash, row.ValuesFrom);
        AppendString(ref hash, row.PhaseOf);
        AppendString(ref hash, row.Knowledge?.Source);
        AppendString(ref hash, row.Knowledge?.Mask);
        AppendVisibility(ref hash, row.Visibility);
    }

    private static void AppendDiscreteTopologies(ref Fnv1aHash hash, WorldStateSection? state) {
        var rows = state?.Lattices;
        hash.Add(rows?.Count ?? 0);
        for (var index = 0; index < (rows?.Count ?? 0); index++) {
            var row = rows![index];
            // A physical field normalizes on the same flat terms as the discrete kinds: its footprint, no wrap, no
            // radius — the same bytes the hash has always folded for it.
            var (width, depth, layers, wrap, radius) = ((row is WorldFieldTopology field)
                ? (field.Width, field.Depth, field.Layers, TopologyWrap.None, 0)
                : Discrete(row));
            AppendString(ref hash, row.Name);
            hash.Add((byte)row.Kind);
            hash.Add((byte)wrap);
            hash.Add(radius);
            hash.Add(width);
            hash.Add(depth);
            hash.Add(layers);
        }
    }
    private static (int Width, int Depth, int Layers, TopologyWrap Wrap, int Radius) Discrete(LatticeTopology topology) {
        var normalized = TopologyCompilation.Normalize(topology);
        return (normalized.Width, normalized.Depth, normalized.Layers, normalized.Wrap, normalized.Radius);
    }
}
