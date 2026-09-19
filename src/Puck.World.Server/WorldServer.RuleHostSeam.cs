using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Gets the arena host every state effect and resolved transform fires through.</summary>
    public WorldArenaHost ArenaHost => m_arenaHost;
    /// <summary>Gets the raw authority gate this server serializes federation operations and the fixed-step fold
    /// against.</summary>
    /// <remarks>Unlike <see cref="ExecuteAuthorityOperation{T}"/>, taking this gate directly does not refuse once
    /// the activation has frozen for retirement — which is what a read-back answering during a freeze needs.</remarks>
    public Lock AuthorityGate => m_authorityGate;
    /// <summary>Gets the tick the latest authoritative step completed, or zero before the first step.</summary>
    public ulong CompletedTick => m_tick.CompletedTick;
    /// <inheritdoc cref="WorldRuleHost.DecisionWork"/>
    public WorldDecisionWork DecisionWork => m_ruleHost.DecisionWork;
    /// <summary>Gets the input-hold runtime the hold reads and the pad fold consult.</summary>
    public WorldInputHoldRuntime InputHold => m_inputHold;
    /// <summary>Gets the rule facade — the evaluator, the compiled rules, groups and tables, the edge latches, the
    /// decision, pattern and identity-fact runtimes, and the world facts and effect arms the evaluator reaches
    /// through.</summary>
    public WorldRuleHost RuleHost => m_ruleHost;
    /// <inheritdoc cref="WorldRuleHost.Time"/>
    public ArenaTime Time => m_ruleHost.Time;

    /// <inheritdoc cref="WorldRuleHost.Answer"/>
    public QueryAnswer Answer(WorldQuery query) => m_ruleHost.Answer(query: query);
    /// <inheritdoc cref="WorldRuleHost.AnswerSubmittedQuery"/>
    public QueryAnswer AnswerSubmittedQuery(WorldQuery query, WorldPrincipal principal) =>
        m_ruleHost.AnswerSubmittedQuery(
            principal: principal,
            query: query
        );
    /// <inheritdoc cref="WorldRuleHost.DescribeDecisions"/>
    public string DescribeDecisions() => m_ruleHost.DescribeDecisions();
    /// <inheritdoc cref="WorldRuleHost.DescribeMatch"/>
    public string DescribeMatch(string patternName, string rowName, string? attribute, string? key, string? direction) =>
        m_ruleHost.DescribeMatch(
            attribute: attribute,
            direction: direction,
            key: key,
            patternName: patternName,
            rowName: rowName
        );
    /// <inheritdoc cref="WorldRuleHost.DescribePatternBudget"/>
    public string DescribePatternBudget() => m_ruleHost.DescribePatternBudget();
    /// <inheritdoc cref="WorldRuleHost.DescribePatterns"/>
    public string DescribePatterns() => m_ruleHost.DescribePatterns();
    /// <inheritdoc cref="WorldRuleHost.DescribeRuleRuntimeDiagnostics"/>
    public string DescribeRuleRuntimeDiagnostics() => m_ruleHost.DescribeRuleRuntimeDiagnostics();
    /// <inheritdoc cref="WorldRuleHost.DescribeRuleTrace"/>
    public string DescribeRuleTrace() => m_ruleHost.DescribeRuleTrace();
    /// <inheritdoc cref="WorldRuleHost.DescribeSymmetry"/>
    public string DescribeSymmetry(string topologyName, string? cellKey) =>
        m_ruleHost.DescribeSymmetry(
            cellKey: cellKey,
            topologyName: topologyName
        );
    /// <inheritdoc cref="WorldRuleHost.DescribeTables"/>
    public string DescribeTables() => m_ruleHost.DescribeTables();
    /// <inheritdoc cref="WorldRuleHost.DisarmRuleTrace"/>
    public bool DisarmRuleTrace() => m_ruleHost.DisarmRuleTrace();
    /// <inheritdoc cref="WorldRuleHost.RuleRuntimeDiagnostics"/>
    public IReadOnlyList<RuleRuntimeDiagnostic> RuleRuntimeDiagnostics() => m_ruleHost.RuleRuntimeDiagnostics();
    /// <inheritdoc cref="WorldRuleHost.TryArmRuleTrace"/>
    public bool TryArmRuleTrace(string rule, int evaluations, out string refusal) =>
        m_ruleHost.TryArmRuleTrace(
            evaluations: evaluations,
            refusal: out refusal,
            rule: rule
        );
}
