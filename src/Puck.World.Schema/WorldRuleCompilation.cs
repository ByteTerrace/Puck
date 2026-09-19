namespace Puck.World;

/// <summary>The rules, interactions, and pinned tables compiled for one exact definition. Pass a validation result
/// directly into installation while the definition and its collection contents remain unchanged; this is a receipt
/// for that operation, not a cache to transfer across document edits.</summary>
public sealed class WorldRuleCompilation {
    internal WorldRuleCompilation(WorldDefinition definition, Puck.State.Rules.CompiledRule[] rules, Puck.State.Rules.CompiledRuleGroup[] groups, Puck.State.Rules.CompiledRule[] interactions, CompiledTable[] tables) {
        Definition = definition;
        Rules = rules;
        Groups = groups;
        Ungrouped = Puck.State.Rules.RuleCompiler.Ungrouped(
            groups: groups,
            rules: rules
        );
        Interactions = interactions;
        Tables = tables;
    }

    /// <summary>Gets the exact definition compiled.</summary>
    public WorldDefinition Definition { get; }
    /// <summary>Gets the compiled rule groups in document order; each member is an index into
    /// <see cref="Rules"/>.</summary>
    public Puck.State.Rules.CompiledRuleGroup[] Groups { get; }
    /// <summary>Gets the compiled interactions in document order.</summary>
    public Puck.State.Rules.CompiledRule[] Interactions { get; }
    /// <summary>Gets the compiled rules in document order.</summary>
    public Puck.State.Rules.CompiledRule[] Rules { get; }
    /// <summary>Gets the rules of <see cref="Rules"/> that no group of <see cref="Groups"/> claims — the set a host
    /// evaluates directly, because a claimed rule runs only under its group.</summary>
    public Puck.State.Rules.CompiledRule[] Ungrouped { get; }
    /// <summary>Gets the pinned tables in document order.</summary>
    public CompiledTable[] Tables { get; }

    internal static CompiledTable[] CompileTables(WorldDefinition definition, WorldFactsCompileContext? context) {
        var rows = (definition.Tables ?? []);
        var result = new CompiledTable[rows.Count];

        if (rows.Count == 0) { return result; }
        context ??= WorldFactsCompiler.Context(definition: definition);
        for (var index = 0; (index < rows.Count); index++) {
            if (!context.TryTable(
                rows[index].Name,
                out _,
                out var table,
                out var error
            )) {
                throw new InvalidOperationException(message: $"tables[{rows[index].Name}]: {error}");
            }
            result[index] = table!;
        }
        return result;
    }

    /// <summary>Compiles a fresh bundle with one context shared across rules, interactions, and tables.
    /// This compiles programs; document admission remains the validator's responsibility.</summary>
    /// <param name="definition">The definition to compile.</param>
    public static WorldRuleCompilation Compile(WorldDefinition definition) {
        var context = WorldFactsCompiler.Context(definition: definition);
        var (rules, groups) = WorldFactsCompiler.CompileDocument(
            context: context,
            definition: definition
        );

        return new(
            definition,
            rules,
            groups,
            WorldFactsCompiler.CompileAllInteractions(
                context: context,
                definition: definition
            ),
            CompileTables(
                context: context,
                definition: definition
            )
        );
    }
}
