using Puck.Abstractions.Machines;

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
                reason = $"{StateRowPath(index: index)} is required.";
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
                    ValidateStateRow(row, definition.Generators, dynamics, StateRowPath(index: index), errors, spaces, enums);
                }
            }
            ValidatePopulationCapacityRow(definition.Population, rows, errors);
            ValidateHostBackendRow(definition.Host, definition.Generators, rows, errors);
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
    private static void ValidateHostBackendRow(WorldHostDefaults host, IReadOnlyList<GeneratorRow>? generators, IReadOnlyList<WorldStateRow> rows, List<string> errors) {
        if (host.BackendRow is not { } backendRow) { return; }
        if (host.Backend is not null) {
            errors.Add(item: "host declares both 'backend' and 'backendRow' — the backend is an authored literal or a row read, never both.");
        }
        if (WorldDefinitionRows.FindStateRow(name: backendRow, rows: rows) is not { } tokenRow) {
            errors.Add(item: $"host.backendRow names state row '{backendRow}', which the document does not declare.");
        } else if ((tokenRow.Kind != CellKind.Text) || tokenRow.IsKeyed || (tokenRow.Field is not null)) {
            errors.Add(item: $"host.backendRow names state row '{backendRow}', which must be a scalar kind=Text row.");
        } else if (tokenRow.Draw is { } draw) {
            ValidateBackendTokens(
                draw: draw,
                errors: errors,
                generators: generators,
                rowName: backendRow
            );
        }
    }
    /// <summary>Proves a drawn census over its whole outcome set: every value the row's source can produce is settled
    /// into a candidate and put through this same validator, so a document's admission cannot depend on what the boot
    /// rolled.</summary>
    /// <remarks>The predicate is the validator itself rather than a hand-listed band, so every capacity-bounded
    /// refusal the document already carries — the seat floor, the network-player share, each authored body index —
    /// is proved for each outcome without a second walk that could drift from the checks it mirrors. A source whose
    /// outcome set is unbounded, or wider than <see cref="WorldBodiesLimits.MaxDrawnCensusOutcomes"/>, refuses by
    /// name: a census nothing can prove is not a census the world may boot on.</remarks>
    private static void ValidateCensusOutcomes(WorldDefinition definition, IMachineValidationCatalog? machines, List<string> errors) {
        if (
            (definition.Population.CapacityRow is not { } capacityRow) ||
            (WorldDefinitionRows.FindStateRow(
            name: capacityRow,
            rows: definition.State
        ) is not { Draw: { } draw } censusRow) ||
            !GeneratorEngine.TryResolveSource(
            draw: draw,
            generator: out var generator,
            generators: definition.Generators,
            reason: out _
        )
        ) {
            return;
        }

        if (!GeneratorEngine.TryEnumerateOutcomes(
            ceiling: WorldBodiesLimits.MaxDrawnCensusOutcomes,
            generator: generator,
            outcomes: out var outcomes,
            reason: out var reason,
            targetKind: censusRow.Kind
        )) {
            errors.Add(item: $"bodies.capacityRow names state row '{capacityRow}', whose source cannot be proven against the census: {reason}.");

            return;
        }

        foreach (var outcome in outcomes) {
            // This very run already proved the settled census; every other outcome earns its own candidate.
            if (outcome == definition.Population.Capacity) { continue; }

            var refusals = new List<string>();

            if (
                (outcome < 0L) ||
                (outcome > WorldBodiesLimits.CapacityCeiling)
            ) {
                refusals.Add(item: $"bodies.capacity {outcome} is outside 0..{WorldBodiesLimits.CapacityCeiling}.");
            } else {
                _ = ValidateCore(
                    definition: (definition with { PopulationRaw = (definition.Population with { CapacityRaw = ((int)outcome) }) }),
                    neighbours: null,
                    validateAdjacencyClaims: false,
                    retainCompilation: false,
                    throwOnErrors: false,
                    errorSink: refusals,
                    deferredSink: null,
                    machines: machines,
                    proveCensusOutcomes: false
                );
            }

            if (refusals is [{ } refusal, ..]) {
                errors.Add(item: $"bodies.capacityRow names state row '{capacityRow}', whose source can draw census {outcome}, which this document does not admit: {refusal}");
            }
        }
    }
}
