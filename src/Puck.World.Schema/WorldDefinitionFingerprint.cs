using System.Runtime.CompilerServices;
using System.Text.Json;
using Puck.Assets;
using Puck.Maths;

namespace Puck.World;

/// <summary>Canonical document fingerprint for optimistic edits. Computed only when preparing or admitting a
/// guarded edit, never on an idle simulation tick.</summary>
public static class WorldDefinitionFingerprint {
    private sealed class PlacementLookup(WorldPlacementsSection section) {
        public Dictionary<string, (WorldPlacement Row, int Ordinal)> Rows { get; } =
            (section.Rows ?? []).Select(selector: (row, ordinal) => (row, ordinal)).ToDictionary(
            pair => pair.row.Id,
            pair => (pair.row, pair.ordinal),
            StringComparer.Ordinal
        );
    }

    private static readonly ConditionalWeakTable<WorldPlacementsSection, PlacementLookup> Placements = new();

    private static void WriteStateInputs(Utf8JsonWriter writer, WorldDefinition definition, IEnumerable<string> stateRows) {
        writer.WriteStartArray();
        foreach (var name in stateRows.Order(comparer: StringComparer.Ordinal)) {
            writer.WriteStringValue(value: name);
            if (WorldDefinitionRows.FindStateRow(
                definition.State,
                name
            ) is { } row) {
                JsonSerializer.Serialize(
                    writer,
                    row,
                    WorldJsonContext.Default.WorldStateRow
                );
            } else { writer.WriteNullValue(); }
        }
        writer.WriteEndArray();
    }

    /// <summary>Hashes authored document inputs, optionally restricting the world state rows included.</summary>
    /// <param name="definition">The proposal's base definition.</param>
    /// <param name="stateRows">World state dependencies, or null for every row. Other document sections remain included.</param>
    /// <returns>The <see cref="ContentPin.Hex"/> of the canonical document bytes.</returns>
    public static string Compute(WorldDefinition definition, IReadOnlyList<string>? stateRows = null) {
        if (
            (stateRows is not null) &&
            (definition.StateRaw is { } state)
        ) {
            var selected = new HashSet<string>(
                collection: stateRows,
                comparer: StringComparer.Ordinal
            );

            definition = definition with { StateRaw = state with { World = [.. definition.State.Where(predicate: row => selected.Contains(item: row.Name))] } };
        }
        return ContentPin.Compute(content: WorldDefinitionSerialization.Serialize(definition: definition)).Hex;
    }
    /// <summary>Hashes one <c>views.pipelines</c> row: the revision a parameter preview is based on and a commit of it
    /// names. Any change to the row, including another commit, moves it; the row's name is part of it, so a revision
    /// taken from one instance never matches another.</summary>
    /// <param name="pipeline">The pipeline row.</param>
    /// <returns>The <see cref="ContentPin.Hex"/> of the row's canonical bytes.</returns>
    public static string ComputePipeline(WorldViewPipeline pipeline) {
        ArgumentNullException.ThrowIfNull(argument: pipeline);

        return ContentPin.Compute(content: JsonSerializer.SerializeToUtf8Bytes(
            value: pipeline,
            jsonTypeInfo: WorldJsonContext.Default.WorldViewPipeline
        )).Hex;
    }
    /// <summary>Hashes named inputs in ordinal name order, including absent rows and the generation seed.</summary>
    public static string ComputeInputs(WorldDefinition definition, IReadOnlyList<string> placementIds, IReadOnlyList<string> stateRows) {
        using var bytes = new MemoryStream();

        using (var writer = new Utf8JsonWriter(bytes)) {
            writer.WriteStartArray();
            writer.WriteNumberValue(value: (definition.Generation?.WorldSeed ?? 0UL));
            foreach (var id in placementIds.Order(comparer: StringComparer.Ordinal)) {
                writer.WriteStringValue(value: id);
                if (WorldDefinitionRows.FindPlacement(
                    id: id,
                    placements: definition.Placements
                ) is { } placement) {
                    JsonSerializer.Serialize(
                        writer,
                        placement,
                        WorldJsonContext.Default.WorldPlacement
                    );
                } else { writer.WriteNullValue(); }
            }
            WriteStateInputs(
                definition: definition,
                stateRows: stateRows,
                writer: writer
            );
            writer.WriteEndArray();
        }
        return ContentPin.Compute(content: bytes.GetBuffer().AsSpan(
            0,
            checked((int)bytes.Length)
        )).Hex;
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
            writer.WriteNumberValue(value: region.MinXRaw);
            writer.WriteNumberValue(value: region.MinZRaw);
            writer.WriteNumberValue(value: region.MaxXRaw);
            writer.WriteNumberValue(value: region.MaxZRaw);
            writer.WriteNumberValue(value: region.MinYRaw);
            writer.WriteNumberValue(value: region.MaxYRaw);

            var stateRows = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var placement in SpatialRows(
                definition: definition,
                region: region
            )) {
                JsonSerializer.Serialize(
                    writer,
                    placement,
                    WorldJsonContext.Default.WorldPlacement
                );
                WorldStateDocumentValues.CollectReferencedRows(
                    graph: placement,
                    rows: stateRows
                );
            }
            // Canonical rows retain state references. Include their source values so a bound extent changing
            // inside the same census cannot leave a spatial guard looking unchanged.
            WriteStateInputs(
                definition: definition,
                stateRows: stateRows,
                writer: writer
            );
            writer.WriteEndArray();
        }
        return ContentPin.Compute(content: bytes.GetBuffer().AsSpan(
            0,
            checked((int)bytes.Length)
        )).Hex;
    }
    /// <summary>Returns the authored rows observed by a bounded spatial read in document order.</summary>
    public static IReadOnlyList<WorldPlacement> SpatialRows(WorldDefinition definition, in WorldSpatialReadRegion region) {
        if (definition.PlacementsRaw is not { } section) { return []; }
        var lookup = Placements.GetValue(
            section,
            static value => new PlacementLookup(section: value)
        ).Rows;
        var selected = new HashSet<string>(comparer: StringComparer.Ordinal);

        void Include(string id) {
            while (
                selected.Add(item: id) &&
                lookup.TryGetValue(
                key: id,
                value: out var found
            ) &&
                (found.Row.Parent is { } parent)
            ) {
                id = parent;
            }
        }
        var index = definition.SpatialQueryIndex;

        foreach (var volume in index.Query(region.Bounds).Volumes) { Include(id: volume.PlacementId); }
        // A dynamic spatial row has no static exclusion proof. Preserve it as an explicit uncertainty dependency.
        foreach (var unsupported in index.Unsupported) { Include(id: unsupported.PlacementId); }
        return selected.Where(predicate: lookup.ContainsKey).Select(selector: id => lookup[id]).OrderBy(keySelector: item => item.Ordinal).Select(selector: item => item.Row).ToArray();
    }
}
/// <summary>A fixed-point world XYZ box used by a guarded spatial read.</summary>
public readonly record struct WorldSpatialReadRegion(long MinXRaw, long MinZRaw, long MaxXRaw, long MaxZRaw,
    long MinYRaw = -long.MaxValue, long MaxYRaw = long.MaxValue) {
    /// <summary>The conservatively rounded fixed-point query envelope.</summary>
    public FixedSpatialAabb Bounds {
        get {
            static (FixedQ4816 Center, FixedQ4816 Extent) Axis(long min, long max) {
                var center = ((((Int128)min) + max) / 2);
                var extent = Int128.Max(
                    x: (center - min),
                    y: (((Int128)max) - center)
                );

                return (FixedQ4816.FromRawBits(value: checked((long)center)), FixedQ4816.FromRawBits(value: checked((long)extent)));
            }
            var x = Axis(
                MinXRaw,
                MaxXRaw
            ); var y = Axis(
                MinYRaw,
                MaxYRaw
            ); var z = Axis(
                MinZRaw,
                MaxZRaw
            );

            return new(
                Center: new(
                    X: x.Center,
                    Y: y.Center,
                    Z: z.Center
                ),
                Extent: new(
                    X: x.Extent,
                    Y: y.Extent,
                    Z: z.Extent
                )
            );
        }
    }
    /// <summary>An empty accumulation region; enclose at least one shape before querying.</summary>
    public static WorldSpatialReadRegion Empty => new(
        MaxXRaw: long.MinValue,
        MaxYRaw: long.MinValue,
        MaxZRaw: long.MinValue,
        MinXRaw: long.MaxValue,
        MinYRaw: long.MaxValue,
        MinZRaw: long.MaxValue
    );

    /// <summary>Expands the region to include a finite shape's full world envelope.</summary>
    public WorldSpatialReadRegion Enclose(in FixedSpatialAabb box) => new(
        Math.Min(
            val1: MinXRaw,
            val2: checked((box.Center.X.Value - box.Extent.X.Value))
        ),
        Math.Min(
            val1: MinZRaw,
            val2: checked((box.Center.Z.Value - box.Extent.Z.Value))
        ),
        Math.Max(
            val1: MaxXRaw,
            val2: checked((box.Center.X.Value + box.Extent.X.Value))
        ),
        Math.Max(
            val1: MaxZRaw,
            val2: checked((box.Center.Z.Value + box.Extent.Z.Value))
        ),
        Math.Min(
            val1: MinYRaw,
            val2: checked((box.Center.Y.Value - box.Extent.Y.Value))
        ),
        Math.Max(
            val1: MaxYRaw,
            val2: checked((box.Center.Y.Value + box.Extent.Y.Value))
        )
    );
}
