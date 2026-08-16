using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The string-merge normalizer (the `string-merge` pass): a `+` whose two operands are both string (or
// interpolated string) literals becomes the single literal holding the concatenated value, bottom-up, so
// an all-literal chain collapses to one literal in one run. Split literals defeat exact-text content
// search — the runtime message never exists contiguously in source — and the merged form is what the
// compiler folds to anyway.
//
// Safety boundary: BOTH operands of a merged `+` must themselves be literals, which pins the operator to
// the language's own string concatenation — no user-defined `operator+` can be in play, and no merge ever
// re-associates across a non-literal operand. Left alone: a seam whose interior carries a comment (merging
// would delete it), verbatim/raw INTERPOLATED operands (their format-clause escaping differs from the
// regular form the merge emits), and UTF-8 literals (not strings). Verbatim and raw PLAIN literals merge
// by value; the result is re-escaped as a regular literal. A merged result with no interpolation holes is
// emitted as a plain literal; one with holes is explicitly cast to `string`, preserving the original
// concatenation's static type so an interpolated-string-handler overload cannot become newly applicable.
// Redundant parentheses around a lone string literal are unwrapped, so a dissolved chain leaves no husk.
// The output contains no `+` of literals, so the pass is idempotent.
internal sealed class StringMergeRewriter : CSharpSyntaxRewriter {
    // Either a run of literal text (Text non-null) or one carried-over interpolation hole.
    private readonly record struct Segment(string? Text, InterpolationSyntax? Hole);

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node) {
        var visited = ((BinaryExpressionSyntax)base.VisitBinaryExpression(node: node)!);

        if (!visited.IsKind(kind: SyntaxKind.AddExpression) || HasInteriorComment(node: visited)) {
            return visited;
        }

        var segments = new List<Segment>();

        if (!TryCollectSegments(expression: Unwrap(expression: visited.Left), segments: segments)
            || !TryCollectSegments(expression: Unwrap(expression: visited.Right), segments: segments)) {
            return visited;
        }

        return BuildMerged(segments: segments)
            .WithLeadingTrivia(trivia: visited.GetLeadingTrivia())
            .WithTrailingTrivia(trivia: visited.GetTrailingTrivia());
    }
    public override SyntaxNode? VisitParenthesizedExpression(ParenthesizedExpressionSyntax node) {
        var visited = ((ParenthesizedExpressionSyntax)base.VisitParenthesizedExpression(node: node)!);
        var inner = visited.Expression;

        // A parenthesized lone literal is always redundant (a literal is a primary expression), and a
        // freshly merged chain would otherwise leave its old grouping parens behind.
        if (((inner is LiteralExpressionSyntax literal) && literal.IsKind(kind: SyntaxKind.StringLiteralExpression))
            || (inner is InterpolatedStringExpressionSyntax)) {
            return inner
                .WithLeadingTrivia(trivia: visited.GetLeadingTrivia().AddRange(trivia: inner.GetLeadingTrivia()))
                .WithTrailingTrivia(trivia: inner.GetTrailingTrivia().AddRange(trivia: visited.GetTrailingTrivia()));
        }

        return visited;
    }

    // A comment anywhere inside the expression (between the operands, inside grouping parens) would be
    // deleted by a merge; the node's own leading/trailing trivia survives on the merged literal.
    private static bool HasInteriorComment(BinaryExpressionSyntax node) {
        var leading = node.GetLeadingTrivia();
        var trailing = node.GetTrailingTrivia();

        foreach (var trivia in node.DescendantTrivia()) {
            if ((trivia.IsKind(kind: SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(kind: SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(kind: SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(kind: SyntaxKind.MultiLineDocumentationCommentTrivia))
                && !leading.Contains(value: trivia)
                && !trailing.Contains(value: trivia)) {
                return true;
            }
        }

        return false;
    }
    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) {
        while (expression is ParenthesizedExpressionSyntax parenthesized) {
            expression = parenthesized.Expression;
        }

        return expression;
    }
    private static bool TryCollectSegments(ExpressionSyntax expression, List<Segment> segments) {
        switch (expression) {
            case LiteralExpressionSyntax literal when literal.IsKind(kind: SyntaxKind.StringLiteralExpression):
                // Regular, verbatim, and raw string literals merge by decoded value; anything else on a
                // string-literal expression (e.g. a UTF-8 literal) is not a string and must not merge.
                if (!literal.Token.IsKind(kind: SyntaxKind.StringLiteralToken)
                    && !literal.Token.IsKind(kind: SyntaxKind.SingleLineRawStringLiteralToken)
                    && !literal.Token.IsKind(kind: SyntaxKind.MultiLineRawStringLiteralToken)) {
                    return false;
                }

                segments.Add(item: new Segment(Text: literal.Token.ValueText, Hole: null));

                return true;
            case InterpolatedStringExpressionSyntax interpolated:
                // Only the regular `$"` form: a verbatim or raw interpolated operand's format clauses
                // escape differently from the regular form this pass emits, so it is left alone.
                if (!interpolated.StringStartToken.IsKind(kind: SyntaxKind.InterpolatedStringStartToken)) {
                    return false;
                }

                foreach (var content in interpolated.Contents) {
                    switch (content) {
                        case InterpolatedStringTextSyntax text:
                            segments.Add(item: new Segment(Text: text.TextToken.ValueText, Hole: null));

                            break;
                        case InterpolationSyntax hole:
                            segments.Add(item: new Segment(Hole: hole, Text: null));

                            break;
                        default:
                            return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }
    private static ExpressionSyntax BuildMerged(List<Segment> segments) {
        if (segments.TrueForAll(match: static segment => (segment.Hole is null))) {
            return SyntaxFactory.LiteralExpression(
                kind: SyntaxKind.StringLiteralExpression,
                token: SyntaxFactory.Literal(value: string.Concat(values: segments.Select(selector: static segment => segment.Text))));
        }

        var contents = new List<InterpolatedStringContentSyntax>();
        var buffer = new StringBuilder();

        foreach (var segment in segments) {
            if (segment.Hole is { } hole) {
                FlushText(buffer: buffer, contents: contents);
                contents.Add(item: hole);
            } else {
                _ = buffer.Append(value: segment.Text);
            }
        }

        FlushText(buffer: buffer, contents: contents);

        var interpolation = SyntaxFactory.InterpolatedStringExpression(
            stringStartToken: SyntaxFactory.Token(kind: SyntaxKind.InterpolatedStringStartToken),
            contents: SyntaxFactory.List(nodes: contents),
            stringEndToken: SyntaxFactory.Token(kind: SyntaxKind.InterpolatedStringEndToken));

        // A literal-concatenation expression has type string before its parent is bound. A bare
        // interpolation can instead convert to an interpolated-string-handler parameter and change
        // overload resolution. Keep the original expression type explicit at that semantic boundary.
        return SyntaxFactory.CastExpression(
            type: SyntaxFactory.PredefinedType(keyword: SyntaxFactory.Token(kind: SyntaxKind.StringKeyword)),
            expression: interpolation);
    }
    private static void FlushText(StringBuilder buffer, List<InterpolatedStringContentSyntax> contents) {
        if (buffer.Length == 0) {
            return;
        }

        var value = buffer.ToString();

        buffer.Clear();

        // SyntaxFactory.Literal owns regular-literal escaping; its quoted text minus the delimiters is
        // the token text for a regular interpolated context, where braces must additionally double.
        var escaped = SyntaxFactory.Literal(value: value).Text[1..^1]
            .Replace(newValue: "{{", oldValue: "{")
            .Replace(newValue: "}}", oldValue: "}");

        contents.Add(item: SyntaxFactory.InterpolatedStringText(textToken: SyntaxFactory.Token(
            kind: SyntaxKind.InterpolatedStringTextToken,
            leading: default,
            text: escaped,
            trailing: default,
            valueText: value)));
    }
}
