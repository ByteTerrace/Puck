using Puck.Maths;

namespace Puck.World.Server;

public static partial class WorldRuntimeStateHash {
    private static void AppendVisibility(ref Fnv1aHash hash, WorldStateVisibility? visibility) {
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
            WorldStateDomain.Slot => 0,
            WorldStateDomain.Keys => 1,
            WorldStateDomain.KeysOf => 2,
            WorldStateDomain.CellsOf => 3,
            WorldStateDomain.Ring => 4,
            _ => throw new InvalidOperationException($"unknown state domain '{domain.GetType().Name}'"),
        }));
        switch (domain) {
            case WorldStateDomain.KeysOf keysOf:
                AppendString(ref hash, keysOf.Row.Value);
                hash.Add((byte)(keysOf.Ordered ? 1 : 0));
                break;
            case WorldStateDomain.CellsOf cellsOf:
                AppendString(ref hash, cellsOf.Topology);
                hash.Add(cellsOf.Empty);
                break;
            case WorldStateDomain.Ring ring:
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
            AppendString(ref hash, row.Name);
            hash.Add((byte)row.Kind);
            hash.Add((byte)row.Wrap);
            hash.Add(row.Radius);
            hash.Add(row.Width);
            hash.Add(row.Depth);
            hash.Add(row.Layers);
        }
    }
}
