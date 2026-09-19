namespace Puck.State.Rules;

/// <summary>The one way a gate, a binding, or an effect reads a value: a literal already converted to the
/// destination's raw encoding, a live operand, or a compiled expression program. <c>compareState</c> and
/// <c>compareValue</c> both lower through this, so the two spellings are one comparison.</summary>
/// <param name="RawValue">The authored constant in the destination's raw encoding, read only when neither
/// <paramref name="Operand"/> nor <paramref name="Expression"/> applies.</param>
/// <param name="Operand">The live operand, or <see langword="null"/>.</param>
/// <param name="Expression">The compiled postfix program, or <see langword="null"/>.</param>
public readonly record struct CompiledValueSource(long RawValue = 0L, IRuleOperand? Operand = null, CompiledExpressionToken[]? Expression = null) {
    /// <summary>Gets a value indicating whether this source is a compiled expression.</summary>
    public bool IsExpression => (Expression is not null);
    /// <summary>Gets a value indicating whether this source is a literal constant.</summary>
    public bool IsLiteral => ((Operand is null) && (Expression is null));
    /// <summary>Gets a value indicating whether this source is a live operand.</summary>
    public bool IsOperand => (Operand is not null);

    /// <summary>Creates a literal constant value source.</summary>
    /// <param name="rawValue">The raw value in the destination's encoding.</param>
    /// <returns>The source.</returns>
    public static CompiledValueSource Constant(long rawValue) => new(RawValue: rawValue);
    /// <summary>Creates an expression value source.</summary>
    /// <param name="expression">The compiled postfix program.</param>
    /// <returns>The source.</returns>
    public static CompiledValueSource FromExpression(CompiledExpressionToken[] expression) => new(Expression: expression);
    /// <summary>Creates an operand value source.</summary>
    /// <param name="operand">The live operand.</param>
    /// <returns>The source.</returns>
    public static CompiledValueSource FromOperand(IRuleOperand operand) => new(Operand: operand);
    /// <summary>Appends every state cell this source reads.</summary>
    /// <param name="into">The read set being collected.</param>
    public void CollectReads(List<CellAccess> into) {
        Operand?.CollectReads(into: into);
        if (Expression is { } expression) {
            RuleDataflow.CollectExpression(
                into: into,
                tokens: expression
            );
        }
    }
    /// <summary>Appends every fact this source carries, so a rule's needs fold the whole source.</summary>
    /// <param name="into">The needs being built.</param>
    public void CollectFacts(RuleNeedsBuilder into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.AddFact(fact: Operand);
        RuleDataflow.CollectExpressionFacts(
            into: into,
            tokens: Expression
        );
    }
    /// <summary>Returns the conservative work units one read of this source costs.</summary>
    /// <param name="context">The context the source was priced against.</param>
    /// <param name="kind">The kind an expression source is evaluated in.</param>
    /// <returns>The work units.</returns>
    public RuleWork Cost(IRuleCostContext context, CellKind kind = CellKind.Fixed) {
        if (Operand is { } operand) {
            return operand.Cost(context: context);
        }
        if (Expression is { } expression) {
            return RuleWorkBudget.ExpressionCost(
                context: context,
                kind: kind,
                tokens: expression
            );
        }

        return 0L;
    }
}
