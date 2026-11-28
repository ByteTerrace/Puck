using Puck.State.Rules;

namespace Puck.State;

/// <summary>A judge that runs compiled rules: one candidate's verdict is <see cref="RuleEvaluator"/> evaluating the
/// job's judge rules over the scoped arena through an <see cref="IEffectHost"/>, and its score is a compiled
/// expression read over the same host.</summary>
/// <remarks>
/// <para>The latch is <see cref="RuleLatch.Fresh"/>: it keeps no edge bit, so a candidate's verdict depends on the
/// position it reached and never on the candidate judged before it, and a closed gate writes nothing into the
/// candidate's scope.</para>
/// </remarks>
public sealed class RuleArenaSearchJudge : IArenaSearchJudge {
    private readonly RuleEvaluator m_evaluator;
    private readonly ArenaSearchEffectHost m_host;
    private readonly int[] m_keyRows;
    private readonly RuleLatch m_latch = new(fresh: true);
    private readonly CompiledRule[] m_rules;
    private readonly CompiledProgram? m_score;
    private readonly bool m_readsTick;

    /// <summary>Initializes a judge over the rules one search job judges its candidates with.</summary>
    /// <param name="host">The host every rule fires through; its arena is the one the search scopes.</param>
    /// <param name="rules">The judge rules, in the order they evaluate.</param>
    /// <param name="score">The compiled score program read in <see cref="CellKind.Int"/>, or
    /// <see langword="null"/> for a job that compares no plies.</param>
    /// <param name="member">The one family index the judge binds a sweep over a row family to, for a job over one member
    /// of a set of rules (<see cref="RuleEvaluator.MemberScope"/>), or <see langword="null"/> for every member.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> or <paramref name="rules"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Two of <paramref name="rules"/> share a name, so they would share latch
    /// state and one could read the other's crossing within a candidate.</exception>
    public RuleArenaSearchJudge(ArenaSearchEffectHost host, CompiledRule[] rules, CompiledProgram? score = null, int? member = null) {
        ArgumentNullException.ThrowIfNull(argument: host);
        ArgumentNullException.ThrowIfNull(argument: rules);

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var rule in rules) {
            if (!names.Add(item: rule.Name)) {
                throw new ArgumentException(message: $"judge rule '{rule.Name}' is named twice", paramName: nameof(rules));
            }
        }

        // Each candidate runs once over a fresh latch. There is no prior evaluation to memoize.
        m_evaluator = new RuleEvaluator(host: host) { MemberScope = member, SchedulingEnabled = false };
        m_host = host;
        m_rules = rules;
        m_score = score;
        m_keyRows = RuleDataflow.KeyRows(
            member: member,
            rules: rules,
            score: score
        );
        m_readsTick = ReadsTickOf(
            rules: rules,
            score: score
        );
    }

    /// <inheritdoc/>
    public StateArena Arena => m_host.Arena;
    /// <inheritdoc/>
    public IReadOnlyList<int> KeyRows => m_keyRows;
    /// <inheritdoc/>
    public bool ReadsTick => m_readsTick;
    /// <inheritdoc/>
    public int RuleCount => m_rules.Length;
    /// <inheritdoc/>
    public bool Scores => (m_score is not null);

    /// <inheritdoc/>
    public JudgeOutcome Judge(in ArenaSearchView view) {
        m_host.SearchPly = view.Ply;
        m_host.Advance(
            engineTick: view.EngineTick,
            tick: view.Tick
        );

        // Whatever the candidate queued is dropped however the evaluation ends, so nothing it queued is counted
        // against the next candidate.
        try {
            var outcome = m_evaluator.Evaluate(
                applied: out var applied,
                latch: m_latch,
                rules: m_rules,
                stepTicks: 1UL
            );

            return new JudgeOutcome(
                Applied: applied,
                Faulted: (outcome == RuleOutcome.Faulted)
            );
        } finally {
            m_host.DiscardQueuedArms();
        }
    }
    /// <inheritdoc/>
    public Outcome Score(in ArenaSearchView view) {
        if (m_score is not { } program) {
            return Outcome.Finite(raw: 0L);
        }

        m_host.SearchPly = view.Ply;
        m_host.Advance(
            engineTick: view.EngineTick,
            tick: view.Tick
        );

        var frame = new EvaluationFrame(reader: m_host);

        return RuleExpressions.Evaluate(
            frame: ref frame,
            kind: CellKind.Int,
            program: program
        );
    }
    /// <inheritdoc/>
    public bool TryAdmit(ArenaSearchPlan plan, out string refusal) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        foreach (var rule in m_rules) {
            if (RuleEffects.ContainsRewind(effects: rule.Effects)) {
                refusal = $"search '{plan.Name}' judges with rule '{rule.Name}', whose rewindGroup effect requires a settled authoritative turn boundary";
                return false;
            }
            if (!RuleNeeds.Admit(
                needs: rule.Needs,
                reader: m_host,
                refusal: out var named
            )) {
                refusal = $"search '{plan.Name}' judges with rule '{rule.Name}', which {named}";

                return false;
            }
        }

        refusal = string.Empty;

        return true;
    }

    // Tick is an operand rather than a state row, so the row reach above cannot express this dependency. Rules
    // carry compiler-owned needs; a standalone score has no rule needs, so walk its already-bounded token DAG.
    private static bool ReadsTickOf(CompiledRule[] rules, CompiledExpressionToken[]? score) {
        if (rules.Any(predicate: static rule => rule.Needs.ReadsTick)) {
            return true;
        }

        return ContainsTick(tokens: score);
    }
    private static bool ContainsTick(CompiledExpressionToken[]? tokens) {
        foreach (var token in (tokens ?? [])) {
            if (token.Operand is TickOperand) {
                return true;
            }
            if (
                (token.Fold is { } fold) &&
                ContainsTick(tokens: fold.Body)
            ) {
                return true;
            }
            if (
                (token.Call is { } call) &&
                ContainsTick(tokens: call.Body)
            ) {
                return true;
            }
        }

        return false;
    }
}
