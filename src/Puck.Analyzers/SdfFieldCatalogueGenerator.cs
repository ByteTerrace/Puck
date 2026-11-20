using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Puck.SignedDistance;

namespace Puck.Analyzers;

[Generator(LanguageNames.CSharp)]
public sealed class SdfFieldCatalogueGenerator : IIncrementalGenerator {
    private const string FieldNamespace = "Puck.SignedDistance";

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var entries = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: FieldNamespace + ".SdfFieldEntryAttribute",
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (entry, cancellationToken) => Read(entry, cancellationToken)
        );
        context.RegisterSourceOutput(entries.Collect(), static (output, entries) => Emit(output, entries));
    }

    private static Entry Read(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken) {
        var type = (INamedTypeSymbol)context.TargetSymbol;
        var catalogue = type.ContainingType;
        var entryContract = type.BaseType!;
        var facets = ImmutableArray.CreateBuilder<Facet>();
        var shaders = ImmutableArray.CreateBuilder<string>();
        string? formula = null;
        Location? invalidFormula = null;

        foreach (var member in entryContract.GetMembers()) {
            if (member is not IPropertySymbol { IsAbstract: true } property) { continue; }
            if (property.Type.Name == "SdfShaderAction") {
                shaders.Add(property.Name);
                continue;
            }
            var implementation = type.GetMembers(property.Name).OfType<IPropertySymbol>().SingleOrDefault();
            if (implementation is null) { continue; } // The C# compiler reports the missing abstract override.

            var syntax = (PropertyDeclarationSyntax)implementation.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken);
            if (property.Type.Name == nameof(SdfFormula)) {
                var formulaExpression = syntax.ExpressionBody?.Expression;
                var symbol = formulaExpression is null ? null : context.SemanticModel.GetSymbolInfo(formulaExpression, cancellationToken).Symbol;
                if (symbol is IFieldSymbol { IsStatic: true } field && field.ContainingType.Name == nameof(SdfFieldFormulas)) {
                    formula = field.Name;
                } else if (formulaExpression?.IsKind(SyntaxKind.NullLiteralExpression) != true) {
                    invalidFormula = syntax.Identifier.GetLocation();
                }
                continue;
            }
            var value = syntax.ExpressionBody is { Expression: { } expression }
                ? context.SemanticModel.Compilation.GetSemanticModel(expression.SyntaxTree).GetConstantValue(expression, cancellationToken)
                : default;
            var facetType = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var constant = value.HasValue ? FormatConstant(value.Value, facetType) : null;
            facets.Add(new Facet(property.Name, facetType, constant, syntax.Identifier.GetLocation()));
        }

        var arguments = catalogue.GetAttributes().Single(static attribute => attribute.AttributeClass?.Name == "SdfFieldCatalogueAttribute").ConstructorArguments;
        return new Entry(
            catalogue.Name,
            type.Name,
            (int)context.Attributes[0].ConstructorArguments[0].Value!,
            (string)arguments[0].Value!,
            (bool)arguments[1].Value!,
            Documentation(catalogue, cancellationToken),
            Documentation(type, cancellationToken),
            facets.ToImmutable(),
            shaders.ToImmutable(),
            formula,
            invalidFormula
        );
    }

    private static string? FormatConstant(object? value, string type) => value switch {
        bool flag => flag ? "true" : "false",
        int or uint or byte or ushort => "(" + type + ")" + Convert.ToString(value, CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string Documentation(INamedTypeSymbol type, CancellationToken cancellationToken) => string.Concat(
        type.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken).GetLeadingTrivia()
            .Where(static trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .Select(static trivia => trivia.ToFullString())
    );

    private static readonly DiagnosticDescriptor NonconstantFacet = new(
        id: "PUCKFIELD001",
        title: "Field facets must be constants",
        messageFormat: "Field facet '{0}' must be a constant expression",
        category: "Puck.Field",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    private static readonly DiagnosticDescriptor InvalidFormula = new(
        id: "PUCKFIELD002",
        title: "Field formula must name a shared definition",
        messageFormat: "Use a field from SdfFieldFormulas or an explicit null formula",
        category: "Puck.Field",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static void Emit(SourceProductionContext context, ImmutableArray<Entry> entries) {
        foreach (var group in entries.GroupBy(static entry => entry.Catalogue)) {
            var ordered = group.OrderBy(static entry => entry.Id).ToArray();
            var catalogue = group.Key;
            var discriminator = ordered[0].Discriminator;
            var source = new StringBuilder("// <auto-generated/>\nnamespace Puck.SignedDistance;\n\n");
            foreach (var entry in ordered) {
                if (entry.InvalidFormula is { } location) {
                    context.ReportDiagnostic(Diagnostic.Create(InvalidFormula, location));
                }
            }

            source.AppendLine("#pragma warning disable CS1591 // Preserve the catalogue's documented enum members.");
            source.Append(ordered[0].CatalogueDocumentation).AppendLine($"public enum {discriminator}{(ordered[0].Unsigned ? " : uint" : "")} {{");
            foreach (var entry in ordered) {
                source.Append(entry.Documentation);
                source.AppendLine($"    {entry.Name} = {entry.Id.ToString(CultureInfo.InvariantCulture)},");
            }
            source.AppendLine("}");
            source.AppendLine("#pragma warning restore CS1591");
            source.AppendLine();
            source.AppendLine($"public static partial class {catalogue} {{");
            source.AppendLine($"    internal static bool Contains({discriminator} value) => value is {string.Join(" or ", ordered.Select(entry => discriminator + "." + entry.Name))};");
            foreach (var facet in ordered.SelectMany(static entry => entry.Facets).GroupBy(static facet => (facet.Name, facet.Type))) {
                var name = facet.Key.Name == "Resources" ? "GetResources" : facet.Key.Name;
                source.AppendLine($"    internal static {facet.Key.Type} {name}Of({discriminator} value) => value switch {{");
                foreach (var entry in ordered) {
                    foreach (var arm in entry.Facets.Where(arm => arm.Name == facet.Key.Name)) {
                        if (arm.Constant is { } constant) {
                            source.AppendLine($"        {discriminator}.{entry.Name} => {constant},");
                        } else {
                            context.ReportDiagnostic(Diagnostic.Create(NonconstantFacet, arm.Location, arm.Name));
                        }
                    }
                }
                source.AppendLine($"        _ => throw new global::System.ArgumentOutOfRangeException(nameof(value), value, \"No {facet.Key.Name} facet for this field entry.\"),");
                source.AppendLine("    };");
            }
            foreach (var shader in ordered[0].Shaders) {
                source.AppendLine("    /// <summary>Enumerates the shader actions for this reader in encoding order.</summary>");
                source.AppendLine($"    public static global::System.Collections.Generic.IEnumerable<({discriminator} Value, SdfShaderAction Action)> {shader}Actions() {{");
                foreach (var entry in ordered) {
                    source.AppendLine($"        yield return ({discriminator}.{entry.Name}, new {entry.Name}().{shader});");
                }
                source.AppendLine("    }");
            }
            var formulas = ordered.Where(static entry => entry.Formula is not null).ToArray();
            if (formulas.Length != 0) {
                source.AppendLine("    /// <summary>Lowers the catalogue's shared formulas to shader functions.</summary>");
                source.AppendLine("    public static string GenerateFormulasHlsl() => SdfFormula.EmitAll(true,");
                source.AppendLine("        " + string.Join(", ", formulas.Select(static entry => "SdfFieldFormulas." + entry.Formula)) + ");");
            }
            source.AppendLine("}");
            context.AddSource(catalogue + ".g.cs", source.ToString());
            if (formulas.Length != 0) {
                EmitFormulaEvaluator(context, catalogue, discriminator, formulas);
            }
        }
    }

    private static void EmitFormulaEvaluator(SourceProductionContext context, string catalogue, string discriminator, Entry[] entries) {
        var formulas = entries.Select(entry => (Entry: entry, Formula: GetFormula(entry.Formula!))).ToArray();
        var source = new StringBuilder("// <auto-generated/>\nusing Puck.Maths;\n\nnamespace Puck.SignedDistance.Queries;\n\npublic sealed partial class SdfFieldEvaluator {\n");
        var shape = discriminator == "SdfShapeType";
        source.AppendLine(shape
            ? "    private static FixedQ4816 EvaluateShape(in CompiledInstruction instruction, FixedVector3 p, ref PowerWork powerWork) => ((SdfShapeType)instruction.Shape) switch {"
            : "    private static FixedQ4816 BlendShape(FixedQ4816 current, FixedQ4816 candidate, uint blend, FixedQ4816 smoothRadius) => ((SdfBlendOp)blend) switch {");
        foreach (var (entry, formula) in formulas) {
            source.AppendLine($"        {discriminator}.{entry.Name} => {formula.Dispatch(shader: false)},");
        }
        source.AppendLine(shape
            ? "        _ => EvaluateShapeEscape(instruction, p),"
            : "        _ => throw new global::System.ArgumentOutOfRangeException(nameof(blend), blend, \"No field blend formula for this value.\"),");
        source.AppendLine("    };");
        source.Append(SdfFormula.EmitAll(false, formulas.Select(static item => item.Formula).ToArray()));
        source.AppendLine("}");
        context.AddSource(catalogue + ".Formulas.g.cs", source.ToString());
    }

    private static SdfFormula GetFormula(string name) =>
        (SdfFormula)typeof(SdfFieldFormulas).GetField(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;

    private sealed record Entry(
        string Catalogue, string Name, int Id, string Discriminator, bool Unsigned, string CatalogueDocumentation,
        string Documentation, ImmutableArray<Facet> Facets, ImmutableArray<string> Shaders, string? Formula, Location? InvalidFormula
    );
    private sealed record Facet(string Name, string Type, string? Constant, Location Location);
}
