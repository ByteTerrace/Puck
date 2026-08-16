using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The object-initializer ordering normalizer (the `init-order` pass): the member assignments in a
// `new T { A = ..., B = ... }` initializer are sorted alphabetically by member name — the same
// alphabetical convention `named-args` applies to call arguments. Only pure `identifier = value` object
// initializers are touched (collection/array initializers and any with a non-identifier element are left
// alone, since their order is significant). The per-slot trivia is reassigned so the one-per-line layout
// is preserved across the sort.
internal sealed class InitOrderRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitInitializerExpression(InitializerExpressionSyntax node) {
        var visited = ((InitializerExpressionSyntax)base.VisitInitializerExpression(node: node)!);

        if (!visited.IsKind(kind: SyntaxKind.ObjectInitializerExpression)
            || (visited.Expressions.Count < 2)
            || !visited.Expressions.All(predicate: static expression => (expression is AssignmentExpressionSyntax { Left: IdentifierNameSyntax }))) {
            return visited;
        }

        // Initializer values are evaluated in written order. Leave the initializer as-is when it is
        // already sorted (nothing to do) or when any value is side-effecting (reordering would change
        // evaluation order).
        var memberNames = visited.Expressions.Select(selector: static expression =>
            ((IdentifierNameSyntax)((AssignmentExpressionSyntax)expression).Left).Identifier.ValueText);

        if (memberNames.SequenceEqual(second: memberNames.OrderBy(keySelector: static name => name, comparer: StringComparer.Ordinal))
            || visited.Expressions.Any(predicate: static expression => ExpressionSafety.HasSideEffect(expression: ((AssignmentExpressionSyntax)expression).Right))) {
            return visited;
        }

        // Trivia is reassigned by SLOT — separators included — so a comment written above one member, or
        // after one member's comma, would end up documenting whichever member the sort moves into that
        // slot. Leave an annotated initializer as written.
        if (RewriteShaping.IsAnnotated(list: visited.Expressions)) {
            return visited;
        }

        var ordered = visited.Expressions
            .OrderBy(
                keySelector: static expression => ((IdentifierNameSyntax)((AssignmentExpressionSyntax)expression).Left).Identifier.ValueText,
                comparer: StringComparer.Ordinal)
            .ToArray();

        return visited.WithExpressions(expressions: RewriteShaping.ReorderInPlace(original: visited.Expressions, ordered: ordered));
    }
}
