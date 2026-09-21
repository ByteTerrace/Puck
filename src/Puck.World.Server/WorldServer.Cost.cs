namespace Puck.World.Server;

public sealed partial class WorldServer {
    private WorldRuleCompilation? m_ruleCompilation;

    /// <summary>Gets the shared cost analysis for the installed rule compilation. Reading it does not compile
    /// rules again. Installing a new definition replaces the receipt and its lazily computed report together.</summary>
    public WorldCostReport CostReport => (m_ruleCompilation
        ?? throw new InvalidOperationException(message: "The server has no installed rule compilation.")).CostReport;

    /// <summary>Gets the shared hazard analysis for the installed programs. Document installation replaces
    /// this analysis with its compilation; ordinary state writes do not recompile rules.</summary>
    public IReadOnlyList<Puck.State.Rules.RuleHazard> RuleHazards => (m_ruleCompilation
        ?? throw new InvalidOperationException(message: "The server has no installed rule compilation.")).Hazards;
}
