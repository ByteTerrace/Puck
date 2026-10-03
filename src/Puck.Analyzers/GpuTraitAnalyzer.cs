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
/// <b>What counts as a reach.</b> Constructing, calling, reading or taking a delegate to a member that carries
/// <c>[OpensGpuDevice]</c> or sits in a type that does, or that a type containing it does. The marks sit on the ways
/// onto the device: the native device APIs and the test helpers that bring a device up.
/// </para>
/// <para>
/// <b>What admits one.</b> A test class (a type that declares or inherits an xUnit test method) is admitted only by the
/// trait, on itself or a base type, the way xUnit gives a class its traits; an enclosing type's trait does not reach a
/// nested test class, which xUnit discovers on its own, and the mark does not stand in for the trait on a test class or
/// a test method, which xUnit runs with no analyzed caller. Elsewhere, the mark on the reaching member, its property or
/// a containing type hands the obligation to its callers, and so does the trait on a containing type.
/// </para>
/// <para>
/// The rule runs only in a compilation that references xUnit v3 (<c>Xunit.TraitAttribute</c> and
/// <c>Xunit.v3.IFactAttribute</c>), a test assembly; production code reaches the device by design. Generated code is not
/// analyzed. A device a test reaches through a composed host, or through a native entry point no mark names, is outside
/// what the rule sees.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GpuTraitAnalyzer : DiagnosticAnalyzer {
    private const string Category = "Puck.Testing";
    private const string FactName = "Xunit.v3.IFactAttribute";
    private const string MarkerName = "Puck.OpensGpuDeviceAttribute";
    private const string TraitName = "Xunit.TraitAttribute";

    /// <summary>GPU001: a test class reaches the GPU without the <c>Gpu</c> trait.</summary>
    public static readonly DiagnosticDescriptor Gpu001UntraitedGpuReach = new(
        id: "GPU001",
        title: "GPU reached without the Gpu trait",
        messageFormat: "'{0}' reaches the GPU through '{1}' without the Gpu trait; add [Trait(\"Category\", \"Gpu\")] to its test class or a base type of it, or, when it is a helper and not a test, mark '{0}' [OpensGpuDevice] to hand the obligation to its callers",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every test class that opens a GPU device carries [Trait(\"Category\", \"Gpu\")], on itself or a base type, so a test run beside a GPU leg leaves out exactly the classes that contend for the device. A member marked [OpensGpuDevice], or one in a marked type, is a way onto the device; a helper that reaches one carries the mark and hands the obligation to its callers. A test class or test method is never a helper: the mark does not admit it."
    );

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(item: Gpu001UntraitedGpuReach);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(action: static start => {
            if (
                (start.Compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: TraitName) is not { } trait) ||
                (start.Compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: FactName) is not { } fact)
            ) {
                return;
            }

            var xunit = new XunitTypes(Fact: fact, Trait: trait);

            start.RegisterOperationAction(
                action: operation => Analyze(context: operation, xunit: xunit),
                operationKinds: [OperationKind.Invocation, OperationKind.ObjectCreation, OperationKind.MethodReference, OperationKind.PropertyReference, OperationKind.FieldReference]
            );
        });
    }

    // The xUnit types that say what a test is and which trait it carries.
    private sealed record XunitTypes(INamedTypeSymbol Fact, INamedTypeSymbol Trait);

    private static void Analyze(OperationAnalysisContext context, XunitTypes xunit) {
        var reached = Reached(operation: context.Operation);

        if ((reached is null) || Admitted(symbol: context.ContainingSymbol, xunit: xunit)) {
            return;
        }

        context.ReportDiagnostic(diagnostic: Diagnostic.Create(
            descriptor: Gpu001UntraitedGpuReach,
            location: context.Operation.Syntax.GetLocation(),
            messageArgs: [context.ContainingSymbol.ToDisplayString(format: SymbolDisplayFormat.CSharpShortErrorMessageFormat), reached.ToDisplayString(format: SymbolDisplayFormat.CSharpShortErrorMessageFormat)]
        ));
    }
    // The marked symbol the operation reaches, or null: the member itself, its property, or a type containing it.
    private static ISymbol? Reached(IOperation operation) {
        switch (operation) {
            case IObjectCreationOperation { Constructor: { } constructor }:
                return Reached(member: constructor);
            case IInvocationOperation { TargetMethod: var method }:
                return Reached(member: (method.ReducedFrom ?? method.OriginalDefinition));
            case IMethodReferenceOperation { Method: var method }:
                return Reached(member: (method.ReducedFrom ?? method.OriginalDefinition));
            case IPropertyReferenceOperation { Property: var property }:
                return Reached(member: property.OriginalDefinition);
            case IFieldReferenceOperation { Field: { IsConst: false } field }:
                return MarkedType(type: field.ContainingType);
            default:
                return null;
        }
    }
    private static ISymbol? Reached(ISymbol member) {
        if (Marked(symbol: member)) {
            return member;
        }
        if ((member is IMethodSymbol { AssociatedSymbol: { } associated }) && Marked(symbol: associated)) {
            return associated;
        }

        return MarkedType(type: member.ContainingType);
    }
    // The innermost marked type among the type and those containing it, or null.
    private static INamedTypeSymbol? MarkedType(INamedTypeSymbol? type) {
        for (var current = type; (current is not null); current = current.ContainingType) {
            if (Marked(symbol: current)) {
                return current.OriginalDefinition;
            }
        }

        return null;
    }
    // Whether the member the operation sits in admits a reach. Walking outward, the first test class decides alone: its
    // trait, on itself or a base type, admits; nothing enclosing it does, and neither does a mark. Before that, a mark on a
    // member that is not a test method, on its property, or on a type that is not a test class admits, as does the trait
    // on such a type.
    private static bool Admitted(ISymbol symbol, XunitTypes xunit) {
        for (var current = symbol; ((current is not null) && (current is not INamespaceSymbol)); current = current.ContainingSymbol) {
            switch (current) {
                case INamedTypeSymbol type when TestClass(type: type, xunit: xunit):
                    return Traited(type: type, xunit: xunit);
                case INamedTypeSymbol type:
                    if (Marked(symbol: type) || Traited(type: type, xunit: xunit)) {
                        return true;
                    }

                    break;
                case IMethodSymbol method when TestMethod(method: method, xunit: xunit):
                    break;
                default:
                    if (Marked(symbol: current) || ((current is IMethodSymbol { AssociatedSymbol: { } associated }) && Marked(symbol: associated))) {
                        return true;
                    }

                    break;
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
    // Whether xUnit discovers the type as a test class: it, or a base type it inherits tests from, declares a test method.
    private static bool TestClass(INamedTypeSymbol type, XunitTypes xunit) {
        for (var current = type; (current is not null); current = current.BaseType) {
            foreach (var member in current.GetMembers()) {
                if ((member is IMethodSymbol method) && TestMethod(method: method, xunit: xunit)) {
                    return true;
                }
            }
        }

        return false;
    }
    // Whether xUnit runs the method as a test: an attribute on it is an xUnit fact ([Fact], [Theory] and their kin).
    private static bool TestMethod(IMethodSymbol method, XunitTypes xunit) {
        foreach (var attribute in method.GetAttributes()) {
            if (attribute.AttributeClass is not { } type) {
                continue;
            }

            foreach (var implemented in type.AllInterfaces) {
                if (SymbolEqualityComparer.Default.Equals(x: implemented, y: xunit.Fact)) {
                    return true;
                }
            }
        }

        return false;
    }
    // Whether the type carries the Gpu trait, on itself or a base type: xUnit gives a class the traits its base types
    // carry, and never those of a type enclosing it.
    private static bool Traited(INamedTypeSymbol type, XunitTypes xunit) {
        for (var current = type; (current is not null); current = current.BaseType) {
            foreach (var attribute in current.GetAttributes()) {
                if (
                    SymbolEqualityComparer.Default.Equals(x: attribute.AttributeClass, y: xunit.Trait) &&
                    (attribute.ConstructorArguments.Length == 2) &&
                    ((attribute.ConstructorArguments[0].Value as string) == "Category") &&
                    ((attribute.ConstructorArguments[1].Value as string) == "Gpu")
                ) {
                    return true;
                }
            }
        }

        return false;
    }
}
