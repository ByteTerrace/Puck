using System.Globalization;
using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One rule binding's decision state. Key -1 denotes a global decision; other keys are the forEach row's integer keys.</summary>
public readonly record struct WorldDecisionCheckpoint(
    string Rule, int Key, int Generation, int Selected, bool Evaluated, bool InterruptHeld,
    ulong PeriodRemaining, ulong CommitmentRemaining, ulong RandomState, ulong DrawCount,
    ulong Reconsiderations, long LastScore, int Candidate = -1, int CandidateGeneration = 0
);
public sealed partial class WorldRuleHost : IWorldPersistedDecisions, IJournalParticipant {
    private DecisionRuntime[] m_decisions = [];
    private readonly Dictionary<string, DecisionRuntime> m_decisionsByName = new(comparer: StringComparer.Ordinal);

    private sealed class DecisionRuntime(CompiledWorldFactsRule rule) {
        // Where the host lists the runtime: what a journal record names it by.
        public int Ordinal;

        public CompiledWorldFactsRule Rule = rule;
        public readonly Dictionary<int, DecisionBinding> Bindings = [];
        // Sorted active binding keys also supply the canonical hash/checkpoint order, without per-tick sorting allocations.
        public readonly List<int> Keys = [];
        public readonly DecisionChoice[] Choices = new DecisionChoice[rule.Decision!.Options.Sum(selector: static option => (option.Neighbors?.Source.MaxCandidates ?? 1))];
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

    internal void ReconcileDecisions() {
        var next = new List<DecisionRuntime>();

        foreach (var compiled in m_rules) {
            if (
                (compiled is not CompiledWorldFactsRule rule) ||
                (rule.Decision is not { } policy)
            ) { continue; }
            DecisionRuntime? retained = null;

            foreach (var previous in m_decisions) {
                if (
                    (previous.Rule.Name == rule.Name) &&
                    (previous.Rule.Decision!.PolicyIdentity == policy.PolicyIdentity)
                ) {
                    retained = previous;
                    break;
                }
            }
            if (retained is not null) { retained.Rule = rule; }
            next.Add(item: (retained ?? new DecisionRuntime(rule: rule)));
        }
        m_decisions = next.ToArray();
        m_decisionsByName.Clear();
        for (var ordinal = 0; (ordinal < m_decisions.Length); ordinal++) {
            var runtime = m_decisions[ordinal];

            runtime.Ordinal = ordinal;
            m_decisionsByName.Add(
            key: runtime.Rule.Name,
            value: runtime
        );
        }
    }

    // What a decision's firing scope restores on a rewind: the binding as the decision found it, so a firing whose effects
    // refuse has chosen nothing and the decision picks again.
    private sealed class DecisionPrelude(WorldRuleHost host) : IFiringPrelude {
        public DecisionRuntime? Runtime;
        public int Key;
        public DecisionBinding Before;

        public void Record(ArenaJournal journal) {
            var before = Before;
            Span<long> words = [
                Runtime!.Ordinal,
                Key,
                before.Generation,
                before.Selected,
                before.Candidate,
                before.CandidateGeneration,
                (before.Evaluated ? 1L : 0L),
                (before.InterruptHeld ? 1L : 0L),
                unchecked((long)before.PeriodRemaining),
                unchecked((long)before.CommitmentRemaining),
                unchecked((long)before.RandomState),
                unchecked((long)before.DrawCount),
                unchecked((long)before.Reconsiderations),
                before.LastScore,
            ];

            journal.RecordParticipant(
                participant: host,
                words: words
            );
        }
    }

    private DecisionPrelude? m_decisionPrelude;

    void IJournalParticipant.Undo(ReadOnlySpan<long> words) {
        var runtime = m_decisions[((int)words[0])];

        runtime.Bindings[((int)words[1])] = new DecisionBinding {
            Generation = ((int)words[2]),
            Selected = ((int)words[3]),
            Candidate = ((int)words[4]),
            CandidateGeneration = ((int)words[5]),
            Evaluated = (words[6] != 0),
            InterruptHeld = (words[7] != 0),
            PeriodRemaining = unchecked((ulong)words[8]),
            CommitmentRemaining = unchecked((ulong)words[9]),
            RandomState = unchecked((ulong)words[10]),
            DrawCount = unchecked((ulong)words[11]),
            Reconsiderations = unchecked((ulong)words[12]),
            LastScore = words[13],
        };
    }

    private RuleOutcome EvaluateDecisionRule(CompiledWorldFactsRule rule, ulong stepTicks, out bool applied) {
        applied = false;

        if (!m_decisionsByName.TryGetValue(
            key: rule.Name,
            value: out var runtime
        )) { return RuleOutcome.Idle; }

        var aggregate = RuleOutcome.Idle;

        if (rule.ForEachOrdinal >= 0) {
            CarrierKeys(
                into: m_carrierScratchLeft,
                rowOrdinal: rule.ForEachOrdinal
            );
        } else {
            m_carrierScratchLeft.Clear();
            m_carrierScratchLeft.Add(item: -1);
        }
        foreach (var oldKey in runtime.Keys) {
            if (m_carrierScratchLeft.BinarySearch(item: oldKey) < 0) { runtime.Bindings.Remove(key: oldKey); }
        }
        runtime.Keys.Clear();
        foreach (var key in m_carrierScratchLeft) {
            if (
                (runtime.Keys.Count == 0) ||
                (runtime.Keys[^1] != key)
            ) { runtime.Keys.Add(item: key); }
        }
        try {
            foreach (var key in runtime.Keys) {
                BoundEachKey = (((key >= 0) && RuleReads.TryIndexKey(
                    keys: Host.Arena.Keys,
                    index: key,
                    key: out var bound
                ))
                    ? bound
                    : default
                );
                aggregate = RuleEvaluator.Aggregate(
                    left: aggregate,
                    right: EvaluateDecisionBinding(
                    applied: out var moved,
                    key: key,
                    runtime: runtime,
                    stepTicks: stepTicks
                ));
                applied |= moved;
            }
        } finally {
            BoundEachKey = default;
        }
        return aggregate;
    }
    private RuleOutcome EvaluateDecisionBinding(DecisionRuntime runtime, int key, ulong stepTicks, out bool applied) {
        var rule = runtime.Rule;
        var policy = rule.Decision!;
        var generation = ((((uint)key) < ((uint)Host.Population.Capacity))
            ? Host.Population.Generation(index: key)
            : 0
        );
        ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: runtime.Bindings,
            exists: out var exists,
            key: key
        );

        if (
            !exists ||
            (state.Generation != generation)
        ) {
            var seed = Fnv1aHash.Create();

            seed.Add(value: Fnv1aHash.Compute(values: rule.Name.AsSpan()));
            seed.Add(value: (Host.Definition.Generation?.WorldSeed ?? 0));
            seed.Add(value: policy.Seed);
            seed.Add(value: ((long)key));
            seed.Add(value: ((long)generation));
            state = new DecisionBinding {
                Generation = generation,
                Selected = -1,
                Candidate = -1,
                RandomState = Pcg32XshRr.Create(
                state: seed.Value,
                stream: 0
            ).State,
            };
        }
        state.PeriodRemaining = DrainDecisionTicks(
            step: stepTicks,
            value: state.PeriodRemaining
        );
        state.CommitmentRemaining = DrainDecisionTicks(
            step: stepTicks,
            value: state.CommitmentRemaining
        );
        // Everything the decision reads (bindings, interrupt, gate, option gates, scores) answers a value, and a fault
        // decides nothing: the decision keeps its choice and its interrupt edge, and runs no onNoChoice.
        return DecideBinding(applied: out applied, key: key, runtime: runtime, state: ref state, stepTicks: stepTicks);
    }
    private RuleOutcome HoldDecision(CompiledWorldFactsRule rule) {
        m_evaluator.ReportRefusal(
            detail: $"{RuleEvaluation.GateFaultReason}; the decision kept its choice",
            effect: "decision",
            refusal: RuleEffectRefusal.Arithmetic,
            ruleName: rule.Name,
            tick: Tick
        );

        return RuleOutcome.Faulted;
    }
    private RuleOutcome DecideBinding(DecisionRuntime runtime, int key, ref DecisionBinding state, ulong stepTicks, out bool applied) {
        var rule = runtime.Rule;
        var policy = rule.Decision!;
        var generation = state.Generation;
        var prelude = (m_decisionPrelude ??= new DecisionPrelude(host: this));

        applied = false;
        prelude.Runtime = runtime;
        prelude.Key = key;
        prelude.Before = state;

        var bound = m_evaluator.EvaluateBindings(rule: rule);

        if (bound != BindingsResult.Bound) {
            return ((bound == BindingsResult.Faulted)
                ? RuleOutcome.Faulted
                : RuleOutcome.Idle);
        }

        var interruptVerdict = ((policy.Interrupt is { } interrupt)
            ? m_evaluator.Decide(
                gate: interrupt,
                ruleName: rule.Name
            )
            : Verdict.Closed);
        var gateVerdict = m_evaluator.Decide(
            gate: rule.Gate,
            ruleName: rule.Name
        );
        var open = (gateVerdict == Verdict.Open);
        var faulted = ((interruptVerdict == Verdict.Faulted) || (gateVerdict == Verdict.Faulted));
        var lostEligibility = (open && (state.Selected >= 0) &&
            (((state.Candidate >= 0) && (!DecisionBodyLive(
            generation: state.Generation,
            index: key
        ) || !DecisionBodyLive(
            generation: state.CandidateGeneration,
            index: state.Candidate
        ))) ||
             !Opens(
            faulted: ref faulted,
            verdict: DecisionOptionGate(
            candidate: state.Candidate,
            key: key,
            option: policy.Options[state.Selected],
            ruleName: rule.Name
        ))));

        if (faulted) {
            return HoldDecision(rule: rule);
        }

        var interruptOpen = (interruptVerdict == Verdict.Open);
        var interrupted = (interruptOpen && !state.InterruptHeld);
        var reconsiders = (open && (!state.Evaluated || interrupted || lostEligibility ||
            ((state.PeriodRemaining == 0) && (state.CommitmentRemaining == 0))));
        var choiceCount = 0;

        // Every read comes before the first write, so a fault while gathering (an option gate or score) decides nothing
        // and leaves the binding as the decision found it, its interrupt edge included.
        if (reconsiders) {
            choiceCount = GatherDecisionChoices(
                faulted: out var gatherFaulted,
                key: key,
                lostEligibility: lostEligibility,
                runtime: runtime,
                state: state
            );

            if (gatherFaulted) {
                return HoldDecision(rule: rule);
            }
        }

        state.InterruptHeld = interruptOpen;
        if (!open) {
            var hadChoice = (state.Selected >= 0);

            state.Selected = -1;
            state.Candidate = -1;
            state.CandidateGeneration = 0;
            state.Evaluated = false;
            state.PeriodRemaining = 0;
            state.CommitmentRemaining = 0;
            state.LastScore = 0;
            return (hadChoice
                ? FireDecisionEffects(
                    applied: out applied,
                    effects: policy.OnNoChoice,
                    ruleName: rule.Name,
                    stepTicks: stepTicks
                )
                : RuleOutcome.Idle);
        }
        if (!reconsiders) { return RuleOutcome.Idle; }

        var winner = -1;
        var winnerScore = long.MinValue;
        var total = UInt128.Zero;

        for (var index = 0; (index < choiceCount); index++) {
            var score = runtime.Choices[index].Score;

            if (
                (winner < 0) ||
                (score > winnerScore)
            ) { winner = index; winnerScore = score; }
            if (policy.Mode == WorldDecisionMode.Weighted) { total += ((ulong)score); }
        }
        if (
            (policy.Mode == WorldDecisionMode.Weighted) &&
            (choiceCount > 1) &&
            (total != UInt128.Zero)
        ) {
            // Up to 1024 expanded choices occupy at most 73 bits. The ticket is an unbiased draw below the total
            // within the shared word budget; the cursor counts every raw word, rejected ones included.
            var words = new DrawGeneratorWords<Pcg32XshRr>(generator: Pcg32XshRr.FromRawBits(
                increment: 1,
                multiplier: Pcg32XshRr.DefaultMultiplier,
                state: state.RandomState
            ));
            var drawn = BoundedRandomSampling.TrySampleWide(
                exclusiveHigh: total,
                maxAttempts: GeneratorCapacity.MaxBoundedAttempts,
                reason: out _,
                source: ref words,
                value: out var ticket,
                words: out var consumed
            );

            state.RandomState = words.Generator.State;
            state.DrawCount = unchecked((state.DrawCount + ((ulong)consumed)));
            // A refused ticket keeps the words it consumed, so the next reconsideration draws fresh ones instead of
            // repeating the refusal, and leaves the standing choice as it was.
            if (!drawn) { return RuleOutcome.Idle; }

            for (var index = 0; (index < choiceCount); index++) {
                var weight = ((ulong)runtime.Choices[index].Score);

                if (ticket < weight) { winner = index; winnerScore = runtime.Choices[index].Score; break; }
                ticket -= weight;
            }
        }
        var selected = ((winner < 0)
            ? new DecisionChoice(
                Candidate: -1,
                Generation: 0,
                Option: -1,
                Score: 0
            )
            : runtime.Choices[winner]
        );
        var changed = (!state.Evaluated || (selected.Option != state.Selected) || (selected.Candidate != state.Candidate) || (selected.Generation != state.CandidateGeneration));

        state.Evaluated = true;
        state.PeriodRemaining = policy.PeriodTicks;
        state.Reconsiderations = unchecked((state.Reconsiderations + 1));
        state.LastScore = ((winner < 0)
            ? 0
            : winnerScore
        );
        if (!changed) { return RuleOutcome.Idle; }
        state.Selected = selected.Option;
        state.Candidate = selected.Candidate;
        state.CandidateGeneration = selected.Generation;
        state.CommitmentRemaining = ((winner < 0)
            ? 0
            : policy.CommitmentTicks
        );
        if (winner < 0) {
            return FireDecisionEffects(
            applied: out applied,
            effects: policy.OnNoChoice,
            ruleName: rule.Name,
            stepTicks: stepTicks
        );
        }

        var continuation = (m_decisionContinuation ??= new DecisionContinuation(host: this));

        continuation.Arm(
            generation: generation,
            key: key,
            option: policy.Options[selected.Option].Effects,
            selected: selected
        );

        // The common effects and the chosen option's are one firing: one scope, and the binding the decision
        // installed rewinds with both.
        return FireDecisionEffects(
            applied: out applied,
            continuation: continuation,
            effects: rule.Effects,
            ruleName: rule.Name,
            stepTicks: stepTicks
        );
    }

    // What a decision proposes into its firing after its common effects: the chosen option's effects, bound to the
    // pair they score, unless the population the firing's commit installs no longer holds that pair. The option is then
    // not proposed and the common effects stand; the firing is not refused for it.
    private sealed class DecisionContinuation(WorldRuleHost host) : IFiringContinuation {
        private IRuleEffect[] m_option = [];
        private DecisionChoice m_selected;
        private int m_generation;
        private int m_key;
        private int m_savedLeft;
        private int m_savedRight;

        public void Arm(IRuleEffect[] option, DecisionChoice selected, int key, int generation) {
            m_option = option;
            m_selected = selected;
            m_key = key;
            m_generation = generation;
        }
        public IRuleEffect[]? Next(in EffectFiring firing, ReadOnlySpan<IRuleEffect> queued) {
            if (
                (m_selected.Candidate >= 0) &&
                !host.ProposedPairLive(
                candidate: m_selected.Candidate,
                candidateGeneration: m_selected.Generation,
                firing: in firing,
                key: m_key,
                keyGeneration: m_generation,
                queued: queued
            )
            ) { return null; }

            m_savedLeft = host.m_boundLeft;
            m_savedRight = host.m_boundRight;
            host.m_boundLeft = ((m_selected.Candidate < 0)
                ? -1
                : m_key
            );
            host.m_boundRight = m_selected.Candidate;

            return m_option;
        }
        public void End() {
            host.m_boundLeft = m_savedLeft;
            host.m_boundRight = m_savedRight;
        }
    }

    private DecisionContinuation? m_decisionContinuation;

    private readonly List<WorldMutation> m_proposedRows = [];

    // Whether an option's pair is still the same two live bodies in the population a reconcile of what the firing
    // proposes produces. Until the commit no arm has run, and the only queued arm that changes the population when it
    // lands is a document row, so the rows the firing queued so far are composed against the document it proposes, as
    // its preflight composes them. The population then forecasts the reconcile the install of that unit runs (the
    // structural pass when the unit rebuilds the population, then the cell-driven counts read from the arena the scope
    // holds open), through the same code the install runs. With no row queued, the counts settle from the firing's
    // writes at the tick's publication, and the forecast answers as if they settled at the commit. A composition that
    // refuses keeps nothing: the preflight composes the same rows and refuses the firing.
    private bool ProposedPairLive(in EffectFiring firing, ReadOnlySpan<IRuleEffect> queued, int key, int keyGeneration, int candidate, int candidateGeneration) {
        if (
            !DecisionBodyLive(generation: keyGeneration, index: key) ||
            !DecisionBodyLive(generation: candidateGeneration, index: candidate)
        ) {
            return false;
        }

        m_proposedRows.Clear();
        foreach (var arm in queued) {
            if (arm is WorldDocumentEffect { Write: not WorldDocumentWrite.Save } row) {
                m_proposedRows.Add(item: DocumentRow(effect: row));
            }
        }

        try {
            var proposed = Host.Document.Definition;
            var structural = false;

            if (m_proposedRows.Count != 0) {
                var current = Host.MaterializeArena();
                var unit = DocumentUnit(rows: m_proposedRows);

                if (!WorldDocument.TryCompose(
                    candidate: out proposed,
                    current: current,
                    engineTick: firing.EngineTick,
                    evictedKey: out _,
                    instanceIdentity: Host.InstanceIdentity,
                    mutation: unit,
                    reason: out _,
                    tick: firing.Tick
                )) {
                    return false;
                }

                structural = Host.Document.RebuildsPopulation(
                    candidate: proposed,
                    mutation: unit,
                    previous: current
                );
            }

            return Host.Population.ReconcileKeeps(
                arena: Host.Arena,
                bodies: [key, candidate],
                definition: proposed,
                structural: structural,
                tick: Host.NextInputTick
            );
        } finally {
            m_proposedRows.Clear();
        }
    }

    /// <inheritdoc/>
    public long CaptureBinding() => (((long)m_boundLeft) << 32) | ((uint)m_boundRight);
    /// <inheritdoc/>
    public void ApplyBinding(long binding) {
        m_boundLeft = ((int)(binding >> 32));
        m_boundRight = ((int)binding);
    }

    // A decision's own effect sequence is one firing, exactly as a rule's is.
    private RuleOutcome FireDecisionEffects(IRuleEffect[] effects, string ruleName, ulong stepTicks, out bool applied, IFiringContinuation? continuation = null) => m_evaluator.FireEffects(
        applied: out applied,
        continuation: continuation,
        effects: effects,
        prelude: m_decisionPrelude,
        ruleName: ruleName,
        stepTicks: stepTicks,
        tick: Tick
    );
    private static ulong DrainDecisionTicks(ulong value, ulong step) => ((value > step)
        ? (value - step)
        : 0
    );

    /// <summary>Echoes authored decision policies and each active binding's choice, last evaluated score, timers, and local random draw count.</summary>
    /// <returns>A deterministic, headless-safe console read-back. Scores and timers describe the last completed simulation step.</returns>
    internal string DescribeDecisions() {
        lock (Host.AuthorityGate) {
            var rows = new List<string>();

            foreach (var runtime in m_decisions) {
                var policy = runtime.Rule.Decision!;
                var bindings = new List<string>();

                foreach (var key in runtime.Keys) {
                    var state = runtime.Bindings[key];
                    var selected = ((state.Selected < 0)
                        ? "none"
                        : policy.Options[state.Selected].Name
                    );

                    bindings.Add(item: string.Create(
                        CultureInfo.InvariantCulture,
                        $"{key}:{selected}/candidate={state.Candidate}@{state.CandidateGeneration}/lastScoreRaw={state.LastScore}/period={state.PeriodRemaining}/commit={state.CommitmentRemaining}/decisions={state.Reconsiderations}/draws={state.DrawCount}"
                    ));
                }
                var sources = policy.Options.Where(predicate: static option => (option.Neighbors is not null)).Select(selector: static option => {
                    var n = option.Neighbors!.Source;

                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"{option.Name}(range={n.Range},budget={n.CandidateBudget},max={n.MaxCandidates},halfAngle={n.HalfAngleDegrees},sight={n.RequiresLineOfSight},retain={n.RetainCurrent})"
                    );
                });

                rows.Add(item: $"{runtime.Rule.Name} mode={policy.Mode} scoreKind={policy.ScoreKind} options={policy.Options.Length} periodTicks={policy.PeriodTicks} commitmentTicks={policy.CommitmentTicks} neighbors=[{string.Join(
                    separator: ";",
                    values: sources
                )}] [{string.Join(
                    separator: ",",
                    values: bindings
                )}]");
            }
            return $"[world.decisions: {rows.Count} policy(s) inspected={m_decisionWork.Inspected} scored={m_decisionWork.Scored} sightTests={m_decisionWork.SightTests} limitedQueries={m_decisionWork.LimitedQueries} imagePoints={m_decisionWork.ImagePoints} gridBuilds={m_decisionWork.GridBuilds} | {string.Join(
                separator: " | ",
                values: rows
            )}]";
        }
    }

    IReadOnlyList<WorldDecisionCheckpoint> IWorldPersistedSection<IReadOnlyList<WorldDecisionCheckpoint>>.Capture() {
        var rows = new List<WorldDecisionCheckpoint>();

        foreach (var runtime in m_decisions) {
            foreach (var key in runtime.Keys) {
                var s = runtime.Bindings[key];

                rows.Add(item: new(
                    runtime.Rule.Name,
                    key,
                    s.Generation,
                    s.Selected,
                    s.Evaluated,
                    s.InterruptHeld,
                    s.PeriodRemaining,
                    s.CommitmentRemaining,
                    s.RandomState,
                    s.DrawCount,
                    s.Reconsiderations,
                    s.LastScore,
                    s.Candidate,
                    s.CandidateGeneration
                ));
            }
        }
        return rows.ToArray();
    }
    void IWorldPersistedSection<IReadOnlyList<WorldDecisionCheckpoint>>.AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(value: ((uint)m_decisions.Length));
        foreach (var runtime in m_decisions) {
            hash.Add(value: Fnv1aHash.Compute(values: runtime.Rule.Name.AsSpan()));
            hash.Add(value: ((uint)runtime.Keys.Count));
            foreach (var key in runtime.Keys) {
                var s = runtime.Bindings[key];

                hash.Add(value: ((long)key)); hash.Add(value: ((long)s.Generation)); hash.Add(value: ((long)s.Selected));
                hash.Add(value: ((byte)(s.Evaluated
                    ? 1
                    : 0))); hash.Add(value: ((byte)(s.InterruptHeld
                    ? 1
                    : 0)));
                hash.Add(value: s.PeriodRemaining); hash.Add(value: s.CommitmentRemaining); hash.Add(value: s.RandomState);
                hash.Add(value: s.DrawCount); hash.Add(value: s.Reconsiderations); hash.Add(value: s.LastScore);
                hash.Add(value: s.Candidate); hash.Add(value: s.CandidateGeneration);
            }
        }
    }
    void IWorldPersistedDecisions.ValidateCheckpoint(WorldServerCheckpoint checkpoint, WorldDefinition definition) {
        var rules = WorldFactsCompiler.CompileAll(definition: definition);
        var seen = new HashSet<(string Rule, int Key)>();

        if (checkpoint.Decisions is null) { throw new InvalidOperationException(message: "decision checkpoint rows are required"); }
        foreach (var s in checkpoint.Decisions) {
            var rule = ((CompiledWorldFactsRule?)Array.Find(
                array: rules,
                match: candidate => (candidate.Name == s.Rule)
            ));

            if (
                (rule?.Decision is not { } policy) ||
                !seen.Add(item: (s.Rule, s.Key)) ||
                ((rule.ForEachOrdinal < 0)
                ? (s.Key != -1)
                : (s.Key < 0)) ||
                (s.Generation < 0) ||
                (s.Selected < -1) ||
                (s.Selected >= policy.Options.Length) ||
                (s.Candidate < -1) ||
                (s.Candidate >= definition.Population.Capacity) ||
                (s.CandidateGeneration < 0) ||
                (((s.Selected >= 0) && (policy.Options[s.Selected].Neighbors is not null))
                ? ((s.Candidate < 0) || (s.Candidate == s.Key) || (s.Key >= definition.Population.Capacity))
                : ((s.Candidate != -1) || (s.CandidateGeneration != 0))) ||
                (s.PeriodRemaining > policy.PeriodTicks) ||
                (s.CommitmentRemaining > policy.CommitmentTicks) ||
                (!s.Evaluated && ((s.Selected != -1) || (s.PeriodRemaining != 0) || (s.CommitmentRemaining != 0))) ||
                ((s.Selected == -1) && ((s.CommitmentRemaining != 0) || (s.LastScore != 0))) ||
                ((policy.Interrupt is null) && s.InterruptHeld) ||
                ((policy.Mode == WorldDecisionMode.HighestScore) && (s.DrawCount != 0))
            ) {
                throw new InvalidOperationException(message: "invalid decision checkpoint binding or policy state");
            }
        }
        foreach (var compiled in rules) {
            var rule = ((CompiledWorldFactsRule)compiled);

            if (rule.Decision is null) { continue; }
            var count = seen.Count(predicate: row => (row.Rule == rule.Name));
            var row = ((rule.ForEachOrdinal >= 0)
                ? WorldDefinitionRows.FindStateRow(
                    definition.State,
                    definition.StateCatalog.Descriptors[rule.ForEachOrdinal].Name
                )
                : null
            );
            var ceiling = ((row is null)
                ? 1
                : (row.Capacity ?? row.CellCeiling)
            );

            if (count > ceiling) { throw new InvalidOperationException(message: "decision checkpoint exceeds its binding capacity"); }
        }
    }
    void IWorldPersistedSection<IReadOnlyList<WorldDecisionCheckpoint>>.Restore(IReadOnlyList<WorldDecisionCheckpoint> checkpoint) {
        foreach (var runtime in m_decisions) { runtime.Bindings.Clear(); runtime.Keys.Clear(); }
        foreach (var s in checkpoint) {
            var runtime = Array.Find(
                array: m_decisions,
                match: candidate => (candidate.Rule.Name == s.Rule)
            )!;

            runtime.Bindings.Add(
                key: s.Key,
                value: new DecisionBinding {
                    Candidate = s.Candidate,
                    CandidateGeneration = s.CandidateGeneration,
                    CommitmentRemaining = s.CommitmentRemaining,
                    DrawCount = s.DrawCount,
                    Evaluated = s.Evaluated,
                    Generation = s.Generation,
                    InterruptHeld = s.InterruptHeld,
                    LastScore = s.LastScore,
                    PeriodRemaining = s.PeriodRemaining,
                    RandomState = s.RandomState,
                    Reconsiderations = s.Reconsiderations,
                    Selected = s.Selected,
                }
            );
            runtime.Keys.Add(item: s.Key);
        }
        foreach (var runtime in m_decisions) { runtime.Keys.Sort(); }
    }
}
