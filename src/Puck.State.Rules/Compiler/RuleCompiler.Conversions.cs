namespace Puck.State.Rules;

public static partial class RuleCompiler {
    // Every typed expression slot and direct copy uses this assignment rule. A conversion token runs through
    // ExpressionArithmetic, including constant folding, so overflow faults instead of wrapping shifted bits.
    private static ExpressionOp? ImplicitConversion(CellKind actual, CellKind required, string ruleName, string verb) {
        if (actual == required) { return null; }
        if (actual == CellKind.Int && required == CellKind.Fixed) { return ExpressionOp.ToFixed; }
        throw new RuleException(
            detail: $"'{verb}' requires kind={StateSpelling.Kind(required)} but found kind={StateSpelling.Kind(actual)}" +
                (actual == CellKind.Fixed && required == CellKind.Int ? "; use toInt(value) for an explicit conversion (truncating toward zero)" : ""),
            refusal: RuleRefusal.EffectSourceKindMismatch,
            ruleName: ruleName
        );
    }

    /// <summary>Compiles a typed operand copy through the expression stack's assignment conversion.</summary>
    /// <param name="operand">The resolved source.</param>
    /// <param name="required">The destination kind.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored operation, for diagnostics.</param>
    /// <returns>The operand or its checked widening program.</returns>
    public static CompiledValueSource ConvertOperand(IRuleOperand operand, CellKind required, string ruleName, string verb) {
        ArgumentNullException.ThrowIfNull(operand);
        if (ImplicitConversion(operand.ValueKind, required, ruleName, verb) is not { } conversion) {
            return CompiledValueSource.FromOperand(operand);
        }
        var tokens = new List<CompiledExpressionToken>();
        RuleExpressions.AppendFeeds(operand: operand, into: tokens);
        tokens.Add(new CompiledExpressionToken(ExpressionOp.Operand, Operand: operand, CarrierKind: operand.ValueKind));
        tokens.Add(new CompiledExpressionToken(conversion, CarrierKind: operand.ValueKind));
        return CompiledValueSource.FromExpression(FoldConstants(tokens.ToArray(), required), required);
    }

    private static CompiledExpressionToken[] ConvertExpression(CompiledExpressionToken[] tokens, CellKind actual, CellKind required, string ruleName, string verb) =>
        ImplicitConversion(actual, required, ruleName, verb) is { } conversion
            ? FoldConstants([.. tokens, new CompiledExpressionToken(conversion, CarrierKind: actual)], required)
            : tokens;
}
