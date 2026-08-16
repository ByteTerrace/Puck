using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Puck.Cli.Format.Rewriters;

// The null-pattern normalizer (the `null-pattern` pass): rewrites equality/inequality against the `null`
// literal into the pattern form the house style requires — `x is null` / `x is not null` — whichever
// side the literal sits on (`x == null`, `null != x`). This is semantic rather than syntax-only: pointer
// operands are declined because patterns do not accept pointer types, and a user-defined equality
// operator is declined because `is null` deliberately bypasses it. The non-null operand becomes the
// pattern subject (its inner trivia preserved); the node's own outer trivia is carried across.
internal sealed class NullPatternRewriter : CSharpSyntaxRewriter {
    private readonly SemanticModel m_model;

    public NullPatternRewriter(SemanticModel model) {
        m_model = model;
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node) {
        var visited = ((BinaryExpressionSyntax)base.VisitBinaryExpression(node: node)!);
        var kind = visited.Kind();

        if (kind is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)) {
            return visited;
        }

        var leftIsNull = visited.Left.IsKind(kind: SyntaxKind.NullLiteralExpression);
        var rightIsNull = visited.Right.IsKind(kind: SyntaxKind.NullLiteralExpression);

        if (leftIsNull == rightIsNull) {
            return visited;
        }

        // GetOperation returns IBinaryOperation for built-in and user-defined statically bound
        // comparisons. Dynamic/error-bound expressions produce another operation shape (or none) and
        // are conservatively left alone. OperatorMethod identifies the overloaded-equality case.
        if ((m_model.GetOperation(node: node) is not IBinaryOperation operation)
            || (operation.OperatorMethod is not null)) {
            return visited;
        }

        var subjectOperation = (rightIsNull ? operation.LeftOperand : operation.RightOperand);

        if ((subjectOperation.Type is null)
            || (subjectOperation.Type.TypeKind is TypeKind.Pointer or TypeKind.Dynamic or TypeKind.Error)) {
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
