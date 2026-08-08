using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The ternary-layout normalizer (the `ternary-lines` pass): a conditional expression
// `cond ? whenTrue : whenFalse` is laid out across three lines with the `?` and `:` operators LEADING
// their branch, each indented one level beyond the line the condition opens on —
//   return someTest
//       ? "A"
//       : "B";
// A `? : ? :` chain (the whenFalse — or whenTrue — is itself a conditional) nests one level deeper per
// link; the chain's root drives the layout so the nested links indent off the rebuilt shape rather than
// their original source position. Condition and branch inner trivia are preserved and only the outer
// layout trivia is reset, so a second run reproduces the same shape — the pass is idempotent. A ternary
// carrying a comment or #directive in one of those reset slots is left exactly as authored.
// Indentation is computed structurally, never read from the condition's own line, so sibling ternaries
// in one expression cannot shift each other across runs.
internal sealed class TernaryLinesRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node) {
        var visited = (ConditionalExpressionSyntax)base.VisitConditionalExpression(node: node)!;

        // A conditional that is a branch of another conditional is a link in a `? : ? :` chain — its
        // root lays it out (one level deeper), so leave it alone here.
        if ((node.Parent is ConditionalExpressionSyntax parent) && ((parent.WhenTrue == node) || (parent.WhenFalse == node))) {
            return visited;
        }

        // The layout reissues every trivia slot between the condition and the last branch, so a comment
        // or #directive in one of them would be deleted outright — and the write guard only counts parse
        // errors, so the loss would be written. Leave such a ternary exactly as authored. The whole chain
        // is declined together, since the root rebuilds every link.
        if (IsAnnotated(conditional: visited)) {
            return visited;
        }

        return Layout(conditional: visited, conditionIndent: ConditionIndent(node: node));
    }

    private static ConditionalExpressionSyntax Layout(ConditionalExpressionSyntax conditional, string conditionIndent) {
        var branchIndent = (conditionIndent + "    ");
        var branchLead = new[] { SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(text: branchIndent) };

        var whenTrue = ((conditional.WhenTrue is ConditionalExpressionSyntax trueChain)
            ? Layout(conditional: trueChain, conditionIndent: branchIndent).WithLeadingTrivia()
            : conditional.WhenTrue.WithLeadingTrivia().WithTrailingTrivia());
        var whenFalse = ((conditional.WhenFalse is ConditionalExpressionSyntax falseChain)
            ? Layout(conditional: falseChain, conditionIndent: branchIndent).WithLeadingTrivia()
            : conditional.WhenFalse.WithLeadingTrivia().WithTrailingTrivia());

        return conditional
            .WithCondition(condition: conditional.Condition.WithTrailingTrivia())
            .WithQuestionToken(questionToken: conditional.QuestionToken.WithLeadingTrivia(trivia: branchLead).WithTrailingTrivia(SyntaxFactory.Space))
            .WithWhenTrue(whenTrue: whenTrue)
            .WithColonToken(colonToken: conditional.ColonToken.WithLeadingTrivia(trivia: branchLead).WithTrailingTrivia(SyntaxFactory.Space))
            .WithWhenFalse(whenFalse: whenFalse);
    }

    // True when a trivia slot the layout resets carries prose or a directive: the condition's trailing
    // side, either operator token, or either branch's outer trivia. The condition's own leading trivia
    // and all inner trivia survive a rewrite, so neither is consulted.
    private static bool IsAnnotated(ConditionalExpressionSyntax conditional) =>
        (RewriteShaping.HasCommentOrDirective(trivia: conditional.Condition.GetTrailingTrivia())
        || RewriteShaping.IsAnnotated(token: conditional.QuestionToken)
        || RewriteShaping.IsAnnotated(token: conditional.ColonToken)
        || IsAnnotatedBranch(branch: conditional.WhenTrue)
        || IsAnnotatedBranch(branch: conditional.WhenFalse));

    // A branch that is itself a chain link is rebuilt link by link, so its own slots count too.
    private static bool IsAnnotatedBranch(ExpressionSyntax branch) => ((branch is ConditionalExpressionSyntax link)
        ? (RewriteShaping.HasCommentOrDirective(trivia: link.GetLeadingTrivia()) || IsAnnotated(conditional: link))
        : RewriteShaping.IsAnnotated(node: branch));

    // The indent this ternary's `? t` / `: f` branches hang from: the enclosing statement/member indent
    // plus one level per enclosing ternary whose BRANCH holds this node.
    private static string ConditionIndent(ConditionalExpressionSyntax node) => new(
        c: ' ',
        count: RewriteShaping.StructuralIndent(
            node: node,
            addsLevel: static (ancestor, child) => ((ancestor is ConditionalExpressionSyntax conditional) && ((conditional.WhenTrue == child) || (conditional.WhenFalse == child)))));
}
