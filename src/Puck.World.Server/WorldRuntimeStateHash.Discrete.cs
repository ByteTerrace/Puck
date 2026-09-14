using Puck.Maths;

namespace Puck.World.Server;

public static partial class WorldRuntimeStateHash {
    private static void AppendVisibility(ref Fnv1aHash hash, StateVisibility? visibility) {
        hash.Add(value: ((byte)((visibility is null)
            ? 0
            : 1)));
        if (visibility is null) { return; }
        hash.Add(value: (visibility.Readers?.Count ?? -1));
        for (var index = 0; (index < (visibility.Readers?.Count ?? 0)); index++) {
            AppendString(
                hash: ref hash,
                value: visibility.Readers![index]
            );
        }
    }
    private static void AppendDiscreteRow(ref Fnv1aHash hash, WorldStateRow row) {
        var domain = row.EffectiveDomain;

        hash.Add(value: ((byte)(domain switch {
            StateDomain.Slot => 0,
            StateDomain.Keys => 1,
            StateDomain.KeysOf => 2,
            StateDomain.CellsOf => 3,
            StateDomain.Ring => 4,
            _ => throw new InvalidOperationException(message: $"unknown state domain '{domain.GetType().Name}'"),
        })));
        switch (domain) {
            case StateDomain.KeysOf keysOf:
                AppendString(
                    hash: ref hash,
                    value: keysOf.Row.Value
                );
                hash.Add(value: ((byte)(keysOf.Ordered
                    ? 1
                    : 0)));
                break;
            case StateDomain.CellsOf cellsOf:
                AppendString(
                    hash: ref hash,
                    value: cellsOf.Topology
                );
                hash.Add(value: cellsOf.Empty);
                break;
            case StateDomain.Ring ring:
                hash.Add(value: ring.Capacity);
                hash.Add(value: ring.Empty);
                hash.Add(value: row.HistoryCursor);
                break;
        }
        AppendString(
            hash: ref hash,
            value: row.ValuesFrom
        );
        AppendString(
            hash: ref hash,
            value: row.PhaseOf
        );

        if (row.Inverse is { } inverse) {
            AppendString(
                hash: ref hash,
                value: inverse.Tokens.Value
            );
            AppendString(
                hash: ref hash,
                value: inverse.Codes.Value
            );
        } else {
            AppendString(
                hash: ref hash,
                value: null
            );
            AppendString(
                hash: ref hash,
                value: null
            );
        }
        AppendString(
            hash: ref hash,
            value: row.Knowledge?.Source
        );
        AppendString(
            hash: ref hash,
            value: row.Knowledge?.Mask
        );
        AppendVisibility(
            hash: ref hash,
            visibility: row.Visibility
        );
    }
    private static void AppendDiscreteTopologies(ref Fnv1aHash hash, WorldStateSection? state) {
        var rows = state?.Lattices;

        hash.Add(value: (rows?.Count ?? 0));
        for (var index = 0; (index < (rows?.Count ?? 0)); index++) {
            var row = rows![index];
            // A physical field normalizes on the same flat terms as the discrete kinds: its footprint, no wrap, no
            // radius — the same bytes the hash has always folded for it.
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
