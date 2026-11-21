namespace Puck.Transpiler.Ast;

/// <summary>What a name a node binds is.</summary>
public enum SyntaxBinderKind {
    /// <summary>A lambda's parameter.</summary>
    LambdaParameter,
    /// <summary>A template's or a function's parameter.</summary>
    Parameter,
    /// <summary>A <c>let</c> written inside a template, a block or a loop body.</summary>
    Constant,
    /// <summary>The element a compile-time or test <c>for</c> walks.</summary>
    LoopItem,
    /// <summary>The zero-based index of a <c>for</c> or a <c>repeat</c>.</summary>
    LoopIndex,
    /// <summary>The alias a <c>claim</c>, a <c>for each</c> or a rule's pool binds an instance to.</summary>
    PoolBinding,
    /// <summary>A rule's <c>local</c>.</summary>
    RuleLocal,
}
/// <summary>One name a node binds for its children.</summary>
/// <param name="Name">The bound name.</param>
/// <param name="Declaration">The node that declares it: the parameter, <c>let</c> or <c>local</c> itself, or the
/// lambda, loop or claim whose header names it.</param>
/// <param name="Kind">What the name is.</param>
public readonly record struct SyntaxBinder(string Name, SyntaxNode Declaration, SyntaxBinderKind Kind) {
    /// <summary>Gets a value indicating whether the name hides a document constant of the same name where it is in
    /// scope. A rule local does not: a bare name in a rule reads the compile-time binding first.</summary>
    public bool Shadows => (Kind != SyntaxBinderKind.RuleLocal);
}
/// <summary>The one description of the names a syntax node binds and where in it each is visible, which every reader
/// that resolves a name without lowering the tree goes by: the import rewriter's shielding, hover and
/// go-to-definition.</summary>
/// <remarks>It states what lowering binds (<see cref="Lowering.DocumentLowering"/> and the vocabularies' rule
/// bodies): a parameter in its body and in the defaults after it, a template's <c>let</c> anywhere in its body, a
/// block's or a loop body's <c>let</c> in the statements after it, a loop's variables in its body and never in the
/// sequence it walks. A document's own declarations are not here: they are the names an import renames.</remarks>
public static class SyntaxScopes {
    private static void AddLets<TNode>(List<SyntaxBinder> into, IReadOnlyList<TNode> statements, SyntaxNode? before) where TNode : SyntaxNode {
        foreach (var statement in statements) {
            if (ReferenceEquals(objA: statement, objB: before)) {
                return;
            }
            if (statement is LetNode constant) {
                into.Add(item: new SyntaxBinder(Declaration: constant, Kind: SyntaxBinderKind.Constant, Name: constant.Name));
            }
        }
    }
    private static void AddLoop(List<SyntaxBinder> into, SyntaxNode loop, string? item, string? index) {
        if (item is not null) { into.Add(item: new SyntaxBinder(Declaration: loop, Kind: SyntaxBinderKind.LoopItem, Name: item)); }
        if (index is not null) { into.Add(item: new SyntaxBinder(Declaration: loop, Kind: SyntaxBinderKind.LoopIndex, Name: index)); }
    }
    private static void AddParameters(List<SyntaxBinder> into, IReadOnlyList<TemplateParameterNode> parameters, SyntaxNode? before) {
        foreach (var parameter in parameters) {
            if (ReferenceEquals(objA: parameter, objB: before)) {
                return;
            }
            into.Add(item: new SyntaxBinder(Declaration: parameter, Kind: SyntaxBinderKind.Parameter, Name: parameter.Name));
        }
    }

    /// <summary>Returns the names <paramref name="node"/> binds that are visible in <paramref name="child"/>.</summary>
    /// <param name="node">The node that may bind names.</param>
    /// <param name="child">The direct child of <paramref name="node"/> the names are read from, or
    /// <see langword="null"/> for a position in <paramref name="node"/> that stands in none of its children, which
    /// sees every name the node declares.</param>
    /// <returns>The bound names; empty when <paramref name="node"/> binds none there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SyntaxBinder> Binds(SyntaxNode node, SyntaxNode? child) {
        ArgumentNullException.ThrowIfNull(argument: node);
        var bound = new List<SyntaxBinder>();

        switch (node) {
            case LambdaExpressionNode lambda:
                bound.AddRange(collection: lambda.Parameters.Select(selector: name => new SyntaxBinder(Declaration: lambda, Kind: SyntaxBinderKind.LambdaParameter, Name: name)));
                break;
            // A function's defaults are evaluated where it is called, so no parameter is visible in one.
            case FunctionExpressionNode function when (child is not TemplateParameterNode):
                AddParameters(before: null, into: bound, parameters: function.Parameters);
                break;
            case TemplateNode template when (child is TemplateParameterNode):
                AddParameters(before: child, into: bound, parameters: template.Parameters);
                break;
            case TemplateNode template:
                AddParameters(before: null, into: bound, parameters: template.Parameters);
                AddLets(before: null, into: bound, statements: template.Body.Statements);
                break;
            case BlockNode block:
                AddLets(before: child, into: bound, statements: block.Statements);
                break;
            case ForStatementNode loop when !ReferenceEquals(objA: child, objB: loop.Sequence):
                AddLoop(index: loop.Index, into: bound, item: loop.Item, loop: loop);
                AddLets(before: child, into: bound, statements: loop.Body);
                break;
            case ForExpressionNode loop when !ReferenceEquals(objA: child, objB: loop.Sequence):
                AddLoop(index: loop.Index, into: bound, item: loop.Item, loop: loop);
                AddLets(before: child, into: bound, statements: loop.Bindings);
                break;
            case TestGivenForNode loop when !ReferenceEquals(objA: child, objB: loop.Sequence):
                AddLoop(index: loop.Index, into: bound, item: loop.Item, loop: loop);
                break;
            case TestWhenLoopNode loop when !ReferenceEquals(objA: child, objB: loop.Sequence):
                AddLoop(index: loop.Index, into: bound, item: loop.Item, loop: loop);
                break;
            case RepeatStatementNode repeat when !ReferenceEquals(objA: child, objB: repeat.Count):
                AddLoop(index: repeat.Index, into: bound, item: null, loop: repeat);
                break;
            case ClaimStatementNode claim:
                bound.Add(item: new SyntaxBinder(Declaration: claim, Kind: SyntaxBinderKind.PoolBinding, Name: claim.Alias));
                break;
            case ClaimPairStatementNode claim:
                bound.Add(item: new SyntaxBinder(Declaration: claim, Kind: SyntaxBinderKind.PoolBinding, Name: claim.Alias));
                break;
            case PoolForEachStatementNode each:
                bound.Add(item: new SyntaxBinder(Declaration: each, Kind: SyntaxBinderKind.PoolBinding, Name: each.Alias));
                break;
            case RuleBlockNode rule:
                if (rule.PoolBinding is { } binding) {
                    bound.Add(item: new SyntaxBinder(Declaration: rule, Kind: SyntaxBinderKind.PoolBinding, Name: binding));
                }
                bound.AddRange(collection: rule.Statements.OfType<LocalStatementNode>().Select(selector: static local => new SyntaxBinder(Declaration: local, Kind: SyntaxBinderKind.RuleLocal, Name: local.Name)));
                break;
            default:
                return [];
        }

        return bound;
    }
    /// <summary>Returns the binder <paramref name="name"/> resolves to at the innermost position of
    /// <paramref name="path"/>, walking outward.</summary>
    /// <param name="path">The nodes the position stands in, outermost first (<see cref="SyntaxWalk.PathAt"/>).</param>
    /// <param name="name">The name read at the position.</param>
    /// <returns>The nearest binder that hides a document constant of the same name; failing that, the nearest rule
    /// local, which a document constant of that name still outranks; <see langword="null"/> when no node on
    /// <paramref name="path"/> binds <paramref name="name"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="name"/> is
    /// <see langword="null"/>.</exception>
    public static SyntaxBinder? Resolve(IReadOnlyList<SyntaxNode> path, string name) {
        ArgumentNullException.ThrowIfNull(argument: path);
        ArgumentNullException.ThrowIfNull(argument: name);
        SyntaxBinder? local = null;

        // A position on a nested declaration's own name is that declaration, which is not in scope in itself.
        if ((path.Count >= 2) && (path[^2] is not DocumentNode) && ((path[^1] switch {
            LetNode constant => new SyntaxBinder(Declaration: constant, Kind: SyntaxBinderKind.Constant, Name: constant.Name),
            TemplateParameterNode parameter => new SyntaxBinder(Declaration: parameter, Kind: SyntaxBinderKind.Parameter, Name: parameter.Name),
            LocalStatementNode declared => new SyntaxBinder(Declaration: declared, Kind: SyntaxBinderKind.RuleLocal, Name: declared.Name),
            _ => ((SyntaxBinder?)null),
        }) is { } self) && string.Equals(a: self.Name, b: name, comparisonType: StringComparison.Ordinal)) {
            return self;
        }
        for (var index = (path.Count - 1); (index >= 0); --index) {
            // The last matching binder is the nearest declaration, which is the one a later `let` of the same name
            // leaves in force.
            foreach (var binder in Binds(child: (((index + 1) < path.Count) ? path[(index + 1)] : null), node: path[index]).Reverse()) {
                if (!string.Equals(a: binder.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                    continue;
                }
                if (binder.Shadows) {
                    return binder;
                }
                local ??= binder;
            }
        }

        return local;
    }
}
