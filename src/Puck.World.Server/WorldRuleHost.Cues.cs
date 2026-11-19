namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    private Dictionary<string, CueReads> m_cueReads = new(StringComparer.Ordinal);
    private Dictionary<string, CueReads> m_tickCueReads = new(StringComparer.Ordinal);
    private StateArena? m_cueArena;

    internal sealed record CueReads(int[] Rows, string[] Placements, bool FullBodies, bool AuthorityOnly);

    private void CompileCueReads() {
        if (!m_rules.Any(HasCue) && !m_interactions.Any(HasCue)) {
            m_cueReads = new(StringComparer.Ordinal);
            return;
        }
        var reads = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var factsByRule = new Dictionary<string, RuleNeedsBuilder>(StringComparer.Ordinal);
        var wholePopulation = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in m_rules.Concat(m_interactions)) {
            if (!reads.TryGetValue(rule.Name, out var rows)) { reads.Add(rule.Name, rows = []); }
            if (!factsByRule.TryGetValue(rule.Name, out var facts)) { factsByRule.Add(rule.Name, facts = new()); }
            var access = RuleDataflow.Reads(rule);
            RuleDataflow.CollectJudgement(rule, access, facts);
            // A firing also reads the locals deferred to it, which a judgement leaves out.
            foreach (var local in (rule.Locals ?? [])) { RuleDataflow.CollectExpressionSchedulingFacts(local.Expression, facts); }
            RuleDataflow.CollectEffectFacts(rule.Effects, facts, includeSources: true);
            if (rule is CompiledWorldFactsRule world) {
                if (world.Interaction is not null) { wholePopulation.Add(rule.Name); }
                if (world.Decision is { } decision) {
                    RuleDataflow.CollectGateFacts(decision.Interrupt ?? [], facts);
                    RuleDataflow.CollectEffectFacts(decision.OnNoChoice, facts, includeSources: true);
                    foreach (var option in decision.Options) {
                        RuleDataflow.CollectGateFacts(option.Gate, facts);
                        RuleDataflow.CollectExpressionSchedulingFacts(option.Score, facts);
                        RuleDataflow.CollectEffectFacts(option.Effects, facts, includeSources: true);
                    }
                }
            }
            rule.CollectWrites(access);
            foreach (var item in access) { if (item.RowOrdinal >= 0) { rows.Add(item.RowOrdinal); } }
            rows.UnionWith(rule.Needs.HostOwnedRows);
        }
        foreach (var group in m_groups) {
            var groupRows = new HashSet<int>();
            var access = new List<CellAccess>();
            var groupFacts = new RuleNeedsBuilder();
            RuleDataflow.CollectGate(group.Trigger, access);
            RuleDataflow.CollectGateFacts(group.Trigger, groupFacts);
            foreach (var local in group.Locals) {
                RuleDataflow.CollectExpression(local.Expression, access);
                RuleDataflow.CollectExpressionSchedulingFacts(local.Expression, groupFacts);
            }
            foreach (var item in access) { if (item.RowOrdinal >= 0) { groupRows.Add(item.RowOrdinal); } }
            groupRows.UnionWith(group.Needs.HostOwnedRows);
            foreach (var member in group.Members) {
                var name = m_rules[member].Name;
                groupRows.UnionWith(reads[name]);
                foreach (var fact in factsByRule[name].FacetFacts) { groupFacts.AddFact(fact); }
            }
            foreach (var member in group.Members) {
                var name = m_rules[member].Name;
                reads[name].UnionWith(groupRows);
                foreach (var fact in groupFacts.FacetFacts) { factsByRule[name].AddFact(fact); }
            }
        }
        m_cueReads = new(StringComparer.Ordinal);
        foreach (var (name, rows) in reads) {
            var placements = new HashSet<string>(StringComparer.Ordinal);
            var fullBodies = wholePopulation.Contains(name);
            var authorityOnly = false;
            foreach (var fact in factsByRule[name].FacetFacts) {
                switch (fact) {
                    case WorldRegionOccupancyOperand region:
                        placements.Add(region.PlacementId);
                        fullBodies = true;
                        break;
                    case WorldPlacementInfluenceOperand influence:
                        placements.Add(influence.PlacementId);
                        fullBodies = true;
                        break;
                    case WorldPopulationOperand or WorldPhysicsQuiescentOperand or WorldBodyFactOperand:
                        fullBodies = true;
                        break;
                    case WorldClockOperand or WorldHostOwnedCellOperand or WorldArgBodyOperand or IRuleEffect:
                        break;
                    case IRuleOperand:
                        authorityOnly = true;
                        break;
                }
            }
            m_cueReads.Add(name, new(rows.ToArray(), placements.ToArray(), fullBodies, authorityOnly));
        }
    }

    private static bool HasCue(CompiledRule rule) => ContainsCue(rule.Effects) ||
        (rule is CompiledWorldFactsRule { Decision: { } decision } &&
        (ContainsCue(decision.OnNoChoice) || decision.Options.Any(option => ContainsCue(option.Effects))));

    private static bool ContainsCue(IRuleEffect[] effects) => effects.Any(effect =>
        effect is WorldCueEffect || effect.Arms.Any(ContainsCue));

    private void BeginCueReads() {
        m_tickCueReads = m_cueReads;
        m_cueArena = Arena;
    }
}
