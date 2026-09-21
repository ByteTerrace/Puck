namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Boot preparation can replace draw cells before whole-document admission. Validate the original cells,
    // source shapes and entire source domains here, so a lucky sample cannot hide an invalid declaration.
    // This is not an admission receipt and does not compile rules or prove any other document section.
    internal static bool TryValidateBootInputs(WorldDefinition definition, out string reason) {
        var rows = definition.AuthoredState;
        var hasDraw = false;
        for (var index = 0; (index < rows.Count); index++) {
            if (rows[index] is not { } row) {
                reason = $"state[{index}] is required.";
                return false;
            }
            hasDraw |= WorldDrawBootResolver.NeedsFirstFill(row: row);
        }
        if (!hasDraw && (definition.Population.CapacityRow is null) && (definition.Host.BackendRow is null)) {
            reason = string.Empty;
            return true;
        }
        var errors = new List<string>();
        try {
            if (hasDraw) {
                ValidateGenerators(definition.Generators, errors);
                // Drawn-mask validation relies on well-formed source entries. Refuse before reading those entries.
                RefuseCollected(errors: errors);
                var dynamics = ValidateDynamics(dynamics: definition.Dynamics, errors: errors);
                var spaces = ValidateSpaces(definition.Spaces, errors);
                var enums = ValidateEnums(enums: definition.Enums, errors: errors);
                RefuseCollected(errors: errors);
                for (var index = 0; (index < rows.Count); index++) {
                    var row = rows[index];
                    if (!WorldDrawBootResolver.NeedsFirstFill(row: row)) { continue; }
                    ValidateStateRow(row, definition.Generators, dynamics, $"state[{index}]", errors, spaces, enums);
                }
            }
            ValidatePopulationCapacityRow(definition.Population, rows, errors);
            ValidateHostBackendRow(definition.Host, rows, errors);
            RefuseCollected(errors: errors);
            reason = string.Empty;
            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }
    }

    private static void ValidatePopulationCapacityRow(WorldBodiesDefaults population, IReadOnlyList<WorldStateRow> rows, List<string> errors) {
        if (population.CapacityRow is not { } capacityRow) { return; }
        // CapacityRaw may hold the last settled value; the named row supplies the census on a fresh load.
        if (WorldDefinitionRows.FindStateRow(name: capacityRow, rows: rows) is not { } censusRow) {
            errors.Add(item: $"bodies.capacityRow names state row '{capacityRow}', which the document does not declare.");
        } else if ((censusRow.Kind != CellKind.Int) || censusRow.IsKeyed || (censusRow.Field is not null)) {
            errors.Add(item: $"bodies.capacityRow names state row '{capacityRow}', which must be a scalar kind=Int row.");
        }
    }

    private static void ValidateHostBackendRow(WorldHostDefaults host, IReadOnlyList<WorldStateRow> rows, List<string> errors) {
        if (host.BackendRow is not { } backendRow) { return; }
        if (host.Backend is not null) {
            errors.Add(item: "host declares both 'backend' and 'backendRow' — the backend is an authored literal or a row read, never both.");
        }
        if (WorldDefinitionRows.FindStateRow(name: backendRow, rows: rows) is not { } tokenRow) {
            errors.Add(item: $"host.backendRow names state row '{backendRow}', which the document does not declare.");
        } else if ((tokenRow.Kind != CellKind.Text) || tokenRow.IsKeyed || (tokenRow.Field is not null)) {
            errors.Add(item: $"host.backendRow names state row '{backendRow}', which must be a scalar kind=Text row.");
        }
    }
}
