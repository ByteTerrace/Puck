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
        AppendString(ref hash, row.Board?.Topology);
        hash.Add(row.Board?.Empty ?? 0);
        hash.Add(row.Tokens?.Capacity ?? 0);
        AppendString(ref hash, row.Zone?.Tokens);
        hash.Add((byte)(row.Zone?.Ordered == true ? 1 : 0));
        AppendString(ref hash, row.KeysFrom);
        AppendString(ref hash, row.ValuesFrom);
        AppendString(ref hash, row.PhaseOf);
        AppendString(ref hash, row.Knowledge?.Source);
        AppendString(ref hash, row.Knowledge?.Mask);
        AppendVisibility(ref hash, row.Visibility);
        if (row.History is { } history) {
            hash.Add(history.Capacity);
            hash.Add(history.Empty);
            hash.Add(row.HistoryCursor);
        }
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
