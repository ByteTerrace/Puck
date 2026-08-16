using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The `paren-clarity` pass: wraps comparison / arithmetic / shift / bitwise / is-pattern expressions,
// ternaries, and casts in their own parentheses for explicit precedence (`((0 == a) || (0 == b))`,
// `var x = (a + b);`, `return (cond ? p : q);`, `MaxSets: ((uint)sets.Length)`). Left bare only where
// already delimited: inside existing parens, as the sole condition of if/while/do/switch/lock, and — for
// casts — as the operand of checked/unchecked (whose own parentheses delimit) or as a ternary branch
// (the ?/: operators delimit). Same-operator chains (`a || b || c`) are not re-nested — only leaf
// operands get wrapped — and unary operators are left alone. Purely syntactic and idempotent.
internal sealed class ParenClarityRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node) =>
        MaybeWrap(original: node, visited: ((ExpressionSyntax)base.VisitBinaryExpression(node: node)!));
    public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node) =>
        MaybeWrap(original: node, visited: ((ExpressionSyntax)base.VisitConditionalExpression(node: node)!));
    public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node) =>
        MaybeWrap(original: node, visited: ((ExpressionSyntax)base.VisitIsPatternExpression(node: node)!));
    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node) =>
        MaybeWrap(original: node, visited: ((ExpressionSyntax)base.VisitCastExpression(node: node)!));
    // Flag-combining bitwise (`a | b`) is idiomatic bare in value position — the gold standard wraps it
    // only inside a comparison (precedence vs ==/!=). Drop a redundant bitwise paren wherever the
    // surrounding construct binds looser than the operator (arguments, initializers, returns,
    // assignments, ternary arms), so the pass settles on the gold shape regardless of any extra parens
    // already present.
    public override SyntaxNode? VisitParenthesizedExpression(ParenthesizedExpressionSyntax node) {
        var visited = ((ParenthesizedExpressionSyntax)base.VisitParenthesizedExpression(node: node)!);

        if ((visited.Expression is BinaryExpressionSyntax inner)
            && IsBitwise(kind: inner.Kind())
            && IsLooseContext(parent: node.Parent)) {
            return inner.WithLeadingTrivia(trivia: visited.GetLeadingTrivia()).WithTrailingTrivia(trivia: visited.GetTrailingTrivia());
        }

        return visited;
    }

    private static ExpressionSyntax MaybeWrap(ExpressionSyntax original, ExpressionSyntax visited) {
        if (!NeedsParens(node: original)) {
            return visited;
        }

        var inner = visited.WithoutLeadingTrivia().WithoutTrailingTrivia();

        return SyntaxFactory.ParenthesizedExpression(expression: inner)
            .WithLeadingTrivia(trivia: visited.GetLeadingTrivia())
            .WithTrailingTrivia(trivia: visited.GetTrailingTrivia());
    }
    // Decided on the ORIGINAL node, so the parent is the real (pre-rewrite) context.
    private static bool NeedsParens(ExpressionSyntax node) {
        var parent = node.Parent;

        if (parent is null or ParenthesizedExpressionSyntax) {
            return false;
        }

        // Statement/expression slots whose own keyword parentheses already delimit.
        if (((parent is IfStatementSyntax ifStatement) && (ifStatement.Condition == node))
            || ((parent is WhileStatementSyntax whileStatement) && (whileStatement.Condition == node))
            || ((parent is DoStatementSyntax doStatement) && (doStatement.Condition == node))
            || ((parent is SwitchStatementSyntax switchStatement) && (switchStatement.Expression == node))
            || ((parent is LockStatementSyntax lockStatement) && (lockStatement.Expression == node))) {
            return false;
        }

        // A cast wraps everywhere a binary would, with two extra bare spots the gold standard keeps:
        // the operand of checked/unchecked (its own parentheses already delimit) and a ternary BRANCH
        // (the ?/: operators delimit; a ternary CONDITION still wraps).
        if (node is CastExpressionSyntax) {
            return ((parent is not CheckedExpressionSyntax)
                && ((parent is not ConditionalExpressionSyntax conditional) || (conditional.Condition == node)));
        }

        if (node is BinaryExpressionSyntax binary) {
            var kind = binary.Kind();

            // A logical operand of the SAME operator is left bare so `a || b || c` keeps a single flat
            // group instead of re-nesting (its leaf operands still wrap).
            if (kind is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression) {
                return ((parent is not BinaryExpressionSyntax parentBinary) || (parentBinary.Kind() != kind));
            }

            // Flag-combining bitwise gets clarity parens only where precedence against a comparison is
            // genuinely confusing (an operand of ==/!=/</> ...), matching the gold standard's bare
            // `a | b` in plain value position.
            if (IsBitwise(kind: kind)) {
                return ((parent is BinaryExpressionSyntax comparison) && IsComparison(kind: comparison.Kind()));
            }
        }

        return true;
    }
    private static bool IsBitwise(SyntaxKind kind) => (kind
        is SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression or SyntaxKind.ExclusiveOrExpression);
    private static bool IsComparison(SyntaxKind kind) => (kind
        is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or SyntaxKind.LessThanExpression
        or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression);
    // Constructs that bind looser than a bitwise operator, so wrapping its result adds nothing: a
    // redundant bitwise paren in any of these can be dropped safely.
    private static bool IsLooseContext(SyntaxNode? parent) => (parent is ArgumentSyntax
        or AttributeArgumentSyntax or EqualsValueClauseSyntax or ReturnStatementSyntax
        or ArrowExpressionClauseSyntax or AssignmentExpressionSyntax or ConditionalExpressionSyntax
        or InitializerExpressionSyntax or ExpressionStatementSyntax);
}
