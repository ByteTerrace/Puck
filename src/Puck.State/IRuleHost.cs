namespace Puck.State;

/// <summary>What the rule evaluator drives: the section it reads (<see cref="IRuleReader"/>), the mutation door its
/// state effects install through, the private preflight scope a transaction composes under, the effect arms only the
/// host can fire, and the rule kinds only the host can evaluate. A document project implements this once and owns
/// one <see cref="RuleEvaluator"/> over it; a headless resolver implements it over a bare row list.</summary>
/// <remarks>Every member is reached on the tick path. A host allocates nothing per call beyond what its own door
/// already allocates for a mutation.</remarks>
public interface IRuleHost : IRuleReader {
    /// <summary>Runs one state mutation through the host's own door. Under preflight the host composes and validates
    /// a private candidate and makes it visible to every later read until the enclosing
    /// <see cref="EndPreflight"/> — nothing installs, and the candidate is the state the next preflighted step reads.
    /// Outside preflight the host installs, journals, and delivers exactly as it does for any other principal's
    /// mutation.</summary>
    /// <param name="mutation">The state-neutral mutation.</param>
    /// <param name="tick">The simulation tick the mutation lands on.</param>
    /// <param name="preflight">Whether to compose privately rather than install.</param>
    /// <param name="reason">Why the door refused, or empty when it did not.</param>
    /// <returns><see langword="true"/> when the mutation composed (preflight) or installed.</returns>
    bool TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason);
    /// <summary>Opens a private preflight scope: the host remembers its installed state so that every candidate
    /// <see cref="TryApply"/> makes visible under the scope is discarded by the matching
    /// <see cref="EndPreflight"/>. Scopes nest.</summary>
    void BeginPreflight();
    /// <summary>Closes the innermost preflight scope, restoring the state <see cref="BeginPreflight"/> remembered.</summary>
    void EndPreflight();
    /// <summary>Fires an effect arm the library does not own — one a document project registered through its
    /// <see cref="EffectFamily"/>. Under preflight the host validates without acting and answers
    /// <see cref="EffectOutcome.Refused"/> for what could not fire, which fails the enclosing transaction; a refusal is
    /// reported through <see cref="RuleEvaluator.ReportRefusal{TRefusal}(TRefusal, string, EffectFact, ulong, string)"/>
    /// before it is answered.</summary>
    /// <param name="effect">The compiled effect.</param>
    /// <param name="ruleName">The firing rule's name.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="stepTicks">How many ticks the step spans.</param>
    /// <param name="preflight">Whether to validate without acting.</param>
    EffectOutcome FireEffect(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight);
    /// <summary>Evaluates a rule kind only the host understands — a compiled record that widened
    /// <see cref="CompiledRule"/> with a host concept (a pairwise interaction over participants, a decision with its
    /// own timers). The host runs each evaluation through <see cref="RuleEvaluator.EvaluateOnce"/> under the latch
    /// bindings it chooses, so the edge latch, the trace, and the refusal ledger stay one mechanism.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="stepTicks">How many ticks the step spans.</param>
    /// <param name="applied">Whether any effect installed a mutation.</param>
    /// <returns><see langword="true"/> when the host evaluated the rule; <see langword="false"/> hands it to the
    /// library's own forEach-or-once evaluation.</returns>
    bool TryEvaluateOwn(CompiledRule rule, RuleLatch latch, ulong tick, ulong stepTicks, out bool applied);
    /// <summary>Observes the first occurrence of a refusal category in the evaluator's ledger — the one moment a host
    /// narrates it, so a level rule refusing every tick never becomes an unbounded stream. The ledger itself keeps
    /// the exact running count.</summary>
    /// <param name="diagnostic">The category's first entry.</param>
    void RefusalRecorded(in RuleRuntimeDiagnostic diagnostic);
}

/// <summary>What a fired effect did, as the host answers it for the arms it owns.</summary>
public enum EffectOutcome : byte {
    /// <summary>Nothing installed: the effect emitted (a cue, a pose), or could not move its destination.</summary>
    Skipped,
    /// <summary>A mutation installed (or, under preflight, composed).</summary>
    Applied,
    /// <summary>The effect could not fire; under preflight this fails the enclosing transaction.</summary>
    Refused,
}

/// <summary>A state-neutral mutation a firing effect asks its host to run: the four shapes the library's own effects
/// produce. The host maps each onto its own mutation vocabulary, stamped with whatever principal a rule's own act
/// carries there.</summary>
public abstract record StateMutation {
    private StateMutation() { }

    /// <summary>Sets or adds one cell of a row, minting the cell when the row's shape admits it; a text row takes
    /// <see cref="Text"/> and ignores <see cref="Value"/>.</summary>
    /// <param name="Row">The row.</param>
    /// <param name="Key">The cell key, resolved.</param>
    /// <param name="Value">The raw value in the row's encoding.</param>
    /// <param name="Write">Set or add.</param>
    /// <param name="Text">The text payload for a text row, or <see langword="null"/>.</param>
    public sealed record UpsertCell(string Row, string Key, long Value, StateWriteKind Write, string? Text = null) : StateMutation;
    /// <summary>Removes one cell of a keyed row.</summary>
    /// <param name="Row">The row.</param>
    /// <param name="Key">The cell key, resolved.</param>
    public sealed record RemoveCell(string Row, string Key) : StateMutation;
    /// <summary>Advances a generator-backed row by one draw.</summary>
    /// <param name="Row">The row.</param>
    public sealed record Generate(string Row) : StateMutation;
    /// <summary>Applies one atomic state transform.</summary>
    /// <param name="Transform">The transform.</param>
    public sealed record Apply(StateTransform Transform) : StateMutation;
}
