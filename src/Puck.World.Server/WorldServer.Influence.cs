namespace Puck.World.Server;

public sealed partial class WorldServer {
    private WorldSpatialQueryIndex? m_influenceIndex;

    private readonly Dictionary<(string Template, string Key), string> m_influenceChildren = [];
    private readonly HashSet<string> m_incompleteInfluenceChannels = new(comparer: StringComparer.Ordinal);

    RuleFact IWorldRuleReader.Read(PlacementInfluenceOperand operand) {
        var index = m_definition.SpatialQueryIndex;

        if (!ReferenceEquals(
            objA: index,
            objB: m_influenceIndex
        )) {
            m_influenceChildren.Clear();
            m_incompleteInfluenceChannels.Clear();
            foreach (var placement in m_definition.Placements) {
                if (WorldPlacementDeal.TrySplitChildId(
                    placement.Id,
                    out var template,
                    out var key
                )) {
                    m_influenceChildren[(template, key)] = placement.Id;
                }
            }
            foreach (var unsupported in index.Unsupported) {
                var row = WorldDefinitionRows.FindPlacement(
                    id: unsupported.PlacementId,
                    placements: m_definition.Placements
                );

                foreach (var volume in (row?.Spatial ?? [])) {
                    if (
                        (volume.Role == WorldPlacementSpatialRole.Influence) &&
                        (volume.Channel is { } channel)
                    ) {
                        m_incompleteInfluenceChannels.Add(item: channel);
                    }
                }
            }
            m_influenceIndex = index;
        }

        var target = operand.PlacementId;

        if (
            (operand.Key is not null) ||
            (operand.KeyFrom is not null)
        ) {
            var key = RuleEvaluation.ResolveKey(
                this,
                operand.Key,
                operand.KeyFrom
            );

            if (
                (key is null) ||
                !m_influenceChildren.TryGetValue(
                key: (target, key),
                value: out target!
            )
            ) {
                return RuleFact.Absent(kind: CellKind.Int);
            }
        }
        // An unrepresented influence provider makes a zero count unknowable. Never report a falsely safe value.
        if (m_incompleteInfluenceChannels.Contains(item: operand.Channel)) {
            return RuleFact.Absent(kind: CellKind.Int);
        }
        return (index.HasOccupationTarget(placementId: target)
            ? RuleFact.Finite(
                index.CountInfluences(
                    channel: operand.Channel,
                    placementId: target
                ),
                CellKind.Int
            )
            : RuleFact.Absent(kind: CellKind.Int)
        );
    }
}
