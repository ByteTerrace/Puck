namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Runs only over a candidate whose rules compiled: every plan derives from the compiled rules and the work sheet.
    private static void ValidateSearch(WorldDefinition definition, List<string> errors) {
        var rows = definition.Search.Rows;

        if (rows.Count == 0) {
            return;
        }
        if (rows.Count > WorldSearchCapacity.MaxJobs) {
            errors.Add(item: $"search declares {rows.Count} jobs; the maximum is {WorldSearchCapacity.MaxJobs}.");

            return;
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];

            if (row is null) {
                errors.Add(item: $"search[{index}] is null.");

                return;
            }
            if (!SafeName.TryParse(candidate: row.Name, name: out _, reason: out var nameReason)) {
                errors.Add(item: $"search[{index}] name: {nameReason}");

                return;
            }
            if (!names.Add(item: row.Name)) {
                errors.Add(item: $"search declares '{row.Name}' more than once.");

                return;
            }

            foreach (var (output, label) in new[] { (row.Legal, "legal"), (row.Count, "count"), (row.Best, "best"), (row.Reach, "reach"), (row.Counts, "counts") }) {
                if ((output is not null) && (string.Equals(a: output, b: row.Tokens, comparisonType: StringComparison.Ordinal) || string.Equals(a: output, b: row.Board, comparisonType: StringComparison.Ordinal))) {
                    errors.Add(item: $"search '{row.Name}' {label} '{output}' is one of the rows the job searches over; an output row is never an input.");

                    return;
                }
            }
        }

        if (!WorldSearchCompilation.TryPlanAll(definition: definition, rules: WorldRuleCompiler.CompileAll(definition: definition), plans: out _, judge: out _, reason: out var reason)) {
            errors.Add(item: $"{reason}.");
        }
    }
}
