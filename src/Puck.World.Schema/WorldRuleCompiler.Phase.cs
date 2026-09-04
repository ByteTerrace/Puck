namespace Puck.World;

public static partial class WorldRuleCompiler {
    private static ResolvedOperand ResolvePhaseOperand(string name, string? key, string ruleName, WorldDefinition definition) {
        var tokens = name.Split(':');
        if (key is not null || tokens.Length != 3 || WorldDefinitionRows.FindStateRow(definition.State, tokens[1])?.Phase is null ||
            tokens[2] is not ("current" or "active" or "ready" or "sequence" or "round" or "deadline" or "direction" or "skipped")) {
            throw new WorldRuleException(WorldRuleRefusal.StateCellUnaddressable, ruleName,
                "phase query requires $phase:<row>:<current|active|ready|sequence|round|deadline|direction|skipped>, without key");
        }
        return new(new CompiledWorldOperand(WorldRuleFactKind.Phase, tokens[1], tokens[2], ValueKind: CellKind.Int), CellKind.Int, name);
    }
}
