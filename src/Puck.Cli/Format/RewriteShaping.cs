using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format;

// The layout primitives the rewriters share: the annotation guard that keeps a pass off human-arranged
// code, the accessibility grouping key, blank-line forcing, the structural indent anchor, and the
// per-slot separated-list rebuild.
internal static class RewriteShaping {
    // The one home for a newline a pass INVENTS. .editorconfig declares `end_of_line = lf` for every file in
    // the tree and .gitattributes pins the same bytes into the working tree, so an invented line break is a
    // bare line feed — never Environment.NewLine and never SyntaxFactory.CarriageReturnLineFeed, either of
    // which leaves an LF file carrying mixed terminators the moment any pass changes anything: invisible in a
    // diff, and a whole root of files git reports modified with nothing to show. Newlines COPIED from the
    // source keep whatever the source had (Roslyn preserves that trivia); phase 0 owns normalizing those.
    public static readonly SyntaxTrivia EndOfLine = SyntaxFactory.LineFeed;

    // True when a trivia run carries prose or a preprocessor directive. Every reordering pass is gated
    // on this: trivia is reassigned by SLOT, so moving an element out from under its own comment (or
    // across an #if) silently changes what the annotation documents — and the write guard only counts
    // parse errors, so such a rewrite would be written.
    public static bool HasCommentOrDirective(SyntaxTriviaList trivia) => trivia.Any(predicate: static item =>
        (item.IsKind(kind: SyntaxKind.SingleLineCommentTrivia)
        || item.IsKind(kind: SyntaxKind.MultiLineCommentTrivia)
        || item.IsKind(kind: SyntaxKind.SingleLineDocumentationCommentTrivia)
        || item.IsKind(kind: SyntaxKind.MultiLineDocumentationCommentTrivia)
        || item.IsDirective));
    // The same guard over a node's own surrounding trivia.
    public static bool IsAnnotated(SyntaxNode node) =>
        (HasCommentOrDirective(trivia: node.GetLeadingTrivia()) || HasCommentOrDirective(trivia: node.GetTrailingTrivia()));
    // The same guard over a token's surrounding trivia.
    public static bool IsAnnotated(SyntaxToken token) =>
        (HasCommentOrDirective(trivia: token.LeadingTrivia) || HasCommentOrDirective(trivia: token.TrailingTrivia));
    // The same guard over a separated list: every element AND every separator. A comment written after a
    // comma attaches to the SEPARATOR, not to either neighbouring element, so an element-only check would
    // let a slot-preserving reorder leave it annotating whichever element moved into that slot.
    public static bool IsAnnotated<TNode>(SeparatedSyntaxList<TNode> list) where TNode : SyntaxNode =>
        (list.Any(predicate: static node => IsAnnotated(node: node))
        || list.GetSeparators().Any(predicate: static separator => IsAnnotated(token: separator)));
    // The accessibility-scope key two adjacent members are grouped by. Only the access modifiers count
    // (public/private/protected/internal); ordering is normalized so `protected internal` and `internal
    // protected` compare equal. A member with no explicit accessibility (interface members,
    // implicit-private) keys as the empty string, so such siblings group together.
    public static string AccessibilityScope(MemberDeclarationSyntax member) => string.Join(
        separator: ' ',
        values: member.Modifiers
            .Where(predicate: static modifier =>
                (modifier.IsKind(kind: SyntaxKind.PublicKeyword)
                || modifier.IsKind(kind: SyntaxKind.PrivateKeyword)
                || modifier.IsKind(kind: SyntaxKind.ProtectedKeyword)
                || modifier.IsKind(kind: SyntaxKind.InternalKeyword)))
            .Select(selector: static modifier => modifier.ValueText)
            .OrderBy(keySelector: static text => text, comparer: StringComparer.Ordinal));
    // Forces exactly `desired` blank lines ahead of a construct: the leading end-of-lines are collapsed
    // and that many newlines are prepended, while the indentation whitespace (and any other lead) is
    // kept, so the construct stays where it sits horizontally.
    public static SyntaxTriviaList SetBlankLines(SyntaxTriviaList lead, int desired) {
        var trivia = lead.ToList();
        var start = 0;

        while ((start < trivia.Count) && trivia[start].IsKind(kind: SyntaxKind.EndOfLineTrivia)) {
            start++;
        }

        var rebuilt = new List<SyntaxTrivia>();

        for (var index = 0; (index < desired); index++) {
            rebuilt.Add(item: EndOfLine);
        }

        rebuilt.AddRange(collection: trivia.Skip(count: start));

        return SyntaxFactory.TriviaList(trivias: rebuilt);
    }
    // The indent a wrapped construct hangs from, computed STRUCTURALLY so it is stable across runs
    // (idempotency). Anchoring to the input's current line would compound: once an enclosing construct
    // wraps, this one moves to a deeper line, and reading that line's indent would add a level every
    // run. Instead: take the enclosing statement's indent (which the wrapping passes never move) and add
    // one level per enclosing construct that `addsLevel` accepts — a count driven by tree shape, not by
    // the current layout, so a second pass reproduces the same value exactly. An enclosing arrow-body
    // (`=>`) clause anchors at ITS expression's line instead: the construct opens on the arrow
    // continuation line, which may sit deeper than the member's own line — and that line is safe to read
    // because no pass moves the arrow break or the expression's first token off it.
    public static int StructuralIndent(SyntaxNode node, Func<SyntaxNode, SyntaxNode, bool> addsLevel) {
        var anchor = node;
        SyntaxNode? anchorLineOwner = null;

        for (var candidate = node.Parent; (candidate is not null); candidate = candidate.Parent) {
            if (candidate is ArrowExpressionClauseSyntax arrow) {
                anchor = candidate;
                anchorLineOwner = arrow.Expression;

                break;
            }

            if (candidate is StatementSyntax or MemberDeclarationSyntax) {
                anchor = candidate;

                break;
            }
        }

        var anchorLine = node.SyntaxTree!.GetText().Lines.GetLineFromPosition(position: (anchorLineOwner ?? anchor).GetFirstToken().SpanStart).ToString();
        var baseIndent = (anchorLine.Length - anchorLine.TrimStart().Length);

        var depth = 0;
        var child = node;

        for (var ancestor = node.Parent; ((ancestor is not null) && (ancestor != anchor)); child = ancestor, ancestor = ancestor.Parent) {
            if (addsLevel(arg1: ancestor, arg2: child)) {
                depth++;
            }
        }

        return (baseIndent + (4 * depth));
    }
    // Reassigns an already-reordered element sequence back into a separated list, keeping each SLOT's
    // trivia and separator where they were — so the source's existing single-line or one-per-line layout
    // survives the reorder. Sound only because every caller first declines a list whose elements OR
    // separators carry an annotation (see the SeparatedSyntaxList overload of IsAnnotated).
    public static SeparatedSyntaxList<TNode> ReorderInPlace<TNode>(SeparatedSyntaxList<TNode> original, IReadOnlyList<TNode> ordered) where TNode : SyntaxNode {
        var separators = original.GetSeparators().ToArray();
        var nodesAndTokens = new List<SyntaxNodeOrToken>(capacity: (original.Count * 2));

        for (var slot = 0; (slot < ordered.Count); slot++) {
            nodesAndTokens.Add(
                item: ordered[slot]
                    .WithLeadingTrivia(trivia: original[slot].GetLeadingTrivia())
                    .WithTrailingTrivia(trivia: original[slot].GetTrailingTrivia()));

            if (slot < separators.Length) {
                nodesAndTokens.Add(item: separators[slot]);
            }
        }

        return SyntaxFactory.SeparatedList<TNode>(nodesAndTokens: nodesAndTokens);
    }
}
