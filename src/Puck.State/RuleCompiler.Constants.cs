namespace Puck.State;

public static partial class RuleCompiler {
    // Validation precedes folding: every authored operand, kind, and token still has to be legal. Only a
    // successful reader-free subtree becomes a raw constant. A domain refusal stays in the program, including
    // an unselected ternary branch: expressions are eager, so folding must not hide that refusal.
    private static CompiledExpressionToken[] FoldConstants(CompiledExpressionToken[] tokens, CellKind kind) {
        Span<int> starts = stackalloc int[RuleCapacity.MaxExpressionTokens];
        Span<bool> constants = stackalloc bool[RuleCapacity.MaxExpressionTokens];
        var output = new CompiledExpressionToken[tokens.Length];
        var count = 0;
        var depth = 0;
        foreach (var token in tokens) {
            var arity = token.Operation switch {
                ExpressionOp.Constant or ExpressionOp.Operand => 0,
                ExpressionOp.Clamp or ExpressionOp.Select or ExpressionOp.BitField => 3,
                ExpressionOp.BitInsert => 4,
                ExpressionOp.BoardShift or ExpressionOp.BoardFill or ExpressionOp.BoardImage => 1,
                _ => ExpressionArithmetic.FunctionArity(token.Operation) is > 0 and var functionArity
                    ? functionArity : ExpressionArithmetic.IsUnary(token.Operation) ? 1 : 2,
            };
            var start = arity == 0 ? count : starts[depth - arity];
            var constant = token.Operation != ExpressionOp.Operand;
            for (var argument = depth - arity; argument < depth; argument++) { constant &= constants[argument]; }
            depth -= arity;
            output[count++] = token;
            if (constant && arity != 0) {
                constant = RuleEvaluation.TryEvaluateConstantExpression(output.AsSpan(start, count - start), kind, out var value);
                if (constant) {
                    count = start;
                    output[count++] = new CompiledExpressionToken(Operation: ExpressionOp.Constant, Constant: value);
                }
            }
            starts[depth] = start;
            constants[depth++] = constant;
        }
        return count == tokens.Length ? tokens : output[..count];
    }
}
