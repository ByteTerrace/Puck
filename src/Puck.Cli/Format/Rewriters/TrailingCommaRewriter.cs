using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The trailing-comma normalizer (the `trailing-comma` pass): a MULTI-LINE initializer (object /
// collection / array, where the last element and the closing brace sit on different source lines) gets a
// trailing comma after its last element, so adding or reordering a member later is a one-line diff —
//   new SomeOptions {
//       Count = count,
//       Kind = kind,
//   }
// Single-line initializers (`{ A = 1, B = 2 }`) are left alone — a trailing comma there reads oddly —
// and an initializer that already ends with a comma is untouched, so the pass is idempotent.
internal sealed class TrailingCommaRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitInitializerExpression(InitializerExpressionSyntax node) {
        var visited = (InitializerExpressionSyntax)base.VisitInitializerExpression(node: node)!;
        var expressions = visited.Expressions;
        var separators = expressions.GetSeparators().ToList();

        if ((expressions.Count == 0) || (separators.Count >= expressions.Count)) {
            return visited;
        }

        var text = node.SyntaxTree!.GetText();
        var lastExpressionLine = text.Lines.IndexOf(position: node.Expressions[^1].Span.End);
        var closeBraceLine = text.Lines.IndexOf(position: node.CloseBraceToken.SpanStart);

        if (lastExpressionLine == closeBraceLine) {
            return visited;
        }

        var last = expressions[^1];
        var trailingComma = SyntaxFactory.Token(kind: SyntaxKind.CommaToken).WithTrailingTrivia(trivia: last.GetTrailingTrivia());
        var nodesAndTokens = new List<SyntaxNodeOrToken>(capacity: (expressions.Count * 2));

        for (var index = 0; (index < expressions.Count); index++) {
            nodesAndTokens.Add(item: ((index == (expressions.Count - 1)) ? last.WithTrailingTrivia() : expressions[index]));
            nodesAndTokens.Add(item: ((index < separators.Count) ? separators[index] : trailingComma));
        }

        return visited.WithExpressions(expressions: SyntaxFactory.SeparatedList<ExpressionSyntax>(nodesAndTokens: nodesAndTokens));
    }
}
