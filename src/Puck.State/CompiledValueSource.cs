namespace Puck.State;

/// <summary>
/// A compiled value source representing a literal constant, a live operand, or a compiled expression program.
/// Provides a unified interface for dependency collection, cost accounting, and live fact evaluation.
/// </summary>
public readonly record struct CompiledValueSource {
    /// <summary>The compiled numeric expression program, or <see langword="null"/>.</summary>
    public CompiledExpressionToken[]? Expression { get; }
    /// <summary>The live copy-source operand, or <see langword="null"/>.</summary>
    public OperandFact? Operand { get; }
    /// <summary>The raw authored constant, pre-converted to destination encoding.</summary>
    public long RawValue { get; }

    /// <summary>Initializes a value source from its constituent components.</summary>
    public CompiledValueSource(long rawValue = 0L, OperandFact? operand = null, CompiledExpressionToken[]? expression = null) {
        RawValue = rawValue;
        Operand = operand;
        Expression = expression;
    }

    /// <summary>Constructs a literal constant value source.</summary>
    public static CompiledValueSource Constant(long rawValue) => new(rawValue: rawValue);
    /// <summary>Constructs an operand value source.</summary>
    public static CompiledValueSource FromOperand(OperandFact operand) => new(operand: operand);
    /// <summary>Constructs an expression value source.</summary>
    public static CompiledValueSource FromExpression(CompiledExpressionToken[] expression) => new(expression: expression);

    /// <summary>Gets a value indicating whether this source is a literal constant.</summary>
    public bool IsLiteral => ((Operand is null) && (Expression is null));
    /// <summary>Gets a value indicating whether this source is a live operand.</summary>
    public bool IsOperand => (Operand is not null);
    /// <summary>Gets a value indicating whether this source is a compiled expression.</summary>
    public bool IsExpression => (Expression is not null);

    /// <summary>Gets a value indicating whether this source reads host-only state.</summary>
    public bool ReadsHost => ((Operand is { HostOnly: true }) || RuleDataflow.ExpressionReadsHost(tokens: Expression));

    /// <summary>Appends every state cell this source reads.</summary>
    public void CollectReads(List<RuleAccess> into) {
        Operand?.CollectReads(into: into);
        if (Expression is { } expression) {
            RuleDataflow.CollectExpression(
                into: into,
                tokens: expression
            );
        }
    }

    /// <summary>Computes the conservative work cost of this value source.</summary>
    public long Cost(RuleCompileContext context, CellKind kind = CellKind.Fixed) {
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

    /// <summary>Reads this value source for the evaluation in flight.</summary>
    public bool TryRead(IRuleReader reader, CellKind kind, ulong tick, ulong engineTick, out RuleFact fact, out ExpressionFault fault) {
        fault = ExpressionFault.None;
        if (Expression is { } expression) {
            var ok = RuleEvaluation.TryEvaluateExpression(
                fault: out fault,
                kind: kind,
                program: expression,
                reader: reader,
                value: out var exprRaw
            );
            fact = (ok
                ? RuleFact.Finite(
                    kind: kind,
                    value: exprRaw
                )
                : default
            );
            return ok;
        }
        if (Operand is { } operand) {
            fact = operand.Read(reader: reader);
            return true;
        }
        fact = RuleFact.Finite(
            kind: kind,
            value: RawValue
        );
        return true;
    }
}
