namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // A verdict row is an ordinary keyed Int row a rule writes; the trait only has to be answerable by a reader of
    // the export, so the checks are exactly the ones that keep it answerable: a status cell that exists, an
    // envelope that admits the three status codes, a gate a failing verdict can name, no trait that moves a value
    // without a rule, and room for the engine's firing stamp.
    private static void ValidateVerdicts(WorldDefinition definition, List<string> errors) {
        var rows = definition.State;
        var verdicts = 0;

        for (var index = 0; (index < rows.Count); index++) {
            if (rows[index] is not { } row) {
                continue;
            }

            ValidateWitnessRow(
                definition: definition,
                errors: errors,
                row: row
            );

            if (row.Verdict is null) {
                continue;
            }

            verdicts++;

            ValidateVerdictRow(
                errors: errors,
                row: row
            );
        }

        if (verdicts > WorldVerdict.MaxRows) {
            errors.Add(item: $"state.world declares {verdicts} verdict rows, which exceeds {WorldVerdict.MaxRows}.");
        }
    }
    /// <summary>Validates one verdict row on its own terms — the checks both the whole-document walk and a state
    /// mutation's touched-row walk run, so the boot loader and the live pipeline agree about a verdict row.</summary>
    /// <param name="row">The row; returns at once when it carries no verdict trait.</param>
    /// <param name="errors">The refusals collected so far.</param>
    private static void ValidateVerdictRow(WorldStateRow row, List<string> errors) {
        if (row.Verdict is not { } verdict) {
            return;
        }

        var path = $"state.world '{row.Name}'.verdict";

        if (string.IsNullOrWhiteSpace(value: verdict.Gate)) {
            errors.Add(item: $"{path}.gate is required — a failing verdict names its gate.");
        } else if (verdict.Gate.Length > WorldVerdict.MaxGateLength) {
            errors.Add(item: $"{path}.gate length {verdict.Gate.Length} exceeds {WorldVerdict.MaxGateLength}.");
        }

        if (row.Kind != CellKind.Int) {
            errors.Add(item: $"{path} is declared on a {row.Kind} row — a verdict's status is an int status code, so the row is kind Int.");
        }

        if (row.IsSlot) {
            errors.Add(item: $"{path} is declared on a slot row — a verdict's status cell and the values its gate saw are keyed cells of the same row.");
        }

        if (row.Field is not null) {
            errors.Add(item: $"{path} is declared beside 'field' — a physical-field row's cells are the field's, not a rule's to write.");
        }

        if (row.HostOwned) {
            errors.Add(item: $"{path} is declared on a host-owned row, which has no storage a rule effect can write.");
        }

        if (
            (row.Min is { } min) &&
            (min > WorldVerdict.NotEvaluated)
        ) {
            errors.Add(item: $"{path} sits on a row whose min {min} refuses the unevaluated status code {WorldVerdict.NotEvaluated}.");
        }

        // The stamp is a tick, and the row that holds it holds it in the same envelope as its status codes, so an
        // authored ceiling would silently refuse the stamp write and leave the verdict reading never-evaluated.
        if (row.Max is { } max) {
            errors.Add(item: $"{path} sits on a row declaring max {max} — a verdict row holds the engine's firing tick in '{WorldVerdict.FiredTickKey}' beside its status, and no authored ceiling bounds a tick.");
        }

        ValidateVerdictTraits(
            errors: errors,
            path: path,
            row: row
        );
        ValidateVerdictCells(
            errors: errors,
            path: path,
            row: row,
            status: verdict.Status
        );
    }
    /// <summary>Validates one witness row: the verdict it names exists, and nothing but a rule's firing moves its
    /// cells. Both the whole-document walk and a state mutation's touched-row walk run it.</summary>
    /// <param name="definition">The document the row belongs to.</param>
    /// <param name="row">The row; returns at once when it is no witness.</param>
    /// <param name="errors">The refusals collected so far.</param>
    private static void ValidateWitnessRow(WorldDefinition definition, WorldStateRow row, List<string> errors) {
        if (row.Witness is not { } witnessed) {
            return;
        }

        var path = $"state.world '{row.Name}'.witness";

        if (row.Verdict is not null) {
            errors.Add(item: $"{path} is declared beside 'verdict' — a row is a verdict or a witness of one, never both.");
        }

        if (WorldDefinitionRows.FindStateRow(
            name: witnessed.Value,
            rows: definition.State
        )?.Verdict is null) {
            errors.Add(item: $"{path} '{witnessed}' names no verdict row.");
        }

        if (row.Kind is not (CellKind.Bool or CellKind.Fixed)) {
            errors.Add(item: $"{path} is declared on a {row.Kind} row — a gate reads numbers, an Int one is a cell of the verdict row itself, so a witness is kind Fixed or Bool.");
        }

        if (row.IsSlot) {
            errors.Add(item: $"{path} is declared on a slot row — the values a gate saw are keyed cells, one per cell it read.");
        }

        if (row.Field is not null) {
            errors.Add(item: $"{path} is declared beside 'field' — a physical-field row's cells are the field's, not a rule's to write.");
        }

        if (row.HostOwned) {
            errors.Add(item: $"{path} is declared on a host-owned row, which has no storage a rule effect can write.");
        }

        ValidateVerdictTraits(
            errors: errors,
            path: path,
            row: row
        );
    }
    // Every trait that moves a cell with no rule behind it. A verdict reached by one of them says nothing about the
    // expectation it names: the status accrues, eases, or rotates into a passing code on its own.
    private static void ValidateVerdictTraits(WorldStateRow row, string path, List<string> errors) {
        var traits = new List<string>();

        if (row.Advance is not null) {
            traits.Add(item: "advance");
        }

        if (row.Dynamics is not null) {
            traits.Add(item: "dynamics");
        }

        if (row.Cycle is not null) {
            traits.Add(item: "cycle");
        }

        if (row.Draw is not null) {
            traits.Add(item: "draw");
        }

        if (row.ValuesFrom is not null) {
            traits.Add(item: "valuesFrom");
        }

        foreach (var cell in (row.Cells ?? [])) {
            if (cell is null) {
                continue;
            }

            if (cell.Advance is not null) {
                traits.Add(item: $"cells['{cell.Key}'].advance");
            }

            if (cell.Dynamics is not null) {
                traits.Add(item: $"cells['{cell.Key}'].dynamics");
            }

            if (cell.Cycle is not null) {
                traits.Add(item: $"cells['{cell.Key}'].cycle");
            }

            if (cell.Clock is not null) {
                traits.Add(item: $"cells['{cell.Key}'].clock");
            }
        }

        if (traits.Count > 0) {
            errors.Add(item: $"{path} is declared beside {string.Join(
                separator: ", ",
                values: traits
            )} — a value-over-time trait moves a verdict's cells with no rule behind it, so the status could reach a passing code nothing evaluated.");
        }
    }
    // The status cell, the firing stamp, and the room the stamp needs. A status other than "not evaluated" is
    // admitted only beside a firing stamp, which is what makes the live document a rule wrote round-trip through the
    // boot loader while an authored pass still refuses.
    private static void ValidateVerdictCells(WorldStateRow row, CellName status, string path, List<string> errors) {
        var cells = (row.Cells ?? []);
        var stamp = StateRows.FindCell(
            cells: cells,
            key: WorldVerdict.FiredTickKey
        );
        var statusCell = StateRows.FindCell(
            cells: cells,
            key: status
        );

        if (statusCell is null) {
            errors.Add(item: $"{path}.status '{status}' names no cell of row '{row.Name}'.");
        } else if (
            // A row of another kind is refused above by name; its cells carry no number to compare.
            (statusCell.Value.Kind == CellKind.Int) &&
            (statusCell.Value.Raw != WorldVerdict.NotEvaluated) &&
            ((stamp is null) || (stamp.Value.Kind != CellKind.Int) || (stamp.Value.Raw == 0L))
        ) {
            errors.Add(item: $"{path}.status cell '{status}' carries {statusCell.Value.Raw} with no '{WorldVerdict.FiredTickKey}' stamp — a status only a rule's firing may write is authored at {WorldVerdict.NotEvaluated}, so a verdict no rule wrote reads as never evaluated.");
        }

        // A keyed row's ceiling counts the stamp: a row whose declared capacity is exactly full at boot refuses the
        // stamp write, and the verdict then reads never-evaluated whatever its rule did.
        if (
            (stamp is null) &&
            (row.CellCeiling <= cells.Count)
        ) {
            errors.Add(item: $"{path} sits on a row whose cell ceiling {row.CellCeiling} leaves no room beside its {cells.Count} declared cell(s) for the engine's '{WorldVerdict.FiredTickKey}' stamp.");
        }
    }
}
