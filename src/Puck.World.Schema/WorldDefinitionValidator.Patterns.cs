namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Every pattern compiles here, so a $match: operand only ever names a machine that exists inside its budget.
    private static void ValidatePatterns(WorldDefinition definition, List<string> errors) {
        _ = CompiledWorldPatterns.TryCompileAll(definition: definition, patterns: out _, errors: errors);

        for (var index = 0; index < definition.Patterns.Count; index++) {
            var row = definition.Patterns[index];

            if (row?.Attribute is not { } attribute) {
                continue;
            }

            var attributeRow = WorldDefinitionRows.FindStateRow(rows: definition.State, name: attribute);

            if (attributeRow is null || !attributeRow.IsKeyed || attributeRow.Kind != row.Kind) {
                errors.Add(item: $"patterns[{index}] ('{row.Name}') attribute '{attribute}' must name a keyed state row of kind {row.Kind}.");
            }
        }
    }
}
