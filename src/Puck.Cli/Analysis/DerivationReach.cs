using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Puck.Analyzers;

namespace Puck.Cli.Analysis;

public sealed record DerivationSymbol(string Assembly, string Id, bool External);
public sealed record DerivationResult(string Name, string Fingerprint, IReadOnlyList<DerivationSymbol> Symbols);
/// <summary>Resolves producer dependencies in their owning compilations, including metadata references back to source.</summary>
public static class DerivationReach {
    private const string FingerprintType = "Puck.SignedDistance.Baking.DerivationFingerprint";

    public static IReadOnlyList<DerivationResult> Compute(IEnumerable<Compilation> compilations) {
        var sources = compilations.ToDictionary(keySelector: compilation => compilation.AssemblyName!, comparer: StringComparer.Ordinal);
        var types = sources.Values.SelectMany(selector: compilation => Types(container: compilation.Assembly.GlobalNamespace)).ToArray();
        var roots = types.SelectMany(selector: type => type.GetMembers()).SelectMany(selector: symbol => symbol.GetAttributes()
            .Where(predicate: attribute => (attribute.AttributeClass?.ToDisplayString() == "Puck.DerivationAttribute"))
            .Select(selector: attribute => (Name: ((string)attribute.ConstructorArguments[0].Value!), Symbol: symbol)))
            .GroupBy(keySelector: root => root.Name, comparer: StringComparer.Ordinal);

        return roots.OrderBy(keySelector: group => group.Key, comparer: StringComparer.Ordinal)
            .Select(selector: group => new Walk(sources: sources, types: types).Run(name: group.Key, roots: group.Select(selector: root => root.Symbol))).ToArray();
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol container) {
        foreach (var member in container.GetMembers()) {
            if (member is INamedTypeSymbol type) {
                yield return type;
                foreach (var nested in Types(container: type)) { yield return nested; }
            } else if (member is INamespaceSymbol space) {
                foreach (var nested in Types(container: space)) { yield return nested; }
            }
        }
    }

    private sealed class Walk(Dictionary<string, Compilation> sources, INamedTypeSymbol[] types) {
        private static readonly HashSet<string> CompilerPatterns = new(collection: [
            "Current", "Deconstruct", "Dispose", "DisposeAsync", "GetAwaiter", "GetEnumerator",
            "GetResult", "IsCompleted", "MoveNext", "OnCompleted", "UnsafeOnCompleted",
        ], comparer: StringComparer.Ordinal);

        private readonly Queue<ISymbol> m_pending = new();
        private readonly Queue<(ISymbol Member, INamedTypeSymbol Receiver)> m_pendingDispatch = new();
        private readonly Dictionary<string, DerivationSymbol> m_reach = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<SyntaxNode>> m_declarations = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<SyntaxNode, IReadOnlyList<string>> m_bindings = [];
        private readonly Dictionary<SyntaxTree, SemanticModel> m_models = [];
        private readonly HashSet<ITypeSymbol> m_genericArguments = new(comparer: SymbolEqualityComparer.Default);
        private readonly HashSet<ITypeSymbol> m_incomingTypes = new(comparer: SymbolEqualityComparer.Default);
        private readonly HashSet<string> m_constructed = new(comparer: StringComparer.Ordinal);
        private readonly List<INamedTypeSymbol> m_constructedTypes = [];
        private readonly List<ISymbol> m_dispatched = [];
        private readonly HashSet<string> m_dispatchedKeys = new(comparer: StringComparer.Ordinal);
        private HashSet<string> m_dispatchAssemblies = new(comparer: StringComparer.Ordinal);
        private INamedTypeSymbol[] m_universe = [];

        private bool m_serializesArguments;

        private static string Id(ISymbol symbol) => (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(format: SymbolDisplayFormat.FullyQualifiedFormat));
        private static string Key(ISymbol symbol) => $"{symbol.ContainingAssembly?.Name}:{Id(symbol: symbol)}";

        public DerivationResult Run(string name, IEnumerable<ISymbol> roots) {
            var entries = roots.ToArray();
            var assemblies = new HashSet<string>(comparer: StringComparer.Ordinal);
            var pending = new Queue<string>(collection: entries.Select(selector: entry => entry.ContainingAssembly.Name));

            while (pending.TryDequeue(result: out var assembly)) {
                if (!assemblies.Add(item: assembly) || !sources.TryGetValue(key: assembly, value: out var compilation)) { continue; }
                foreach (var reference in compilation.ReferencedAssemblyNames) { pending.Enqueue(item: reference.Name); }
            }
            m_dispatchAssemblies = assemblies;
            m_universe = types.Where(predicate: type => (assemblies.Contains(item: type.ContainingAssembly.Name) && (type.TypeKind is TypeKind.Class or TypeKind.Struct))).ToArray();
            // An entry point is called from outside the reach: whatever its caller passes in, and whatever it is called on,
            // is constructed there, so every subtype of those types is constructed as far as the reach can tell.
            foreach (var root in entries) {
                if (root is IMethodSymbol entry) {
                    foreach (var parameter in entry.Parameters) { ConstructSubtypes(type: parameter.Type); }

                    if (!entry.IsStatic) { ConstructSubtypes(type: entry.ContainingType); }
                }
            }
            foreach (var root in entries) { Add(symbol: root); }
            while ((m_pending.Count > 0) || (m_pendingDispatch.Count > 0)) {
                if (m_pendingDispatch.TryDequeue(result: out var dispatch)) {
                    Dispatch(symbol: dispatch.Member, type: dispatch.Receiver);
                    continue;
                }
                var symbol = m_pending.Dequeue();

                try { Visit(symbol: symbol); } catch (InvalidOperationException exception) { throw new InvalidOperationException(message: $"{exception.Message}; reached from {Key(symbol: symbol)}", innerException: exception); }
            }
            return new DerivationResult(Name: name, Fingerprint: TokenFingerprint.ComputeDeclarations(declarations: m_declarations, bindings: m_bindings),
                Symbols: m_reach.OrderBy(keySelector: pair => pair.Key, comparer: StringComparer.Ordinal).Select(selector: pair => pair.Value).ToArray());
        }

        private void Add(ISymbol? symbol) {
            if (symbol is null) { return; }
            if (symbol is IAliasSymbol alias) { Add(symbol: alias.Target); return; }
            if (symbol is IArrayTypeSymbol array) { Add(symbol: array.ElementType); return; }
            if (symbol is IPointerTypeSymbol pointer) { Add(symbol: pointer.PointedAtType); return; }
            if (symbol is ITypeParameterSymbol parameter) {
                foreach (var constraint in parameter.ConstraintTypes) { Add(symbol: constraint); }
                return;
            }
            if (symbol is ILocalSymbol local) { Add(symbol: local.Type); return; }
            if (symbol is IParameterSymbol argument) { Add(symbol: argument.Type); return; }
            if (symbol is IMethodSymbol method) {
                foreach (var type in method.TypeArguments.Where(predicate: type => (type is not ITypeParameterSymbol))) { AddGenericArgument(type: type); }
                if (method.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction) { Add(symbol: method.ContainingSymbol); return; }
                // A metadata-only callee can return an implementation constructed outside the producer's reach.
                if (!sources.ContainsKey(key: method.ContainingAssembly.Name)) { ConstructSubtypes(type: method.ReturnType); }
                if (method.AssociatedSymbol is { } associated) { Add(symbol: associated); return; }
                if (IsReflective(method: method)) { ConstructReflectively(method: method); }
                symbol = (method.ReducedFrom ?? method);
                if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor) { Construct(type: constructor.ContainingType); }
            }
            if (symbol is INamedTypeSymbol named) {
                if (named.TypeKind == TypeKind.Error) { throw new InvalidOperationException(message: $"derivations: unresolved type {named}"); }
                if (named.ToDisplayString() == FingerprintType) { return; }
                foreach (var type in named.TypeArguments.Where(predicate: type => (type is not ITypeParameterSymbol))) { AddGenericArgument(type: type); }
                // A value is its own instance: no constructor call stands between a struct or enum and its use.
                if (named.IsValueType) { Construct(type: named); }
            }
            if ((symbol.ContainingType?.ToDisplayString() == FingerprintType) || (symbol is INamespaceSymbol)) { return; }
            // Storage can hold values supplied through an enclosing instance, a registry or an external factory.
            if (symbol is IFieldSymbol { IsConst: false } fieldValue) { ConstructSubtypes(type: fieldValue.Type); }
            if (symbol is IPropertySymbol propertyValue) { ConstructSubtypes(type: propertyValue.Type); }
            Add(symbol: symbol.ContainingType);
            symbol = symbol.OriginalDefinition;
            var assembly = symbol.ContainingAssembly;

            if (assembly is null) { return; }
            var key = Key(symbol: symbol);

            if (m_reach.ContainsKey(key: key)) { return; }
            if (!sources.TryGetValue(key: assembly.Name, value: out var compilation)) {
                m_reach.Add(key: key, value: new DerivationSymbol(Assembly: assembly.Identity.ToString(), Id: Id(symbol: symbol), External: true));
                m_declarations.Add(key: $"{assembly.Identity}:{Id(symbol: symbol)}", value: []);
                if (symbol is IMethodSymbol or IPropertySymbol) { AddDispatch(symbol: symbol); }
                return;
            }
            if (!SymbolEqualityComparer.Default.Equals(x: assembly, y: compilation.Assembly)) {
                var id = symbol.GetDocumentationCommentId();

                symbol = ((id is null) ? null : DocumentationCommentId.GetSymbolsForDeclarationId(compilation: compilation, id: id)
                    .FirstOrDefault(predicate: candidate => SymbolEqualityComparer.Default.Equals(x: candidate.ContainingAssembly, y: compilation.Assembly)));
                if (symbol is null) { throw new InvalidOperationException(message: $"derivations: cannot resolve repository symbol {key} to source"); }
            }
            m_reach.Add(key: key, value: new DerivationSymbol(Assembly: assembly.Name, Id: Id(symbol: symbol), External: false));
            m_pending.Enqueue(item: symbol);
        }
        private void AddGenericArgument(ITypeSymbol type) {
            if (!m_genericArguments.Add(item: type)) { return; }
            Add(symbol: type);
            if (m_serializesArguments) { ConstructSerialized(type: type, seen: new HashSet<ITypeSymbol>(comparer: SymbolEqualityComparer.Default)); }
            // A new T() in the generic body has no concrete constructor symbol in its operation tree.
            if ((type is INamedTypeSymbol named) && sources.ContainsKey(key: named.ContainingAssembly.Name)) {
                foreach (var constructor in named.InstanceConstructors.Where(predicate: constructor => ((constructor.DeclaredAccessibility == Accessibility.Public) && (constructor.Parameters.Length == 0)))) { Add(symbol: constructor); }
            }
        }
        private void Visit(ISymbol symbol) {
            Add(symbol: symbol.ContainingType);
            switch (symbol) {
                case INamedTypeSymbol type:
                    Add(symbol: type.BaseType);
                    foreach (var contract in type.Interfaces) { Add(symbol: contract); }
                    foreach (var constructor in type.StaticConstructors) { Add(symbol: constructor); }
                    // External code can call back through these contracts (comparers in Dictionary, for example).
                    foreach (var contract in type.AllInterfaces) {
                        foreach (var member in contract.GetMembers()) {
                            var callback = type.FindImplementationForInterfaceMember(interfaceMember: member);

                            if ((callback is not null) && sources.ContainsKey(key: callback.ContainingAssembly.Name)) { Add(symbol: callback); }
                        }
                    }
                    foreach (var member in type.GetMembers().Where(predicate: member => (member.IsVirtual || member.IsOverride))) { Add(symbol: member); }
                    AddInitializerOrder(type: type);
                    // Roslyn's public operation tree does not expose every lowered call. Keep the
                    // compiler's disposal, iteration, await and deconstruction patterns conservatively.
                    foreach (var member in type.GetMembers().Where(predicate: member => CompilerPatterns.Contains(item: member.Name[(member.Name.LastIndexOf(value: '.') + 1)..]))) { Add(symbol: member); }
                    if (type.TypeKind == TypeKind.Enum) {
                        foreach (var member in type.GetMembers().OfType<IFieldSymbol>()) { Add(symbol: member); }
                    }
                    break;
                case IMethodSymbol method:
                    Add(symbol: method.ReturnType);
                    foreach (var parameter in method.Parameters) { Add(symbol: parameter.Type); }
                    if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor) {
                        foreach (var member in method.ContainingType.GetMembers().Where(predicate: member => (member.IsStatic == method.IsStatic))) {
                            if ((member is IFieldSymbol) || (member is IPropertySymbol { IsImplicitlyDeclared: true }) || member.DeclaringSyntaxReferences.Any(predicate: reference => (reference.GetSyntax() is PropertyDeclarationSyntax { Initializer: not null }))) {
                                Add(symbol: member);
                            }
                        }
                        if (method.MethodKind == MethodKind.Constructor) {
                            foreach (var constructor in (method.ContainingType.BaseType?.InstanceConstructors ?? [])) { Add(symbol: constructor); }
                        }
                    }
                    AddDispatch(symbol: method);
                    break;
                case IPropertySymbol property:
                    Add(symbol: property.Type);
                    AddDispatch(symbol: property);
                    break;
                case IFieldSymbol field: Add(symbol: field.Type); break;
                case IEventSymbol eventSymbol: Add(symbol: eventSymbol.Type); break;
            }

            var references = symbol.DeclaringSyntaxReferences.AsEnumerable();

            if (symbol is IMethodSymbol { PartialImplementationPart: { } implementation }) {
                references = references.Concat(second: implementation.DeclaringSyntaxReferences);
            }
            var nodes = references.Select(selector: reference => reference.GetSyntax()).ToArray();
            // Synthesized record operations depend on the record's stored members, which have no method body.
            if (symbol is IMethodSymbol { IsImplicitlyDeclared: true, ContainingType.IsRecord: true } recordMethod) {
                foreach (var member in recordMethod.ContainingType.GetMembers().Where(predicate: member => (!member.IsStatic && (member is IPropertySymbol or IFieldSymbol)))) { Add(symbol: member); }
                if (recordMethod.Name == "<Clone>$") {
                    foreach (var constructor in recordMethod.ContainingType.InstanceConstructors.Where(predicate: constructor => ((constructor.Parameters is [var parameter]) && SymbolEqualityComparer.Default.Equals(x: parameter.Type, y: recordMethod.ContainingType)))) { Add(symbol: constructor); }
                }
            }
            m_declarations.Add(key: Key(symbol: symbol), value: nodes);
            foreach (var declaration in nodes) {
                var bindings = new List<string>();

                void Bind(ISymbol? target, string role) {
                    if (target is IAliasSymbol alias) { target = alias.Target; }
                    if ((target is null) || (target.ToDisplayString() == FingerprintType) || (target.ContainingType?.ToDisplayString() == FingerprintType)) { return; }
                    bindings.Add(item: $"{role}:{Key(symbol: target)}:{target.ToDisplayString(format: SymbolDisplayFormat.FullyQualifiedFormat)}");
                }
                void Depend(ISymbol? target, string role) { Bind(role: role, target: target); Add(symbol: target); }
                if (!m_models.TryGetValue(key: declaration.SyntaxTree, value: out var model)) {
                    model = sources[symbol.ContainingAssembly.Name].GetSemanticModel(syntaxTree: declaration.SyntaxTree);
                    m_models.Add(key: declaration.SyntaxTree, value: model);
                }
                var error = model.GetDiagnostics(span: declaration.Span).FirstOrDefault(predicate: diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));

                if (error is not null) { throw new InvalidOperationException(message: $"derivations: {error}"); }
                foreach (var part in TokenFingerprint.DeclarationParts(declaration: declaration)) {
                    if (part is BaseFieldDeclarationSyntax field) {
                        foreach (var variable in field.Declaration.Variables) { Add(symbol: model.GetDeclaredSymbol(declaration: variable)); }
                    } else if (part is PropertyDeclarationSyntax property) {
                        Add(symbol: model.GetDeclaredSymbol(declaration: property));
                    }
                    foreach (var node in part.DescendantNodesAndSelf()) {
                        if (node is ExpressionSyntax expression) {
                            if (expression is OmittedArraySizeExpressionSyntax or OmittedTypeArgumentSyntax) { continue; }
                            if (expression is AssignmentExpressionSyntax assignment) { AddDeconstruction(info: model.GetDeconstructionInfo(assignment: assignment), bind: Bind); }
                            var target = model.GetSymbolInfo(expression: expression).Symbol;

                            Bind(role: "symbol", target: target);
                            Add(symbol: target);
                            var info = model.GetTypeInfo(expression: expression);

                            Bind(target: info.Type, role: "type");
                            Bind(target: info.ConvertedType, role: "converted-type");
                            Bind(target: model.GetConversion(expression: expression).MethodSymbol, role: "conversion");
                            // Contextual syntax such as nameof has no standalone type. Real binding errors
                            // are refused by the declaration diagnostics above.
                            if (info.Type is not IErrorTypeSymbol) { Add(symbol: info.Type); }
                            if (info.ConvertedType is not IErrorTypeSymbol) { Add(symbol: info.ConvertedType); }
                            Add(symbol: model.GetConversion(expression: expression).MethodSymbol);
                        } else if (node is ConstructorInitializerSyntax initializer) {
                            Depend(target: model.GetSymbolInfo(constructorInitializer: initializer).Symbol, role: "base-constructor");
                        } else if (node is CommonForEachStatementSyntax loop) {
                            var info = model.GetForEachStatementInfo(forEachStatement: loop);

                            Depend(target: info.GetEnumeratorMethod, role: "enumerator");
                            Depend(target: info.MoveNextMethod, role: "move-next");
                            Depend(target: info.CurrentProperty, role: "current");
                            Depend(target: info.DisposeMethod, role: "dispose");
                            Depend(target: info.ElementConversion.MethodSymbol, role: "element-conversion");
                            Depend(target: info.CurrentConversion.MethodSymbol, role: "current-conversion");
                            if (loop is ForEachVariableStatementSyntax deconstruction) { AddDeconstruction(info: model.GetDeconstructionInfo(@foreach: deconstruction), bind: Bind); }
                        }
                    }
                    // Implicit calls (collection initializers, conversions, operators and property access) are
                    // compiler operations too; syntax alone need not carry an explicit member name for them.
                    if (model.GetOperation(node: part) is { } operation) {
                        foreach (var child in operation.DescendantsAndSelf()) {
                            Depend(target: child.Type, role: "operation-type");
                            switch (child) {
                                case IInvocationOperation invocation: Depend(target: invocation.TargetMethod, role: "invoke"); break;
                                case IObjectCreationOperation creation: Depend(target: creation.Constructor, role: "construct"); break;
                                case IWithOperation with: Depend(target: with.CloneMethod, role: "clone"); break;
                                case IMemberReferenceOperation reference: Depend(target: reference.Member, role: "member"); break;
                                case IConversionOperation conversion: Depend(target: conversion.OperatorMethod, role: "operator-conversion"); break;
                                case IBinaryOperation binary: Depend(target: binary.OperatorMethod, role: "binary"); break;
                                case IUnaryOperation unary: Depend(target: unary.OperatorMethod, role: "unary"); break;
                                case IIncrementOrDecrementOperation increment: Depend(target: increment.OperatorMethod, role: "increment"); break;
                                case ICompoundAssignmentOperation assignment: Depend(target: assignment.OperatorMethod, role: "compound-assignment"); break;
                            }
                        }
                    }
                }
                m_bindings[declaration] = bindings;
            }
        }
        private void AddInitializerOrder(INamedTypeSymbol type) {
            if (type.DeclaringSyntaxReferences.Length < 2) { return; }
            var trees = sources[type.ContainingAssembly.Name].SyntaxTrees.Select(selector: (tree, index) => (tree, index))
                .ToDictionary(elementSelector: pair => pair.index, keySelector: pair => pair.tree);
            var initializers = type.DeclaringSyntaxReferences.OrderBy(keySelector: reference => trees[reference.SyntaxTree])
                .ThenBy(keySelector: reference => reference.Span.Start).SelectMany(selector: reference => TokenFingerprint.DeclarationParts(declaration: reference.GetSyntax()))
                .Where(predicate: part => (part is BaseFieldDeclarationSyntax or PropertyDeclarationSyntax)).ToArray();
            var identities = new List<string>();

            foreach (var initializer in initializers) {
                var model = sources[type.ContainingAssembly.Name].GetSemanticModel(syntaxTree: initializer.SyntaxTree);

                if (initializer is BaseFieldDeclarationSyntax field) {
                    foreach (var variable in field.Declaration.Variables.Where(predicate: variable => (variable.Initializer is not null))) {
                        identities.Add(item: Key(symbol: model.GetDeclaredSymbol(declaration: variable)!));
                    }
                } else { identities.Add(item: Key(symbol: model.GetDeclaredSymbol(declaration: initializer)!)); }
            }
            if (identities.Count > 0) { m_declarations.Add(key: $"{Key(symbol: type)}:initializer-order:{string.Join(separator: "|", values: identities)}", value: []); }
        }
        private void AddDeconstruction(DeconstructionInfo info, Action<ISymbol?, string> bind) {
            bind(arg1: info.Method, arg2: "deconstruct");
            bind(arg1: info.Conversion?.MethodSymbol, arg2: "deconstruct-conversion");
            Add(symbol: info.Method);
            Add(symbol: info.Conversion?.MethodSymbol);
            if (!info.Nested.IsDefault) {
                foreach (var nested in info.Nested) { AddDeconstruction(bind: bind, info: nested); }
            }
        }
        // Rapid type analysis: a virtual or interface member's overrides and implementations join the reach only in types the
        // reach constructs, so a call through a System.Object member does not pull in every override in the repository. A type is
        // constructed when one of its constructors is reached, when it is a value type, or when it is the argument of a type
        // parameter that a new() constraint lets generic code instantiate. Incoming parameters, storage members and external
        // returns admit every possible subtype. Dispatch is re-resolved whenever a type is added.
        private void AddDispatch(ISymbol symbol) {
            if (!symbol.IsVirtual && !symbol.IsAbstract && (symbol.ContainingType.TypeKind != TypeKind.Interface)) { return; }
            if (!m_dispatchedKeys.Add(item: Key(symbol: symbol))) { return; }

            m_dispatched.Add(item: symbol);

            foreach (var receiver in m_constructedTypes) { m_pendingDispatch.Enqueue(item: (symbol, receiver)); }
        }
        // Every type of the reach's assemblies that is, or derives from or implements, the given one is treated as constructed.
        private void ConstructSubtypes(ITypeSymbol? type) {
            if ((type is null) || !m_incomingTypes.Add(item: type)) { return; }

            switch (type) {
                case IArrayTypeSymbol array:
                    ConstructSubtypes(type: array.ElementType);
                    return;
                case ITypeParameterSymbol parameter:
                    if (parameter.ConstraintTypes.Length == 0) { ConstructAll(); } else { foreach (var constraint in parameter.ConstraintTypes) { ConstructSubtypes(type: constraint); } }

                    return;
                case INamedTypeSymbol named:
                    var target = named.OriginalDefinition;

                    foreach (var candidate in m_universe) {
                        if (Derives(candidate: candidate, target: target)) { Construct(type: candidate); }
                    }
                    foreach (var argument in named.TypeArguments) { ConstructSubtypes(type: argument); }

                    return;
            }
        }
        private static bool Derives(INamedTypeSymbol candidate, INamedTypeSymbol target) {
            if (target.SpecialType is SpecialType.System_Object) { return true; }

            for (var current = candidate; (current is not null); current = current.BaseType) {
                if (Key(symbol: current.OriginalDefinition) == Key(symbol: target)) { return true; }
            }

            return candidate.AllInterfaces.Any(predicate: contract => (Key(symbol: contract.OriginalDefinition) == Key(symbol: target)));
        }
        private void ConstructAll() {
            foreach (var candidate in m_universe) { Construct(type: candidate); }
        }
        // Code that creates a type from a name or a metadata handle, or reads one from bytes, can create any type of the
        // universe, and runs its constructor.
        private static bool IsReflective(IMethodSymbol method) => (
            (method.ContainingAssembly is { } assembly) && !assembly.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck.") && (
                ((method.ContainingType?.ToDisplayString() == "System.Activator") && method.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "CreateInstance")) ||
                ((method.ContainingType?.ToDisplayString() == "System.Reflection.ConstructorInfo") && (method.Name == "Invoke")) ||
                ((method.ContainingType?.ToDisplayString() == "System.Reflection.Assembly") && (method.Name == "CreateInstance")) ||
                ((method.ContainingType?.ToDisplayString() == "System.Type") && (method.Name == "InvokeMember")) ||
                ((method.ContainingType?.ToDisplayString() == "System.Text.Json.JsonSerializer") && method.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "Deserialize")) ||
                ((method.ContainingType?.ToDisplayString() is "System.IServiceProvider" or "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions") && (method.Name is "GetService" or "GetRequiredService")) ||
                ((method.ContainingType?.ToDisplayString() is "System.Runtime.CompilerServices.RuntimeHelpers" or "System.Runtime.Serialization.FormatterServices") && method.Name.Contains(comparisonType: StringComparison.Ordinal, value: "UninitializedObject"))
            )
        );
        // What a reflective site creates. A closed Deserialize<T> creates T and, through its members' types, the types a
        // serializer reads into; Activator.CreateInstance<T> creates T; any other site (a Type argument, a service locator) can
        // create any type of the universe.
        private void ConstructReflectively(IMethodSymbol method) {
            var closed = (method.IsGenericMethod && method.TypeArguments.All(predicate: static argument => (argument is INamedTypeSymbol or IArrayTypeSymbol)));

            if (method.IsGenericMethod && (method.ContainingType?.ToDisplayString() == "System.Text.Json.JsonSerializer") && !closed) {
                // A type parameter of the enclosing generic code: the types it is closed over at the reach's call sites.
                m_serializesArguments = true;

                var seen = new HashSet<ITypeSymbol>(comparer: SymbolEqualityComparer.Default);

                foreach (var argument in m_genericArguments.ToArray()) { ConstructSerialized(seen: seen, type: argument); }
            } else if (closed && (method.ContainingType?.ToDisplayString() == "System.Text.Json.JsonSerializer")) {
                var seen = new HashSet<ITypeSymbol>(comparer: SymbolEqualityComparer.Default);

                foreach (var argument in method.TypeArguments) { ConstructSerialized(seen: seen, type: argument); }
            } else if (closed && (method.ContainingType?.ToDisplayString() == "System.Activator")) {
                foreach (var argument in method.TypeArguments) { ConstructWithConstructors(type: (argument as INamedTypeSymbol)); }
            } else {
                ConstructAll();

                foreach (var candidate in m_universe) { ConstructWithConstructors(type: candidate); }
            }
        }
        private void ConstructWithConstructors(INamedTypeSymbol? type) {
            if ((type is null) || !m_dispatchAssemblies.Contains(item: (type.ContainingAssembly?.Name ?? string.Empty))) { return; }

            Construct(type: type);

            if (!type.IsAbstract) {
                foreach (var constructor in type.InstanceConstructors) { Add(symbol: constructor); }
            }
        }
        // A serializer builds a value of T and of every type its members hold, subtypes of an abstract or interface member
        // included, and instantiates the converters its attributes name.
        private void ConstructSerialized(ITypeSymbol? type, HashSet<ITypeSymbol> seen) {
            switch (type) {
                case IArrayTypeSymbol array:
                    ConstructSerialized(type: array.ElementType, seen: seen);
                    return;
                case not INamedTypeSymbol:
                    return;
            }

            var named = ((INamedTypeSymbol)type!);

            if (!seen.Add(item: named.OriginalDefinition)) { return; }

            foreach (var argument in named.TypeArguments) { ConstructSerialized(seen: seen, type: argument); }

            if (!m_universe.Contains(value: named.OriginalDefinition, comparer: SymbolEqualityComparer.Default)) {
                if (named.TypeKind is TypeKind.Interface) { SubtypesWithConstructors(target: named.OriginalDefinition, seen: seen); }

                return;
            }
            if (named.IsAbstract) { SubtypesWithConstructors(target: named.OriginalDefinition, seen: seen); } else { ConstructWithConstructors(type: named.OriginalDefinition); }

            foreach (var attribute in named.GetAttributes().Concat(second: named.GetMembers().SelectMany(selector: static member => member.GetAttributes()))) {
                if ((attribute.AttributeClass?.Name is "JsonConverterAttribute") && (attribute.ConstructorArguments.FirstOrDefault().Value is INamedTypeSymbol converter)) { ConstructWithConstructors(type: converter); }
            }
            foreach (var member in named.GetMembers().Where(predicate: static member => !member.IsStatic)) {
                switch (member) {
                    case IPropertySymbol property:
                        ConstructSerialized(type: property.Type, seen: seen);
                        break;
                    case IFieldSymbol field:
                        ConstructSerialized(type: field.Type, seen: seen);
                        break;
                }
            }
            for (var basis = named.BaseType; (basis is not null); basis = basis.BaseType) { ConstructSerialized(seen: seen, type: basis); }
        }
        private void SubtypesWithConstructors(INamedTypeSymbol target, HashSet<ITypeSymbol> seen) {
            foreach (var candidate in m_universe) {
                if (Derives(candidate: candidate, target: target)) {
                    ConstructWithConstructors(type: candidate);
                    ConstructSerialized(seen: seen, type: candidate);
                }
            }
        }
        private void Construct(INamedTypeSymbol type) {
            type = type.OriginalDefinition;

            if (!m_constructed.Add(item: Key(symbol: type))) { return; }

            // An instance of a type is an instance of each type it derives from: the overrides it inherits are its own.
            if (type.BaseType is { } basis) { Construct(type: basis); }

            m_constructedTypes.Add(item: type);

            foreach (var member in m_dispatched) { m_pendingDispatch.Enqueue(item: (member, type)); }
        }
        private void Dispatch(ISymbol symbol, INamedTypeSymbol type) {
            if (!m_dispatchAssemblies.Contains(item: (type.ContainingAssembly?.Name ?? string.Empty))) { return; }
            if (symbol.ContainingType.TypeKind == TypeKind.Interface) {
                foreach (var contract in type.AllInterfaces.Where(predicate: contract => (Key(symbol: contract.OriginalDefinition) == Key(symbol: symbol.ContainingType)))) {
                    foreach (var member in contract.GetMembers(name: symbol.Name).Where(predicate: member => (Id(symbol: member.OriginalDefinition) == Id(symbol: symbol)))) {
                        Add(symbol: type.FindImplementationForInterfaceMember(interfaceMember: member));
                    }
                }
            } else {
                foreach (var member in type.GetMembers(name: symbol.Name)) {
                    ISymbol? overridden = member switch { IMethodSymbol method => method.OverriddenMethod, IPropertySymbol property => property.OverriddenProperty, _ => null };

                    while (overridden is not null) {
                        if (Key(symbol: overridden.OriginalDefinition) == Key(symbol: symbol)) { Add(symbol: member); break; }
                        overridden = overridden switch { IMethodSymbol method => method.OverriddenMethod, IPropertySymbol property => property.OverriddenProperty, _ => null };
                    }
                }
            }
        }
    }
}
