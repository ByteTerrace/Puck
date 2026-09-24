using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The named-argument normalizer (the `named-args` pass). Semantic: it resolves each call's method symbol
// to read parameter names, so it runs against a Compilation (NamedArgsPhase) rather than the syntactic
// pipeline. Every real method/ctor call gets its arguments named (`name: value`) and sorted
// alphabetically by parameter name — the house convention. A call written fully named is already past
// the naming half and still gets the sort. Left as written (skipped) when there is no resolvable method
// symbol (function-pointer / delegate invokes have none), a mix of named and positional arguments, a
// `params` parameter, an omitted optional argument, a comment or #directive on any argument or
// separator, or when the sort would move a side-effecting argument (see ExpressionSafety) — the cases
// where naming-and-reordering is unsafe or ambiguous. An out/ref/in keyword rides with its argument
// (named arguments allow it: `value: out x`). A call whose target has a [DynamicallyAccessedMembers]
// parameter is named but never sorted: ILLink's trim dataflow binds arguments to parameters by
// position even when they are named, so moving the annotated argument out of its declared slot turns
// a clean build into IL2072 under IsAotCompatible.
//
// A rewrite may never move a call to another overload, so the named and reordered argument list is
// speculatively re-bound before it is emitted and dropped unless it still resolves to the same method
// symbol. Naming makes overloads applicable that the written positions excluded, and alphabetizing can do
// the same, so agreement is checked rather than assumed. Unresolved code needs no separate guard: an
// invocation whose arguments or receiver carry an error type resolves to candidates rather than to a
// symbol, which the symbol check above already declines.
internal sealed class NamedArgsRewriter : CSharpSyntaxRewriter {
    private readonly SemanticModel m_model;

    public NamedArgsRewriter(SemanticModel model) {
        m_model = model;
    }

    // True when the parameter carries System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute
    // — the trim/AOT dataflow annotation that pins its argument to the declared position.
    private static bool HasTrimAnnotation(IParameterSymbol parameter) => parameter.GetAttributes().Any(predicate: static attribute =>
        ((attribute.AttributeClass is { Name: "DynamicallyAccessedMembersAttribute" } attributeClass)
        && (attributeClass.ContainingNamespace.ToDisplayString() == "System.Diagnostics.CodeAnalysis")));
    // The written arguments carrying their parameter names, permuted into `order`. The same construction
    // produces the emitted list and the list the speculative re-bind is run against, so the two can never
    // disagree about which name lands on which expression.
    private static ArgumentSyntax[] NameAndOrder(SeparatedSyntaxList<ArgumentSyntax> arguments, ImmutableArray<IParameterSymbol> parameters, int[] order) {
        var named = new ArgumentSyntax[arguments.Count];

        for (var index = 0; (index < arguments.Count); index++) {
            // An already-named argument is carried as written — its name colon and expression are already
            // in house shape; only its slot (and that slot's trivia) may move.
            named[index] = ((arguments[index].NameColon is not null)
                ? arguments[index]
                : WithParameterName(
                    argument: arguments[index],
                    parameterName: parameters[index].Name
                )
            );
        }

        return Array.ConvertAll(
            array: order,
            converter: index => named[index]
        );
    }
    // A parameter declared as a verbatim identifier (`object? @object`) has the bare keyword as its symbol
    // name; written back without the `@` it is a keyword again, not an argument name. The token therefore
    // carries the escaped spelling as its text and the parameter's own name as its value — binding reads
    // the value, so a token built from the escaped spelling alone matches no parameter. An out/ref/in
    // keyword rides with its argument, which named arguments allow (`value: out x`).
    private static ArgumentSyntax WithParameterName(ArgumentSyntax argument, string parameterName) {
        var identifier = SyntaxFactory.Identifier(
            leading: default,
            contextualKind: SyntaxKind.IdentifierToken,
            text: ((SyntaxFacts.GetKeywordKind(text: parameterName) != SyntaxKind.None)
            ? $"@{parameterName}"
            : parameterName),
            trailing: default,
            valueText: parameterName
        );

        return SyntaxFactory.Argument(
            expression: argument.Expression.WithoutLeadingTrivia().WithoutTrailingTrivia(),
            nameColon: SyntaxFactory
                .NameColon(name: SyntaxFactory.IdentifierName(identifier: identifier))
                .WithColonToken(colonToken: SyntaxFactory.Token(kind: SyntaxKind.ColonToken).WithTrailingTrivia(trivia: SyntaxFactory.Space)),
            refKindKeyword: (argument.RefKindKeyword.IsKind(kind: SyntaxKind.None)
                ? default
                : argument.RefKindKeyword.WithLeadingTrivia().WithTrailingTrivia(SyntaxFactory.Space)
            )
        );
    }
    // True when the rewritten call still resolves to `method`. The probe is built from the call's own
    // expressions so only the names and their order differ from what is already bound; a target-typed
    // `new(...)` is probed through its resolved type, because speculative binding carries no target type.
    // An object or collection initializer is dropped from the probe: it cannot change which constructor is
    // chosen, and it can reference members the speculative position does not see. A call anywhere inside a `?.`
    // chain (`x?.M(...)`, `x?.y!.M(...)`) binds only within that access, so it is re-bound in place.
    private bool BindsToSameMethod(SyntaxNode originalCall, IMethodSymbol method, ArgumentListSyntax arguments) {
        if (
            (originalCall is InvocationExpressionSyntax bound) &&
            (ConditionalAccessRoot(node: bound) is not null)
        ) {
            return SymbolEqualityComparer.Default.Equals(
                x: SpeculativeSymbolInPlace(
                    arguments: arguments,
                    call: bound
                ),
                y: method
            );
        }

        ExpressionSyntax? probe = originalCall switch {
            InvocationExpressionSyntax invocation => invocation.WithArgumentList(argumentList: arguments),
            ObjectCreationExpressionSyntax creation => creation.WithArgumentList(argumentList: arguments).WithInitializer(initializer: null),
            ImplicitObjectCreationExpressionSyntax => SyntaxFactory.ObjectCreationExpression(
            argumentList: arguments,
            initializer: null,
            type: SyntaxFactory.ParseTypeName(text: method.ContainingType.ToDisplayString(format: SymbolDisplayFormat.FullyQualifiedFormat))
        ),
            _ => null,
        };

        return ((probe is not null)
            && SymbolEqualityComparer.Default.Equals(
            x: m_model.GetSpeculativeSymbolInfo(
                position: originalCall.SpanStart,
                expression: probe,
                bindingOption: SpeculativeBindingOption.BindAsExpression
            ).Symbol,
            y: method
        ));
    }
    // Binds the call with its argument list replaced, in place: the enclosing statement, expression body, or initializer
    // is re-bound speculatively with the swap made, and the swapped call's symbol is read back. Null when the call sits
    // somewhere no speculative model can be rooted, which leaves it positional.
    private ISymbol? SpeculativeSymbolInPlace(InvocationExpressionSyntax call, ArgumentListSyntax arguments) {
        var marker = new SyntaxAnnotation();
        var replacement = call.WithArgumentList(argumentList: arguments).WithAdditionalAnnotations(marker);

        foreach (var ancestor in call.Ancestors()) {
            SemanticModel? speculative = null;
            SyntaxNode? rooted = null;

            switch (ancestor) {
                case StatementSyntax statement when m_model.TryGetSpeculativeSemanticModel(
                    position: statement.SpanStart,
                    speculativeModel: out speculative,
                    statement: ((StatementSyntax)(rooted = statement.ReplaceNode(
                        newNode: replacement,
                        oldNode: call
                    )))
                ):
                case ArrowExpressionClauseSyntax arrow when m_model.TryGetSpeculativeSemanticModel(
                    expressionBody: ((ArrowExpressionClauseSyntax)(rooted = arrow.ReplaceNode(
                        newNode: replacement,
                        oldNode: call
                    ))),
                    position: arrow.SpanStart,
                    speculativeModel: out speculative
                ):
                case EqualsValueClauseSyntax initializer when m_model.TryGetSpeculativeSemanticModel(
                    initializer: ((EqualsValueClauseSyntax)(rooted = initializer.ReplaceNode(
                        newNode: replacement,
                        oldNode: call
                    ))),
                    position: initializer.SpanStart,
                    speculativeModel: out speculative
                ):
                    var swapped = rooted!.GetAnnotatedNodes(syntaxAnnotation: marker).Single();

                    return speculative!.GetSymbolInfo(node: swapped).Symbol;
                case MemberDeclarationSyntax:
                    return null;
            }
        }

        return null;
    }
    // The outermost conditional access whose `WhenNotNull` chain holds the node: `a?.b?.M()` nests one access
    // inside the other's `WhenNotNull`, and only the outermost carries a receiver that binds on its own.
    private static ConditionalAccessExpressionSyntax? ConditionalAccessRoot(SyntaxNode node) {
        ConditionalAccessExpressionSyntax? root = null;
        var current = node;

        while (current.Parent is { } parent) {
            if (
                (parent is ConditionalAccessExpressionSyntax access) &&
                access.WhenNotNull.Span.Contains(span: current.Span)
            ) {
                root = access;
            } else if (root is not null) {
                break;
            }

            current = parent;
        }

        return root;
    }
    // SemanticModel only accepts nodes from its own syntax tree. Child visits may have rebuilt the
    // argument list already, so evaluation-safety is always inspected on the original bound call.
    private static SeparatedSyntaxList<ArgumentSyntax> OriginalArguments(SyntaxNode call) => call switch {
        InvocationExpressionSyntax invocation => invocation.ArgumentList.Arguments,
        ObjectCreationExpressionSyntax creation => creation.ArgumentList!.Arguments,
        ImplicitObjectCreationExpressionSyntax creation => creation.ArgumentList.Arguments,
        _ => default,
    };
    // The ORIGINAL node carries the symbol (the rewritten copy is detached from the model); the VISITED
    // list supplies the already-child-rewritten argument expressions. Returns the reordered+named list,
    // or null to leave the call alone.
    private ArgumentListSyntax? Rebuild(SyntaxNode originalCall, ArgumentListSyntax? visitedList) {
        if (
            (visitedList is null) ||
            (visitedList.Arguments.Count == 0)
        ) {
            return null;
        }

        if (
            (m_model.GetSymbolInfo(node: originalCall).Symbol is not IMethodSymbol method) ||
            (method.MethodKind is MethodKind.FunctionPointerSignature or MethodKind.DelegateInvoke)
        ) {
            return null;
        }

        var arguments = visitedList.Arguments;
        var originalArguments = OriginalArguments(call: originalCall);
        var parameters = method.Parameters;
        var namedCount = arguments.Count(predicate: static argument => (argument.NameColon is not null));

        // A partly named call is declined: naming its positional remainder needs the argument-to-
        // parameter mapping the mix obscures. A fully named call is only sorted, never renamed.
        if (
            (arguments.Count != parameters.Length) ||
            parameters.Any(predicate: static parameter => parameter.IsParams) ||
            ((namedCount != 0) && (namedCount != arguments.Count))
        ) {
            return null;
        }

        // Trivia is reassigned by slot while the arguments move — separators included — so a comment
        // written above one argument, or after one argument's comma, would end up documenting whichever
        // argument lands in that slot. Leave the call positional.
        if (RewriteShaping.IsAnnotated(list: arguments)) {
            return null;
        }

        // A target with a [DynamicallyAccessedMembers] parameter keeps its written argument order:
        // ILLink's trim dataflow binds arguments positionally, named or not, so a sort would move the
        // annotated argument out of its declared slot and fail the build with IL2072. Naming in place
        // is still safe — the positions do not move.
        var sortable = !parameters.Any(predicate: static parameter => HasTrimAnnotation(parameter: parameter));

        // Naming preserves written positions, but the alphabetical SORT moves them. When that move is
        // real AND any argument is side-effecting, leave the call as written — C# evaluates arguments
        // left-to-right in WRITTEN order (named or not), so reordering would change evaluation order.
        // The written name is the argument's own name colon when it has one (a fully named call may
        // already sit in any order), else the parameter at its position.
        var writtenNames = arguments.Select(selector: (argument, index) => (argument.NameColon?.Name.Identifier.ValueText ?? parameters[index].Name)).ToArray();

        if (
            sortable &&
            !writtenNames.SequenceEqual(second: writtenNames.OrderBy(
            keySelector: static name => name,
            comparer: StringComparer.Ordinal
        )) &&
            originalArguments.Any(predicate: argument => ExpressionSafety.HasSideEffect(
            expression: argument.Expression,
            model: m_model
        ))
        ) {
            return null;
        }

        // Slots are addressed by original position and then permuted; the per-slot trivia is reassigned
        // afterwards so the call's existing single-line or one-argument-per-line layout survives the
        // reorder unchanged.
        var order = Enumerable.Range(
            count: arguments.Count,
            start: 0
        ).ToArray();

        if (sortable) {
            order = [.. order.OrderBy(
                keySelector: index => writtenNames[index],
                comparer: StringComparer.Ordinal
            )];
        }

        // A call that is already fully named and already in order has nothing to rewrite, so it never
        // reaches the re-bind probe.
        if (
            (namedCount == arguments.Count) &&
            order.SequenceEqual(second: Enumerable.Range(
            count: arguments.Count,
            start: 0
        ))
        ) {
            return null;
        }

        if (!BindsToSameMethod(
            arguments: SyntaxFactory.ArgumentList(arguments: SyntaxFactory.SeparatedList(nodes: NameAndOrder(
                arguments: originalArguments,
                order: order,
                parameters: parameters
            ))),
            method: method,
            originalCall: originalCall
        )) {
            return null;
        }

        return visitedList.WithArguments(arguments: RewriteShaping.ReorderInPlace(
            ordered: NameAndOrder(
                arguments: arguments,
                order: order,
                parameters: parameters
            ),
            original: arguments
        ));
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node) {
        var visited = ((ImplicitObjectCreationExpressionSyntax)base.VisitImplicitObjectCreationExpression(node: node)!);

        return ((Rebuild(
            originalCall: node,
            visitedList: visited.ArgumentList
        ) is { } rebuilt)
            ? visited.WithArgumentList(argumentList: rebuilt)
            : visited
        );
    }
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
        var visited = ((InvocationExpressionSyntax)base.VisitInvocationExpression(node: node)!);

        return ((Rebuild(
            originalCall: node,
            visitedList: visited.ArgumentList
        ) is { } rebuilt)
            ? visited.WithArgumentList(argumentList: rebuilt)
            : visited
        );
    }
    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node) {
        var visited = ((ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node: node)!);

        return (((visited.ArgumentList is { } list) && (Rebuild(
            originalCall: node,
            visitedList: list
        ) is { } rebuilt))
            ? visited.WithArgumentList(argumentList: rebuilt)
            : visited
        );
    }
}
