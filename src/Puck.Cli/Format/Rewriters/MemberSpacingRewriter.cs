using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The blank-line-between-members normalizer (the `member-spacing` pass): ZERO blank lines between
// members that share BOTH kind and accessibility scope (so a run of `public` methods packs tight
// whether each body is one line or many, and attributed one-liners like the interop declaration groups
// stay tight too), and exactly ONE blank line at every subject shift (a member-kind change OR an
// accessibility-scope change, e.g. a `protected override` method followed by a `public` one). A field's
// kind is its storage class — const, static readonly, static, readonly, mutable — with the initializer-
// coupled block (see InitializerCoupling) as its own kind, so each of member-groups' organizational
// groups is one blank-line-delimited unit. Leading comments stay attached while the blank lines ahead
// of them are normalized; #directive-led members are left alone because their guarded region cannot be
// inferred from trivia alone. This is the one spacing rule .editorconfig and the SDK formatter cannot express.
internal sealed class MemberSpacingRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => Fix(node: ((TypeDeclarationSyntax)base.VisitClassDeclaration(node: node)!));
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => Fix(node: ((TypeDeclarationSyntax)base.VisitStructDeclaration(node: node)!));
    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => Fix(node: ((TypeDeclarationSyntax)base.VisitInterfaceDeclaration(node: node)!));
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => Fix(node: ((TypeDeclarationSyntax)base.VisitRecordDeclaration(node: node)!));
    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node) =>
        Fix(node: ((CompilationUnitSyntax)base.VisitCompilationUnit(node: node)!));
    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node) =>
        Fix(node: ((NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node: node)!));
    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node) =>
        Fix(node: ((FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node: node)!));

    private static TypeDeclarationSyntax Fix(TypeDeclarationSyntax node) =>
        node.WithMembers(members: Normalize(members: node.Members, normalizeFirst: true));
    private static SyntaxNode Fix(SyntaxNode node) => node switch {
        CompilationUnitSyntax compilationUnit => compilationUnit.WithMembers(members: Normalize(members: compilationUnit.Members, normalizeFirst: false)),
        NamespaceDeclarationSyntax @namespace => @namespace.WithMembers(members: Normalize(members: @namespace.Members, normalizeFirst: false)),
        FileScopedNamespaceDeclarationSyntax @namespace => @namespace.WithMembers(members: Normalize(members: @namespace.Members, normalizeFirst: false)),
        _ => throw new ArgumentException(message: "A member container must be a compilation unit or namespace.", paramName: nameof(node)),
    };
    private static SyntaxList<MemberDeclarationSyntax> Normalize(SyntaxList<MemberDeclarationSyntax> members, bool normalizeFirst) {
        if (members.Count == 0) {
            return members;
        }

        // KEEP IN SYNC with member-groups: the coupled set is the same block that pass gathers, so the
        // spacing here keeps it one tight unit even when it mixes fields and properties.
        var coupled = InitializerCoupling.CoupledMemberNames(members: members);
        var first = members[0];
        var firstLead = first.GetLeadingTrivia();
        var result = new List<MemberDeclarationSyntax> {
            ((normalizeFirst && !firstLead.Any(predicate: static trivia => trivia.IsDirective))
                ? first.WithLeadingTrivia(trivia: RewriteShaping.SetBlankLines(desired: 0, lead: firstLead))
                : first),
        };

        for (var index = 1; (index < members.Count); index++) {
            var previous = members[(index - 1)];
            var current = members[index];
            var lead = current.GetLeadingTrivia();

            if (lead.Any(predicate: static trivia => trivia.IsDirective)) {
                result.Add(item: current);

                continue;
            }

            var sameSubject = ((Bucket(coupled: coupled, member: previous) == Bucket(coupled: coupled, member: current))
                && (RewriteShaping.AccessibilityScope(member: previous) == RewriteShaping.AccessibilityScope(member: current)));

            result.Add(item: current.WithLeadingTrivia(trivia: RewriteShaping.SetBlankLines(desired: (sameSubject ? 0 : 1), lead: lead)));
        }

        return SyntaxFactory.List(nodes: result);
    }
    private static string Bucket(MemberDeclarationSyntax member, HashSet<string> coupled) => member switch {
        FieldDeclarationSyntax field => FieldBucket(coupled: coupled, field: field),
        EventFieldDeclarationSyntax => "event",
        PropertyDeclarationSyntax property => (coupled.Contains(item: property.Identifier.ValueText) ? "member-coupled" : "property"),
        IndexerDeclarationSyntax => "indexer",
        ConstructorDeclarationSyntax => "ctor",
        DestructorDeclarationSyntax => "dtor",
        MethodDeclarationSyntax => "method",
        OperatorDeclarationSyntax => "operator",
        ConversionOperatorDeclarationSyntax => "operator",
        DelegateDeclarationSyntax => "delegate",
        BaseTypeDeclarationSyntax => "type",
        _ => member.Kind().ToString()
    };
    // The field buckets mirror member-groups' emission order: the coupled block, then one bucket per
    // storage kind, so each organizational group reads as one blank-line-delimited unit.
    private static string FieldBucket(FieldDeclarationSyntax field, HashSet<string> coupled) {
        if (coupled.Contains(item: field.Declaration.Variables[0].Identifier.ValueText)) {
            return "member-coupled";
        }

        var isConst = false;
        var isStatic = false;
        var isReadonly = false;

        foreach (var modifier in field.Modifiers) {
            isConst |= modifier.IsKind(kind: SyntaxKind.ConstKeyword);
            isStatic |= modifier.IsKind(kind: SyntaxKind.StaticKeyword);
            isReadonly |= modifier.IsKind(kind: SyntaxKind.ReadOnlyKeyword);
        }

        return (isConst
            ? "const"
            : (isStatic
                ? (isReadonly ? "field-static-readonly" : "field-static")
                : (isReadonly ? "field-readonly" : "field")));
    }
}
