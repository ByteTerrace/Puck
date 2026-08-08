using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The null-pattern normalizer (the `null-pattern` pass): rewrites equality/inequality against the `null`
// literal into the pattern form the house style requires — `x is null` / `x is not null` — whichever
// side the literal sits on (`x == null`, `null != x`). The non-null operand becomes the pattern subject
// (its inner trivia preserved); the node's own outer trivia is carried across. Comparisons with no
// `null` literal are untouched, and an existing is-pattern is not a binary expression, so the pass is
// idempotent.
internal sealed class NullPatternRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node) {
        var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node: node)!;
        var kind = visited.Kind();

        if (kind is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)) {
            return visited;
        }

        var leftIsNull = visited.Left.IsKind(kind: SyntaxKind.NullLiteralExpression);
        var rightIsNull = visited.Right.IsKind(kind: SyntaxKind.NullLiteralExpression);

        if (leftIsNull == rightIsNull) {
            return visited;
        }

        var subject = (rightIsNull ? visited.Left : visited.Right);
        PatternSyntax pattern = SyntaxFactory.ConstantPattern(expression: SyntaxFactory.LiteralExpression(kind: SyntaxKind.NullLiteralExpression));

        if (kind is SyntaxKind.NotEqualsExpression) {
            pattern = SyntaxFactory.UnaryPattern(
                operatorToken: SyntaxFactory.Token(kind: SyntaxKind.NotKeyword).WithTrailingTrivia(trivia: SyntaxFactory.Space),
                pattern: pattern);
        }

        var isToken = SyntaxFactory.Token(kind: SyntaxKind.IsKeyword)
            .WithLeadingTrivia(trivia: SyntaxFactory.Space)
            .WithTrailingTrivia(trivia: SyntaxFactory.Space);

        return SyntaxFactory.IsPatternExpression(expression: subject.WithoutTrivia(), isKeyword: isToken, pattern: pattern)
            .WithLeadingTrivia(trivia: visited.GetLeadingTrivia())
            .WithTrailingTrivia(trivia: visited.GetTrailingTrivia());
    }
}
