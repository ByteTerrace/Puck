namespace Puck.State;

/// <summary>One operation in a compiled postfix Boolean gate.</summary>
public enum GateOp : byte {
    /// <summary>Evaluate one comparison.</summary>
    Compare,
    /// <summary>Conjoin <see cref="GateToken.Arity"/> preceding results.</summary>
    All,
    /// <summary>Disjoin <see cref="GateToken.Arity"/> preceding results.</summary>
    Any,
    /// <summary>Invert the preceding result.</summary>
    Not,
}
/// <summary>One token in a compiled postfix rule gate. The representation preserves arbitrary nested
/// <see cref="ActionPredicate.All"/>/<see cref="ActionPredicate.Any"/>/<see cref="ActionPredicate.Not"/> trees while
/// evaluation remains a single bounded, allocation-free pass.</summary>
/// <param name="Left">The primary operand — the <c>(State, Key)</c> side of the authored <c>compareState</c>. Set
/// only for a <see cref="GateOp.Compare"/> token spelled as an ordinary comparison; <see langword="null"/> for a
/// logical token (which reads no operand) and for a <c>compareValue</c> token (which reads
/// <paramref name="LeftExpression"/>/<paramref name="RightExpression"/> instead).</param>
/// <param name="Comparison">The comparison to apply.</param>
/// <param name="Value">The authored constant comparand, converted directly from its exact decimal token to the
/// left operand's raw cell encoding at compile time — read only when <paramref name="Comparand"/> is
/// <see langword="null"/> (the constant spelling).</param>
/// <param name="ValueKind">The encoding carried by <paramref name="Value"/>.</param>
/// <param name="Comparand">The comparand operand — another row/reserved channel read live on the same terms as
/// <paramref name="Left"/> (the <c>(ComparandState, ComparandKey)</c> spelling) — or <see langword="null"/> when the
/// comparand is the authored constant <paramref name="Value"/> instead.</param>
/// <param name="Describe">The authored spelling of this conjunct, for the rules read-back — an
/// <see cref="ActionPredicate.All"/> gate prints its predicates rather than a type name, which is the whole point of
/// keeping the text beside the compiled form.</param>
/// <param name="Op">The postfix Boolean operation.</param>
/// <param name="Arity">The number of preceding results consumed by an <c>all</c> or <c>any</c> token.</param>
/// <param name="LeftExpression">Left postfix expression for compareValue; null for an ordinary comparison.</param>
/// <param name="RightExpression">Right postfix expression for compareValue.</param>
public readonly record struct GateToken(
    OperandFact? Left,
    ActionStateComparison Comparison,
    long Value,
    CellKind ValueKind,
    OperandFact? Comparand,
    string Describe,
    GateOp Op = GateOp.Compare,
    int Arity = 0,
    CompiledExpressionToken[]? LeftExpression = null,
    CompiledExpressionToken[]? RightExpression = null
);
/// <summary>One token in an allocation-free postfix numeric expression.</summary>
/// <param name="Operation">The stack operation.</param>
/// <param name="Constant">The raw destination-kind literal for a constant token.</param>
/// <param name="Operand">The live operand for an operand token.</param>
/// <param name="Board">The compiled topology and direction of a <see cref="ExpressionOp.BoardShift"/>, <see cref="ExpressionOp.BoardFill"/>, or <see cref="ExpressionOp.BoardImage"/> token.</param>
public readonly record struct CompiledExpressionToken(ExpressionOp Operation, long Constant = 0L, OperandFact? Operand = null, BoardQuery? Board = null);
/// <summary>One compiled rule: its name, its mode, the flattened gate, and the compiled effects. A document project
/// derives its own record to carry what only it compiles beside these.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="Mode">Level or edge (see <see cref="ActionTriggerMode"/>).</param>
/// <param name="Gate">The flattened postfix Boolean program; empty means "always".</param>
/// <param name="Effects">The compiled effects, in authored order.</param>
/// <param name="ForEach">The keyed row a rule iterates (<see cref="Rule.ForEach"/>), or <see langword="null"/>.</param>
/// <param name="Bindings">The compiled per-evaluation bindings, in declared order.</param>
/// <param name="Zones">The compiled <see cref="Rule.Zones"/> table, or <see langword="null"/>.</param>
public record CompiledRule(string Name, ActionTriggerMode Mode, GateToken[] Gate, EffectFact[] Effects, string? ForEach = null, CompiledRuleBinding[]? Bindings = null, ZoneTable? Zones = null) {
    private RuleSchedule? m_schedule;

    /// <summary>Gets the rule's memoized read schedule — its gate's and bindings' distinct row reads, and whether
    /// they carry a host or tick dependency — built once against <paramref name="reader"/> from the fully
    /// constructed rule (so a document project's own <see cref="CollectReads"/> override is included) and cached for
    /// the rule's lifetime.</summary>
    /// <param name="reader">The evaluation in flight, for resolving row names and traits.</param>
    internal RuleSchedule Schedule(IRuleReader reader) => (m_schedule ??= RuleSchedule.Build(
        reads: RuleDataflow.Reads(rule: this),
        volatileBase: (RuleDataflow.ReadsHost(rule: this) || RuleDataflow.ReadsTick(rule: this)),
        reader: reader
    ));

    /// <summary>Appends every state cell one evaluation reads: the gate, the bindings, and the effects. A document
    /// project's rule appends the branches it alone carries.</summary>
    /// <param name="into">The read set being collected.</param>
    public virtual void CollectReads(List<RuleAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        RuleDataflow.CollectGate(gate: Gate, into: into);
        foreach (var binding in (Bindings ?? [])) {
            RuleDataflow.CollectExpression(tokens: binding.Expression, into: into);
        }
        RuleDataflow.CollectEffectReads(effects: Effects, into: into);
    }

    /// <summary>Appends every state cell one evaluation writes.</summary>
    /// <param name="into">The write set being collected.</param>
    public virtual void CollectWrites(List<RuleAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        RuleDataflow.CollectEffectWrites(effects: Effects, into: into);
    }

    /// <summary>Returns the orthogonal cost components: per-rule setup, per-evaluation check, and per-firing effects.</summary>
    /// <param name="context">The compile context the rule was resolved against.</param>
    public virtual RuleCost CostBreakdown(RuleCompileContext context) {
        var check = RuleWorkBudget.SaturatingAdd(left: 1L, right: RuleWorkBudget.GateCost(tokens: Gate, context: context));

        foreach (var binding in (Bindings ?? [])) {
            check = RuleWorkBudget.SaturatingAdd(left: check, right: RuleWorkBudget.ExpressionCost(tokens: binding.Expression, kind: binding.Kind, context: context));
        }

        var effects = RuleWorkBudget.EffectsCost(effects: Effects, context: context);

        return new RuleCost(Setup: 0L, Check: check, Effects: effects);
    }

    /// <summary>Returns the conservative work units one evaluation costs: one for the visit, plus the gate, the
    /// bindings, and the effects.</summary>
    /// <param name="context">The compile context the rule was resolved against.</param>
    public virtual long Cost(RuleCompileContext context) => CostBreakdown(context: context).Total;
}
/// <summary>One compiled <see cref="RuleBinding"/>: its ordinal is its slot in the evaluation's bound-value
/// scratch, and its expression may read only bindings with a smaller ordinal.</summary>
/// <param name="Name">The authored name.</param>
/// <param name="Kind">The value kind the expression was compiled in.</param>
/// <param name="Expression">The compiled postfix program.</param>
public sealed record CompiledRuleBinding(string Name, CellKind Kind, CompiledExpressionToken[] Expression) {
    private RuleSchedule? m_schedule;

    /// <summary>Gets the binding's own memoized read schedule — its expression's distinct row reads, and whether
    /// they carry a host or tick dependency — built once against <paramref name="reader"/> and cached for the
    /// binding's lifetime.</summary>
    /// <param name="reader">The evaluation in flight, for resolving row names and traits.</param>
    internal RuleSchedule Schedule(IRuleReader reader) {
        if (m_schedule is null) {
            var reads = new List<RuleAccess>();

            RuleDataflow.CollectExpression(tokens: Expression, into: reads);
            m_schedule = RuleSchedule.Build(
                reads: reads,
                volatileBase: (RuleDataflow.ExpressionReadsHost(tokens: Expression) || RuleDataflow.ExpressionReadsTick(tokens: Expression)),
                reader: reader
            );
        }

        return m_schedule;
    }
}
