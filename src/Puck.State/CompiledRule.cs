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
public readonly record struct GateToken {
    /// <summary>The left-hand value source (literal, operand, or expression).</summary>
    public CompiledValueSource LeftSource { get; }
    /// <summary>The comparison to apply.</summary>
    public ActionStateComparison Comparison { get; }
    /// <summary>The right-hand value source (literal, operand, or expression).</summary>
    public CompiledValueSource RightSource { get; }
    /// <summary>The encoding carried by the comparison.</summary>
    public CellKind ValueKind { get; }
    /// <summary>The authored spelling of this conjunct.</summary>
    public string Describe { get; }
    /// <summary>The postfix Boolean operation.</summary>
    public GateOp Op { get; }
    /// <summary>The number of preceding results consumed by an <c>all</c> or <c>any</c> token.</summary>
    public int Arity { get; }

    /// <summary>The primary operand, or <see langword="null"/>.</summary>
    public OperandFact? Left => LeftSource.Operand;
    /// <summary>The comparand operand, or <see langword="null"/>.</summary>
    public OperandFact? Comparand => RightSource.Operand;
    /// <summary>The authored constant comparand.</summary>
    public long Value => RightSource.RawValue;
    /// <summary>Left postfix expression for compareValue; null for an ordinary comparison.</summary>
    public CompiledExpressionToken[]? LeftExpression => LeftSource.Expression;
    /// <summary>Right postfix expression for compareValue.</summary>
    public CompiledExpressionToken[]? RightExpression => RightSource.Expression;

    /// <summary>Initializes a gate token from unified value sources.</summary>
    /// <param name="leftSource">The left-hand value source.</param>
    /// <param name="comparison">The comparison to apply.</param>
    /// <param name="rightSource">The right-hand value source.</param>
    /// <param name="valueKind">The encoding carried by the comparison.</param>
    /// <param name="describe">The authored spelling of this conjunct.</param>
    /// <param name="op">The postfix Boolean operation.</param>
    /// <param name="arity">The number of preceding results consumed by an <c>all</c> or <c>any</c> token.</param>
    public GateToken(
        CompiledValueSource leftSource,
        ActionStateComparison comparison,
        CompiledValueSource rightSource,
        CellKind valueKind,
        string describe,
        GateOp op = GateOp.Compare,
        int arity = 0
    ) {
        LeftSource = leftSource;
        Comparison = comparison;
        RightSource = rightSource;
        ValueKind = valueKind;
        Describe = describe;
        Op = op;
        Arity = arity;
    }

    /// <summary>Initializes a gate token from constituent operands and expressions.</summary>
    /// <param name="Left">The primary operand.</param>
    /// <param name="Comparison">The comparison to apply.</param>
    /// <param name="Value">The authored constant comparand.</param>
    /// <param name="ValueKind">The encoding carried by the comparison.</param>
    /// <param name="Comparand">The comparand operand.</param>
    /// <param name="Describe">The authored spelling.</param>
    /// <param name="Op">The postfix Boolean operation.</param>
    /// <param name="Arity">The arity.</param>
    /// <param name="LeftExpression">The left expression program.</param>
    /// <param name="RightExpression">The right expression program.</param>
    public GateToken(
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
    ) : this(
        leftSource: ((LeftExpression is not null)
            ? CompiledValueSource.FromExpression(expression: LeftExpression)
            : ((Left is not null)
                ? CompiledValueSource.FromOperand(operand: Left)
                : default)),
        comparison: Comparison,
        rightSource: ((RightExpression is not null)
            ? CompiledValueSource.FromExpression(expression: RightExpression)
            : ((Comparand is not null)
                ? CompiledValueSource.FromOperand(operand: Comparand)
                : CompiledValueSource.Constant(rawValue: Value))),
        valueKind: ValueKind,
        describe: Describe,
        op: Op,
        arity: Arity
    ) {
    }
}
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
/// <param name="ForEachHandle">The pre-resolved handle of <paramref name="ForEach"/>, or <see langword="default"/>.</param>
public record CompiledRule(string Name, ActionTriggerMode Mode, GateToken[] Gate, EffectFact[] Effects, string? ForEach = null, CompiledRuleBinding[]? Bindings = null, ZoneTable? Zones = null, StateHandle ForEachHandle = default) {
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

        RuleDataflow.CollectGate(
            gate: Gate,
            into: into
        );
        foreach (var binding in (Bindings ?? [])) {
            RuleDataflow.CollectExpression(
                tokens: binding.Expression,
                into: into
            );
        }
        RuleDataflow.CollectEffectReads(
            effects: Effects,
            into: into
        );
    }
    /// <summary>Appends every state cell one evaluation writes.</summary>
    /// <param name="into">The write set being collected.</param>
    public virtual void CollectWrites(List<RuleAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        RuleDataflow.CollectEffectWrites(
            effects: Effects,
            into: into
        );
    }
    /// <summary>Returns the conservative work units one evaluation costs: one for the visit, plus the gate, the
    /// bindings, and the effects.</summary>
    /// <param name="context">The compile context the rule was resolved against.</param>
    public virtual long Cost(RuleCompileContext context) => CostBreakdown(context: context).Total;
    /// <summary>Returns the orthogonal cost components: per-rule setup, per-evaluation check, and per-firing effects.</summary>
    /// <param name="context">The compile context the rule was resolved against.</param>
    public virtual RuleCost CostBreakdown(RuleCompileContext context) {
        var check = RuleWorkBudget.SaturatingAdd(
            left: 1L,
            right: RuleWorkBudget.GateCost(
                tokens: Gate,
                context: context
            )
        );

        foreach (var binding in (Bindings ?? [])) {
            check = RuleWorkBudget.SaturatingAdd(
                left: check,
                right: RuleWorkBudget.ExpressionCost(
                    tokens: binding.Expression,
                    kind: binding.Kind,
                    context: context
                )
            );
        }

        var effects = RuleWorkBudget.EffectsCost(
            effects: Effects,
            context: context
        );

        return new RuleCost(
            Check: check,
            Effects: effects,
            Setup: 0L
        );
    }
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

            RuleDataflow.CollectExpression(
                tokens: Expression,
                into: reads
            );
            m_schedule = RuleSchedule.Build(
                reads: reads,
                volatileBase: (RuleDataflow.ExpressionReadsHost(tokens: Expression) || RuleDataflow.ExpressionReadsTick(tokens: Expression)),
                reader: reader
            );
        }

        return m_schedule;
    }
}
