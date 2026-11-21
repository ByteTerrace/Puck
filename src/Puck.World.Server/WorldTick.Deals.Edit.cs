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
            var memo = new DealMemo();
            for (var index = 0; (index < definition.Placements.Count); index++) {
                if (WorldPlacementDeal.IsChild(placement: definition.Placements[index], parent: template)) { memo.Children.Add(item: index); }
            }
            Deal(definition.Placements, arena, template, deal, ordinal, variantOrdinal, memo, definition.Generation?.WorldSeed ?? 0UL);
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

    // The reconstructed document already carries the projection. Its next ordinary tick owes no second sweep: every
    // template is indexed against the installed list, marked swept at its current rows, and its children left as they
    // stand.
    internal void PrimeDealProjection() {
        var arena = Host.Arena;
        var placements = Host.Definition.Placements;
        var worldSeed = (Host.Definition.Generation?.WorldSeed ?? 0UL);

        m_dealMemos.Clear();
        m_dealTemplatesOf = null;
        _ = IndexDealTemplates(placements: placements, catalog: arena.Catalog);
        foreach (var (index, memo) in m_dealTemplates) {
            var template = placements[index];

            memo.Arena = arena;
            memo.RowVersion = ((memo.RowOrdinal < 0) ? 0UL : arena.RowVersion(rowOrdinal: memo.RowOrdinal));
            memo.VariantVersion = ((memo.VariantOrdinal < 0) ? 0UL : arena.RowVersion(rowOrdinal: memo.VariantOrdinal));
            memo.Template = template;
            memo.Swept = true;
            memo.WorldSeed = worldSeed;
            memo.Distribution = template.Distribution;
            memo.Slots = WorldPlacementDeal.InstanceCount(template: template, worldSeed: worldSeed);
        }
        LeaveDealChildren(placements: placements);
    }
}
