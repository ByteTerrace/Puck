namespace Puck.State.Rules;

/// <summary>The one way a gate, a binding, or an effect reads a value: a literal already converted to the
/// destination's raw encoding, a live operand, or a compiled expression program. <c>compareState</c> and
/// <c>compareValue</c> both lower through this, so the two spellings are one comparison.</summary>
/// <param name="RawValue">The authored constant in the destination's raw encoding, read only when neither
/// <paramref name="Operand"/> nor <paramref name="Expression"/> applies.</param>
/// <param name="Operand">The live operand, or <see langword="null"/>.</param>
/// <param name="Expression">The compiled postfix program, or <see langword="null"/>.</param>
/// <param name="Text">The literal text of a Text comparison, or null for the numeric and operand forms.</param>
/// <param name="CarrierKind">The expression's arithmetic kind when it differs from the result kind.</param>
public readonly record struct CompiledValueSource(long RawValue = 0L, IRuleOperand? Operand = null, CompiledExpressionToken[]? Expression = null, string? Text = null, CellKind? CarrierKind = null) {
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
    /// <summary>Creates the source a compiled program reads as: the literal a program the constant folder reduced to
    /// one constant answers, the operand a program that is one plain read answers, or the program itself. A value's
    /// program and price therefore depend on what it means, never on whether it was spelled as an expression.</summary>
    /// <param name="expression">The compiled postfix program.</param>
    /// <param name="kind">The kind the source is read in, <see cref="RuleEvaluation.ReadSource"/>'s: a Bool
    /// destination's program computes in Int and lands as 0 or 1.</param>
    /// <param name="carrierKind">The arithmetic kind, or null to use the destination kind.</param>
    /// <returns>The source.</returns>
    /// <remarks>Each canonical form answers what <see cref="RuleEvaluation.ReadSource"/> answers for the program: a
    /// constant program its constant (Bool-normalized), and a lone operand read in its own kind with no computed
    /// address its fact. A fact read directly keeps a fact's semantics (a forever fact compares as infinity, and a
    /// copy of one skips), as <c>compareState</c> and <c>fromState</c> read it.</remarks>
    public static CompiledValueSource FromExpression(CompiledExpressionToken[] expression, CellKind kind, CellKind? carrierKind = null) {
        ArgumentNullException.ThrowIfNull(argument: expression);

        if (expression is [{ Operation: ExpressionOp.Constant } constant]) {
            return Constant(rawValue: ((kind == CellKind.Bool)
                ? ((constant.Constant != 0L) ? 1L : 0L)
                : constant.Constant));
        }
        if (
            (expression is [{ Operation: ExpressionOp.Operand, Operand: { } operand, Fold: null, Call: null }]) &&
            (operand.ValueKind == kind) &&
            ((carrierKind ?? kind) == kind) &&
            (RuleExpressions.FeedCount(operand: operand) == 0)
        ) {
            return FromOperand(operand: operand);
        }

        return new(Expression: expression, CarrierKind: carrierKind);
    }
    /// <summary>Creates an operand value source; a rule local bound to a compile-time constant is that constant.</summary>
    /// <param name="operand">The live operand.</param>
    /// <returns>The source.</returns>
    public static CompiledValueSource FromOperand(IRuleOperand operand) => (((operand is LocalOperand local) && local.TryConstant(raw: out var raw))
        ? Constant(rawValue: raw)
        : new(Operand: operand));
    /// <summary>The value this source declares for an arm that fires outside the arena and reads its values before it
    /// runs (<see cref="IRuleEffect.Sources"/>), or null for a literal, which the arm reads as
    /// <see cref="RawValue"/>.</summary>
    /// <param name="kind">The encoding the arm reads the value in.</param>
    /// <returns>The declared source, or <see langword="null"/>.</returns>
    public EffectSource? AsEffectSource(CellKind kind) => ((Operand is { } operand)
        ? EffectSource.FromOperand(kind: kind, operand: operand)
        : ((Expression is { } program)
            ? EffectSource.Expression(kind: kind, program: program)
            : null));
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
            // A read inside a program prices the tokens that compute an address beside the operand's own; a source
            // that is an operand alone prices the same programs, since it runs them to read.
            var cost = operand.Cost(context: context);

            foreach (var address in operand.Addresses) {
                if (address.Feed is { } feed) {
                    cost += RuleWorkBudget.ExpressionCost(
                        context: context,
                        kind: CellKind.Int,
                        tokens: feed
                    );
                }
            }

            return cost;
        }
        if (Expression is { } expression) {
            return RuleWorkBudget.ExpressionCost(
                context: context,
                kind: (CarrierKind ?? kind),
                tokens: expression
            );
        }

        return 0L;
    }
}
