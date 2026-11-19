namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    private Dictionary<string, CueReads> m_cueReads = new(StringComparer.Ordinal);
    private Dictionary<string, CueReads> m_tickCueReads = new(StringComparer.Ordinal);
    private StateArena? m_cueArena;

    internal sealed record CueReads(int[] Rows, string[] Placements, bool FullBodies, bool AuthorityOnly);

    private void CompileCueReads() {
        var reads = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var factsByRule = new Dictionary<string, RuleNeedsBuilder>(StringComparer.Ordinal);
        var wholePopulation = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in m_rules.Concat(m_interactions)) {
            if (!reads.TryGetValue(rule.Name, out var rows)) { reads.Add(rule.Name, rows = []); }
            if (!factsByRule.TryGetValue(rule.Name, out var facts)) { factsByRule.Add(rule.Name, facts = new()); }
            var access = RuleDataflow.Reads(rule);
            RuleDataflow.CollectJudgement(rule, access, facts);
            CollectCueFacts(rule.Effects, facts);
            if (rule is CompiledWorldFactsRule world) {
                if (world.Interaction is not null) { wholePopulation.Add(rule.Name); }
                if (world.Decision is { } decision) {
                    RuleDataflow.CollectGateFacts(decision.Interrupt ?? [], facts);
                    CollectCueFacts(decision.OnNoChoice, facts);
                    foreach (var option in decision.Options) {
                        RuleDataflow.CollectGateFacts(option.Gate, facts);
                        RuleDataflow.CollectExpressionSchedulingFacts(option.Score, facts);
                        CollectCueFacts(option.Effects, facts);
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

    private static void CollectCueFacts(IRuleEffect[] effects, RuleNeedsBuilder facts) {
        foreach (var effect in effects) {
            if (effect is IfEffect branch) { RuleDataflow.CollectGateFacts(branch.Condition, facts); }
            foreach (var source in effect.Sources) {
                facts.AddFact(source.Operand);
                RuleDataflow.CollectExpressionSchedulingFacts(source.Program, facts);
                CompiledCellRef.CollectFacts(source.KeyFrom, facts);
            }
            foreach (var arm in effect.Arms) { CollectCueFacts(arm, facts); }
        }
    }

    private void BeginCueReads() {
        m_tickCueReads = m_cueReads;
        m_cueArena = Arena;
    }
}
