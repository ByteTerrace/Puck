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
/// nested test class, which xUnit discovers on its own. The mark does not stand in for the trait on what the runner calls
/// with no analyzed caller: a test class, a test method, or a test class's constructors and lifecycle members
/// (<c>Dispose</c>, <c>DisposeAsync</c>, <c>InitializeAsync</c>). Elsewhere, the mark on the reaching member, its property
/// or a containing type hands the obligation to its callers, and so does the trait on a containing type.
/// </para>
/// <para>
/// <b>What the runner builds.</b> An untraited test class is refused when constructing it reaches the GPU with no call
/// site to analyze: a base type it is built on, or a class, collection or assembly fixture the runner builds for it, that
/// is marked, sits in a marked type, or has a marked constructor or lifecycle member.
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

            var compilation = start.Compilation;
            var xunit = new XunitTypes(
                AssemblyFixture: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.AssemblyFixtureAttribute"),
                AssemblyFixtureOf: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.AssemblyFixtureAttribute`1"),
                ClassFixture: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.IClassFixture`1"),
                Collection: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.CollectionAttribute"),
                CollectionDefinition: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.CollectionDefinitionAttribute"),
                CollectionFixture: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.ICollectionFixture`1"),
                CollectionOf: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.CollectionAttribute`1"),
                Fact: fact,
                Trait: trait
            );
            var definitions = new Lazy<ImmutableDictionary<string, ImmutableArray<INamedTypeSymbol>>>(valueFactory: () => CollectionDefinitions(compilation: compilation, xunit: xunit));

            start.RegisterOperationAction(
                action: operation => Analyze(context: operation, xunit: xunit),
                operationKinds: [OperationKind.Invocation, OperationKind.ObjectCreation, OperationKind.MethodReference, OperationKind.PropertyReference, OperationKind.FieldReference]
            );
            start.RegisterSymbolAction(
                action: symbol => AnalyzeTestClass(context: symbol, definitions: definitions, xunit: xunit),
                symbolKinds: [SymbolKind.NamedType]
            );
        });
    }

    // The xUnit types that say what a test is, which trait it carries, and which fixtures the runner builds for it.
    private sealed record XunitTypes(
        INamedTypeSymbol? AssemblyFixture,
        INamedTypeSymbol? AssemblyFixtureOf,
        INamedTypeSymbol? ClassFixture,
        INamedTypeSymbol? Collection,
        INamedTypeSymbol? CollectionDefinition,
        INamedTypeSymbol? CollectionFixture,
        INamedTypeSymbol? CollectionOf,
        INamedTypeSymbol Fact,
        INamedTypeSymbol Trait
    );

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
    // member the runner does not call itself, on its property, or on a type that is not a test class admits, as does the
    // trait on such a type.
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
                case IMethodSymbol method when EntryPoint(method: method, xunit: xunit):
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
    // Refuses an untraited test class whose construction the runner drives into the GPU with no analyzed call site: a
    // base type it is built on, or a class, collection or assembly fixture the runner builds for it, that is marked or
    // whose constructors or lifecycle members are.
    private static void AnalyzeTestClass(SymbolAnalysisContext context, Lazy<ImmutableDictionary<string, ImmutableArray<INamedTypeSymbol>>> definitions, XunitTypes xunit) {
        var type = ((INamedTypeSymbol)context.Symbol);

        if (type.IsAbstract || type.IsStatic || !TestClass(type: type, xunit: xunit) || Traited(type: type, xunit: xunit)) {
            return;
        }

        foreach (var built in Built(definitions: definitions, type: type, xunit: xunit)) {
            if (OpensOnConstruction(type: built) is { } reached) {
                context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                    descriptor: Gpu001UntraitedGpuReach,
                    location: type.Locations[0],
                    messageArgs: [type.ToDisplayString(format: SymbolDisplayFormat.CSharpShortErrorMessageFormat), reached.ToDisplayString(format: SymbolDisplayFormat.CSharpShortErrorMessageFormat)]
                ));

                return;
            }
        }
    }
    // The types the runner constructs for a test class beyond the class itself: its base types and its fixtures.
    private static IEnumerable<INamedTypeSymbol> Built(Lazy<ImmutableDictionary<string, ImmutableArray<INamedTypeSymbol>>> definitions, INamedTypeSymbol type, XunitTypes xunit) {
        for (var current = type.BaseType; (current is not null); current = current.BaseType) {
            yield return current;
        }
        foreach (var fixture in Fixtures(fixture: xunit.ClassFixture, type: type)) {
            yield return fixture;
        }
        foreach (var attribute in type.GetAttributes()) {
            if (attribute.AttributeClass is not { } attributeClass) {
                continue;
            }

            foreach (var collection in Collections(attribute: attribute, attributeClass: attributeClass, definitions: definitions, xunit: xunit)) {
                foreach (var fixture in Fixtures(fixture: xunit.CollectionFixture, type: collection)) {
                    yield return fixture;
                }
            }
        }
        foreach (var attribute in type.ContainingAssembly.GetAttributes()) {
            if (attribute.AttributeClass is not { } attributeClass) {
                continue;
            }
            if (SymbolEqualityComparer.Default.Equals(x: attributeClass.OriginalDefinition, y: xunit.AssemblyFixtureOf)) {
                if (attributeClass.TypeArguments[0] is INamedTypeSymbol received) {
                    yield return received;
                }
            } else if (SymbolEqualityComparer.Default.Equals(x: attributeClass, y: xunit.AssemblyFixture) && (attribute.ConstructorArguments.Length == 1) && (attribute.ConstructorArguments[0].Value is INamedTypeSymbol fixture)) {
                yield return fixture;
            }
        }
    }
    // The collection definitions a [Collection] attribute names: by name, by type, or by its type argument.
    private static IEnumerable<INamedTypeSymbol> Collections(AttributeData attribute, INamedTypeSymbol attributeClass, Lazy<ImmutableDictionary<string, ImmutableArray<INamedTypeSymbol>>> definitions, XunitTypes xunit) {
        if (SymbolEqualityComparer.Default.Equals(x: attributeClass.OriginalDefinition, y: xunit.CollectionOf)) {
            if (attributeClass.TypeArguments[0] is INamedTypeSymbol definition) {
                yield return definition;
            }

            yield break;
        }
        if (!SymbolEqualityComparer.Default.Equals(x: attributeClass, y: xunit.Collection) || (attribute.ConstructorArguments.Length != 1)) {
            yield break;
        }

        switch (attribute.ConstructorArguments[0].Value) {
            case string name when definitions.Value.TryGetValue(key: name, value: out var named):
                foreach (var definition in named) {
                    yield return definition;
                }

                break;
            case INamedTypeSymbol definition:
                yield return definition;

                break;
        }
    }
    // The fixture types a type receives through IClassFixture<T> or ICollectionFixture<T>.
    private static IEnumerable<INamedTypeSymbol> Fixtures(INamedTypeSymbol? fixture, INamedTypeSymbol type) {
        if (fixture is null) {
            yield break;
        }

        foreach (var implemented in type.AllInterfaces) {
            if (SymbolEqualityComparer.Default.Equals(x: implemented.OriginalDefinition, y: fixture) && (implemented.TypeArguments[0] is INamedTypeSymbol received)) {
                yield return received;
            }
        }
    }
    // Every type in the compilation's own source that names a collection, by the name its [CollectionDefinition] gives.
    private static ImmutableDictionary<string, ImmutableArray<INamedTypeSymbol>> CollectionDefinitions(Compilation compilation, XunitTypes xunit) {
        var definitions = new Dictionary<string, List<INamedTypeSymbol>>(comparer: StringComparer.Ordinal);
        var pending = new Stack<INamespaceOrTypeSymbol>();

        pending.Push(item: compilation.Assembly.GlobalNamespace);
        while (pending.Count > 0) {
            foreach (var member in pending.Pop().GetMembers()) {
                if (member is INamespaceOrTypeSymbol container) {
                    pending.Push(item: container);
                }
                if (member is not INamedTypeSymbol type) {
                    continue;
                }

                foreach (var attribute in type.GetAttributes()) {
                    if (SymbolEqualityComparer.Default.Equals(x: attribute.AttributeClass, y: xunit.CollectionDefinition) && (attribute.ConstructorArguments.Length == 1) && (attribute.ConstructorArguments[0].Value is string name)) {
                        if (!definitions.TryGetValue(key: name, value: out var named)) {
                            definitions[name] = named = [];
                        }

                        named.Add(item: type);
                    }
                }
            }
        }

        return definitions.ToImmutableDictionary(keySelector: static pair => pair.Key, elementSelector: static pair => pair.Value.ToImmutableArray(), keyComparer: StringComparer.Ordinal);
    }
    // The marked symbol constructing the type reaches, or null: a mark on the type, a type containing it or a base type,
    // or on a constructor or lifecycle member of any of them.
    private static ISymbol? OpensOnConstruction(INamedTypeSymbol type) {
        for (var current = type; (current is not null); current = current.BaseType) {
            if (MarkedType(type: current) is { } marked) {
                return marked;
            }

            foreach (var member in current.GetMembers()) {
                if ((member is IMethodSymbol method) && (Constructed(method: method) || Lifecycle(method: method)) && Marked(symbol: method)) {
                    return method;
                }
            }
        }

        return null;
    }
    // Whether the runner calls the method itself, with no analyzed call site: a test, or a constructor or lifecycle member
    // of a test class.
    private static bool EntryPoint(IMethodSymbol method, XunitTypes xunit) =>
        (TestMethod(method: method, xunit: xunit) || ((Constructed(method: method) || Lifecycle(method: method)) && TestClass(type: method.ContainingType, xunit: xunit)));
    private static bool Constructed(IMethodSymbol method) =>
        (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor);
    // Dispose, DisposeAsync and InitializeAsync without parameters: IDisposable, IAsyncDisposable and IAsyncLifetime.
    private static bool Lifecycle(IMethodSymbol method) =>
        (!method.IsStatic && (method.Parameters.Length == 0) && (method.Name is "Dispose" or "DisposeAsync" or "InitializeAsync"));
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
