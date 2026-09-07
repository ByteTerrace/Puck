using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.World;

/// <summary>Canonical document fingerprint for optimistic edits. Computed only when preparing or admitting a
/// guarded edit, never on an idle simulation tick.</summary>
public static class WorldDefinitionFingerprint {
    private sealed class PlacementLookup(WorldPlacementsSection section) {
        public Dictionary<string, (WorldPlacement Row, int Ordinal)> Rows { get; } =
            (section.Rows ?? []).Select((row, ordinal) => (row, ordinal)).ToDictionary(pair => pair.row.Id, pair => (pair.row, pair.ordinal), StringComparer.Ordinal);
    }
    private static readonly ConditionalWeakTable<WorldPlacementsSection, PlacementLookup> s_placements = new();

    /// <summary>Hashes named inputs in ordinal name order, including absent rows and the generation seed.</summary>
    public static string ComputeInputs(WorldDefinition definition, IReadOnlyList<string> placementIds, IReadOnlyList<string> stateRows) {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) {
            writer.WriteStartArray();
            writer.WriteNumberValue(definition.Generation?.WorldSeed ?? 0UL);
            foreach (var id in placementIds.Order(StringComparer.Ordinal)) {
                writer.WriteStringValue(id);
                if (WorldDefinitionRows.FindPlacement(id: id, placements: definition.Placements) is { } placement) {
                    JsonSerializer.Serialize(writer, placement, WorldJsonContext.Default.WorldPlacement);
                } else { writer.WriteNullValue(); }
            }
            WriteStateInputs(writer, definition, stateRows);
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }
    /// <summary>Hashes authored document inputs, optionally restricting the world state rows included.</summary>
    /// <param name="definition">The proposal's base definition.</param>
    /// <param name="stateRows">World state dependencies, or null for every row. Other document sections remain included.</param>
    /// <returns>Uppercase SHA-256 hex of the canonical document bytes.</returns>
    public static string Compute(WorldDefinition definition, IReadOnlyList<string>? stateRows = null) {
        if (stateRows is not null && definition.StateRaw is { } state) {
            var selected = new HashSet<string>(stateRows, StringComparer.Ordinal);
            definition = definition with { StateRaw = state with { World = [.. definition.State.Where(row => selected.Contains(row.Name))] } };
        }
        return Convert.ToHexString(SHA256.HashData(WorldDefinitionSerialization.Serialize(definition)));
    }

    /// <summary>Computes the canonical census for one bounded spatial read. The census includes every authored
    /// spatial volume whose conservative envelope intersects the region, plus all of those rows'
    /// parent frames. That makes a new or enlarged obstacle entering the region a negative dependency while edits
    /// wholly outside it remain independent.</summary>
    /// <param name="definition">The document being observed.</param>
    /// <param name="region">The finite fixed-point world XYZ read bounds.</param>
    public static string ComputeSpatial(WorldDefinition definition, in WorldSpatialReadRegion region) {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) {
            writer.WriteStartArray();
            writer.WriteNumberValue(region.MinXRaw);
            writer.WriteNumberValue(region.MinZRaw);
            writer.WriteNumberValue(region.MaxXRaw);
            writer.WriteNumberValue(region.MaxZRaw);
            writer.WriteNumberValue(region.MinYRaw);
            writer.WriteNumberValue(region.MaxYRaw);

            var stateRows = new HashSet<string>(StringComparer.Ordinal);
            foreach (var placement in SpatialRows(definition, region)) {
                JsonSerializer.Serialize(writer, placement, WorldJsonContext.Default.WorldPlacement);
                WorldStateDocumentValues.CollectReferencedRows(placement, stateRows);
            }
            // Canonical rows retain state references. Include their source values so a bound extent changing
            // inside the same census cannot leave a spatial guard looking unchanged.
            WriteStateInputs(writer, definition, stateRows);
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    private static void WriteStateInputs(Utf8JsonWriter writer, WorldDefinition definition, IEnumerable<string> stateRows) {
        writer.WriteStartArray();
        foreach (var name in stateRows.Order(StringComparer.Ordinal)) {
            writer.WriteStringValue(name);
            if (WorldDefinitionRows.FindStateRow(definition.State, name) is { } row) {
                JsonSerializer.Serialize(writer, row, WorldJsonContext.Default.WorldStateRow);
            } else { writer.WriteNullValue(); }
        }
        writer.WriteEndArray();
    }

    /// <summary>Returns the authored rows observed by a bounded spatial read in document order.</summary>
    public static IReadOnlyList<WorldPlacement> SpatialRows(WorldDefinition definition, in WorldSpatialReadRegion region) {
        if (definition.PlacementsRaw is not { } section) { return []; }
        var lookup = s_placements.GetValue(section, static value => new PlacementLookup(value)).Rows;
        var selected = new HashSet<string>(StringComparer.Ordinal);
        void Include(string id) {
            while (selected.Add(id) && lookup.TryGetValue(id, out var found) && found.Row.Parent is { } parent) {
                id = parent;
            }
        }
        var index = definition.SpatialQueryIndex;
        foreach (var volume in index.Query(region.Bounds).Volumes) { Include(volume.PlacementId); }
        // A dynamic spatial row has no static exclusion proof. Preserve it as an explicit uncertainty dependency.
        foreach (var unsupported in index.Unsupported) { Include(unsupported.PlacementId); }
        return selected.Where(lookup.ContainsKey).Select(id => lookup[id]).OrderBy(item => item.Ordinal).Select(item => item.Row).ToArray();
    }
}

/// <summary>A fixed-point world XYZ box used by a guarded spatial read.</summary>
public readonly record struct WorldSpatialReadRegion(long MinXRaw, long MinZRaw, long MaxXRaw, long MaxZRaw,
    long MinYRaw = -long.MaxValue, long MaxYRaw = long.MaxValue) {
    /// <summary>An empty accumulation region; enclose at least one shape before querying.</summary>
    public static WorldSpatialReadRegion Empty => new(long.MaxValue, long.MaxValue, long.MinValue, long.MinValue, long.MaxValue, long.MinValue);

    /// <summary>The conservatively rounded fixed-point query envelope.</summary>
    public FixedSpatialAabb Bounds {
        get {
            static (FixedQ4816 Center, FixedQ4816 Extent) Axis(long min, long max) {
                var center = ((Int128)min + max) / 2;
                var extent = Int128.Max(center - min, (Int128)max - center);
                return (FixedQ4816.FromRawBits(checked((long)center)), FixedQ4816.FromRawBits(checked((long)extent)));
            }
            var x = Axis(MinXRaw, MaxXRaw); var y = Axis(MinYRaw, MaxYRaw); var z = Axis(MinZRaw, MaxZRaw);
            return new(new(x.Center, y.Center, z.Center), new(x.Extent, y.Extent, z.Extent));
        }
    }

    /// <summary>Expands the region to include a finite shape's full world envelope.</summary>
    public WorldSpatialReadRegion Enclose(in FixedSpatialAabb box) => new(
        Math.Min(MinXRaw, checked(box.Center.X.Value - box.Extent.X.Value)),
        Math.Min(MinZRaw, checked(box.Center.Z.Value - box.Extent.Z.Value)),
        Math.Max(MaxXRaw, checked(box.Center.X.Value + box.Extent.X.Value)),
        Math.Max(MaxZRaw, checked(box.Center.Z.Value + box.Extent.Z.Value)),
        Math.Min(MinYRaw, checked(box.Center.Y.Value - box.Extent.Y.Value)),
        Math.Max(MaxYRaw, checked(box.Center.Y.Value + box.Extent.Y.Value)));
}
