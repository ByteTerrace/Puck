namespace Puck.World.Server;

public sealed partial class WorldTick {
    // Journal reconstruction is an edit boundary. Derive children before a later edit checks its layout inputs,
    // and only re-derive a template when its input rows moved, just as the live sweep does.
    internal WorldDefinition ProjectDealsForEdit(WorldDefinition definition, WorldDefinition? previous) {
        StateArena? arena = null;
        m_dealChanges.Clear();
        m_dealReports.Clear();
        foreach (var template in definition.Placements) {
            if (template.Deal is not { } deal) { continue; }
            var row = WorldDefinitionRows.FindStateRow(definition.State, deal.Row);
            var variant = deal.Variants is { } variants ? WorldDefinitionRows.FindStateRow(definition.State, variants.Row) : null;
            var before = previous is null ? null : WorldDefinitionRows.FindPlacement(previous.Placements, template.Id);
            if (before == template && ReferenceEquals(row, WorldDefinitionRows.FindStateRow(previous!.State, deal.Row)) &&
                ReferenceEquals(variant, deal.Variants is { } oldVariant ? WorldDefinitionRows.FindStateRow(previous.State, oldVariant.Row) : null)) { continue; }
            arena ??= WorldStateReader.Seed(definition);
            var ordinal = arena.Catalog.TryResolve(StateLane.Document, deal.Row, out var handle) ? handle.Ordinal : -1;
            var variantOrdinal = deal.Variants is { } declared && arena.Catalog.TryResolve(StateLane.Document, declared.Row, out var variantHandle) ? variantHandle.Ordinal : -1;
            Deal(definition, arena, template, deal, ordinal, variantOrdinal, new DealMemo(), definition.Generation?.WorldSeed ?? 0UL);
        }
        if (m_dealChanges.Count == 0) { return definition; }
        var placements = definition.Placements.ToList();
        foreach (var (id, placement) in m_dealChanges) {
            var index = placements.FindIndex(row => row.Id == id);
            if (placement is null) { if (index >= 0) { placements.RemoveAt(index); } }
            else if (index < 0) { placements.Add(placement); }
            else { placements[index] = placement; }
        }
        return definition with { PlacementRowsRaw = placements.ToArray() };
    }

    // The reconstructed document already carries the projection. Its next ordinary tick owes no second sweep.
    internal void PrimeDealProjection() {
        m_dealMemos.Clear();
        var arena = Host.Arena;
        foreach (var template in Host.Definition.Placements) {
            if (template.Deal is not { } deal) { continue; }
            var row = arena.Catalog.TryResolve(StateLane.Document, deal.Row, out var handle) ? handle.Ordinal : -1;
            var variant = deal.Variants is { } declared && arena.Catalog.TryResolve(StateLane.Document, declared.Row, out var other) ? other.Ordinal : -1;
            m_dealMemos[template.Id] = new DealMemo {
                Arena = arena, RowVersion = row < 0 ? 0UL : arena.RowVersion(row),
                VariantVersion = variant < 0 ? 0UL : arena.RowVersion(variant),
                Template = template, Swept = true, WorldSeed = Host.Definition.Generation?.WorldSeed ?? 0UL,
                Distribution = template.Distribution,
                Offsets = WorldPlacementDeal.Offsets(template, Host.Definition.Generation?.WorldSeed ?? 0UL).Select(offset => offset.ToVector3()).ToArray(),
            };
        }
    }
}
