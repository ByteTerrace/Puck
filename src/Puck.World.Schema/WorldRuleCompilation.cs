namespace Puck.World;

/// <summary>The rules, interactions, and pinned tables compiled for one exact definition. Pass a validation result
/// directly into installation while the definition and its collection contents remain unchanged; this is a receipt
/// for that operation, not a cache to transfer across document edits.</summary>
public sealed class WorldRuleCompilation {
    internal WorldRuleCompilation(WorldDefinition definition, CompiledWorldRule[] rules, CompiledWorldRule[] interactions, CompiledTable[] tables) {
        Definition = definition;
        Rules = rules;
        Interactions = interactions;
        Tables = tables;
    }
    /// <summary>Gets the exact definition compiled.</summary>
    public WorldDefinition Definition { get; }
    /// <summary>Gets the compiled rules in document order.</summary>
    public CompiledWorldRule[] Rules { get; }
    /// <summary>Gets the compiled interactions in document order.</summary>
    public CompiledWorldRule[] Interactions { get; }
    /// <summary>Gets the pinned tables in document order.</summary>
    public CompiledTable[] Tables { get; }

    /// <summary>Compiles a fresh bundle with one context shared across rules, interactions, and tables.
    /// This compiles programs; document admission remains the validator's responsibility.</summary>
    /// <param name="definition">The definition to compile.</param>
    public static WorldRuleCompilation Compile(WorldDefinition definition) {
        var context = WorldRuleCompiler.Context(definition);
        return new(definition, WorldRuleCompiler.CompileAll(definition, context),
            WorldRuleCompiler.CompileAllInteractions(definition, context), CompileTables(definition, context));
    }
    internal static CompiledTable[] CompileTables(WorldDefinition definition, WorldRuleCompileContext? context) {
        var rows = definition.Tables ?? [];
        var result = new CompiledTable[rows.Count];
        if (rows.Count == 0) { return result; }
        context ??= WorldRuleCompiler.Context(definition);
        for (var index = 0; index < rows.Count; index++) {
            if (!context.TryTable(rows[index].Name, out _, out var table, out var error)) {
                throw new InvalidOperationException($"tables[{rows[index].Name}]: {error}");
            }
            result[index] = table!;
        }
        return result;
    }
}
