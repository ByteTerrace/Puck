namespace Puck.State;

/// <summary>The runtime refusals a rule firing draws: the evaluator's own, and the vector transforms' data-dependent
/// ones. Every member is tagged <see cref="RefusalAttribute"/> under the <c>state.rule.fire</c> door. A document
/// project's own effect arms declare their own tagged enum and report through the same ledger.</summary>
public enum RuleEffectRefusal : byte {
    /// <summary>A binding, a gate conjunct's expression, or an effect's value source overflowed, divided by zero,
    /// left a function's domain, read a fact with no number, or read a key that names no cell. A gate conjunct that
    /// compares a <c>forever</c> operand directly is not a refusal: it answers under positive-infinity
    /// semantics.</summary>
    [Refusal(door: "state.rule.fire", condition: "a rule expression overflows, divides by zero, leaves a function's domain, or reads a fact with no number or no cell", kind: RefusalKind.Verdict)]
    Arithmetic,

    /// <summary>An effect's mutation was refused by the arena's admission door or by the host's own door, which
    /// rewinds the whole firing.</summary>
    [Refusal(door: "state.rule.fire", condition: "a rule-produced mutation is refused by the arena's admission door or the host's own door", kind: RefusalKind.Verdict)]
    MutationRejected,

    /// <summary>An irreversible arm refused after the firing's scope committed. The arena stays committed and the
    /// arms that already fired are never replayed.</summary>
    [Refusal(door: "state.rule.fire", condition: "an irreversible arm refuses after the firing's scope committed", kind: RefusalKind.Verdict)]
    IrreversibleArmFailed,

    /// <summary>A fixpoint group ran its whole pass ceiling without a pass leaving its write set unchanged. The
    /// group stops running until its trigger re-arms it.</summary>
    [Refusal(door: "state.rule.fire", condition: "a fixpoint group breaches its pass ceiling without closing", kind: RefusalKind.Verdict)]
    GroupPassCeiling,

    /// <summary>A firing's writes grew the arena's undo record past <c>ArenaCapacity.MaxJournalBytes</c>. The
    /// firing rewinds, so nothing it wrote remains.</summary>
    [Refusal(door: "state.rule.fire", condition: "a firing's writes grow the arena's undo record past its byte ceiling", kind: RefusalKind.Verdict)]
    JournalCeiling,

    /// <summary>A vector <c>mix</c>'s weighted sum is the zero vector, which has no direction to normalize.</summary>
    [Refusal(door: "state.rule.fire", condition: "a vector mix's weighted sum of terms is zero", kind: RefusalKind.Verdict)]
    VectorMixZero,

    /// <summary>A vector <c>mean</c> admits no candidate cell, or its candidates sum to the zero vector.</summary>
    [Refusal(door: "state.rule.fire", condition: "a vector mean admits no candidate, or its candidates sum to zero", kind: RefusalKind.Verdict)]
    VectorMeanEmpty,
}
