namespace Puck.World;

public static partial class WorldRuleCompiler {
    // A placement's compiled ordinal is its own position in definition.Placements — the SAME order
    // WorldPopulation.ReconcileInhabitants walks to seat inhabited placements, and the index
    // WorldPopulation.BodyForPlacementOrdinal's tick-path table is aligned to.
    private static int ResolvePlacementOrdinal(string channel, WorldDefinition definition, string placementId, string ruleName) {
        var placements = definition.Placements;

        for (var ordinal = 0; (ordinal < placements.Count); ordinal++) {
            if (!string.Equals(a: placements[ordinal].Id, b: placementId, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            if (placements[ordinal].Inhabit is not { Count: 1 }) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{channel}' names 'placement:{placementId}', which does not carry an inhabit facet with count 1"
                );
            }

            return ordinal;
        }

        throw new WorldRuleException(
            refusal: WorldRuleRefusal.SpatialChannelMalformed,
            ruleName: ruleName,
            detail: $"'{channel}' names 'placement:{placementId}', which is not a declared placement"
        );
    }
    // 'placement:$each' requires a rule declaring ForEach over a row whose EVERY declared key names an inhabited
    // placement — resolved once per rule compile (s_forEachPlacementOrdinals caches it across repeated occurrences
    // in the SAME rule) in the row's own cell order, the SAME order WorldServer.EachKeys walks at runtime.
    private static IReadOnlyList<int> ResolveForEachPlacementOrdinals(string channel, WorldDefinition definition, string ruleName) {
        if (s_forEachPlacementOrdinals is { } cached) {
            return cached;
        }

        if (s_forEachRow is not { } forEachRow) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.SpatialChannelMalformed,
                ruleName: ruleName,
                detail: $"'{channel}' names 'placement:$each', which requires the enclosing rule to declare 'forEach'"
            );
        }

        var row = (WorldDefinitionRows.FindStateRow(rows: definition.State, name: forEachRow)
            ?? throw new WorldRuleException(
            refusal: WorldRuleRefusal.SpatialChannelMalformed,
            ruleName: ruleName,
            detail: $"'{channel}' names 'placement:$each', but forEach row '{forEachRow}' names no declared state row"
        ));
        var cells = (row.Cells ?? []);
        var ordinals = new int[cells.Count];

        for (var index = 0; (index < cells.Count); index++) {
            ordinals[index] = ResolvePlacementOrdinal(
                channel: channel,
                definition: definition,
                placementId: cells[index].Key.Value,
                ruleName: ruleName
            );
        }

        s_forEachPlacementOrdinals = ordinals;

        return ordinals;
    }
}
