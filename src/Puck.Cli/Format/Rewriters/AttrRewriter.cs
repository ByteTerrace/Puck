using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The attribute-ordering normalizer (the `attr-order` pass): a declaration's multiple attributes each
// get their OWN attribute list on their own line, sorted alphabetically by attribute name.
// Single-attribute declarations and the rare combined [A, B] list are left alone. Only the
// attribute-list ORDER changes (each slot keeps its own trivia, so the multi-line layout is preserved).
internal sealed class AttrRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) => Reorder(node: ((MethodDeclarationSyntax)base.VisitMethodDeclaration(node: node)!));
    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) => Reorder(node: ((ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node: node)!));
    public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node) => Reorder(node: ((DestructorDeclarationSyntax)base.VisitDestructorDeclaration(node: node)!));
    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node) => Reorder(node: ((OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node: node)!));
    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) => Reorder(node: ((ConversionOperatorDeclarationSyntax)base.VisitConversionOperatorDeclaration(node: node)!));
    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node) => Reorder(node: ((PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node: node)!));
    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node) => Reorder(node: ((IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node: node)!));
    public override SyntaxNode? VisitEventDeclaration(EventDeclarationSyntax node) => Reorder(node: ((EventDeclarationSyntax)base.VisitEventDeclaration(node: node)!));
    public override SyntaxNode? VisitEventFieldDeclaration(EventFieldDeclarationSyntax node) => Reorder(node: ((EventFieldDeclarationSyntax)base.VisitEventFieldDeclaration(node: node)!));
    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) => Reorder(node: ((FieldDeclarationSyntax)base.VisitFieldDeclaration(node: node)!));
    public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node) => Reorder(node: ((DelegateDeclarationSyntax)base.VisitDelegateDeclaration(node: node)!));
    public override SyntaxNode? VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node) => Reorder(node: ((EnumMemberDeclarationSyntax)base.VisitEnumMemberDeclaration(node: node)!));
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => Reorder(node: ((ClassDeclarationSyntax)base.VisitClassDeclaration(node: node)!));
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => Reorder(node: ((StructDeclarationSyntax)base.VisitStructDeclaration(node: node)!));
    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => Reorder(node: ((InterfaceDeclarationSyntax)base.VisitInterfaceDeclaration(node: node)!));
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => Reorder(node: ((RecordDeclarationSyntax)base.VisitRecordDeclaration(node: node)!));
    public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node) => Reorder(node: ((EnumDeclarationSyntax)base.VisitEnumDeclaration(node: node)!));

    // Moves each attribute (with its own trailing newline) into its alphabetical slot and reassigns each
    // slot's leading trivia, preserving the one-per-line layout.
    private static T Reorder<T>(T node) where T : MemberDeclarationSyntax {
        var lists = node.AttributeLists;

        if ((lists.Count <= 1) || lists.Any(predicate: static list => (list.Attributes.Count != 1))) {
            return node;
        }

        // Slot trivia stays put while the attributes move, so a comment or an #if/#endif around one
        // attribute would end up describing — or conditionally compiling — a different one. Leave the
        // whole declaration alone rather than invert what the annotation guards.
        if (lists.Any(predicate: static list => RewriteShaping.IsAnnotated(node: list))) {
            return node;
        }

        var order = Enumerable.Range(start: 0, count: lists.Count)
            .OrderBy(keySelector: index => SimpleName(attribute: lists[index].Attributes[0]), comparer: StringComparer.Ordinal)
            .ToList();

        if (order.SequenceEqual(second: Enumerable.Range(start: 0, count: lists.Count))) {
            return node;
        }

        var newLists = new List<AttributeListSyntax>();

        for (var slot = 0; (slot < lists.Count); slot++) {
            newLists.Add(item: lists[order[slot]].WithLeadingTrivia(trivia: lists[slot].GetLeadingTrivia()));
        }

        return ((T)node.WithAttributeLists(attributeLists: SyntaxFactory.List(nodes: newLists)));
    }
    private static string SimpleName(AttributeSyntax attribute) => attribute.Name switch {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => attribute.Name.ToString()
    };
}
