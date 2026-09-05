namespace Puck.World;

public static partial class WorldRuleCompiler {
    // $clock:<music>:phaseError — <music> must name the document's declared music row, the only clock a world may
    // author (WorldServer reads only music[0]); phaseError is the only facet today, named so an authored range on
    // it reads as what it is rather than a bare comparison.
    private static ResolvedOperand ResolveClockOperand(string name, string? key, string ruleName, WorldDefinition definition) {
        WorldRuleException Invalid(string detail) => new(WorldRuleRefusal.StateCellUnaddressable, ruleName, detail);
        var tokens = name.Split(':');
        if (tokens.Length != 3 || tokens[2] != "phaseError") {
            throw Invalid("clock read requires $clock:<music>:phaseError");
        }
        if (key is not null) {
            throw Invalid("phaseError does not accept key");
        }
        if (definition.Music is not { Count: > 0 } music || music[0]?.Name != tokens[1]) {
            throw Invalid($"'{tokens[1]}' does not name the document's declared music row");
        }
        return new(new CompiledWorldOperand(WorldRuleFactKind.Clock, Row: null, Key: null, ValueKind: CellKind.Int), CellKind.Int, name);
    }
}
