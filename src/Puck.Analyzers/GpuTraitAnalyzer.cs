using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Puck.Analyzers;

/// <summary>
/// Refuses a test class that reaches the host's GPU without <c>[Trait("Category", "Gpu")]</c>
/// (<see cref="Gpu001UntraitedGpuReach"/>). A run beside a GPU leg leaves the GPU classes out with
/// <c>--filter-not-trait Category=Gpu</c>, so a class that opens a device without the trait contends for it.
/// <para>
/// <b>What counts as a reach.</b> Constructing a class marked <c>[OpensGpuDevice]</c>, or calling, constructing or
/// taking a delegate to a method, constructor or property so marked. The marks sit on the ways onto the device: the
/// native device APIs and the test helpers that bring a device up. A reach is admitted when a containing type carries
/// the trait, or when the reaching member or a containing type carries the mark itself, which hands the obligation to its
/// own callers.
/// </para>
/// <para>
/// The rule runs only in a compilation that references <c>Xunit.TraitAttribute</c>, a test assembly; production code
/// reaches the device by design. Generated code is not analyzed. A device a test reaches through a composed host, or
/// through a native entry point no mark names, is outside what the rule sees.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GpuTraitAnalyzer : DiagnosticAnalyzer {
    private const string Category = "Puck.Testing";
    private const string MarkerName = "Puck.OpensGpuDeviceAttribute";
    private const string TraitName = "Xunit.TraitAttribute";

    /// <summary>GPU001: a test class reaches the GPU without the <c>Gpu</c> trait.</summary>
    public static readonly DiagnosticDescriptor Gpu001UntraitedGpuReach = new(
        id: "GPU001",
        title: "GPU reached without the Gpu trait",
        messageFormat: "'{0}' reaches the GPU through '{1}', but no containing class carries [Trait(\"Category\", \"Gpu\")]; add the trait to the test class, or mark '{0}' [OpensGpuDevice] when it is a helper that brings a device up for its callers",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every test class that opens a GPU device carries [Trait(\"Category\", \"Gpu\")], so a test run beside a GPU leg leaves out exactly the classes that contend for the device. A member marked [OpensGpuDevice] is a way onto the device; a helper that reaches one carries the mark and hands the obligation to its callers."
    );

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(item: Gpu001UntraitedGpuReach);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(action: static start => {
            if (start.Compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: TraitName) is not { } trait) {
                return;
            }

            start.RegisterOperationAction(
                action: operation => Analyze(context: operation, trait: trait),
                operationKinds: ImmutableArray.Create(item1: OperationKind.Invocation, item2: OperationKind.ObjectCreation, item3: OperationKind.MethodReference, item4: OperationKind.PropertyReference)
            );
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol trait) {
        var reached = Reached(operation: context.Operation);

        if ((reached is null) || Admitted(symbol: context.ContainingSymbol, trait: trait)) {
            return;
        }

        context.ReportDiagnostic(diagnostic: Diagnostic.Create(
            descriptor: Gpu001UntraitedGpuReach,
            location: context.Operation.Syntax.GetLocation(),
            messageArgs: [context.ContainingSymbol.ToDisplayString(format: SymbolDisplayFormat.CSharpShortErrorMessageFormat), reached.ToDisplayString(format: SymbolDisplayFormat.CSharpShortErrorMessageFormat)]
        ));
    }
    // The marked symbol the operation reaches, or null.
    private static ISymbol? Reached(IOperation operation) {
        switch (operation) {
            case IObjectCreationOperation { Constructor: { } constructor }:
                return (Marked(symbol: constructor)
                    ? constructor
                    : (Marked(symbol: constructor.ContainingType) ? constructor.ContainingType : null));
            case IInvocationOperation { TargetMethod: var method }:
                return Reached(method: method);
            case IMethodReferenceOperation { Method: var method }:
                return Reached(method: method);
            case IPropertyReferenceOperation { Property: var property }:
                return (Marked(symbol: property) ? property : null);
            default:
                return null;
        }
    }
    private static ISymbol? Reached(IMethodSymbol method) {
        var original = (method.ReducedFrom ?? method.OriginalDefinition);

        if (Marked(symbol: original)) {
            return original;
        }

        return (((original.AssociatedSymbol is { } associated) && Marked(symbol: associated)) ? associated : null);
    }
    // Whether the member the operation sits in, or a type containing it, admits a reach: the trait on a containing type,
    // or the mark on the member, its property or a containing type.
    private static bool Admitted(ISymbol symbol, INamedTypeSymbol trait) {
        for (var current = symbol; ((current is not null) && (current is not INamespaceSymbol)); current = current.ContainingSymbol) {
            if (Marked(symbol: current)) {
                return true;
            }
            if ((current is IMethodSymbol { AssociatedSymbol: { } associated }) && Marked(symbol: associated)) {
                return true;
            }
            if ((current is INamedTypeSymbol type) && Traited(trait: trait, type: type)) {
                return true;
            }
        }

        return false;
    }
    private static bool Marked(ISymbol symbol) {
        foreach (var attribute in symbol.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() == MarkerName) {
                return true;
            }
        }

        return false;
    }
    private static bool Traited(INamedTypeSymbol trait, INamedTypeSymbol type) {
        foreach (var attribute in type.GetAttributes()) {
            if (
                SymbolEqualityComparer.Default.Equals(x: attribute.AttributeClass, y: trait) &&
                (attribute.ConstructorArguments.Length == 2) &&
                ((attribute.ConstructorArguments[0].Value as string) == "Category") &&
                ((attribute.ConstructorArguments[1].Value as string) == "Gpu")
            ) {
                return true;
            }
        }

        return false;
    }
}
