using System.Globalization;
using System.Runtime.InteropServices;
using Puck.Maths;

namespace Puck.World.Server;

/// <summary>One rule binding's decision state. Key -1 denotes a global decision; other keys are the forEach row's integer keys.</summary>
public readonly record struct WorldDecisionCheckpoint(
    string Rule, int Key, int Generation, int Selected, bool Evaluated, bool InterruptHeld,
    ulong PeriodRemaining, ulong CommitmentRemaining, ulong RandomState, ulong DrawCount,
    ulong Reconsiderations, long LastScore, int Candidate = -1, int CandidateGeneration = 0
);

public sealed partial class WorldServer {
    private DecisionRuntime[] m_decisions = [];
    private readonly Dictionary<string, DecisionRuntime> m_decisionsByName = new(StringComparer.Ordinal);

    private sealed class DecisionRuntime(CompiledWorldRule rule) {
        public CompiledWorldRule Rule = rule;
        public readonly Dictionary<int, DecisionBinding> Bindings = [];
        // Sorted active binding keys also supply the canonical hash/checkpoint order, without per-tick sorting allocations.
        public readonly List<int> Keys = [];
        public readonly DecisionChoice[] Choices = new DecisionChoice[rule.Decision!.Options.Sum(static option => option.Neighbors?.Source.MaxCandidates ?? 1)];
    }

    private struct DecisionBinding {
        public int Generation;
        public int Selected;
        public int Candidate;
        public int CandidateGeneration;
        public bool Evaluated;
        public bool InterruptHeld;
        public ulong PeriodRemaining;
        public ulong CommitmentRemaining;
        public ulong RandomState;
        public ulong DrawCount;
        public ulong Reconsiderations;
        public long LastScore;
    }

    private void ReconcileDecisions() {
        var next = new List<DecisionRuntime>();
        foreach (var rule in m_rules) {
            if (rule.Decision is not { } policy) { continue; }
            DecisionRuntime? retained = null;
            foreach (var previous in m_decisions) {
                if (previous.Rule.Name == rule.Name && previous.Rule.Decision!.PolicyIdentity == policy.PolicyIdentity) {
                    retained = previous;
                    break;
                }
            }
            if (retained is not null) { retained.Rule = rule; }
            next.Add(retained ?? new DecisionRuntime(rule));
        }
        m_decisions = next.ToArray();
        m_decisionsByName.Clear();
        foreach (var runtime in m_decisions) { m_decisionsByName.Add(runtime.Rule.Name, runtime); }
    }

    private bool EvaluateDecisionRule(CompiledWorldRule rule, ulong tick, ulong stepTicks) {
        if (!m_decisionsByName.TryGetValue(rule.Name, out var runtime)) { return false; }
        var applied = false;
        if (rule.ForEach is { } row) {
            CarrierKeys(row, m_carrierScratchLeft);
        } else {
            m_carrierScratchLeft.Clear();
            m_carrierScratchLeft.Add(-1);
        }
        foreach (var oldKey in runtime.Keys) {
            if (m_carrierScratchLeft.BinarySearch(oldKey) < 0) { runtime.Bindings.Remove(oldKey); }
        }
        runtime.Keys.Clear();
        foreach (var key in m_carrierScratchLeft) {
            if (runtime.Keys.Count == 0 || runtime.Keys[^1] != key) { runtime.Keys.Add(key); }
        }
        try {
            foreach (var key in runtime.Keys) {
                m_boundEach = key;
                applied |= EvaluateDecisionBinding(runtime, key, tick, stepTicks);
            }
        } finally {
            m_boundEach = -1;
        }
        return applied;
    }

    private bool EvaluateDecisionBinding(DecisionRuntime runtime, int key, ulong tick, ulong stepTicks) {
        var rule = runtime.Rule;
        var policy = rule.Decision!;
        var generation = (uint)key < (uint)m_population.Capacity ? m_population.Generation(key) : 0;
        ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(runtime.Bindings, key, out var exists);
        if (!exists || state.Generation != generation) {
            var seed = Fnv1aHash.Create();
            seed.Add(Fnv1aHash.Compute(rule.Name.AsSpan()));
            seed.Add(m_definition.Generation?.WorldSeed ?? 0);
            seed.Add(policy.Seed);
            seed.Add((long)key);
            seed.Add((long)generation);
            state = new DecisionBinding {
                Generation = generation, Selected = -1, Candidate = -1,
                RandomState = Pcg32XshRr.Create(seed.Value, 0).State,
            };
        }
        state.PeriodRemaining = DrainDecisionTicks(state.PeriodRemaining, stepTicks);
        state.CommitmentRemaining = DrainDecisionTicks(state.CommitmentRemaining, stepTicks);
        var interruptOpen = policy.Interrupt is { } interrupt && RuleGateOpen(interrupt, tick);
        var interrupted = interruptOpen && !state.InterruptHeld;
        state.InterruptHeld = interruptOpen;
        if (!RuleGateOpen(rule.Gate, tick)) {
            var hadChoice = state.Selected >= 0;
            state.Selected = -1;
            state.Candidate = -1;
            state.CandidateGeneration = 0;
            state.Evaluated = false;
            state.PeriodRemaining = 0;
            state.CommitmentRemaining = 0;
            state.LastScore = 0;
            return hadChoice && FireWorldRuleEffects(policy.OnNoChoice, rule.Name, tick, stepTicks);
        }
        var lostEligibility = state.Selected >= 0 &&
            ((state.Candidate >= 0 && (!DecisionBodyLive(key, state.Generation) || !DecisionBodyLive(state.Candidate, state.CandidateGeneration))) ||
             !DecisionOptionGate(policy.Options[state.Selected], key, state.Candidate, tick));
        if (state.Evaluated && !interrupted && !lostEligibility &&
            (state.PeriodRemaining != 0 || state.CommitmentRemaining != 0)) { return false; }

        var choiceCount = GatherDecisionChoices(runtime, key, tick, state, lostEligibility);
        var winner = -1;
        var winnerScore = long.MinValue;
        var total = UInt128.Zero;
        for (var index = 0; index < choiceCount; index++) {
            var score = runtime.Choices[index].Score;
            if (winner < 0 || score > winnerScore) { winner = index; winnerScore = score; }
            if (policy.Mode == WorldDecisionMode.Weighted) { total += (ulong)score; }
        }
        if (policy.Mode == WorldDecisionMode.Weighted && choiceCount > 1) {
            var random = Pcg32XshRr.FromRawBits(1, Pcg32XshRr.DefaultMultiplier, state.RandomState);
            var draw = ((ulong)random.NextUInt32() << 32) | random.NextUInt32();
            state.RandomState = random.State;
            state.DrawCount = unchecked(state.DrawCount + 2);
            // floor(total * draw / 2^64), split before multiplying: up to 1024 expanded choices occupy at most 73 bits.
            // A fixed-width ticket bounds work; an unbiased rejection loop would have no hard iteration bound.
            var ticket = (total >> 64) * draw + (((total & ulong.MaxValue) * draw) >> 64);
            for (var index = 0; index < choiceCount; index++) {
                var weight = (ulong)runtime.Choices[index].Score;
                if (ticket < weight) { winner = index; winnerScore = runtime.Choices[index].Score; break; }
                ticket -= weight;
            }
        }
        var selected = winner < 0 ? new DecisionChoice(-1, -1, 0, 0) : runtime.Choices[winner];
        var changed = !state.Evaluated || selected.Option != state.Selected || selected.Candidate != state.Candidate || selected.Generation != state.CandidateGeneration;
        state.Evaluated = true;
        state.PeriodRemaining = policy.PeriodTicks;
        state.Reconsiderations = unchecked(state.Reconsiderations + 1);
        state.LastScore = winner < 0 ? 0 : winnerScore;
        if (!changed) { return false; }
        state.Selected = selected.Option;
        state.Candidate = selected.Candidate;
        state.CandidateGeneration = selected.Generation;
        state.CommitmentRemaining = winner < 0 ? 0 : policy.CommitmentTicks;
        if (winner < 0) { return FireWorldRuleEffects(policy.OnNoChoice, rule.Name, tick, stepTicks); }
        var applied = FireWorldRuleEffects(rule.Effects, rule.Name, tick, stepTicks);
        if (selected.Candidate >= 0 && (!DecisionBodyLive(key, generation) || !DecisionBodyLive(selected.Candidate, selected.Generation))) { return applied; }
        var left = m_boundLeft; var right = m_boundRight;
        m_boundLeft = selected.Candidate < 0 ? -1 : key; m_boundRight = selected.Candidate;
        try { return FireWorldRuleEffects(policy.Options[selected.Option].Effects, rule.Name, tick, stepTicks) || applied; }
        finally { m_boundLeft = left; m_boundRight = right; }
    }

    private static ulong DrainDecisionTicks(ulong value, ulong step) => value > step ? value - step : 0;

    /// <summary>Echoes authored decision policies and each active binding's choice, last evaluated score, timers, and local random draw count.</summary>
    /// <returns>A deterministic, headless-safe console read-back. Scores and timers describe the last completed simulation step.</returns>
    public string DescribeDecisions() {
        lock (m_authorityGate) {
            var rows = new List<string>();
            foreach (var runtime in m_decisions) {
                var policy = runtime.Rule.Decision!;
                var bindings = new List<string>();
                foreach (var key in runtime.Keys) {
                    var state = runtime.Bindings[key];
                    var selected = state.Selected < 0 ? "none" : policy.Options[state.Selected].Name;
                    bindings.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{key}:{selected}/candidate={state.Candidate}@{state.CandidateGeneration}/lastScoreRaw={state.LastScore}/period={state.PeriodRemaining}/commit={state.CommitmentRemaining}/decisions={state.Reconsiderations}/draws={state.DrawCount}"));
                }
                var sources = policy.Options.Where(static option => option.Neighbors is not null).Select(static option => {
                    var n = option.Neighbors!.Source;
                    return string.Create(CultureInfo.InvariantCulture, $"{option.Name}(range={n.Range},budget={n.CandidateBudget},max={n.MaxCandidates},halfAngle={n.HalfAngleDegrees},sight={n.RequiresLineOfSight},retain={n.RetainCurrent})");
                });
                rows.Add($"{runtime.Rule.Name} mode={policy.Mode} scoreKind={policy.ScoreKind} options={policy.Options.Length} periodTicks={policy.PeriodTicks} commitmentTicks={policy.CommitmentTicks} neighbors=[{string.Join(";", sources)}] [{string.Join(",", bindings)}]");
            }
            return $"[world.decisions: {rows.Count} policy(s) inspected={m_decisionWork.Inspected} scored={m_decisionWork.Scored} sightTests={m_decisionWork.SightTests} limitedQueries={m_decisionWork.LimitedQueries} imagePoints={m_decisionWork.ImagePoints} gridBuilds={m_decisionWork.GridBuilds} | {string.Join(" | ", rows)}]";
        }
    }

    private WorldDecisionCheckpoint[] CaptureDecisions() {
        var rows = new List<WorldDecisionCheckpoint>();
        foreach (var runtime in m_decisions) {
            foreach (var key in runtime.Keys) {
                var s = runtime.Bindings[key];
                rows.Add(new(runtime.Rule.Name, key, s.Generation, s.Selected, s.Evaluated, s.InterruptHeld,
                    s.PeriodRemaining, s.CommitmentRemaining, s.RandomState, s.DrawCount, s.Reconsiderations, s.LastScore, s.Candidate, s.CandidateGeneration));
            }
        }
        return rows.ToArray();
    }

    private void AppendDecisionHash(ref Fnv1aHash hash) {
        hash.Add((uint)m_decisions.Length);
        foreach (var runtime in m_decisions) {
            hash.Add(Fnv1aHash.Compute(runtime.Rule.Name.AsSpan()));
            hash.Add((uint)runtime.Keys.Count);
            foreach (var key in runtime.Keys) {
                var s = runtime.Bindings[key];
                hash.Add((long)key); hash.Add((long)s.Generation); hash.Add((long)s.Selected);
                hash.Add((byte)(s.Evaluated ? 1 : 0)); hash.Add((byte)(s.InterruptHeld ? 1 : 0));
                hash.Add(s.PeriodRemaining); hash.Add(s.CommitmentRemaining); hash.Add(s.RandomState);
                hash.Add(s.DrawCount); hash.Add(s.Reconsiderations); hash.Add(s.LastScore);
                hash.Add(s.Candidate); hash.Add(s.CandidateGeneration);
            }
        }
    }

    private static void ValidateDecisionCheckpoint(WorldServerCheckpoint checkpoint, WorldDefinition definition) {
        var rules = WorldRuleCompiler.CompileAll(definition);
        var seen = new HashSet<(string Rule, int Key)>();
        if (checkpoint.Decisions is null) { throw new InvalidOperationException("decision checkpoint rows are required"); }
        foreach (var s in checkpoint.Decisions) {
            var rule = Array.Find(rules, candidate => candidate.Name == s.Rule);
            if (rule?.Decision is not { } policy || !seen.Add((s.Rule, s.Key)) ||
                (rule.ForEach is null ? s.Key != -1 : s.Key < 0) || s.Generation < 0 ||
                s.Selected < -1 || s.Selected >= policy.Options.Length ||
                s.Candidate < -1 || s.Candidate >= definition.Population.Capacity || s.CandidateGeneration < 0 ||
                (s.Selected >= 0 && policy.Options[s.Selected].Neighbors is not null
                    ? s.Candidate < 0 || s.Candidate == s.Key || s.Key >= definition.Population.Capacity
                    : s.Candidate != -1 || s.CandidateGeneration != 0) ||
                s.PeriodRemaining > policy.PeriodTicks || s.CommitmentRemaining > policy.CommitmentTicks ||
                (!s.Evaluated && (s.Selected != -1 || s.PeriodRemaining != 0 || s.CommitmentRemaining != 0)) ||
                (s.Selected == -1 && (s.CommitmentRemaining != 0 || s.LastScore != 0)) ||
                (policy.Interrupt is null && s.InterruptHeld) || (s.DrawCount & 1) != 0 ||
                (policy.Mode == WorldDecisionMode.HighestScore && s.DrawCount != 0)) {
                throw new InvalidOperationException("invalid decision checkpoint binding or policy state");
            }
        }
        foreach (var rule in rules) {
            if (rule.Decision is null) { continue; }
            var count = seen.Count(row => row.Rule == rule.Name);
            var row = rule.ForEach is { } name ? WorldDefinitionRows.FindStateRow(definition.State, name) : null;
            var ceiling = row is null ? 1 : row.Capacity ?? row.CellCeiling;
            if (count > ceiling) { throw new InvalidOperationException("decision checkpoint exceeds its binding capacity"); }
        }
    }

    private void RestoreDecisions(IReadOnlyList<WorldDecisionCheckpoint> rows) {
        foreach (var runtime in m_decisions) { runtime.Bindings.Clear(); runtime.Keys.Clear(); }
        foreach (var s in rows) {
            var runtime = Array.Find(m_decisions, candidate => candidate.Rule.Name == s.Rule)!;
            runtime.Bindings.Add(s.Key, new DecisionBinding {
                Generation = s.Generation, Selected = s.Selected, Evaluated = s.Evaluated, InterruptHeld = s.InterruptHeld,
                PeriodRemaining = s.PeriodRemaining, CommitmentRemaining = s.CommitmentRemaining,
                RandomState = s.RandomState, DrawCount = s.DrawCount, Reconsiderations = s.Reconsiderations, LastScore = s.LastScore,
                Candidate = s.Candidate, CandidateGeneration = s.CandidateGeneration,
            });
            runtime.Keys.Add(s.Key);
        }
        foreach (var runtime in m_decisions) { runtime.Keys.Sort(); }
    }
}
