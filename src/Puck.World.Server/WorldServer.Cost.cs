namespace Puck.World.Server;

public sealed partial class WorldServer {
    private sealed record PricedCostReport(WorldRuleCompilation Compilation, WorldPipelineSources Sources, WorldCostReport Report);

    private WorldRuleCompilation? m_ruleCompilation;
    private PricedCostReport? m_pricedCostReport;

    /// <summary>Gets the shared cost analysis for the installed rule compilation. Reading it does not compile
    /// rules again. Installing a new definition replaces the receipt and its lazily computed report together. When
    /// <see cref="PipelineSources"/> is attached and the document declares <c>views.graphs</c> rows, the presentation
    /// dimension carries each graph's planned passes, planned once per installed compilation.</summary>
    public WorldCostReport CostReport {
        get {
            var compilation = (m_ruleCompilation
                ?? throw new InvalidOperationException(message: "The server has no installed rule compilation."));

            if (
                (PipelineSources is not { } sources) ||
                (compilation.Definition.Views.Graphs is not { Count: > 0 })
            ) {
                return compilation.CostReport;
            }
            if (
                (m_pricedCostReport is { } priced) &&
                ReferenceEquals(
                    objA: priced.Compilation,
                    objB: compilation
                ) &&
                ReferenceEquals(
                    objA: priced.Sources,
                    objB: sources
                )
            ) {
                return priced.Report;
            }

            var report = (compilation.CostReport with {
                Presentation = WorldPresentationCost.Measure(
                    definition: compilation.Definition,
                    passes: sources.PlanGraph
                ),
            });

            m_pricedCostReport = new PricedCostReport(
                Compilation: compilation,
                Report: report,
                Sources: sources
            );

            return report;
        }
    }
    /// <summary>Gets the shared hazard analysis for the installed programs. Document installation replaces
    /// this analysis with its compilation; ordinary state writes do not recompile rules.</summary>
    public IReadOnlyList<Puck.State.Rules.RuleHazard> RuleHazards => (m_ruleCompilation
        ?? throw new InvalidOperationException(message: "The server has no installed rule compilation.")).Hazards;
}
