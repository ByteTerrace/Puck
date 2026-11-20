using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using static Puck.Analyzers.NamedTypes;

namespace Puck.Analyzers;

/// <summary>
/// CACHE001: a field or auto-property holds a raw <c>System.Runtime.CompilerServices.ConditionalWeakTable</c>, anywhere
/// in its type, outside <c>Puck.Abstractions.CodeDerivedTable</c>, without <c>[SideTable(reason)]</c>
/// (<c>build/SideTableAttribute.cs</c>). A weak table of values derived by code from their keys keeps serving the old
/// code's values after a hot-reload update; <c>CodeDerivedTable</c> forgets them when
/// <c>Puck.Abstractions.CodeRevision</c> moves, so it is the one door for such a cache. A table whose values are not
/// products of code (an attachment a writer records, per-object state, a value whose identity live state was built from)
/// carries the mark with its reason. A test assembly (one that references xUnit v3) is not held to the rule.
/// <para>
/// The rule reads declarations, so a weak table held in a local, or created and handed to another object, is outside
/// what it sees; a table that lives across calls lives in a field.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeDerivedCacheAnalyzer : DiagnosticAnalyzer {
    private const string Category = "Puck.Caches";
    private const string DoorNamespace = "Puck.Abstractions";
    private const string DoorType = "CodeDerivedTable";
    private const string MarkName = "Puck.SideTableAttribute";

    /// <summary>CACHE001: a raw weak table held outside the code-derived door.</summary>
    public static readonly DiagnosticDescriptor Cache001RawWeakTable = new(
        id: "CACHE001",
        title: "A raw ConditionalWeakTable is held outside CodeDerivedTable",
        messageFormat: "'{0}' holds a raw ConditionalWeakTable; hold values derived by code in a CodeDerivedTable, which forgets them when CodeRevision moves, or mark a table whose values are not products of code [SideTable(reason)]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A weak table of values derived by code keeps serving the old code's values after a hot-reload update. Puck.Abstractions.CodeDerivedTable is the one door for such a cache; a side table (an attachment, per-object state, a value whose identity live state was built from) says so with [SideTable(reason)]."
    );

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(item: Cache001RawWeakTable);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(action: start => {
            if (start.Compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.v3.IFactAttribute") is not null) {
                return;
            }

            start.RegisterSymbolAction(action: AnalyzeField, symbolKinds: SymbolKind.Field);
            start.RegisterSymbolAction(action: AnalyzeProperty, symbolKinds: SymbolKind.Property);
        });
    }

    private static void AnalyzeField(SymbolAnalysisContext context) {
        var field = ((IFieldSymbol)context.Symbol);

        if (!field.IsImplicitlyDeclared) {
            Check(context: context, symbol: field, type: field.Type);
        }
    }
    private static void AnalyzeProperty(SymbolAnalysisContext context) {
        var property = ((IPropertySymbol)context.Symbol);

        // An auto-property's backing field is implicit; a property with a body serves a field this rule already reads.
        if (IsAutoProperty(property: property)) {
            Check(context: context, symbol: property, type: property.Type);
        }
    }
    private static void Check(SymbolAnalysisContext context, ISymbol symbol, ITypeSymbol type) {
        if (!Mentions(type: type) || InsideTheDoor(symbol: symbol) || Marked(symbol: symbol)) {
            return;
        }

        foreach (var location in symbol.Locations) {
            context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                descriptor: Cache001RawWeakTable,
                location: location,
                messageArgs: $"{symbol.ContainingType?.Name}.{symbol.Name}"
            ));
        }
    }
    private static bool IsAutoProperty(IPropertySymbol property) {
        foreach (var member in property.ContainingType.GetMembers()) {
            if ((member is IFieldSymbol { IsImplicitlyDeclared: true } backing) && SymbolEqualityComparer.Default.Equals(x: backing.AssociatedSymbol, y: property)) {
                return true;
            }
        }

        return false;
    }
    // Whether the type is, or carries anywhere in its type arguments or element types, a weak table.
    private static bool Mentions(ITypeSymbol type) {
        if (Is(type: type, @namespace: "System.Runtime.CompilerServices", name: "ConditionalWeakTable")) {
            return true;
        }

        switch (type) {
            case IArrayTypeSymbol array:
                return Mentions(type: array.ElementType);
            case INamedTypeSymbol named:
                foreach (var argument in named.TypeArguments) {
                    if (Mentions(type: argument)) {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }
    private static bool InsideTheDoor(ISymbol symbol) {
        for (var type = symbol.ContainingType; (type is not null); type = type.ContainingType) {
            if (Is(type: type, @namespace: DoorNamespace, name: DoorType)) {
                return true;
            }
        }

        return false;
    }
    private static bool Marked(ISymbol symbol) {
        foreach (var attribute in symbol.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() == MarkName) {
                return true;
            }
        }

        return false;
    }
}
