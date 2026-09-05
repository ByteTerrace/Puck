namespace Puck.World;

public static partial class WorldRuleCompiler {
    // $phase:<row> — the row's own generation, the same value a WorldPhaseGuard checks against it.
    private static ResolvedOperand ResolvePhaseOperand(string name, string? key, string ruleName, WorldDefinition definition) {
        var tokens = name.Split(':');
        if (key is not null || tokens.Length != 2 || WorldDefinitionRows.FindStateRow(definition.State, tokens[1])?.Phase is null) {
            throw new WorldRuleException(WorldRuleRefusal.StateCellUnaddressable, ruleName, "phase query requires $phase:<row>, without key");
        }
        return new(new CompiledWorldOperand(WorldRuleFactKind.Phase, tokens[1], null, ValueKind: CellKind.Int, StateHandle: ResolveWorldStateHandle(definition: definition, name: tokens[1])), CellKind.Int, name);
    }
}
