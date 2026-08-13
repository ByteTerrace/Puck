using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The argument-layout normalizer (the `arg-lines` pass): a call with MORE THAN ONE argument gets every
// argument on its own line, indented one level past the line the call opens on, with the closing
// parenthesis hanging on its own line left-justified to that line —
//   foo(
//       a,
//       b
//   );
// Zero- and single-argument calls stay inline (`foo()`, `bar(a)`); a single-argument call that was
// previously split is collapsed back onto one line (trivia INSIDE the argument — e.g. a nested
// multi-argument call — is preserved). Positional order is preserved (the `named-args` pass owns
// alphabetical ordering, and only it knows which calls can be safely reordered), so this is a pure
// layout edit. Indentation is taken structurally, so nested calls settle over successive runs.
internal sealed class ArgLinesRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitArgumentList(ArgumentListSyntax node) {
        var visited = (ArgumentListSyntax)base.VisitArgumentList(node: node)!;

        if (visited.Arguments.Count < 1) {
            return visited;
        }

        // The layout is rebuilt from scratch — argument trivia is replaced and the separators reissued —
        // so a comment or #directive anywhere in those slots would be deleted outright. Leave such a
        // call exactly as written.
        if (IsAnnotated(list: visited)) {
            return visited;
        }

        // A single-argument call stays on one line: clear the open paren's trailing trivia, the
        // argument's surrounding trivia, and the close paren's leading trivia so a previously split
        // `foo(\n    a\n)` collapses back to `foo(a)`.
        if (visited.Arguments.Count == 1) {
            return visited
                .WithOpenParenToken(openParenToken: visited.OpenParenToken.WithTrailingTrivia())
                .WithArguments(arguments: SyntaxFactory.SingletonSeparatedList(node: visited.Arguments[0].WithLeadingTrivia().WithTrailingTrivia()))
                .WithCloseParenToken(closeParenToken: visited.CloseParenToken.WithLeadingTrivia());
        }

        var lineIndent = WrappedIndent(node: node);
        var argumentTrivia = SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(text: new string(c: ' ', count: (lineIndent + 4))));
        var separators = visited.Arguments.GetSeparators().ToArray();
        var nodesAndTokens = new List<SyntaxNodeOrToken>(capacity: (visited.Arguments.Count * 2));

        for (var index = 0; (index < visited.Arguments.Count); index++) {
            nodesAndTokens.Add(item: visited.Arguments[index].WithLeadingTrivia(trivia: argumentTrivia).WithTrailingTrivia());

            if (index < (visited.Arguments.Count - 1)) {
                // The source separator carries through (it is the token the call was written with); only
                // its layout whitespace is dropped, since the argument leads now supply the line breaks.
                nodesAndTokens.Add(
                    item: ((index < separators.Length)
                        ? separators[index].WithLeadingTrivia().WithTrailingTrivia()
                        : SyntaxFactory.Token(kind: SyntaxKind.CommaToken)));
            }
        }

        return visited
            .WithOpenParenToken(openParenToken: visited.OpenParenToken.WithTrailingTrivia())
            .WithArguments(arguments: SyntaxFactory.SeparatedList<ArgumentSyntax>(nodesAndTokens: nodesAndTokens))
            .WithCloseParenToken(
                closeParenToken: visited.CloseParenToken.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(text: new string(c: ' ', count: lineIndent))));
    }

    // True when any trivia slot this pass rewrites carries prose or a directive.
    private static bool IsAnnotated(ArgumentListSyntax list) =>
        (RewriteShaping.HasCommentOrDirective(trivia: list.OpenParenToken.TrailingTrivia)
        || RewriteShaping.HasCommentOrDirective(trivia: list.CloseParenToken.LeadingTrivia)
        || RewriteShaping.IsAnnotated(list: list.Arguments));

    // The indent this call's wrapped body hangs from: the enclosing statement's indent plus one level per
    // ENCLOSING multi-argument call (every such call is itself wrapped and pushes this one deeper), plus
    // one level per enclosing ternary whose BRANCH holds this call — ternary-lines lays each branch out
    // one level past its condition, so a call opening on a branch line hangs from that deeper line.
    private static int WrappedIndent(ArgumentListSyntax node) =>
        RewriteShaping.StructuralIndent(
            node: node,
            addsLevel: static (ancestor, child) => ((ancestor is ArgumentListSyntax { Arguments.Count: > 1 })
                || ((ancestor is ConditionalExpressionSyntax conditional) && ((conditional.WhenTrue == child) || (conditional.WhenFalse == child)))));
}
