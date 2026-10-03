using System.Collections.Immutable;
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
            .Select(selector: group => new Walk(sources: sources).Run(name: group.Key, roots: group.Select(selector: root => root.Symbol))).ToArray();
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

    private sealed class Walk(Dictionary<string, Compilation> sources) {
        private static readonly HashSet<string> CompilerPatterns = new(collection: [
            "Current", "Deconstruct", "Dispose", "DisposeAsync", "GetAwaiter", "GetEnumerator",
            "GetResult", "IsCompleted", "MoveNext", "OnCompleted", "UnsafeOnCompleted",
        ], comparer: StringComparer.Ordinal);

        private readonly Queue<ISymbol> m_pending = new();
        private readonly Dictionary<string, DerivationSymbol> m_reach = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<SyntaxNode>> m_declarations = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<SyntaxTree, SemanticModel> m_models = [];
        private readonly HashSet<string> m_constructed = new(comparer: StringComparer.Ordinal);
        private readonly List<INamedTypeSymbol> m_constructedTypes = [];
        private readonly List<ISymbol> m_dispatched = [];
        private readonly HashSet<string> m_dispatchedKeys = new(comparer: StringComparer.Ordinal);
        private HashSet<string> m_dispatchAssemblies = new(comparer: StringComparer.Ordinal);

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
            foreach (var root in entries) { Add(symbol: root); }
            while (m_pending.TryDequeue(result: out var symbol)) {
                try { Visit(symbol: symbol); } catch (InvalidOperationException exception) { throw new InvalidOperationException(message: $"{exception.Message}; reached from {Key(symbol: symbol)}", innerException: exception); }
            }
            return new DerivationResult(Name: name, Fingerprint: TokenFingerprint.ComputeDeclarations(declarations: m_declarations),
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
                if (method.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction) { Add(symbol: method.ContainingSymbol); return; }
                if (method.AssociatedSymbol is { } associated) { Add(symbol: associated); return; }
                foreach (var type in method.TypeArguments.Where(predicate: type => (type is not ITypeParameterSymbol))) { Add(symbol: type); }
                ConstructFromArguments(parameters: method.OriginalDefinition.TypeParameters, arguments: method.TypeArguments);
                symbol = (method.ReducedFrom ?? method);
                if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor) { Construct(type: constructor.ContainingType); }
            }
            if (symbol is INamedTypeSymbol named) {
                if (named.TypeKind == TypeKind.Error) { throw new InvalidOperationException(message: $"derivations: unresolved type {named}"); }
                if (named.ToDisplayString() == FingerprintType) { return; }
                foreach (var type in named.TypeArguments.Where(predicate: type => (type is not ITypeParameterSymbol))) { Add(symbol: type); }
                ConstructFromArguments(parameters: named.OriginalDefinition.TypeParameters, arguments: named.TypeArguments);
                // A value is its own instance: no constructor call stands between a struct or enum and its use.
                if (named.IsValueType) { Construct(type: named); }
            }
            if ((symbol.ContainingType?.ToDisplayString() == FingerprintType) || (symbol is INamespaceSymbol)) { return; }
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
        private void Visit(ISymbol symbol) {
            Add(symbol: symbol.ContainingType);
            switch (symbol) {
                case INamedTypeSymbol type:
                    Add(symbol: type.BaseType);
                    foreach (var contract in type.Interfaces) { Add(symbol: contract); }
                    foreach (var constructor in type.StaticConstructors) { Add(symbol: constructor); }
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
                            if (expression is AssignmentExpressionSyntax assignment) { AddDeconstruction(info: model.GetDeconstructionInfo(assignment: assignment)); }
                            Add(symbol: model.GetSymbolInfo(expression: expression).Symbol);
                            var info = model.GetTypeInfo(expression: expression);
                            // Contextual syntax such as nameof has no standalone type. Real binding errors
                            // are refused by the declaration diagnostics above.
                            if (info.Type is not IErrorTypeSymbol) { Add(symbol: info.Type); }
                            if (info.ConvertedType is not IErrorTypeSymbol) { Add(symbol: info.ConvertedType); }
                            Add(symbol: model.GetConversion(expression: expression).MethodSymbol);
                        } else if (node is ConstructorInitializerSyntax initializer) {
                            Add(symbol: model.GetSymbolInfo(constructorInitializer: initializer).Symbol);
                        } else if (node is CommonForEachStatementSyntax loop) {
                            var info = model.GetForEachStatementInfo(forEachStatement: loop);

                            Add(symbol: info.GetEnumeratorMethod);
                            Add(symbol: info.MoveNextMethod);
                            Add(symbol: info.CurrentProperty);
                            Add(symbol: info.DisposeMethod);
                            Add(symbol: info.ElementConversion.MethodSymbol);
                            Add(symbol: info.CurrentConversion.MethodSymbol);
                            if (loop is ForEachVariableStatementSyntax deconstruction) { AddDeconstruction(info: model.GetDeconstructionInfo(@foreach: deconstruction)); }
                        }
                    }
                    // Implicit calls (collection initializers, conversions, operators and property access) are
                    // compiler operations too; syntax alone need not carry an explicit member name for them.
                    if (model.GetOperation(node: part) is { } operation) {
                        foreach (var child in operation.DescendantsAndSelf()) {
                            Add(symbol: child.Type);
                            switch (child) {
                                case IInvocationOperation invocation: Add(symbol: invocation.TargetMethod); break;
                                case IObjectCreationOperation creation: Add(symbol: creation.Constructor); break;
                                case IWithOperation with: Add(symbol: with.CloneMethod); break;
                                case IMemberReferenceOperation reference: Add(symbol: reference.Member); break;
                                case IConversionOperation conversion: Add(symbol: conversion.OperatorMethod); break;
                                case IBinaryOperation binary: Add(symbol: binary.OperatorMethod); break;
                                case IUnaryOperation unary: Add(symbol: unary.OperatorMethod); break;
                                case IIncrementOrDecrementOperation increment: Add(symbol: increment.OperatorMethod); break;
                                case ICompoundAssignmentOperation assignment: Add(symbol: assignment.OperatorMethod); break;
                            }
                        }
                    }
                }
            }
        }
        private void AddDeconstruction(DeconstructionInfo info) {
            Add(symbol: info.Method);
            Add(symbol: info.Conversion?.MethodSymbol);
            if (!info.Nested.IsDefault) {
                foreach (var nested in info.Nested) { AddDeconstruction(info: nested); }
            }
        }
        // Rapid type analysis: a virtual or interface member's overrides and implementations join the reach only in types the
        // reach constructs, so a call through a System.Object member does not pull in every override in the repository. A type is
        // constructed when one of its constructors is reached, when it is a value type, or when it is the argument of a type
        // parameter that a new() constraint lets generic code instantiate. Dispatch is re-resolved whenever a type is added.
        private void AddDispatch(ISymbol symbol) {
            if (!symbol.IsVirtual && !symbol.IsAbstract && (symbol.ContainingType.TypeKind != TypeKind.Interface)) { return; }
            if (!m_dispatchedKeys.Add(item: Key(symbol: symbol))) { return; }

            m_dispatched.Add(item: symbol);

            for (var index = 0; (index < m_constructedTypes.Count); index++) { Dispatch(symbol: symbol, type: m_constructedTypes[index]); }
        }
        private void Construct(INamedTypeSymbol type) {
            type = type.OriginalDefinition;

            if (!m_constructed.Add(item: Key(symbol: type))) { return; }

            m_constructedTypes.Add(item: type);

            for (var index = 0; (index < m_dispatched.Count); index++) { Dispatch(symbol: m_dispatched[index], type: type); }
        }
        private void ConstructFromArguments(ImmutableArray<ITypeParameterSymbol> parameters, ImmutableArray<ITypeSymbol> arguments) {
            for (var index = 0; ((index < parameters.Length) && (index < arguments.Length)); index++) {
                if (parameters[index].HasConstructorConstraint && (arguments[index] is INamedTypeSymbol argument)) { Construct(type: argument); }
            }
        }
        private void Dispatch(ISymbol symbol, INamedTypeSymbol type) {
            if (!m_dispatchAssemblies.Contains(item: type.ContainingAssembly?.Name ?? string.Empty)) { return; }
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
