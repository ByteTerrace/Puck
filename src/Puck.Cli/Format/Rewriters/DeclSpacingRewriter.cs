using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The declaration-spacing normalizer (the `decl-spacing` pass): inside a block, a run of local-variable
// declarations is visually separated from the first NON-declaration statement that follows it by exactly
// one blank line (the house "variables apart from the body" rule) —
//   var pointers = GetPointers(deviceHandle: deviceHandle);
//
//   if (pointers.Handle is null) {
// Consecutive declarations stay grouped (no blank between them) and a statement whose own leading trivia
// is a comment/directive is left untouched (its placement is the comment's to own). Only the
// decl-to-statement boundary is normalized — blank lines elsewhere are preserved — so the pass is
// idempotent.
internal sealed class DeclSpacingRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitBlock(BlockSyntax node) {
        var visited = (BlockSyntax)base.VisitBlock(node: node)!;
        var statements = visited.Statements;

        if (statements.Count < 2) {
            return visited;
        }

        var rebuilt = new List<StatementSyntax> { statements[0] };

        for (var index = 1; (index < statements.Count); index++) {
            var previous = statements[(index - 1)];
            var current = statements[index];

            rebuilt.Add(
                item: (((previous is LocalDeclarationStatementSyntax)
                    && (current is not LocalDeclarationStatementSyntax)
                    && OnSeparateLines(previous: previous, current: current)
                    && !RewriteShaping.HasCommentOrDirective(trivia: current.GetLeadingTrivia()))
                    ? current.WithLeadingTrivia(trivia: RewriteShaping.SetBlankLines(lead: current.GetLeadingTrivia(), desired: 1))
                    : current));
        }

        return visited.WithStatements(statements: SyntaxFactory.List(nodes: rebuilt));
    }

    // Only space declarations that already sit on their own lines: a single-line body
    // (`{ int n = f(); return n; }`) must not be blown open — and splitting it was also the source of a
    // non-idempotent run, since the inserted newline retriggered the rule.
    private static bool OnSeparateLines(StatementSyntax previous, StatementSyntax current) =>
        (previous.GetTrailingTrivia().Any(predicate: static trivia => trivia.IsKind(kind: SyntaxKind.EndOfLineTrivia))
        || current.GetLeadingTrivia().Any(predicate: static trivia => trivia.IsKind(kind: SyntaxKind.EndOfLineTrivia)));
}
