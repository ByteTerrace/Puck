using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The member-ordering normalizer (the `member-order` pass): a contiguous block of const fields, or a
// contiguous block of properties, sharing the same accessibility scope is sorted alphabetically by
// declared name — the same convention `named-args`/`init-order` apply to arguments and object-
// initializer members. The block boundary is exactly the run `member-spacing` packs tight (same kind AND
// same scope), so each blank-line-delimited group sorts independently. Regular (non-const) fields are
// NEVER reordered — their order is a [StructLayout]/ABI contract — and initializer-coupled properties
// stay in source order. A block is left untouched if ANY member carries a leading/trailing comment or
// #directive (slot reassignment would scramble the annotation). Per-slot trivia is reassigned
// positionally, so the tight one-per-line layout is preserved and a second run is a no-op.
internal sealed class MemberOrderRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => Fix(node: ((TypeDeclarationSyntax)base.VisitClassDeclaration(node: node)!));
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => Fix(node: ((TypeDeclarationSyntax)base.VisitStructDeclaration(node: node)!));
    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => Fix(node: ((TypeDeclarationSyntax)base.VisitInterfaceDeclaration(node: node)!));
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => Fix(node: ((TypeDeclarationSyntax)base.VisitRecordDeclaration(node: node)!));

    private static TypeDeclarationSyntax Fix(TypeDeclarationSyntax node) {
        if ((node is StructDeclarationSyntax)
            || ((node is RecordDeclarationSyntax record) && record.ClassOrStructKeyword.IsKind(kind: SyntaxKind.StructKeyword))
            || node.Modifiers.Any(predicate: static modifier => modifier.IsKind(kind: SyntaxKind.PartialKeyword))
            || (node.AttributeLists.Count > 0)
            || node.Members.Any(predicate: static member => member.ContainsDirectives)) {
            return node;
        }

        return node.WithMembers(members: Reorder(members: node.Members));
    }
    private static SyntaxList<MemberDeclarationSyntax> Reorder(SyntaxList<MemberDeclarationSyntax> members) {
        if (members.Count < 2) {
            return members;
        }

        var result = new List<MemberDeclarationSyntax>(capacity: members.Count);
        var run = new List<MemberDeclarationSyntax>();
        var coupled = InitializerCoupling.CoupledMemberNames(members: members);
        string? runKey = null;

        foreach (var member in members) {
            var key = (RewriteShaping.IsAnnotated(node: member) ? null : GroupKey(coupled: coupled, member: member));

            if ((key is not null) && (key == runKey)) {
                run.Add(item: member);

                continue;
            }

            FlushRun(result: result, run: run);
            runKey = key;

            if (key is null) {
                result.Add(item: member);
            } else {
                run.Add(item: member);
            }
        }

        FlushRun(result: result, run: run);

        return SyntaxFactory.List(nodes: result);
    }
    private static void FlushRun(List<MemberDeclarationSyntax> result, List<MemberDeclarationSyntax> run) {
        if (run.Count == 1) {
            result.Add(item: run[0]);
        } else if (run.Count > 1) {
            // A property initializer is evaluated in declaration order (in the constructor); if any in
            // the run has a side effect, reordering would change that order, so the run is left as
            // written. (const initializers are compile-time — always safe.)
            if (run.Any(predicate: static member => ((member is PropertyDeclarationSyntax { Initializer.Value: { } value }) && ExpressionSafety.HasSideEffect(expression: value)))) {
                result.AddRange(collection: run);
            } else {
                // The inter-member whitespace is positional (slot i always carries the same surrounding
                // trivia); reassigning it by slot preserves the layout while the declarations move.
                var slots = run.Select(selector: static member => (member.GetLeadingTrivia(), member.GetTrailingTrivia())).ToArray();
                var sorted = run.OrderBy(keySelector: SortKey, comparer: StringComparer.Ordinal).ToArray();

                for (var slot = 0; (slot < sorted.Length); slot++) {
                    result.Add(item: sorted[slot].WithLeadingTrivia(trivia: slots[slot].Item1).WithTrailingTrivia(trivia: slots[slot].Item2));
                }
            }
        }

        run.Clear();
    }
    // The block key: kind (const / property) plus accessibility scope. A null key marks a member that
    // cannot join a sortable run (any other member kind).
    private static string? GroupKey(MemberDeclarationSyntax member, HashSet<string> coupled) {
        var kind = member switch {
            FieldDeclarationSyntax field when field.Modifiers.Any(predicate: static modifier => modifier.IsKind(kind: SyntaxKind.ConstKeyword)) => "const",
            PropertyDeclarationSyntax property when !coupled.Contains(item: property.Identifier.ValueText) => "property",
            _ => null
        };

        return ((kind is null) ? null : $"{kind} {RewriteShaping.AccessibilityScope(member: member)}");
    }
    private static string SortKey(MemberDeclarationSyntax member) => member switch {
        FieldDeclarationSyntax field => field.Declaration.Variables[0].Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        _ => ""
    };
}
