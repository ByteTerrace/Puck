using Puck.World.Protocol;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The contribution facet (see WorldPlacementContribution): the authored half is the host's offer, the stamped half
    // is the engine's. Both halves are checked here so a hand-authored file and a live UpsertPlacement candidate refuse
    // through the same code — the compose arm's own refusals are the earlier, verb-named half of the same rules.
    private static void ValidateContribution(WorldPlacementContribution contribution, WorldPlacement placement, WorldDefinition definition, HashSet<string> prototypeIds, string path, List<string> errors) {
        RequireDeclared(
            value: contribution.SlotCreationId,
            declaredSet: prototypeIds,
            path: path,
            field: "slotCreationId",
            rowNoun: "creation",
            errors: errors
        );

        RequireRange(
            value: contribution.GraceSeconds,
            min: 0f,
            max: WorldContributionCapacity.MaxGraceSeconds,
            name: $"{path}.graceSeconds",
            errors: errors
        );

        switch (contribution.Tenure) {
            case WorldContributionTenure.Presence:
                if (contribution.Link is not { } link) {
                    errors.Add(item: $"{path}.link is required for tenure 'Presence' — name the adjacencies row whose reachability keeps the piece.");
                } else if (WorldDefinitionRows.FindAdjacency(
                    adjacencies: definition.Adjacencies,
                    name: link.Value
                ) is null) {
                    errors.Add(item: $"{path}.link '{link.Value}' names no adjacencies row.");
                }

                break;
            case WorldContributionTenure.Endowed:
                if (contribution.Link is { } endowedLink) {
                    errors.Add(item: $"{path}.link '{endowedLink.Value}' is refused for tenure 'Endowed' — an endowed piece watches no link.");
                }

                if (contribution.GraceSeconds != 0f) {
                    errors.Add(item: $"{path}.graceSeconds {contribution.GraceSeconds} is refused for tenure 'Endowed' — an endowed piece runs no grace.");
                }

                if (contribution.RetractDeadlineTick is { } endowedDeadline) {
                    errors.Add(item: $"{path}.retractDeadlineTick {endowedDeadline} is refused for tenure 'Endowed' — only a presence sweep stamps a deadline.");
                }

                break;
            default:
                errors.Add(item: $"{path}.tenure '{contribution.Tenure}' is not a declared contribution tenure.");

                break;
        }

        if (contribution.Contributor is { } contributor) {
            if (contributor.Kind == PrincipalKind.World) {
                errors.Add(item: $"{path}.contributor {contributor.Describe()} is refused — the world's own program never fills a contribution slot.");
            }

            if (string.Equals(
                a: placement.PrototypeId,
                b: contribution.SlotCreationId,
                comparisonType: StringComparison.Ordinal
            )) {
                errors.Add(item: $"{path} is filled by {contributor.Describe()} but its prototypeId still reads slotCreationId '{contribution.SlotCreationId}' — a filled slot shows the contributed creation.");
            }
        } else {
            if (contribution.RetractDeadlineTick is { } orphanDeadline) {
                errors.Add(item: $"{path}.retractDeadlineTick {orphanDeadline} stands on an unfilled slot — a deadline only ever runs against a stamped contributor.");
            }

            if (!string.Equals(
                a: placement.PrototypeId,
                b: contribution.SlotCreationId,
                comparisonType: StringComparison.Ordinal
            )) {
                errors.Add(item: $"{path} carries no contributor but its prototypeId '{placement.PrototypeId}' is not its slotCreationId '{contribution.SlotCreationId}' — an unfilled slot shows its own slot creation.");
            }
        }
    }
}
