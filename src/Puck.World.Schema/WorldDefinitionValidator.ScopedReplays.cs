namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateScopedReplays(WorldDefinition definition, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var ownedRows = new HashSet<string>(comparer: StringComparer.Ordinal);
        var ownedBodies = new HashSet<int>();

        foreach (var scope in (definition.ReplaysRaw ?? [])) {
            var prefix = $"replays '{scope.Name}': ";

            if (string.IsNullOrWhiteSpace(value: scope.Name) || !names.Add(item: scope.Name)) { errors.Add(item: (prefix + "name must be unique and nonempty")); }
            if ((scope.DurationTicks <= 0) || (scope.Rows.Count == 0) || (scope.Seats.Count == 0)) { errors.Add(item: (prefix + "a positive duration, rows and input principals are required")); }
            foreach (var name in scope.Rows) {
                if (WorldDefinitionRows.FindStateRow(definition.State, name) is not { } row) { errors.Add(item: (prefix + $"no document row '{name}'")); continue; }
                if (!ownedRows.Add(item: name)) { errors.Add(item: (prefix + $"row '{name}' belongs to more than one scope")); }
                if ((row.Field is not null) || (row.Kind == CellKind.Vector) || (row.Advance is not null) || (row.Cycle is not null) || (row.Dynamics is not null) ||
                    (row.Cells ?? []).Any(predicate: cell => ((cell.Clock is not null) || (cell.Advance is not null) || (cell.Cycle is not null) || (cell.Dynamics is not null)))) {
                    errors.Add(item: (prefix + $"row '{name}' needs a wider continuation boundary (field, vector or timed state)"));
                }
                if ((row.Inverse is { } inverse) && (!scope.Rows.Contains(value: inverse.Tokens) || !scope.Rows.Contains(value: inverse.Codes))) {
                    errors.Add(item: (prefix + $"derived board '{name}' requires both inverse input rows"));
                }
            }
            foreach (var body in scope.Bodies) {
                if ((body < 0) || (body >= definition.Population.Capacity) || !ownedBodies.Add(item: body)) { errors.Add(item: (prefix + $"invalid or multiply owned body:{body}")); }
            }
            if (scope.Rows.Contains(value: scope.Control) || (WorldDefinitionRows.FindStateRow(definition.State, scope.Control) is not { Kind: CellKind.Int } control) ||
                new[] { "request", "status", "cursor", "completed" }.Any(predicate: key => !(control.Cells ?? []).Any(predicate: cell => (cell.Key.Value == key)))) {
                errors.Add(item: (prefix + "control must be a separate Int row declaring request, status, cursor and completed"));
            }
            var previous = -1;

            foreach (var input in scope.Inputs) {
                if ((input.Tick < previous) || (input.Tick < 0) || (input.Tick >= scope.DurationTicks) || !scope.Seats.Contains(value: input.Principal)) {
                    errors.Add(item: (prefix + "inputs must be ordered, within the duration, and addressed by a scope principal"));
                }
                previous = input.Tick;
                foreach (var cell in input.Cells) {
                    if (!scope.Rows.Contains(value: cell.Row) || !CellName.TryParse(candidate: cell.Key, name: out _, reason: out _)) { errors.Add(item: (prefix + "input cell must address a named scope row and valid key")); }
                }
            }
        }
    }
}
