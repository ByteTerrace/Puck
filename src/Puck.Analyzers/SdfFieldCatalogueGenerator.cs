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
        var formulas = ImmutableArray.CreateBuilder<FormulaUse>();
        Location? invalidFormula = null;
        // A facet whose action enum carries a Formula member (the C# point reader) says whether the generated formula
        // or a hand-written escape answers; it must agree with the entry's Formula.
        (bool Formula, Location Location)? reader = null;

        foreach (var member in entryContract.GetMembers()) {
            if (member is not IPropertySymbol property) { continue; }
            // A formula an entry may leave out is virtual and null on the contract; every other facet is abstract.
            if (!property.IsAbstract && !(property.IsVirtual && property.Type.Name == nameof(SdfFormula))) { continue; }
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
                    formulas.Add(new FormulaUse(property.Name, field.Name));
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
            if (property.Type.TypeKind == TypeKind.Enum && property.Type.GetMembers("Formula").OfType<IFieldSymbol>().FirstOrDefault() is { HasConstantValue: true } formulaMember) {
                reader = (value.HasValue && Equals(value.Value, formulaMember.ConstantValue), syntax.Identifier.GetLocation());
            }
        }
        var readerMismatch = reader is { } declared && declared.Formula != formulas.Any(static use => use.Property == "Formula") ? declared.Location : null;

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
            formulas.ToImmutable(),
            invalidFormula,
            readerMismatch
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
        messageFormat: "Use a field from SdfFieldFormulas, or null for an entry whose readers are hand-written target escapes",
        category: "Puck.Field",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    private static readonly DiagnosticDescriptor ReaderMismatch = new(
        id: "PUCKFIELD003",
        title: "Field reader must match the formula",
        messageFormat: "Declare the Formula reader exactly when the entry names a shared formula",
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
                if (entry.ReaderMismatch is { } reader) {
                    context.ReportDiagnostic(Diagnostic.Create(ReaderMismatch, reader));
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
            var formulas = ordered.SelectMany(static entry => entry.Formulas.Select(use => (Entry: entry, Use: use))).ToArray();
            if (formulas.Length != 0) {
                source.AppendLine("    /// <summary>Lowers the catalogue's shared formulas to shader functions.</summary>");
                source.AppendLine("    public static string GenerateFormulasHlsl() => SdfFormula.EmitAll(true,");
                source.AppendLine("        " + string.Join(", ", formulas.Select(static item => "SdfFieldFormulas." + item.Use.Field).Distinct()) + ");");
                source.AppendLine("    /// <summary>Lowers the catalogue's shared formulas to the gradient walk's dual overloads.</summary>");
                source.AppendLine("    public static string GenerateDualFormulasHlsl() => SdfFormula.EmitAllDual(");
                source.AppendLine("        " + string.Join(", ", formulas.Select(static item => "SdfFieldFormulas." + item.Use.Field).Distinct()) + ");");
            }
            source.AppendLine("}");
            context.AddSource(catalogue + ".g.cs", source.ToString());
            if (formulas.Length != 0) {
                EmitFormulaEvaluator(context, catalogue, discriminator, formulas.Select(static item => (item.Entry, item.Use.Property, Formula: GetFormula(item.Use.Field))).ToArray());
            }
        }
    }

    // The C# readers' side of each catalogue's formulas: the dispatch each walk calls, then the formulas, once for the
    // fixed-point reader and once for the wide one (SdfFormula's wide lowering), which overloads the same names over
    // its own carriers and reads no material.
    private static void EmitFormulaEvaluator(SourceProductionContext context, string catalogue, string discriminator, (Entry Entry, string Property, SdfFormula Formula)[] formulas) {
        var source = new StringBuilder("// <auto-generated/>\nusing System.Runtime.CompilerServices;\nusing Puck.Maths;\n\nnamespace Puck.SignedDistance.Queries;\n\npublic sealed partial class SdfFieldEvaluator {\n");

        foreach (var wide in new[] { false, true }) {
            var held = formulas.Where(item => !wide || item.Formula.Widens).ToArray();
            string Carried(string signature) => wide
                ? SdfFormula.Widen(signature).Replace("CompiledInstruction", "WideOperands").Replace(", ref PowerWork powerWork", "")
                : signature;
            string Dispatch(SdfFormula formula) => wide ? formula.DispatchWide() : formula.Dispatch(shader: false);
            void Switch(string signature, string subject, string property, string fallback, Func<SdfFormula, bool>? admits = null) {
                source.AppendLine($"    private static {Carried(signature)} => {subject} switch {{");
                foreach (var (entry, _, formula) in held.Where(item => item.Property == property && admits?.Invoke(item.Formula) != false)) {
                    source.AppendLine($"        {discriminator}.{entry.Name} => {Dispatch(formula)},");
                }
                source.AppendLine($"        _ => {fallback},");
                source.AppendLine("    };");
            }

            switch (discriminator) {
                case "SdfShapeType":
                    // A shape without a formula is a target escape: its point reader is hand-written
                    // (EvaluateShapeEscape), as is its shader action.
                    Switch("FixedQ4816 EvaluateShape(in CompiledInstruction instruction, FixedVector3 p, ref PowerWork powerWork)", "((SdfShapeType)instruction.Shape)", "Formula",
                        "EvaluateShapeEscape(instruction: instruction, p: p)");
                    break;
                case "SdfBlendOp":
                    // operand is a scope pop's own lane (PopOperand), zero at a shape's join.
                    Switch("FixedQ4816 BlendShape(FixedQ4816 current, FixedQ4816 candidate, uint blend, FixedQ4816 smoothRadius, FixedQ4816 operand)", "((SdfBlendOp)blend)", "Formula",
                        "throw new global::System.ArgumentOutOfRangeException(nameof(blend), blend, \"No field blend formula for this value.\")");
                    if (!wide) {
                        Switch("bool CandidateWins(FixedQ4816 current, FixedQ4816 candidate, uint blend, FixedQ4816 operand)", "((SdfBlendOp)blend)", "Winner",
                            "throw new global::System.ArgumentOutOfRangeException(nameof(blend), blend, \"No material winner for this value.\")");
                    }
                    // A stairs pop carries its step count in Data1.z; a blend with an operand formula computes its own.
                    Switch("FixedQ4816 PopOperand(in CompiledInstruction instruction)", "((SdfBlendOp)instruction.Blend)", "Operand", "instruction.Data1Z");
                    break;
                case "SdfOp":
                    // Each op's formula bound to its instruction's lanes, taking the walk state it reads.
                    foreach (var (entry, property, formula) in held) {
                        var suffix = property == "DistanceFormula" ? "Distance" : formula.ResultType == SdfFormulaType.Vector3 ? "Point" : "Field";
                        var state = string.Concat(formula.FixedState("p", "distance", "distanceScale", "weight").Select(static item => $", {item.Type} {item.Name}"));
                        source.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
                        source.AppendLine("    private static " + Carried($"{(formula.ResultType == SdfFormulaType.Vector3 ? "FixedVector3" : "FixedQ4816")} {entry.Name}{suffix}(in CompiledInstruction instruction{state})") + $" => {Dispatch(formula)};");
                    }
                    // The walk's dispatch for the ops it does not name itself. A field op that reads more than the
                    // point and the running distance (a relief's mask weight) is called by name.
                    var unvalidated = "throw new global::System.Diagnostics.UnreachableException(\"The walk dispatched an op that has no formula of this kind.\")";
                    Switch("FixedVector3 MapPoint(in CompiledInstruction instruction, FixedVector3 p)", "instruction.Op", "Formula", unvalidated,
                        static formula => formula.ResultType == SdfFormulaType.Vector3);
                    Switch("FixedQ4816 EditField(in CompiledInstruction instruction, FixedVector3 p, FixedQ4816 distance)", "instruction.Op", "Formula", unvalidated,
                        static formula => formula.ResultType == SdfFormulaType.Scalar && !formula.FixedState("weight").Any());
                    break;
            }
            var lowered = held.Select(static item => item.Formula).ToArray();
            source.Append(wide ? SdfFormula.EmitAllWide(lowered) : SdfFormula.EmitAll(false, lowered));
        }

        // The gradient reader's side: each formula's fixed-point dual (SdfFormula's dual lowering over FixedGradient), and
        // a dispatch over the entries that have one; an entry without one answers DualUncovered, and the walk that met it
        // takes the stencil.
        var duals = SdfFormula.EmitAllFixedDual(out var covered, formulas.Select(static item => item.Formula).Distinct().ToArray());
        void DualSwitch(string signature, string subject, string property, string fallback, Func<SdfFormula, bool>? admits = null) {
            source.AppendLine($"    private static {signature} => {subject} switch {{");
            foreach (var (entry, _, formula) in formulas.Where(item => item.Property == property && covered.Contains(item.Formula) && admits?.Invoke(item.Formula) != false)) {
                source.AppendLine($"        {discriminator}.{entry.Name} => {formula.Dispatch(shader: false)},");
            }
            source.AppendLine($"        _ => {fallback},");
            source.AppendLine("    };");
        }
        switch (discriminator) {
            case "SdfShapeType":
                DualSwitch("FixedGradient EvaluateShape(in CompiledInstruction instruction, FixedGradient3 p, ref PowerWork powerWork)", "((SdfShapeType)instruction.Shape)", "Formula", "DualUncovered()");
                break;
            case "SdfBlendOp":
                DualSwitch("FixedGradient BlendShape(FixedGradient current, FixedGradient candidate, uint blend, FixedQ4816 smoothRadius, FixedQ4816 operand)", "((SdfBlendOp)blend)", "Formula", "DualUncovered()");
                break;
            case "SdfOp":
                DualSwitch("FixedGradient3 MapPoint(in CompiledInstruction instruction, FixedGradient3 p)", "instruction.Op", "Formula", "DualUncoveredPoint()",
                    static formula => formula.ResultType == SdfFormulaType.Vector3);
                DualSwitch("FixedGradient EditField(in CompiledInstruction instruction, FixedGradient3 p, FixedGradient distance)", "instruction.Op", "Formula", "DualUncovered()",
                    static formula => formula.ResultType == SdfFormulaType.Scalar && !formula.FixedState("weight").Any());
                break;
        }
        source.Append(duals);
        source.AppendLine("}");
        context.AddSource(catalogue + ".Formulas.g.cs", source.ToString());
    }

    private static SdfFormula GetFormula(string name) =>
        (SdfFormula)typeof(SdfFieldFormulas).GetField(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;

    private sealed record Entry(
        string Catalogue, string Name, int Id, string Discriminator, bool Unsigned, string CatalogueDocumentation,
        string Documentation, ImmutableArray<Facet> Facets, ImmutableArray<string> Shaders, ImmutableArray<FormulaUse> Formulas, Location? InvalidFormula,
        Location? ReaderMismatch
    );
    private sealed record Facet(string Name, string Type, string? Constant, Location Location);
    // An entry's property that names a shared formula, and the SdfFieldFormulas field it names.
    private sealed record FormulaUse(string Property, string Field);
}
