using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Analyzers;

/// <summary>Writes each <c>.puck</c> syntax node's <c>Children</c> and <c>MapChildren</c> from the node's own
/// declaration, so the record is the one description of its children: every walk reads <c>Children</c> and every
/// rebuild goes through <c>MapChildren</c>, and a property added to a node reaches both when it compiles.</summary>
/// <remarks>A child is a settable property whose type is a syntax node or an <c>IReadOnlyList</c> of them, taken in
/// declaration order. A node that carries its children some other way (an interpolated string's holes, an operand's
/// atoms) writes both members by hand, and the generator leaves it alone; a property holding nodes through a carrier
/// type on a generated node is <c>PUCKSYNTAX001</c>. A concrete node that is not a top-level <c>partial</c> record
/// gets no members and fails to compile until it is one or writes them.</remarks>
[Generator(LanguageNames.CSharp)]
public sealed class SyntaxChildrenGenerator : IIncrementalGenerator {
    private const string AnnotationAttribute = RootNamespace + ".SyntaxAnnotationAttribute";
    private const string ChildrenMember = "Children";
    private const string MapMember = "MapChildren";
    private const string RootName = "SyntaxNode";
    private const string RootNamespace = "Puck.Transpiler.Ast";

    private static readonly DiagnosticDescriptor CarriedChild = new(
        id: "PUCKSYNTAX001",
        title: "A syntax node's children must be declared",
        messageFormat: "'{0}' holds syntax nodes through '{1}', which is not a syntax node: write Children and MapChildren by hand on this node, or mark the property [SyntaxAnnotation]",
        category: "Puck.Syntax",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        // Only the part of a record that carries its base list is read, so a record written in parts is read once.
        var nodes = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => ((node is RecordDeclarationSyntax { BaseList: not null } record) && record.Modifiers.Any(SyntaxKind.PartialKeyword)),
            transform: static (syntax, cancellationToken) => Read(syntax, cancellationToken)
        ).Where(static node => (node is not null));

        context.RegisterSourceOutput(nodes.Collect(), static (output, nodes) => Emit(output, nodes!));
    }

    private static bool IsNode(ITypeSymbol? type) => NamedTypes.Derives(type: type, @namespace: RootNamespace, name: RootName);
    private static Node? Read(GeneratorSyntaxContext context, CancellationToken cancellationToken) {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node, cancellationToken) is not INamedTypeSymbol { IsAbstract: false, ContainingType: null } type) {
            return null;
        }
        if (!IsNode(type.BaseType) || (type.GetMembers(ChildrenMember).Length != 0) || (type.GetMembers(MapMember).Length != 0)) {
            return null;
        }
        var properties = new List<IPropertySymbol>();

        for (ITypeSymbol current = type; !NamedTypes.Is(type: current, @namespace: RootNamespace, name: RootName); current = current.BaseType!) {
            properties.AddRange(current.GetMembers().OfType<IPropertySymbol>().Where(static property => (
                !property.IsStatic &&
                !property.IsIndexer &&
                (property.SetMethod is not null) &&
                (property.DeclaredAccessibility == Accessibility.Public) &&
                !NamedTypes.Carries(attribute: AnnotationAttribute, symbol: property)
            )));
        }
        var children = new StringBuilder();
        Location? carried = null;
        var carrier = string.Empty;
        var carriedName = string.Empty;

        foreach (var property in properties.OrderBy(static property => (property.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0))) {
            var optional = (property.NullableAnnotation == NullableAnnotation.Annotated);
            var element = (((property.Type is INamedTypeSymbol { Name: "IReadOnlyList", TypeArguments.Length: 1 } list) && (list.ContainingNamespace.ToDisplayString() == "System.Collections.Generic"))
                ? list.TypeArguments[0]
                : null);

            if (IsNode(element ?? property.Type)) {
                children.Append((element is null) ? 'o' : 'm').Append(optional ? '?' : '!').Append(property.Name).Append(';');
            } else if (
                (carried is null) &&
                SymbolEqualityComparer.Default.Equals((element ?? property.Type).ContainingAssembly, type.ContainingAssembly) &&
                Carries(element ?? property.Type)
            ) {
                carried = property.Locations.FirstOrDefault();
                carrier = (element ?? property.Type).Name;
                carriedName = property.Name;
            }
        }
        return new Node(type.ContainingNamespace.ToDisplayString(), type.Name, children.ToString(), carried, carriedName, carrier);
    }
    // Whether a type that is not itself a node holds one, directly or through a nested case of itself.
    private static bool Carries(ITypeSymbol type) =>
        type.GetMembers().OfType<IPropertySymbol>().Any(static property => IsNode(property.Type)) ||
        type.GetTypeMembers().Any(static nested => nested.GetMembers().OfType<IPropertySymbol>().Any(static property => IsNode(property.Type)));

    private static void Emit(SourceProductionContext context, ImmutableArray<Node> nodes) {
        if (nodes.IsEmpty) {
            return;
        }
        var source = new StringBuilder("// <auto-generated/>\n#nullable enable\n");

        foreach (var group in nodes.GroupBy(static node => node.Namespace).OrderBy(static group => group.Key, StringComparer.Ordinal)) {
            source.Append("namespace ").Append(group.Key).Append(" {\n");
            foreach (var node in group.OrderBy(static node => node.Name, StringComparer.Ordinal)) {
                if (node.Carried is { } location) {
                    context.ReportDiagnostic(Diagnostic.Create(CarriedChild, location, node.CarriedName, node.Carrier));
                }
                EmitNode(source, node);
            }
            source.Append("}\n");
        }
        context.AddSource("SyntaxChildren.g.cs", source.ToString());
    }
    private static void EmitNode(StringBuilder source, Node node) {
        var children = node.Children.Split([';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static child => (Many: (child[0] == 'm'), Optional: (child[1] == '?'), Name: child.Substring(2)))
            .ToArray();
        const string Root = "global::" + RootNamespace + "." + RootName;
        const string List = "global::System.Collections.Generic.IReadOnlyList<" + Root + ">";

        source.Append("    partial record ").Append(node.Name).Append(" {\n");
        source.Append("        /// <inheritdoc/>\n        public override ").Append(List).Append(' ').Append(ChildrenMember).Append("()");
        if (children.Length == 0) {
            source.Append(" => [];\n");
        } else if ((children.Length == 1) && children[0].Many && !children[0].Optional) {
            source.Append(" => ").Append(children[0].Name).Append(";\n");
        } else if (children.All(static child => (!child.Many && !child.Optional))) {
            source.Append(" => [").Append(string.Join(", ", children.Select(static child => child.Name))).Append("];\n");
        } else {
            source.Append(" {\n            var children = new global::System.Collections.Generic.List<").Append(Root).Append(">();\n");
            foreach (var child in children) {
                source.Append("            ");
                if (child.Optional) { source.Append("if (").Append(child.Name).Append(" is not null) { "); }
                source.Append(child.Many ? "children.AddRange(" : "children.Add(").Append(child.Name).Append(");");
                if (child.Optional) { source.Append(" }"); }
                source.Append('\n');
            }
            source.Append("            return children;\n        }\n");
        }
        source.Append("        /// <inheritdoc/>\n        public override ").Append(Root).Append(' ').Append(MapMember).Append("(global::").Append(RootNamespace).Append(".ISyntaxMap map)");
        if (children.Length == 0) {
            source.Append(" => this;\n");
        } else {
            source.Append(" {\n");
            for (var index = 0; (index < children.Length); index++) {
                var child = children[index];
                var call = (child.Many
                    ? (child.Optional ? $"(({child.Name} is null) ? null : map.Many(this, {child.Name}))" : $"map.Many(this, {child.Name})")
                    : $"map.{(child.Optional ? "Optional" : "One")}(this, {child.Name})");

                source.Append("            var child").Append(index).Append(" = ").Append(call).Append(";\n");
            }
            source.Append("            return ((")
                .Append(string.Join(" && ", children.Select(static (child, index) => $"ReferenceEquals(child{index}, {child.Name})")))
                .Append(") ? this : (this with { ")
                .Append(string.Join(", ", children.Select(static (child, index) => $"{child.Name} = child{index}")))
                .Append(" }));\n        }\n");
        }
        source.Append("    }\n");
    }

    private sealed record Node(string Namespace, string Name, string Children, Location? Carried, string CarriedName, string Carrier);
}
