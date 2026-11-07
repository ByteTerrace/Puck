using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Puck.Analyzers;

/// <summary>
/// Refuses a reference captured before a replacement and read after it (<see cref="Stale001ReadAfterReplacement"/>), and
/// keeps the marks that make that check exact complete (<see cref="Stale002UnmarkedReach"/>,
/// <see cref="Stale003UnmarkedFieldSwap"/>).
/// <para>
/// <b>What is replaceable.</b> A type marked <c>[Replaceable]</c> (<c>build/ReplacementAttributes.cs</c>), or derived
/// from one: a host can swap its live instance for a new one, as a document install or checkpoint restore swaps the
/// state arena. A type marked <c>[Replaceable(typeof(Root))]</c> is replaced whenever <c>Root</c> is.
/// </para>
/// <para>
/// <b>What replaces.</b> A call to a member marked <c>[Replaces(typeof(Root))]</c>, to a member that member overrides or
/// implements, or to a method of the same compilation that reaches such a call through its own calls. A call inside a
/// lambda runs when the delegate's receiver decides, so it neither counts toward what the enclosing method reaches nor
/// is ordered against its reads. Constructing an object is not a replacement: whatever its constructor swaps belongs to the new object, which no one
/// holds yet. The roots are types, not instances, so a call that replaces some other instance of the root (a shadow
/// server's arena, a search candidate's) counts too; a read that can only name an instance the call leaves alone is a
/// false positive, suppressed at the read with its reason.
/// </para>
/// <para>
/// <b>STALE001.</b> A local or parameter of a replaceable type read after a call that replaces its root, in the same
/// method body, on a path of the method's control flow graph that writes no new value to it between: after the call,
/// the reference may name the old instance. The graph carries loops (a read earlier in a loop is after the call on the
/// next pass), branches that exclude each other, returns, and the catch and finally handlers a throw from inside a try
/// runs; a local function's body, which has no graph of its own here, is ordered by its text. A variable handed to the
/// call as an argument is the replacement, not a capture, and is exempt for that call. A test assembly (one that
/// references xUnit) is not held to it: a test keeps the retired arena on purpose, or drives a host that never replaces. Fields are not tracked:
/// the field a replacing member assigns is the live pointer, and every other assignment of one is STALE003's.
/// </para>
/// <para>
/// <b>STALE002 and STALE003.</b> The analyzer sees only the compilation it runs in. A member that reaches a replacement
/// carries the mark when it is reached by dispatch (the member it overrides or implements carries it), or when another
/// assembly can call it and its type hands out a replaceable instance, which is where another assembly's capture comes
/// from; a test assembly, which nothing calls, owes no mark. A method that assigns a field a property serves, outside a
/// constructor, carries it too.
/// </para>
/// <para>
/// What the rule cannot see: a replacement reached through a delegate, an event, reflection or a constructor, a capture
/// held in a field of another object, and, across assemblies, a replacement reached through an unmarked public member of
/// a type that does not hand out the root. Generated code is not analyzed.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StaleCaptureAnalyzer : DiagnosticAnalyzer {
    private const string Category = "Puck.Replacement";
    private const string ReplaceableName = "Puck.ReplaceableAttribute";
    private const string ReplacesName = "Puck.ReplacesAttribute";

    /// <summary>STALE001: a captured reference read after a call that can replace it.</summary>
    public static readonly DiagnosticDescriptor Stale001ReadAfterReplacement = new(
        id: "STALE001",
        title: "Reference read after its replacement",
        messageFormat: "'{0}' holds the {1} taken before '{2}' (line {3}) can replace it; read it again from its owner after the call, or open the work after the call in a method of its own that reads it there",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A host can swap the live instance of a [Replaceable] type (the state arena, and the scratch and journal that go with it) during a call marked [Replaces]. A local or parameter captured before such a call names the old instance after it, so state written or read through it is lost or stale.",
        customTags: WellKnownDiagnosticTags.CompilationEnd
    );
    /// <summary>STALE002: a member that reaches a replacement without the mark another assembly or a dispatch reads.</summary>
    public static readonly DiagnosticDescriptor Stale002UnmarkedReach = new(
        id: "STALE002",
        title: "Replacement reached without the mark",
        messageFormat: "'{0}' can replace {1} through '{2}', but {3} does not carry [Replaces(typeof({1}))]; add the mark so callers in other assemblies and through dispatch see the replacement",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The analyzer follows calls inside one compilation. A member another assembly can call, or a member reached through an override or interface, carries [Replaces] when it reaches a replacement, so the next compilation's STALE001 sees it.",
        customTags: WellKnownDiagnosticTags.CompilationEnd
    );
    /// <summary>STALE003: a served field of a replaceable type assigned outside a constructor by an unmarked method.</summary>
    public static readonly DiagnosticDescriptor Stale003UnmarkedFieldSwap = new(
        id: "STALE003",
        title: "Served replaceable field assigned without the mark",
        messageFormat: "'{0}' assigns '{1}', the {2} '{3}' serves, outside a constructor without [Replaces(typeof({2}))]; mark it, since every holder of the instance '{3}' served before is stale after it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A property that returns a field of a [Replaceable] type serves the live instance; assigning that field after construction swaps what every reader of the property reaches. The method that does it carries [Replaces] so the analyzer orders every captured reference against it. A field no property serves (a private cache, a lazily built scratch arena) is not a live pointer and is not held to the mark.",
        customTags: WellKnownDiagnosticTags.CompilationEnd
    );

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Stale001ReadAfterReplacement, Stale002UnmarkedReach, Stale003UnmarkedFieldSwap);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(action: static start => {
            // A test assembly is a leaf: no assembly calls into it, so its public members owe no mark to a caller.
            var state = new CompilationState(leaf: (start.Compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.v3.IFactAttribute") is not null));

            start.RegisterOperationBlockAction(action: state.Collect);
            start.RegisterCompilationEndAction(action: state.Report);
        });
    }

    private static bool Named(AttributeData attribute, string name) =>
        (attribute.AttributeClass?.ToDisplayString() == name);
    // The [Replaceable] root of a type: the type marked, or the root its mark names; null when nothing in its base
    // chain is marked.
    private static INamedTypeSymbol? RootOf(ITypeSymbol? type) {
        for (var current = (type as INamedTypeSymbol); (current is not null); current = current.BaseType) {
            foreach (var attribute in current.OriginalDefinition.GetAttributes()) {
                if (!Named(attribute: attribute, name: ReplaceableName)) {
                    continue;
                }

                var named = (((attribute.ConstructorArguments.Length > 0) ? attribute.ConstructorArguments[0].Value : null) as INamedTypeSymbol);

                return (named ?? current).OriginalDefinition;
            }
        }

        return null;
    }
    // The roots a member's own [Replaces] marks name, its property's included.
    private static ImmutableHashSet<INamedTypeSymbol> OwnMarks(ISymbol member) {
        var builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(equalityComparer: SymbolEqualityComparer.Default);

        AddMarks(builder: builder, member: member);
        if (member is IMethodSymbol { AssociatedSymbol: { } associated }) {
            AddMarks(builder: builder, member: associated);
        }

        return builder.ToImmutable();
    }
    private static void AddMarks(ImmutableHashSet<INamedTypeSymbol>.Builder builder, ISymbol member) {
        foreach (var attribute in member.GetAttributes()) {
            if (Named(attribute: attribute, name: ReplacesName) && (attribute.ConstructorArguments.Length > 0) && (attribute.ConstructorArguments[0].Value is INamedTypeSymbol root)) {
                builder.Add(item: root.OriginalDefinition);
            }
        }
    }
    // The members a method overrides or implements, through the whole override chain.
    private static IEnumerable<IMethodSymbol> Bases(IMethodSymbol method) {
        for (var overridden = method.OverriddenMethod; (overridden is not null); overridden = overridden.OverriddenMethod) {
            yield return overridden.OriginalDefinition;
        }
        foreach (var explicitImplementation in method.ExplicitInterfaceImplementations) {
            yield return explicitImplementation.OriginalDefinition;
        }

        var type = method.ContainingType;

        if ((type is null) || (method.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction) || method.IsStatic) {
            yield break;
        }
        foreach (var contract in type.AllInterfaces) {
            foreach (var candidate in contract.GetMembers(name: method.Name).OfType<IMethodSymbol>()) {
                if (SymbolEqualityComparer.Default.Equals(x: type.FindImplementationForInterfaceMember(interfaceMember: candidate)?.OriginalDefinition, y: method.OriginalDefinition)) {
                    yield return candidate.OriginalDefinition;
                }
            }
        }
    }
    // What a call to the member replaces by its marks alone: its own, and those of every member it overrides or
    // implements.
    private static ImmutableHashSet<INamedTypeSymbol> MarkedRoots(IMethodSymbol method) {
        var roots = OwnMarks(member: method);

        foreach (var member in Bases(method: method)) {
            roots = roots.Union(other: OwnMarks(member: member));
        }

        return roots;
    }
    // Whether a set of marks covers a root: a mark names the root itself or a type the root derives from, so
    // `[Replaces(typeof(object))]`, which a member of an assembly that cannot see the root carries, covers every root.
    private static bool Covers(IEnumerable<INamedTypeSymbol> marks, INamedTypeSymbol root) =>
        marks.Any(predicate: mark => {
            for (var current = root; (current is not null); current = current.BaseType) {
                if (SymbolEqualityComparer.Default.Equals(x: current.OriginalDefinition, y: mark)) {
                    return true;
                }
            }

            return false;
        });
    // Whether the member's assembly links the marks, which every repository project does.
    private static bool CanCarryMark(ISymbol member) =>
        (member.ContainingAssembly?.GetTypeByMetadataName(fullyQualifiedMetadataName: ReplacesName) is not null);
    private static bool VisibleOutside(ISymbol symbol) {
        for (var current = symbol; (current is not null) && (current is not INamespaceSymbol); current = current.ContainingSymbol) {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)) {
                return false;
            }
        }

        return true;
    }
    private static string Show(ISymbol symbol) =>
        symbol.ToDisplayString(format: SymbolDisplayFormat.CSharpShortErrorMessageFormat);

    // One call a method body makes: what it targets, where it ends, whether it sits in a lambda, and the variables it is
    // handed as arguments (the replacement, when the call adopts one).
    private sealed record Call(IMethodSymbol Target, SyntaxNode Syntax, bool InLambda, ImmutableHashSet<ISymbol> Arguments);
    // One read or write of a tracked local or parameter.
    private sealed record Access(ISymbol Variable, SyntaxNode Syntax, bool Write);
    // A field of a replaceable type assigned outside a constructor.
    private sealed record FieldSwap(IFieldSymbol Field, INamedTypeSymbol Root, SyntaxNode Syntax);
    private sealed class Body {
        public List<Access> Accesses { get; } = [];
        public List<Call> Calls { get; } = [];
        public List<FieldSwap> FieldSwaps { get; } = [];
        // The (call, read) pairs the method's control flow graph lets run in that order with no write of the read's
        // variable between; null when the body has no graph of its own (a local function), which falls back to text
        // order.
        public HashSet<(SyntaxNode Call, SyntaxNode Read)>? Flow { get; set; }
    }

    // Where each call and each tracked access of one body sits in its control flow graph: the basic block and, inside
    // it, the text position, which is the evaluation order within a block.
    private static HashSet<(SyntaxNode Call, SyntaxNode Read)> FlowPairs(ControlFlowGraph graph, Body body) {
        var callSyntax = new HashSet<SyntaxNode>(body.Calls.Where(predicate: static call => !call.InLambda).Select(selector: static call => call.Syntax));
        var accessSyntax = new HashSet<SyntaxNode>(body.Accesses.Select(selector: static access => access.Syntax));
        var blockOf = new Dictionary<SyntaxNode, int>();

        foreach (var block in graph.Blocks) {
            var operations = block.Operations.AsEnumerable();

            if (block.BranchValue is { } branch) {
                operations = operations.Append(element: branch);
            }
            foreach (var operation in operations.SelectMany(selector: static operation => operation.DescendantsAndSelf())) {
                if ((callSyntax.Contains(item: operation.Syntax) || accessSyntax.Contains(item: operation.Syntax)) && !blockOf.ContainsKey(key: operation.Syntax)) {
                    blockOf[operation.Syntax] = block.Ordinal;
                }
            }
        }

        var pairs = new HashSet<(SyntaxNode Call, SyntaxNode Read)>();
        var byVariable = body.Accesses.GroupBy(keySelector: static access => access.Variable, comparer: SymbolEqualityComparer.Default).ToList();

        foreach (var call in body.Calls) {
            if (call.InLambda || !blockOf.TryGetValue(key: call.Syntax, value: out var start)) {
                continue;
            }
            foreach (var group in byVariable) {
                var located = group.Where(predicate: access => blockOf.ContainsKey(key: access.Syntax)).ToList();

                // The call's own block: reads after it, up to the first write after it.
                var firstWrite = located.Where(predicate: access => access.Write && (blockOf[access.Syntax] == start) && (access.Syntax.SpanStart >= call.Syntax.Span.End)).Select(selector: static access => access.Syntax.SpanStart).DefaultIfEmpty(defaultValue: int.MaxValue).Min();

                foreach (var read in located.Where(predicate: access => !access.Write && (blockOf[access.Syntax] == start) && (access.Syntax.SpanStart >= call.Syntax.Span.End) && (access.Syntax.SpanStart < firstWrite))) {
                    _ = pairs.Add(item: (call.Syntax, read.Syntax));
                }
                if (firstWrite != int.MaxValue) {
                    continue;
                }

                // Every block reachable from there: reads before the block's first write, and on past a block only when
                // it writes nothing.
                var seen = new HashSet<int>();
                var pending = new Queue<int>(collection: Successors(graph: graph, ordinal: start));

                while (pending.Count > 0) {
                    var ordinal = pending.Dequeue();

                    if (!seen.Add(item: ordinal)) {
                        continue;
                    }

                    var blockWrite = located.Where(predicate: access => access.Write && (blockOf[access.Syntax] == ordinal)).Select(selector: static access => access.Syntax.SpanStart).DefaultIfEmpty(defaultValue: int.MaxValue).Min();

                    foreach (var read in located.Where(predicate: access => !access.Write && (blockOf[access.Syntax] == ordinal) && (access.Syntax.SpanStart < blockWrite))) {
                        _ = pairs.Add(item: (call.Syntax, read.Syntax));
                    }
                    if (blockWrite == int.MaxValue) {
                        foreach (var next in Successors(graph: graph, ordinal: ordinal)) {
                            pending.Enqueue(item: next);
                        }
                    }
                }
            }
        }

        return pairs;
    }
    // The blocks control can reach next from a block: its branch destinations, the finally regions a branch leaves
    // through, and, for a block inside a try, the catch and finally handlers an exception thrown there runs.
    private static IEnumerable<int> Successors(ControlFlowGraph graph, int ordinal) {
        var block = graph.Blocks[ordinal];

        foreach (var branch in new[] { block.FallThroughSuccessor, block.ConditionalSuccessor }) {
            if (branch is null) {
                continue;
            }
            if (branch.Destination is { } destination) {
                yield return destination.Ordinal;
            }
            foreach (var region in branch.FinallyRegions) {
                yield return region.FirstBlockOrdinal;
            }
        }
        for (var region = block.EnclosingRegion; (region is not null); region = region.EnclosingRegion) {
            if ((region.Kind != ControlFlowRegionKind.Try) || (region.EnclosingRegion is not { } handled)) {
                continue;
            }
            foreach (var handler in handled.NestedRegions) {
                if (handler.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.FilterAndHandler or ControlFlowRegionKind.Finally) {
                    yield return handler.FirstBlockOrdinal;
                }
            }
        }
        // The last block of a finally region carries no destination of its own: control leaves it for wherever the
        // branch that entered it was going, so a finally hands on to the blocks after its try.
        if ((block.EnclosingRegion is { Kind: ControlFlowRegionKind.Finally } finallyRegion) && (finallyRegion.LastBlockOrdinal == ordinal) && (finallyRegion.EnclosingRegion is { } tryAndFinally) && ((tryAndFinally.LastBlockOrdinal + 1) < graph.Blocks.Length)) {
            yield return (tryAndFinally.LastBlockOrdinal + 1);
        }
    }

    private sealed class CompilationState(bool leaf) {
        private readonly ConcurrentDictionary<IMethodSymbol, Body> m_bodies = new(comparer: SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<IMethodSymbol, bool> m_marked = new(comparer: SymbolEqualityComparer.Default);
        // The fields of a replaceable type a property getter returns: the live pointers a host serves.
        private readonly ConcurrentDictionary<IFieldSymbol, IPropertySymbol> m_served = new(comparer: SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<INamedTypeSymbol, bool> m_handles = new(comparer: SymbolEqualityComparer.Default);

        public void Collect(OperationBlockAnalysisContext context) {
            if (context.OwningSymbol is not IMethodSymbol owner) {
                return;
            }

            var body = BodyOf(owner: owner.OriginalDefinition);

            foreach (var block in context.OperationBlocks) {
                Walk(inLambda: false, operation: block, owner: owner.OriginalDefinition);
            }
            if (body.Accesses.Count == 0) {
                return;
            }
            // The body is the one root block a graph can be built over: a method body, a constructor body, or the bare
            // block or expression body an accessor or a lambda-bodied member hands over.
            foreach (var block in context.OperationBlocks) {
                var root = block;

                while (root.Parent is not null) {
                    root = root.Parent;
                }
                // The context hands over the body block, whose parent (the method body) it does not own, so the graph is
                // built over that parent directly.
                var graph = root switch {
                    IMethodBodyOperation methodBody => ControlFlowGraph.Create(methodBody, context.CancellationToken),
                    IConstructorBodyOperation constructorBody => ControlFlowGraph.Create(constructorBody, context.CancellationToken),
                    IBlockOperation bare => ControlFlowGraph.Create(bare, context.CancellationToken),
                    _ => ((ControlFlowGraph?)null),
                };

                if (graph is null) {
                    continue;
                }

                lock (body) {
                    body.Flow = FlowPairs(body: body, graph: graph);
                }

                break;
            }
        }

        private Body BodyOf(IMethodSymbol owner) => m_bodies.GetOrAdd(key: owner, valueFactory: static _ => new Body());

        private void Walk(IOperation operation, IMethodSymbol owner, bool inLambda) {
            switch (operation) {
                case ILocalFunctionOperation { Symbol: var local } localFunction:
                    foreach (var child in localFunction.ChildOperations) {
                        Walk(inLambda: false, operation: child, owner: local.OriginalDefinition);
                    }

                    return;
                case IAnonymousFunctionOperation lambda:
                    foreach (var child in lambda.ChildOperations) {
                        Walk(inLambda: true, operation: child, owner: owner);
                    }

                    return;
            }

            Record(inLambda: inLambda, operation: operation, owner: owner);
            foreach (var child in operation.ChildOperations) {
                Walk(inLambda: inLambda, operation: child, owner: owner);
            }
        }
        private void Record(IOperation operation, IMethodSymbol owner, bool inLambda) {
            switch (operation) {
                case IInvocationOperation invocation:
                    AddCall(arguments: invocation.Arguments, inLambda: inLambda, owner: owner, syntax: invocation.Syntax, target: invocation.TargetMethod);
                    break;
                case IPropertyReferenceOperation property: {
                        var accessor = (IsWritten(operation: property) ? property.Property.SetMethod : property.Property.GetMethod);

                        if (accessor is not null) {
                            AddCall(arguments: property.Arguments, inLambda: inLambda, owner: owner, syntax: property.Syntax, target: accessor);
                        }

                        break;
                    }
                case IVariableDeclaratorOperation { Initializer: not null } declarator when (RootOf(type: declarator.Symbol.Type) is not null) && !inLambda:
                    Add(owner: owner, access: new Access(Syntax: declarator.Syntax, Variable: declarator.Symbol, Write: true));
                    break;
                case ILocalReferenceOperation local when (RootOf(type: local.Local.Type) is not null) && !inLambda:
                    Add(owner: owner, access: new Access(Syntax: local.Syntax, Variable: local.Local, Write: IsWritten(operation: local)));
                    break;
                case IParameterReferenceOperation parameter when (RootOf(type: parameter.Parameter.Type) is not null) && !inLambda:
                    Add(owner: owner, access: new Access(Syntax: parameter.Syntax, Variable: parameter.Parameter, Write: IsWritten(operation: parameter)));
                    break;
                case ISimpleAssignmentOperation { Target: IFieldReferenceOperation { Field: var field } } assignment
                    when (owner.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor)) && (RootOf(type: field.Type) is { } root):
                    lock (BodyOf(owner: owner)) {
                        BodyOf(owner: owner).FieldSwaps.Add(item: new FieldSwap(Field: field, Root: root, Syntax: assignment.Syntax));
                    }

                    break;
                case IReturnOperation { ReturnedValue: IFieldReferenceOperation { Field: var served } }
                    when (owner is { MethodKind: MethodKind.PropertyGet, AssociatedSymbol: IPropertySymbol property }) && (RootOf(type: served.Type) is not null):
                    m_served[served.OriginalDefinition] = property;
                    break;
            }
        }
        // Records a call that can matter: one into this compilation's own source, which may reach a replacement, or one
        // whose marks say it replaces. A call into another assembly without a mark replaces nothing the rule can see.
        private void AddCall(IMethodSymbol target, ImmutableArray<IArgumentOperation> arguments, SyntaxNode syntax, IMethodSymbol owner, bool inLambda) {
            target = (target.ReducedFrom ?? target).OriginalDefinition;
            // A lambda runs when whoever receives the delegate decides, most often after the method that built it has
            // returned (a command handler, a callback), so its calls are neither ordered against the method's reads nor
            // counted toward what the method replaces.
            if (
                inLambda ||
                (
                    !target.Locations.Any(predicate: static location => location.IsInSource) &&
                    !m_marked.GetOrAdd(key: target, valueFactory: static method => !MarkedRoots(method: method).IsEmpty)
                )
            ) {
                return;
            }

            var handed = ImmutableHashSet.CreateBuilder<ISymbol>(equalityComparer: SymbolEqualityComparer.Default);

            foreach (var argument in arguments) {
                switch (argument.Value) {
                    case ILocalReferenceOperation local:
                        handed.Add(item: local.Local);
                        break;
                    case IParameterReferenceOperation parameter:
                        handed.Add(item: parameter.Parameter);
                        break;
                }
            }

            var body = BodyOf(owner: owner);

            lock (body) {
                body.Calls.Add(item: new Call(Arguments: handed.ToImmutable(), InLambda: inLambda, Syntax: syntax, Target: target));
            }
        }
        private void Add(IMethodSymbol owner, Access access) {
            var body = BodyOf(owner: owner);

            lock (body) {
                body.Accesses.Add(item: access);
            }
        }
        // Whether the reference is the target of an assignment or an out argument, rather than a read.
        private static bool IsWritten(IOperation operation) => operation.Parent switch {
            ISimpleAssignmentOperation assignment => ReferenceEquals(objA: assignment.Target, objB: operation),
            IArgumentOperation { ArgumentKind: not ArgumentKind.DefaultValue } argument => (argument.Parameter?.RefKind == RefKind.Out),
            IDeclarationExpressionOperation => true,
            _ => false,
        };

        public void Report(CompilationAnalysisContext context) {
            var reach = Reach();

            foreach (var pair in m_bodies) {
                var method = pair.Key;
                var body = pair.Value;

                if (!method.Locations.Any(predicate: static location => location.IsInSource)) {
                    continue;
                }

                // A test holds an arena across a replacement on purpose (to compare the retired one with its
                // successor) or drives a host whose commit never replaces, so its reads are not held to STALE001.
                if (!leaf) {
                    ReportStaleReads(body: body, context: context, reach: reach);
                }
                ReportUnmarkedReach(context: context, method: method, reach: reach);
                foreach (var swap in body.FieldSwaps) {
                    if (m_served.TryGetValue(key: swap.Field.OriginalDefinition, value: out var property) && !Covers(marks: OwnMarks(member: method), root: swap.Root)) {
                        context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                            descriptor: Stale003UnmarkedFieldSwap,
                            location: swap.Syntax.GetLocation(),
                            messageArgs: [Show(symbol: method), swap.Field.Name, swap.Root.Name, Show(symbol: property)]
                        ));
                    }
                }
            }
        }

        // The roots each collected method can replace, with the call that first reaches each, by a fixed point over the
        // compilation's own call graph seeded with the marks.
        private Dictionary<IMethodSymbol, Dictionary<INamedTypeSymbol, IMethodSymbol>> Reach() {
            var reach = new Dictionary<IMethodSymbol, Dictionary<INamedTypeSymbol, IMethodSymbol>>(comparer: SymbolEqualityComparer.Default);
            var marked = new Dictionary<IMethodSymbol, ImmutableHashSet<INamedTypeSymbol>>(comparer: SymbolEqualityComparer.Default);

            foreach (var pair in m_bodies) {
                reach[pair.Key] = new Dictionary<INamedTypeSymbol, IMethodSymbol>(comparer: SymbolEqualityComparer.Default);
            }

            ImmutableHashSet<INamedTypeSymbol> Marked(IMethodSymbol method) {
                if (!marked.TryGetValue(key: method, value: out var roots)) {
                    roots = MarkedRoots(method: method);
                    marked[method] = roots;
                }

                return roots;
            }

            for (var changed = true; changed;) {
                changed = false;
                foreach (var pair in m_bodies) {
                    var own = reach[pair.Key];

                    foreach (var call in pair.Value.Calls) {
                        foreach (var root in Marked(method: call.Target)) {
                            if (!own.ContainsKey(key: root)) {
                                own[root] = call.Target;
                                changed = true;
                            }
                        }
                        if (reach.TryGetValue(key: call.Target, value: out var callee)) {
                            foreach (var root in callee.Keys) {
                                if (!own.ContainsKey(key: root)) {
                                    own[root] = call.Target;
                                    changed = true;
                                }
                            }
                        }
                    }
                }
            }

            return reach;
        }
        private ImmutableHashSet<INamedTypeSymbol> Replaces(Call call, Dictionary<IMethodSymbol, Dictionary<INamedTypeSymbol, IMethodSymbol>> reach) {
            var roots = MarkedRoots(method: call.Target);

            return (reach.TryGetValue(key: call.Target, value: out var callee)
                ? roots.Union(other: callee.Keys)
                : roots);
        }
        private void ReportStaleReads(Body body, CompilationAnalysisContext context, Dictionary<IMethodSymbol, Dictionary<INamedTypeSymbol, IMethodSymbol>> reach) {
            if (body.Accesses.Count == 0) {
                return;
            }

            var replacing = body.Calls
                .Where(predicate: call => !call.InLambda)
                .Select(selector: call => (Call: call, Roots: Replaces(call: call, reach: reach)))
                .Where(predicate: static entry => !entry.Roots.IsEmpty)
                .ToList();

            if (replacing.Count == 0) {
                return;
            }

            foreach (var group in body.Accesses.GroupBy(keySelector: static access => access.Variable, elementSelector: static access => access, comparer: SymbolEqualityComparer.Default)) {
                var variable = group.Key;
                var root = RootOf(type: ((variable as ILocalSymbol)?.Type ?? (variable as IParameterSymbol)?.Type))!;
                var writes = group.Where(predicate: static access => access.Write).ToList();

                foreach (var read in group.Where(predicate: static access => !access.Write).OrderBy(keySelector: static access => access.Syntax.SpanStart)) {
                    var stale = replacing.FirstOrDefault(predicate: entry =>
                        Covers(marks: entry.Roots, root: root) &&
                        !entry.Call.Arguments.Contains(item: variable) &&
                        ((body.Flow is { } flow)
                            ? flow.Contains(item: (entry.Call.Syntax, read.Syntax))
                            : Stale(call: entry.Call.Syntax, read: read.Syntax, variable: variable, writes: writes)));

                    if (stale.Call is null) {
                        continue;
                    }

                    var line = (stale.Call.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

                    context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                        additionalLocations: [stale.Call.Syntax.GetLocation()],
                        descriptor: Stale001ReadAfterReplacement,
                        location: read.Syntax.GetLocation(),
                        messageArgs: [variable.Name, root.Name, Show(symbol: stale.Call.Target), line]
                    ));

                    break;
                }
            }
        }
        // Whether the read can follow the call with no write of the variable between: later in the text with no write in
        // between, or earlier in a loop that holds both and writes the variable nowhere inside it.
        private static bool Stale(SyntaxNode call, SyntaxNode read, ISymbol variable, List<Access> writes) {
            if (call.Span.Contains(span: read.Span) || read.Span.Contains(span: call.Span)) {
                return false;
            }
            if (call.Span.End <= read.SpanStart) {
                return !writes.Any(predicate: write => ((write.Syntax.SpanStart >= call.Span.End) && (write.Syntax.SpanStart < read.SpanStart)));
            }

            var loop = call.Ancestors().FirstOrDefault(predicate: static node => node is ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

            return (loop is not null) &&
                loop.Span.Contains(span: read.Span) &&
                !writes.Any(predicate: write => loop.Span.Contains(span: write.Syntax.Span)) &&
                !(variable.Locations.FirstOrDefault() is { IsInSource: true } declared && loop.Span.Contains(span: declared.SourceSpan) && (declared.SourceTree == call.SyntaxTree));
        }
        // Whether another assembly's caller can hold a capture this type's calls invalidate: the type hands out a
        // replaceable instance (a non-private property or method returns one). A public method of any other type that
        // reaches a replacement is not held to the mark, so a capture taken from one object and invalidated through an
        // unrelated one in another assembly is the rule's documented blind spot; holding every orchestrating type to the
        // mark would mark most of the world's public surface for a capture almost no caller takes.
        private bool Handles(INamedTypeSymbol? type) =>
            (type is not null) &&
            m_handles.GetOrAdd(key: type.OriginalDefinition, valueFactory: static candidate =>
                candidate.GetMembers().Any(predicate: static member => (member.DeclaredAccessibility != Accessibility.Private) && member switch {
                    IPropertySymbol property => (RootOf(type: property.Type) is not null),
                    IMethodSymbol { MethodKind: MethodKind.Ordinary } method => (RootOf(type: method.ReturnType) is not null),
                    _ => false,
                }));
        private void ReportUnmarkedReach(IMethodSymbol method, CompilationAnalysisContext context, Dictionary<IMethodSymbol, Dictionary<INamedTypeSymbol, IMethodSymbol>> reach) {
            if ((method.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction or MethodKind.Constructor or MethodKind.StaticConstructor) || !reach.TryGetValue(key: method, value: out var roots)) {
                return;
            }

            var own = OwnMarks(member: method);

            if (own.IsEmpty && (roots.Count == 0)) {
                return;
            }

            var bases = Bases(method: method).ToList();
            var location = method.Locations.First(predicate: static location => location.IsInSource);
            // A member marked itself still hides the replacement from a caller that dispatches through its base, so its
            // own marks are held to the bases as well.
            var held = own.Select(selector: root => (Root: root, Through: method))
                .Concat(second: roots.Where(predicate: pair => !Covers(marks: own, root: pair.Key)).Select(selector: static pair => (Root: pair.Key, Through: pair.Value)));

            foreach (var (root, through) in held) {
                // A base declared outside the repository (a framework interface) cannot carry the mark, so dispatch
                // through it is a blind spot rather than a finding.
                foreach (var member in bases.Where(predicate: static member => CanCarryMark(member: member))) {
                    if (!Covers(marks: OwnMarks(member: member), root: root)) {
                        context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                            descriptor: Stale002UnmarkedReach,
                            location: location,
                            messageArgs: [Show(symbol: method), root.Name, Show(symbol: through), $"'{Show(symbol: member)}', which it overrides or implements,"]
                        ));
                    }
                }
                if ((bases.Count == 0) && !leaf && !Covers(marks: own, root: root) && VisibleOutside(symbol: method) && Handles(type: method.ContainingType)) {
                    context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                        descriptor: Stale002UnmarkedReach,
                        location: location,
                        messageArgs: [Show(symbol: method), root.Name, Show(symbol: through), $"it is visible outside its assembly on a type that hands out a {root.Name}, and"]
                    ));
                }
            }
        }
    }
}
