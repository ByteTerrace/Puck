using Puck.State.Rules;

namespace Puck.State;

/// <summary>A judge that runs compiled rules: one candidate's verdict is <see cref="Puck.State.Rules.RuleEvaluator"/> evaluating the
/// job's judge rules over the scoped arena through an <see cref="IEffectHost"/>, and its score is a compiled
/// expression read over the same host.</summary>
/// <remarks>
/// <para>The latch is cleared before each run, so a candidate's verdict depends on the position it reached and
/// never on the candidate judged before it.</para>
/// <para>Rules types are spelled in full here while the old compiler still declares the same names in
/// <c>Puck.State</c>; the enclosing namespace would otherwise bind them to it.</para>
/// </remarks>
public sealed class RuleArenaSearchJudge : IArenaSearchJudge {
    private readonly Puck.State.Rules.RuleEvaluator m_evaluator;
    private readonly ArenaSearchEffectHost m_host;
    private readonly int[] m_keyRows;
    private readonly Puck.State.Rules.RuleLatch m_latch = new();
    private readonly Puck.State.Rules.CompiledRule[] m_rules;
    private readonly Puck.State.Rules.CompiledExpressionToken[]? m_score;
    private readonly bool m_readsTick;

    /// <summary>Initializes a judge over the rules one search job judges its candidates with.</summary>
    /// <param name="host">The host every rule fires through; its arena is the one the search scopes.</param>
    /// <param name="rules">The judge rules, in the order they evaluate.</param>
    /// <param name="score">The compiled score program read in <see cref="CellKind.Int"/>, or
    /// <see langword="null"/> for a job that compares no plies.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> or <paramref name="rules"/> is
    /// <see langword="null"/>.</exception>
    public RuleArenaSearchJudge(ArenaSearchEffectHost host, Puck.State.Rules.CompiledRule[] rules, Puck.State.Rules.CompiledExpressionToken[]? score = null) {
        ArgumentNullException.ThrowIfNull(argument: host);
        ArgumentNullException.ThrowIfNull(argument: rules);

        m_evaluator = new Puck.State.Rules.RuleEvaluator(host: host);
        m_host = host;
        m_rules = rules;
        m_score = score;
        m_keyRows = KeyRowsOf(
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
    public bool Judge(in ArenaSearchView view) {
        m_host.SearchPly = view.Ply;
        m_host.Advance(
            engineTick: view.EngineTick,
            tick: view.Tick
        );
        m_latch.Reset();

        // Whatever the candidate queued is dropped however the evaluation ends, so nothing it queued is counted
        // against the next candidate.
        try {
            return m_evaluator.Evaluate(
                latch: m_latch,
                rules: m_rules,
                stepTicks: 1UL
            );
        } finally {
            m_host.DiscardQueuedArms();
        }
    }
    /// <inheritdoc/>
    public long Score(in ArenaSearchView view) {
        if (m_score is not { } program) {
            return 0L;
        }

        m_host.SearchPly = view.Ply;
        m_host.Advance(
            engineTick: view.EngineTick,
            tick: view.Tick
        );

        return (RuleExpressions.TryEvaluate(
            fault: out _,
            kind: CellKind.Int,
            program: program,
            reader: m_host,
            value: out var value
        )
            ? value
            : 0L
        );
    }
    /// <inheritdoc/>
    public bool TryAdmit(ArenaSearchPlan plan, out string refusal) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        foreach (var rule in m_rules) {
            if (ContainsTurnRewind(effects: rule.Effects)) {
                refusal = $"search '{plan.Name}' judges with rule '{rule.Name}', whose rewindTurn effect requires a settled authoritative turn boundary";
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

    private static bool ContainsTurnRewind(IEnumerable<Puck.State.Rules.IRuleEffect> effects) {
        foreach (var effect in effects) {
            if (effect is Puck.State.Rules.RewindTurnEffect) { return true; }
            foreach (var arm in effect.Arms) {
                if (ContainsTurnRewind(effects: arm)) { return true; }
            }
        }
        return false;
    }
    // Every row a verdict can depend on: what the rules read, what they write (a judge rule's own scratch row is a
    // later rule's input), and what the score reads.
    private static int[] KeyRowsOf(Puck.State.Rules.CompiledRule[] rules, Puck.State.Rules.CompiledExpressionToken[]? score) {
        var accesses = new List<CellAccess>();
        var rows = new SortedSet<int>();

        foreach (var rule in rules) {
            accesses.Clear();
            rule.CollectReads(into: accesses);
            rule.CollectWrites(into: accesses);

            foreach (var access in accesses) {
                if (access.RowOrdinal >= 0) {
                    _ = rows.Add(item: access.RowOrdinal);
                }
            }
        }

        accesses.Clear();

        Puck.State.Rules.RuleDataflow.CollectExpression(
            into: accesses,
            tokens: score
        );

        foreach (var access in accesses) {
            if (access.RowOrdinal >= 0) {
                _ = rows.Add(item: access.RowOrdinal);
            }
        }

        return [.. rows];
    }
    // Tick is an operand rather than a state row, so the row reach above cannot express this dependency. Rules
    // carry compiler-owned needs; a standalone score has no rule needs, so walk its already-bounded token DAG.
    private static bool ReadsTickOf(Puck.State.Rules.CompiledRule[] rules, Puck.State.Rules.CompiledExpressionToken[]? score) {
        if (rules.Any(predicate: static rule => rule.Needs.ReadsTick)) {
            return true;
        }

        return ContainsTick(tokens: score);
    }
    private static bool ContainsTick(Puck.State.Rules.CompiledExpressionToken[]? tokens) {
        foreach (var token in (tokens ?? [])) {
            if (token.Operand is Puck.State.Rules.TickOperand) {
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
