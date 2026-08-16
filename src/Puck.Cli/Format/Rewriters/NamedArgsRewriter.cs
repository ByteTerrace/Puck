using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The named-argument normalizer (the `named-args` pass). SEMANTIC: it resolves each call's method symbol
// to read parameter names, so it runs against a Compilation (NamedArgsPhase) rather than the syntactic
// pipeline. Every real method/ctor call gets its arguments named (`name: value`) and sorted
// alphabetically by parameter name — the house convention. A call written fully named is already past
// the naming half and still gets the sort. Left as written (skipped) when there is no resolvable method
// symbol (function-pointer / delegate invokes have none), a mix of named and positional arguments, a
// `params` parameter, an omitted optional argument, a comment or #directive on any argument or
// separator, or when the sort would move a side-effecting argument (see ExpressionSafety) — the cases
// where naming-and-reordering is unsafe or ambiguous. An out/ref/in keyword rides with its argument
// (named arguments allow it: `value: out x`). A call whose target has a [DynamicallyAccessedMembers]
// parameter is named but NEVER sorted: ILLink's trim dataflow binds arguments to parameters by
// POSITION even when they are named, so moving the annotated argument out of its declared slot turns
// a clean build into IL2072 under IsAotCompatible.
internal sealed class NamedArgsRewriter : CSharpSyntaxRewriter {
    private readonly SemanticModel m_model;

    public NamedArgsRewriter(SemanticModel model) {
        m_model = model;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
        var visited = ((InvocationExpressionSyntax)base.VisitInvocationExpression(node: node)!);

        return ((Rebuild(originalCall: node, visitedList: visited.ArgumentList) is { } rebuilt) ? visited.WithArgumentList(argumentList: rebuilt) : visited);
    }
    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node) {
        var visited = ((ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node: node)!);

        return (((visited.ArgumentList is { } list) && (Rebuild(originalCall: node, visitedList: list) is { } rebuilt))
            ? visited.WithArgumentList(argumentList: rebuilt)
            : visited);
    }
    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node) {
        var visited = ((ImplicitObjectCreationExpressionSyntax)base.VisitImplicitObjectCreationExpression(node: node)!);

        return ((Rebuild(originalCall: node, visitedList: visited.ArgumentList) is { } rebuilt) ? visited.WithArgumentList(argumentList: rebuilt) : visited);
    }

    // The ORIGINAL node carries the symbol (the rewritten copy is detached from the model); the VISITED
    // list supplies the already-child-rewritten argument expressions. Returns the reordered+named list,
    // or null to leave the call alone.
    private ArgumentListSyntax? Rebuild(SyntaxNode originalCall, ArgumentListSyntax? visitedList) {
        if ((visitedList is null) || (visitedList.Arguments.Count == 0)) {
            return null;
        }

        if ((m_model.GetSymbolInfo(node: originalCall).Symbol is not IMethodSymbol method)
            || (method.MethodKind is MethodKind.FunctionPointerSignature or MethodKind.DelegateInvoke)) {
            return null;
        }

        var arguments = visitedList.Arguments;
        var originalArguments = OriginalArguments(call: originalCall);
        var parameters = method.Parameters;
        var namedCount = arguments.Count(predicate: static argument => (argument.NameColon is not null));

        // A partly named call is declined: naming its positional remainder needs the argument-to-
        // parameter mapping the mix obscures. A FULLY named call is only sorted, never renamed.
        if ((arguments.Count != parameters.Length)
            || parameters.Any(predicate: static parameter => parameter.IsParams)
            || ((namedCount != 0) && (namedCount != arguments.Count))) {
            return null;
        }

        // Trivia is reassigned by SLOT while the arguments move — separators included — so a comment
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

        if (sortable
            && !writtenNames.SequenceEqual(second: writtenNames.OrderBy(keySelector: static name => name, comparer: StringComparer.Ordinal))
            && originalArguments.Any(predicate: argument => ExpressionSafety.HasSideEffect(expression: argument.Expression, model: m_model))) {
            return null;
        }

        // Content (name + expression) is built per ORIGINAL position, then reordered; the per-slot trivia
        // is reassigned afterwards so the call's existing single-line or one-argument-per-line layout
        // survives the reorder unchanged. An out/ref/in keyword is carried with its argument (named args
        // allow it: `value: out x`).
        var entries = new (string Name, ArgumentSyntax Argument)[arguments.Count];

        for (var index = 0; (index < arguments.Count); index++) {
            var argument = arguments[index];

            // An already-named argument is carried as written — its name colon and expression are
            // already in house shape; only its slot (and that slot's trivia) may move.
            if (argument.NameColon is not null) {
                entries[index] = (writtenNames[index], argument);

                continue;
            }

            var nameColon = SyntaxFactory
                .NameColon(name: SyntaxFactory.IdentifierName(name: parameters[index].Name))
                .WithColonToken(colonToken: SyntaxFactory.Token(kind: SyntaxKind.ColonToken).WithTrailingTrivia(trivia: SyntaxFactory.Space));
            var refKind = (argument.RefKindKeyword.IsKind(kind: SyntaxKind.None)
                ? default
                : argument.RefKindKeyword.WithLeadingTrivia().WithTrailingTrivia(SyntaxFactory.Space));
            var bareExpression = argument.Expression.WithoutLeadingTrivia().WithoutTrailingTrivia();

            entries[index] = (parameters[index].Name, SyntaxFactory.Argument(expression: bareExpression, nameColon: nameColon, refKindKeyword: refKind));
        }

        var ordered = (sortable
            ? entries
                .OrderBy(keySelector: static entry => entry.Name, comparer: StringComparer.Ordinal)
                .Select(selector: static entry => entry.Argument)
                .ToArray()
            : Array.ConvertAll(array: entries, converter: static entry => entry.Argument));

        return visitedList.WithArguments(arguments: RewriteShaping.ReorderInPlace(ordered: ordered, original: arguments));
    }
    // SemanticModel only accepts nodes from its own syntax tree. Child visits may have rebuilt the
    // argument list already, so evaluation-safety is always inspected on the original bound call.
    private static SeparatedSyntaxList<ArgumentSyntax> OriginalArguments(SyntaxNode call) => call switch {
        InvocationExpressionSyntax invocation => invocation.ArgumentList.Arguments,
        ObjectCreationExpressionSyntax creation => creation.ArgumentList!.Arguments,
        ImplicitObjectCreationExpressionSyntax creation => creation.ArgumentList.Arguments,
        _ => default,
    };
    // True when the parameter carries System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute
    // — the trim/AOT dataflow annotation that pins its argument to the declared position.
    private static bool HasTrimAnnotation(IParameterSymbol parameter) => parameter.GetAttributes().Any(predicate: static attribute =>
        ((attribute.AttributeClass is { Name: "DynamicallyAccessedMembersAttribute" } attributeClass)
        && (attributeClass.ContainingNamespace.ToDisplayString() == "System.Diagnostics.CodeAnalysis")));
}
