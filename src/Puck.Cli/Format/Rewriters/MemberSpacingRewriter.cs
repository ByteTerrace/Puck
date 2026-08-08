using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The blank-line-between-members normalizer (the `member-spacing` pass): ZERO blank lines between
// members that share BOTH kind and accessibility scope (so a run of `public` methods packs tight
// whether each body is one line or many, and attributed one-liners like the interop declaration groups
// stay tight too), and exactly ONE blank line at every subject shift (a member-kind change OR an
// accessibility-scope change, e.g. a `protected override` method followed by a `public` one). Comment-
// and #directive-led members are left alone. This is the one spacing rule .editorconfig and the SDK
// formatter cannot express.
internal sealed class MemberSpacingRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => Fix(node: (TypeDeclarationSyntax)base.VisitClassDeclaration(node: node)!);
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => Fix(node: (TypeDeclarationSyntax)base.VisitStructDeclaration(node: node)!);
    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => Fix(node: (TypeDeclarationSyntax)base.VisitInterfaceDeclaration(node: node)!);
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => Fix(node: (TypeDeclarationSyntax)base.VisitRecordDeclaration(node: node)!);
    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node) =>
        Fix(node: (CompilationUnitSyntax)base.VisitCompilationUnit(node: node)!);
    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node) =>
        Fix(node: (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node: node)!);
    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node) =>
        Fix(node: (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node: node)!);

    private static TypeDeclarationSyntax Fix(TypeDeclarationSyntax node) =>
        node.WithMembers(members: Normalize(members: node.Members));
    private static SyntaxNode Fix(SyntaxNode node) => node switch {
        CompilationUnitSyntax compilationUnit => compilationUnit.WithMembers(members: Normalize(members: compilationUnit.Members)),
        NamespaceDeclarationSyntax @namespace => @namespace.WithMembers(members: Normalize(members: @namespace.Members)),
        FileScopedNamespaceDeclarationSyntax @namespace => @namespace.WithMembers(members: Normalize(members: @namespace.Members)),
        _ => throw new ArgumentException(message: "A member container must be a compilation unit or namespace.", paramName: nameof(node)),
    };
    private static SyntaxList<MemberDeclarationSyntax> Normalize(SyntaxList<MemberDeclarationSyntax> members) {
        if (members.Count < 2) {
            return members;
        }

        var result = new List<MemberDeclarationSyntax> { members[0] };

        for (var index = 1; (index < members.Count); index++) {
            var previous = members[(index - 1)];
            var current = members[index];
            var lead = current.GetLeadingTrivia();

            if (RewriteShaping.HasCommentOrDirective(trivia: lead)) {
                result.Add(item: current);

                continue;
            }

            var sameSubject = ((Bucket(member: previous) == Bucket(member: current))
                && (RewriteShaping.AccessibilityScope(member: previous) == RewriteShaping.AccessibilityScope(member: current)));

            result.Add(item: current.WithLeadingTrivia(trivia: RewriteShaping.SetBlankLines(lead: lead, desired: (sameSubject ? 0 : 1))));
        }

        return SyntaxFactory.List(nodes: result);
    }
    private static string Bucket(MemberDeclarationSyntax member) => member switch {
        FieldDeclarationSyntax field => (field.Modifiers.Any(predicate: static modifier => modifier.IsKind(kind: SyntaxKind.ConstKeyword)) ? "const" : "field"),
        EventFieldDeclarationSyntax => "event",
        PropertyDeclarationSyntax => "property",
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
}
