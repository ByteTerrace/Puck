using Puck.World.Protocol;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateStateDisclosure(WorldDefinition definition, List<string> errors) {
        long storage = 0;
        foreach (var row in definition.State) {
            if (row is null) {
                continue;
            }
            if (row.PhaseOf is { } phaseOf && (WorldDefinitionRows.FindStateRow(definition.State, phaseOf)?.Phase is null || row.Phase is not null)) {
                errors.Add($"state row '{row.Name}': phaseOf must name a distinct phase protocol row.");
            }

            storage += row.Capacity ?? (row.Board is { } board ? WorldTopologyCompilation.Find(definition.StateRaw, board.Topology)?.CellCount ?? row.CellCeiling : row.CellCeiling);
            ValidateVisibility(row.Visibility, row.Name.Value, errors);
            if (row.Draw is { Secret: { } secret } draw && (secret.IsEmpty || row.Kind != CellKind.Int || !WorldGeneratorEngine.TryResolveSource(definition.Generators, draw, out var generator, out _) || generator.Source != WorldGeneratorSource.StreamDraw || generator.Mode != WorldGeneratorMode.WithReplacement)) {
                errors.Add($"state row {row.Name}: secret requires a nonzero key and an integer streamDraw source with replacement.");
            }
            foreach (var cell in row.Cells ?? []) {
                if (cell is null) {
                    continue;
                }

                ValidateVisibility(cell.Visibility, row.Name.Value, errors);
                if (row.IsSlot && cell.Visibility is not null) {
                    errors.Add($"state row '{row.Name}': slot visibility belongs on the row.");
                }

                if (cell.Observation is { } observation && (row.Knowledge is null || observation.Tick < 0 || observation.Tick > WorldStateCapacity.MaxIntCellValue)) {
                    errors.Add($"state row '{row.Name}': observation stamps require a knowledge board and a bounded nonnegative tick.");
                }

                if (row.Knowledge is not null && cell.Observation is null) {
                    errors.Add($"state row '{row.Name}': every remembered cell requires an observation stamp.");
                }
            }
            if (row.Knowledge is not { } knowledge) {
                continue;
            }

            var source = WorldDefinitionRows.FindStateRow(definition.State, knowledge.Source);
            var mask = WorldDefinitionRows.FindStateRow(definition.State, knowledge.Mask);
            if (row.Board is null || row.Visibility is null || source?.Board is null || mask?.Board is null || source.Knowledge is not null || mask.Knowledge is not null ||
                source.Board.Topology != row.Board.Topology || mask.Board.Topology != row.Board.Topology || source.Kind != row.Kind || mask.Kind != CellKind.Bool ||
                row.Name.Value == knowledge.Source || row.Name.Value == knowledge.Mask || row.Min != source.Min || row.Max != source.Max || row.NonNegative != source.NonNegative) {
                errors.Add($"state row '{row.Name}': knowledge requires an explicit audience and distinct compatible source/mask boards with the same value envelope.");
            }
        }
        if (storage > 262_144) {
            errors.Add("state declared cell storage exceeds the 262144-cell world budget.");
        }
    }

    private static void ValidateVisibility(WorldStateVisibility? visibility, string name, List<string> errors) {
        if (visibility?.Readers is not { } readers) {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (readers.Count > 32) {
            errors.Add($"state row '{name}': visibility admits at most 32 readers.");
        }

        foreach (var reader in readers) {
            if (!WorldPrincipal.TryParse(reader, out var actor) || actor.Kind is PrincipalKind.World or PrincipalKind.Group || actor.Describe() != reader || !seen.Add(reader)) {
                errors.Add($"state row '{name}': visibility readers must be distinct canonical authenticated principals.");
            }
        }
    }
}
