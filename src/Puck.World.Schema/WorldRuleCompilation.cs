namespace Puck.World;

/// <summary>The rules, interactions, and pinned tables compiled for one exact definition. Pass a validation result
/// directly into installation. A new receipt can share the programs when the inputs that compiled them stand.</summary>
public sealed class WorldRuleCompilation {
    private readonly WorldDocumentInputs m_inputs;
    private readonly int m_codeRevision = Puck.Abstractions.CodeRevision.Current;
    private readonly (WorldRuleWorkBudget Budget, IReadOnlyList<Puck.State.Rules.RuleWorkContributor> Contributors) m_work;
    private readonly Lazy<WorldCostReport> m_costReport;
    private readonly Lazy<WorldRuleTimePricing> m_pricing;
    private readonly Lazy<IReadOnlyList<Puck.State.Rules.RuleHazard>> m_hazards;

    internal WorldRuleCompilation(WorldDefinition definition, Puck.State.Rules.CompiledRule[] rules, Puck.State.Rules.CompiledRuleGroup[] groups, Puck.State.Rules.CompiledRule[] interactions, CompiledTable[] tables,
        Puck.State.Rules.RuleEvaluationOrder order,
        (WorldRuleWorkBudget Budget, IReadOnlyList<Puck.State.Rules.RuleWorkContributor> Contributors) work,
        WorldDocumentInputs inputs) {
        WorldBootWork.Count(kind: WorldBootWork.RuleCompilations);
        Definition = definition;
        m_inputs = inputs;
        Rules = rules;
        Groups = groups;
        Order = order;
        Interactions = interactions;
        Tables = tables;
        m_work = work;
        m_pricing = new(valueFactory: () => WorldRuleTimePricing.Of(budget: m_work.Budget, definition: Definition, lines: m_work.Contributors));
        m_costReport = new(valueFactory: () => WorldCostReport.Generate(compilation: this));
        m_hazards = new(valueFactory: () => Puck.State.Rules.RuleHazards.Analyze(
            catalog: Definition.StateCatalog,
            rules: Rules
        ));
    }

    private WorldRuleCompilation(WorldDefinition definition, WorldRuleCompilation held) {
        Definition = definition;
        m_inputs = held.m_inputs;
        Rules = held.Rules;
        Groups = held.Groups;
        Order = held.Order;
        Interactions = held.Interactions;
        Tables = held.Tables;
        m_work = held.m_work;
        m_hazards = held.m_hazards;
        // The devices and the rule share are this definition's own, so the time pricing is read afresh.
        m_pricing = new(valueFactory: () => WorldRuleTimePricing.Of(budget: m_work.Budget, definition: Definition, lines: m_work.Contributors));
        m_costReport = new(valueFactory: () => WorldCostReport.Generate(compilation: this));
    }

    /// <summary>Returns a receipt for the candidate, sharing programs and work analysis when none of their inputs
    /// changed, or null when the candidate needs a fresh compilation.</summary>
    /// <param name="definition">The candidate definition.</param>
    /// <param name="installedBasis">The definition these programs already serve. The authority supplies it when
    /// binding live arena values changed the definition without changing the installed programs.</param>
    /// <returns>The receipt for the candidate, if its compilation stands.</returns>
    public WorldRuleCompilation? ReuseFor(WorldDefinition definition, WorldDefinition? installedBasis = null) {
        if ((m_codeRevision != Puck.Abstractions.CodeRevision.Current) ||
            WorldDocumentInputs.Changed(installedBasis ?? Definition, definition).Overlaps(m_inputs)) { return null; }
        WorldDocumentReads.Note(m_inputs);
        return ReferenceEquals(Definition, definition) ? this : new(definition, this);
    }

    /// <summary>Gets the exact definition compiled.</summary>
    public WorldDefinition Definition { get; }
    /// <summary>Gets the report shared by consumers of this exact compilation. Analysis is performed once, on
    /// demand, and does not compile the rule programs again.</summary>
    public WorldCostReport CostReport => m_costReport.Value;
    /// <summary>Gets the rule hazards for this exact compilation. Analysis is performed once on demand and
    /// shares the installed programs with cost and work-budget queries.</summary>
    public IReadOnlyList<Puck.State.Rules.RuleHazard> Hazards => m_hazards.Value;
    /// <summary>Gets the work sheet in units, computed once from the compiled programs.</summary>
    public WorldRuleWorkBudget WorkBudget => m_work.Budget;
    /// <summary>Gets the sheet in time on every device the definition authors, against the tick's rule budget: what
    /// admission holds the slowest device to.</summary>
    public WorldRuleTimePricing Pricing => m_pricing.Value;
    /// <summary>Returns a deterministic refusal of a proposed arena reservation's work: the tick's growth and a possible
    /// rollback in time, against what the rule budget leaves after the slowest device's worst tick
    /// (<see cref="WorldRuleTimePricing.RefuseGrowth"/>). The sheet prices every row at its declared room
    /// (<see cref="Puck.State.Rules.RuleCompileContext.RowCapacity(int)"/>), so a reservation never changes it and nothing
    /// is compiled again.</summary>
    /// <param name="growthWork">The growth units already spent this tick plus the proposed change and its rollback.</param>
    /// <returns>The refusal, or <see langword="null"/> when the growth fits.</returns>
    public string? RefuseReservation(long growthWork) => Pricing.RefuseGrowth(growthUnits: growthWork);
    /// <summary>Gets the same sheet's contributor lines in descending cost order.</summary>
    public IReadOnlyList<Puck.State.Rules.RuleWorkContributor> WorkContributors => m_work.Contributors;
    /// <summary>Gets the compiled rule groups in document order; each member is an index into
    /// <see cref="Rules"/>.</summary>
    public Puck.State.Rules.CompiledRuleGroup[] Groups { get; }
    /// <summary>Gets the compiled interactions in document order.</summary>
    public Puck.State.Rules.CompiledRule[] Interactions { get; }
    /// <summary>Gets the compiled rules in document order.</summary>
    public Puck.State.Rules.CompiledRule[] Rules { get; }
    /// <summary>Gets the order a host evaluates the rules in, which the work sheet places its lines by.</summary>
    public Puck.State.Rules.RuleEvaluationOrder Order { get; }
    /// <summary>Gets the rules of <see cref="Rules"/> that no group of <see cref="Groups"/> claims — the set a host
    /// evaluates directly, because a claimed rule runs only under its group.</summary>
    public Puck.State.Rules.CompiledRule[] Ungrouped => Order.Ungrouped;
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
        var programs = WorldDocumentReads.Record(build: () => {
            var context = WorldFactsCompiler.Context(definition: definition);
            var (rules, groups) = WorldFactsCompiler.CompileDocument(context: context, definition: definition);
            var interactions = WorldFactsCompiler.CompileAllInteractions(context: context, definition: definition);
            var tables = CompileTables(definition, context);
            var order = Puck.State.Rules.RuleEvaluationOrder.Of(groups: groups, rules: rules);
            var work = WorldRuleWorkBudget.Analyze(definition, order, interactions, context);
            return (rules, groups, interactions, tables, order, work);
        }, out var inputs);
        return new(definition, programs.rules, programs.groups, programs.interactions, programs.tables, programs.order, programs.work, inputs);
    }
}
