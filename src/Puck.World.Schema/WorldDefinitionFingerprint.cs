using System.Security.Cryptography;
using System.Text.Json;

namespace Puck.World;

/// <summary>Canonical document fingerprint for optimistic edits. Computed only when preparing or admitting a
/// guarded edit, never on an idle simulation tick.</summary>
public static class WorldDefinitionFingerprint {
    /// <summary>Hashes authored document inputs, optionally restricting the world state rows included.</summary>
    /// <param name="definition">The proposal's base definition.</param>
    /// <param name="stateRows">World state dependencies, or null for every row. Other document sections remain included.</param>
    /// <param name="layoutTemplate">Optional placement layout scope; unrelated document sections are omitted.</param>
    /// <returns>Uppercase SHA-256 hex of the canonical document bytes.</returns>
    public static string Compute(WorldDefinition definition, IReadOnlyList<string>? stateRows = null, string? layoutTemplate = null) {
        if (layoutTemplate is not null) {
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes)) {
                writer.WriteStartArray();
                writer.WriteNumberValue(definition.Generation?.WorldSeed ?? 0UL);
                foreach (var placement in LayoutRows(definition, layoutTemplate)) {
                    JsonSerializer.Serialize(writer, placement, WorldJsonContext.Default.WorldPlacement);
                }
                var selected = stateRows is null ? null : new HashSet<string>(stateRows, StringComparer.Ordinal);
                foreach (var row in definition.State) {
                    if (selected is null || selected.Contains(row.Name)) { JsonSerializer.Serialize(writer, row, WorldJsonContext.Default.WorldStateRow); }
                }
                writer.WriteEndArray();
            }
            return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
        }
        if (stateRows is not null && definition.StateRaw is { } state) {
            var selected = new HashSet<string>(stateRows, StringComparer.Ordinal);
            definition = definition with { StateRaw = state with { World = [.. definition.State.Where(row => selected.Contains(row.Name))] } };
        }
        return Convert.ToHexString(SHA256.HashData(WorldDefinitionSerialization.Serialize(definition)));
    }

    /// <summary>Selects a layout's template and children, all footprint rows (including future deal blockers), and
    /// their ancestor frames. Including the census detects newly introduced obstacles, not just edits to old ones.</summary>
    /// <param name="definition">The immutable proposal or commit document.</param>
    /// <param name="templateId">The selected deal template.</param>
    /// <returns>The dependencies in authored order.</returns>
    public static IReadOnlyList<WorldPlacement> LayoutRows(WorldDefinition definition, string templateId) {
        var byId = definition.Placements.ToDictionary(p => p.Id, StringComparer.Ordinal);
        byId.TryGetValue(templateId, out var template);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in definition.Placements) {
            if (placement.Id != templateId && placement.Footprint is null && !WorldPlacementDeal.IsChild(placement, template)) { continue; }
            var frame = placement;
            while (selected.Add(frame.Id) && frame.Parent is { } parent && byId.TryGetValue(parent, out frame!)) { }
        }
        return [.. definition.Placements.Where(p => selected.Contains(p.Id))];
    }
}
