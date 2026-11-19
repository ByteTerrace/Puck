namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    private Dictionary<string, CueReads> m_cueReads = new(StringComparer.Ordinal);
    private Dictionary<string, CueReads> m_tickCueReads = new(StringComparer.Ordinal);
    private StateArena? m_cueArena;

    /// <summary>What a rule's cue depends on, as a recipient must hold it to be told the cue fired.</summary>
    /// <param name="RuleName">The emitting rule.</param>
    /// <param name="Rows">The rows a recipient must read whole under a stable audience.</param>
    /// <param name="Placements">The placements a recipient's projection must carry.</param>
    /// <param name="Screens">The screens whose machine a recipient must hold.</param>
    /// <param name="FullBodies">Whether a recipient's delivery must carry every body.</param>
    /// <param name="Fields">Whether a recipient must read every field region whole.</param>
    /// <param name="Undeclared">The host facts that declare no disclosure, joined, or <see langword="null"/>; any
    /// withholds the cue from every recipient but the authority.</param>
    internal sealed record CueReads(string RuleName, int[] Rows, string[] Placements, int[] Screens, bool FullBodies, bool Fields, string? Undeclared);

    // The one place a cue's dependencies are classified: WorldRuleDisclosure reads each fact's own declaration, so a
    // fact type with none withholds the cue from every recipient but the authority instead of reaching them by omission.
    // A group's members fire as one cascade, so each member's cue depends on everything the group reads.
    private void CompileCueReads() {
        if (!m_rules.Any(HasCue) && !m_interactions.Any(HasCue)) {
            m_cueReads = new(StringComparer.Ordinal);
            return;
        }
        var needs = new Dictionary<string, WorldDisclosureNeeds>(StringComparer.Ordinal);
        foreach (var rule in m_rules.Concat(m_interactions)) {
            if (!needs.TryGetValue(rule.Name, out var ruleNeeds)) { needs.Add(rule.Name, ruleNeeds = new()); }
            WorldRuleDisclosure.Collect(rule: rule, into: ruleNeeds);
        }
        foreach (var group in m_groups) {
            var groupNeeds = new WorldDisclosureNeeds();
            var access = new List<CellAccess>();
            var groupFacts = new RuleNeedsBuilder();
            RuleDataflow.CollectGate(group.Trigger, access);
            RuleDataflow.CollectGateSchedulingFacts(group.Trigger, groupFacts);
            foreach (var local in group.Locals) {
                RuleDataflow.CollectExpression(local.Expression, access);
                RuleDataflow.CollectExpressionSchedulingFacts(local.Expression, groupFacts);
            }
            groupNeeds.AddRows(access);
            foreach (var row in group.Needs.HostOwnedRows) { groupNeeds.AddRow(row); }
            foreach (var fact in groupFacts.FacetFacts) { WorldRuleDisclosure.Classify(fact, groupNeeds); }
            foreach (var member in group.Members) { groupNeeds.UnionWith(needs[m_rules[member].Name]); }
            foreach (var member in group.Members) { needs[m_rules[member].Name].UnionWith(groupNeeds); }
        }
        m_cueReads = new(StringComparer.Ordinal);
        m_withheldNarrated.Clear();
        foreach (var (name, ruleNeeds) in needs) {
            m_cueReads.Add(name, new(name, [.. ruleNeeds.Rows], [.. ruleNeeds.Placements], [.. ruleNeeds.Screens],
                (ruleNeeds.Reach & WorldDisclosureReach.Bodies) != 0, (ruleNeeds.Reach & WorldDisclosureReach.Fields) != 0,
                ruleNeeds.Undeclared.Count == 0 ? null : string.Join(", ", ruleNeeds.Undeclared)));
        }
    }

    private readonly HashSet<string> m_withheldNarrated = new(StringComparer.Ordinal);

    // A cue withheld from a recipient only because a fact under it declares nothing is narrated once per rule per
    // compile, so an author sees which rule lost its audience and why.
    internal void NarrateUndeclaredCue(CueReads reads) {
        if (reads.Undeclared is not { } facts || !m_withheldNarrated.Add(reads.RuleName) || !Host.Output.HasNarrationSink) { return; }
        Host.Output.Narrate(channel: "world.cue",
            text: $"[world.cue: rule '{reads.RuleName}' cues reach only the authority: {facts} declare no disclosure]");
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
