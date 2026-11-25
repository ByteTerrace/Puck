using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Puck.Analyzers;

/// <summary>
/// PUCKSCHEMA001: a public property of a type the generated world model table lists is missing from that table, so
/// the JSON schemas, the dashboard's TypeScript, the name registry and the table itself were generated before the
/// property existed. <c>WorldModelShape.generated.cs</c> (<c>puck schema</c>) lists every member of every type
/// reachable from the world definition; the analyzer reads that file's <c>T(typeof(…), …, members, properties)</c>
/// rows and compares each listed type's public instance properties, base types included, with the names the row
/// carries (a constructed generic type, which the generator lists without members, is skipped). A property marked <c>[JsonIgnore]</c> is not a document member, and a table entry whose member no longer
/// exists already fails the table's own getter at compile time.
/// <para>
/// The rule reads only the table, so it holds when the generated schemas, TypeScript and registry were written by the
/// same <c>puck schema</c> run that wrote the table; one that changes a schema without changing a member (a
/// documentation comment, an enum value) is caught by <c>puck schema --check</c>.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModelShapeAnalyzer : DiagnosticAnalyzer {
    private const string TableFile = "WorldModelShape.generated.cs";
    private const string TableMethod = "T";
    private const int ShapeArguments = 7;
    private const int MembersArgument = 5;
    private const int PropertiesArgument = 6;
    private const int MemberNameArgument = 3;

    /// <summary>PUCKSCHEMA001: the generated model table does not list a public property.</summary>
    public static readonly DiagnosticDescriptor Puckschema001StaleTable = new(
        id: "PUCKSCHEMA001",
        title: "A world document member is missing from the generated model table",
        messageFormat: "'{0}.{1}' is not in WorldModelShape.generated.cs, so the generated schemas, TypeScript, name registry and model table are stale; run `puck schema --bootstrap`, which regenerates all of them (a text member needs [DocumentName] or [DocumentText] first), then rebuild",
        category: "Puck.Schema",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "puck schema --bootstrap rebuilds the model from source without the stale table, so it works while this error stands, and writes every generated product of a model edit in one run."
    );

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(item: Puckschema001StaleTable);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        // The table is auto-generated, and reading it is the whole rule.
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSemanticModelAction(action: Analyze);
    }

    private static void Analyze(SemanticModelAnalysisContext context) {
        var tree = context.SemanticModel.SyntaxTree;

        if (!tree.FilePath.EndsWith(value: TableFile, comparisonType: StringComparison.Ordinal)) {
            return;
        }

        foreach (var row in tree.GetRoot(cancellationToken: context.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>()) {
            if (
                (row.Expression is not IdentifierNameSyntax { Identifier.ValueText: TableMethod }) ||
                (row.ArgumentList.Arguments.Count != ShapeArguments) ||
                (row.ArgumentList.Arguments[index: 0].Expression is not TypeOfExpressionSyntax typeOf) ||
                (context.SemanticModel.GetTypeInfo(expression: typeOf.Type, cancellationToken: context.CancellationToken).Type is not INamedTypeSymbol { IsGenericType: false } type) ||
                (type.ContainingNamespace?.ToDisplayString().StartsWith(value: "System", comparisonType: StringComparison.Ordinal) != false)
            ) {
                continue;
            }

            var listed = new HashSet<string>(comparer: StringComparer.Ordinal);

            AddMembers(listed: listed, argument: row.ArgumentList.Arguments[index: MembersArgument].Expression);
            AddMembers(listed: listed, argument: row.ArgumentList.Arguments[index: PropertiesArgument].Expression);

            foreach (var property in DocumentProperties(type: type)) {
                if (listed.Add(item: property.Name)) {
                    context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                        descriptor: Puckschema001StaleTable,
                        location: (property.Locations.FirstOrDefault(predicate: static location => location.IsInSource) ?? row.GetLocation()),
                        messageArgs: [type.Name, property.Name]
                    ));
                }
            }
        }
    }
    private static void AddMembers(HashSet<string> listed, ExpressionSyntax argument) {
        if (argument is not CollectionExpressionSyntax collection) {
            return;
        }

        foreach (var element in collection.Elements.OfType<ExpressionElementSyntax>()) {
            if (
                (element.Expression is InvocationExpressionSyntax { ArgumentList.Arguments: { Count: > MemberNameArgument } arguments }) &&
                (arguments[index: MemberNameArgument].Expression is LiteralExpressionSyntax { Token.Value: string name })
            ) {
                _ = listed.Add(item: name);
            }
        }
    }
    // What the serializer reads or writes: public instance properties of the type and its base types, indexers and
    // [JsonIgnore] members aside. An interface answers for its own members only, as reflection does.
    private static IEnumerable<IPropertySymbol> DocumentProperties(INamedTypeSymbol type) {
        for (var level = type; (level is not null) && (level.SpecialType != SpecialType.System_Object); level = ((level.TypeKind == TypeKind.Interface) ? null : level.BaseType)) {
            foreach (var property in level.GetMembers().OfType<IPropertySymbol>()) {
                if (
                    (property.DeclaredAccessibility == Accessibility.Public) &&
                    !property.IsStatic &&
                    !property.IsIndexer &&
                    !property.IsImplicitlyDeclared &&
                    !property.GetAttributes().Any(predicate: static attribute => IgnoresAlways(attribute: attribute))
                ) {
                    yield return property;
                }
            }
        }
    }
    private static bool IgnoresAlways(AttributeData attribute) =>
        (attribute.AttributeClass is { Name: "JsonIgnoreAttribute", ContainingNamespace: { Name: "Serialization" } }) &&
        (attribute.NamedArguments.All(predicate: static pair => (pair.Key != "Condition") || (Convert.ToInt32(value: pair.Value.Value) == 1)));
}
