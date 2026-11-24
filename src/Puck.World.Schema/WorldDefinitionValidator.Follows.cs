namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // A followed row names one stable authority to follow (a global, persisted destination, as an adjacency does) and
    // holds one value of a kind both worlds read exactly. Whether the leader's row exists, discloses itself to this
    // world and has the same kind is the leader's document, which only its authority's delivery answers.
    private static void ValidateStateFollows(WorldDefinition definition, List<string> errors) {
        foreach (var row in definition.State) {
            if (row?.Follows is not { } follows) { continue; }

            var path = $"state[{row.Name}].follows";

            if (WorldDefinitionRows.FindDestination(destinations: definition.Destinations, name: follows.Destination) is not { } destination) {
                errors.Add(item: $"{path}.destination '{follows.Destination}' names no destinations row.");
            } else if ((destination.Scope != WorldDestinationScope.Global) || (destination.Durability != WorldDestinationDurability.Persisted)) {
                errors.Add(item: $"{path}.destination '{destination.Name}' must be global and persisted — a followed row names one stable authority that owns its value.");
            }
            if (follows.Row is { } leader && !CellName.TryParse(candidate: leader, name: out _, reason: out var reason)) {
                errors.Add(item: $"{path}.row '{leader}' {reason}");
            }
            if (row.Kind is not (CellKind.Fixed or CellKind.Int or CellKind.Bool)) {
                errors.Add(item: $"{path} is on a {StateSpelling.Kind(kind: row.Kind)} row — a followed row holds one fixed, int or bool value.");
            }
            if (!row.IsSlot) {
                errors.Add(item: $"{path} is on a keyed row — a followed row is a slot that holds the leader's one value.");
            }
            if ((row.Dynamics is not null) || (row.Cycle is not null) || (row.Draw is not null) || row.IsRuleWritten) {
                errors.Add(item: $"{path} is on a row that moves by a follower, a cycle, a draw or a verdict — only the leader moves a followed row, beside its own advance.");
            }
        }
    }
}
