using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldScopedReplayRuntime {
    private CompiledRule[]? m_allRules;
    private CompiledRule[]? m_allInteractions;
    private CompiledRuleGroup[]? m_allGroups;
    private CompiledRule[] m_rules = [];
    private CompiledRule[] m_interactions = [];
    private CompiledRuleGroup[] m_groups = [];
    private HashSet<string> m_ruleNames = [];
    private HashSet<string> m_interactionNames = [];
    private WorldRuleLatchEntry[] m_startRuleLatch = [];
    private WorldRuleLatchEntry[] m_startInteractionLatch = [];
    private WorldRuleGroupEntry[] m_startGroups = [];

    // Keep the cached path free of closures: this runs on every active scope hash.
    private void BindRules() {
        var host = m_server.RuleHost;

        if (ReferenceEquals(objA: m_allRules, objB: host.Rules) && ReferenceEquals(objA: m_allInteractions, objB: host.Interactions) && ReferenceEquals(objA: m_allGroups, objB: host.Groups)) { return; }
        RebindRules(host: host);
    }
    private void RebindRules(WorldRuleHost host) {
        var writes = new List<CellAccess>();
        var bodies = new HashSet<int>();

        bool WritesScope(CompiledRule rule, bool grouped = false) {
            writes.Clear(); rule.CollectWrites(into: writes);
            bodies.Clear(); CollectBodyWrites(into: bodies, rule: rule);
            // Standalone level rules carry no latch. A group's progress still owns their effects.
            return (writes.Any(predicate: cell => m_rows.Contains(item: cell.RowOrdinal)) ||
                ((grouped || (rule.Mode == ActionTriggerMode.Edge) || (rule is CompiledWorldFactsRule { Decision: not null })) &&
                    bodies.Any(predicate: body => (m_bodies.Contains(item: body) || ((body < 0) && (m_bodies.Count > 0))))));
        }
        m_groups = host.Groups.Where(predicate: group => group.Members.Any(predicate: member => WritesScope(rule: host.Rules[member], grouped: true))).ToArray();
        m_rules = host.Rules.Where(predicate: (rule, index) => (WritesScope(rule: rule) || m_groups.Any(predicate: group => group.Members.Contains(value: index)))).ToArray();
        m_interactions = host.Interactions.Where(predicate: rule => WritesScope(rule: rule)).ToArray();
        m_ruleNames = m_rules.Select(selector: rule => rule.Name).ToHashSet(comparer: StringComparer.Ordinal);
        m_interactionNames = m_interactions.Select(selector: rule => rule.Name).ToHashSet(comparer: StringComparer.Ordinal);
        foreach (var group in m_groups) {
            if ((group.Undo is not null) || group.WriteRows.Any(predicate: row => !m_rows.Contains(item: row))) {
                throw new InvalidOperationException(message: $"Scope '{Name}' requires the complete continuation of rule group '{group.Name}'.");
            }
        }
        if (m_rules.Concat(second: m_interactions).Any(predicate: rule => (rule is CompiledWorldFactsRule { Decision: not null }))) {
            throw new InvalidOperationException(message: $"Scope '{Name}' does not support decision continuation.");
        }
        foreach (var rule in m_rules.Concat(second: m_interactions)) {
            bodies.Clear(); CollectBodyWrites(into: bodies, rule: rule);
            if (bodies.Any(predicate: body => ((body < 0) || !m_bodies.Contains(item: body)))) {
                throw new InvalidOperationException(message: $"Scope '{Name}' cannot isolate body continuation of rule '{rule.Name}'.");
            }
        }
        m_allRules = host.Rules; m_allInteractions = host.Interactions; m_allGroups = host.Groups;
    }
    private void AppendRuleHash(ref Fnv1aHash hash) {
        BindRules();
        m_server.RuleHost.RuleGateHeld.AppendStateHash(ref hash, m_rules, m_server.Arena.Keys.Names);
        m_server.RuleHost.InteractionGateHeld.AppendStateHash(ref hash, m_interactions);
        m_server.RuleHost.GroupState.AppendStateHash(compiled: m_groups, hash: ref hash);
    }
    private void CaptureRules() {
        BindRules();
        m_startRuleLatch = m_server.Persistence.FlattenLatch(m_server.RuleHost.RuleGateHeld, named: true, rules: m_ruleNames);
        m_startInteractionLatch = m_server.Persistence.FlattenLatch(m_server.RuleHost.InteractionGateHeld, named: false, rules: m_interactionNames);
        m_startGroups = m_groups.Select(selector: group => {
            var progress = m_server.RuleHost.GroupState.Progress(name: group.Name);

            return new WorldRuleGroupEntry(group.Name, progress.Step, progress.Running, progress.Breached);
        }).ToArray();
    }
    private void RestoreRules() {
        BindRules();
        m_server.Persistence.RestoreLatch(m_server.RuleHost.RuleGateHeld, m_startRuleLatch, m_ruleNames);
        m_server.Persistence.RestoreLatch(m_server.RuleHost.InteractionGateHeld, m_startInteractionLatch, m_interactionNames);
        foreach (var group in m_startGroups) {
            m_server.RuleHost.GroupState.Restore(name: group.Group, progress: new RuleGroupProgress(group.Step, group.Running, group.Breached));
        }
    }
    private bool ValidRuleImage(WorldScopedReplayCheckpoint checkpoint) {
        BindRules();
        // A scoped image returns to this same arena. Its retained keys must already exist; restoring
        // a latch must never intern a forged key into the world's shared namespace.
        bool RetainedKey(WorldRuleLatchEntry entry) => ((entry.Key.Length == 0) ||
            (CellName.TryParse(candidate: entry.Key, name: out var name, reason: out _) && m_server.Arena.Keys.TryResolve(key: out _, name: name)));
        return (checkpoint.RuleGateHeld.All(predicate: entry => (m_ruleNames.Contains(item: entry.Rule) && RetainedKey(entry: entry))) &&
            checkpoint.InteractionGateHeld.All(predicate: entry => (m_interactionNames.Contains(item: entry.Rule) && RetainedKey(entry: entry))) &&
            (checkpoint.RuleGateHeld.Select(selector: entry => (entry.Rule, entry.Key, entry.Left, entry.Right, entry.LeftGeneration, entry.RightGeneration)).Distinct().Count() == checkpoint.RuleGateHeld.Count) &&
            (checkpoint.InteractionGateHeld.Select(selector: entry => (entry.Rule, entry.Key, entry.Left, entry.Right, entry.LeftGeneration, entry.RightGeneration)).Distinct().Count() == checkpoint.InteractionGateHeld.Count) &&
            ((checkpoint.Start is null) ? (checkpoint.RuleGroups.Count == 0) :
                checkpoint.RuleGroups.Select(selector: group => group.Group).Order(comparer: StringComparer.Ordinal).SequenceEqual(second: m_groups.Select(selector: group => group.Name).Order(comparer: StringComparer.Ordinal))) &&
            checkpoint.RuleGroups.All(predicate: group => ((group.Step >= 0) && (group.Step <= m_groups.Single(predicate: compiled => (compiled.Name == group.Group)).Members.Length))));
    }
}
