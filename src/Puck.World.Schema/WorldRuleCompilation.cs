namespace Puck.World;

/// <summary>The rules, interactions, and pinned tables compiled for one exact definition. Pass a validation result
/// directly into installation while the definition and its collection contents remain unchanged; this is a receipt
/// for that operation, not a cache to transfer across document edits.</summary>
public sealed class WorldRuleCompilation {
    private readonly Lazy<(WorldRuleWorkBudget Budget, IReadOnlyList<Puck.State.Rules.RuleWorkContributor> Contributors)> m_work;
    private readonly Lazy<WorldCostReport> m_costReport;
    private readonly Lazy<IReadOnlyList<Puck.State.Rules.RuleHazard>> m_hazards;

    internal WorldRuleCompilation(WorldDefinition definition, Puck.State.Rules.CompiledRule[] rules, Puck.State.Rules.CompiledRuleGroup[] groups, Puck.State.Rules.CompiledRule[] interactions, CompiledTable[] tables, WorldFactsCompileContext? context = null,
        (WorldRuleWorkBudget Budget, IReadOnlyList<Puck.State.Rules.RuleWorkContributor> Contributors)? work = null) {
        WorldBootWork.Count(kind: WorldBootWork.RuleCompilations);
        Definition = definition;
        Rules = rules;
        Groups = groups;
        Ungrouped = Puck.State.Rules.RuleCompiler.Ungrouped(
            groups: groups,
            rules: rules
        );
        Interactions = interactions;
        Tables = tables;
        m_work = new(valueFactory: () => (work ?? WorldRuleWorkBudget.Analyze(Definition, Rules, Interactions, context)));
        m_costReport = new(valueFactory: () => WorldCostReport.Generate(compilation: this));
        m_hazards = new(valueFactory: () => Puck.State.Rules.RuleHazards.Analyze(
            catalog: Definition.StateCatalog,
            rules: Rules
        ));
    }

    /// <summary>Gets the exact definition compiled.</summary>
    public WorldDefinition Definition { get; }
    /// <summary>Gets the report shared by consumers of this exact compilation. Analysis is performed once, on
    /// demand, and does not compile the rule programs again.</summary>
    public WorldCostReport CostReport => m_costReport.Value;
    /// <summary>Gets the rule hazards for this exact compilation. Analysis is performed once on demand and
    /// shares the installed programs with cost and work-budget queries.</summary>
    public IReadOnlyList<Puck.State.Rules.RuleHazard> Hazards => m_hazards.Value;
    /// <summary>Gets the heuristic admission sheet, computed once from the compiled programs.</summary>
    public WorldRuleWorkBudget WorkBudget => m_work.Value.Budget;
    /// <summary>Gets the same sheet's contributor lines in descending cost order.</summary>
    public IReadOnlyList<Puck.State.Rules.RuleWorkContributor> WorkContributors => m_work.Value.Contributors;
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
        ArgumentNullException.ThrowIfNull(definition);
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
            ),
            context
        );
    }
}
