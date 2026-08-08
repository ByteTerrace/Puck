using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The named-argument normalizer (the `named-args` pass). SEMANTIC: it resolves each call's method symbol
// to read parameter names, so it runs against a Compilation (NamedArgsPhase) rather than the syntactic
// pipeline. Every real method/ctor call gets its arguments named (`name: value`) and sorted
// alphabetically by parameter name — the house convention. Left positional (skipped) when there is no
// resolvable method symbol (function-pointer / delegate invokes have none), an out/ref/in or
// already-named argument, a `params` parameter, an omitted optional argument, a comment or #directive on
// any argument or separator, or when the reorder would move a side-effecting argument (see
// ExpressionSafety) — the cases where naming-and-reordering is unsafe or ambiguous.
internal sealed class NamedArgsRewriter : CSharpSyntaxRewriter {
    private readonly SemanticModel m_model;

    public NamedArgsRewriter(SemanticModel model) {
        m_model = model;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node: node)!;

        return ((Rebuild(originalCall: node, visitedList: visited.ArgumentList) is { } rebuilt) ? visited.WithArgumentList(argumentList: rebuilt) : visited);
    }
    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node) {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node: node)!;

        return (((visited.ArgumentList is { } list) && (Rebuild(originalCall: node, visitedList: list) is { } rebuilt))
            ? visited.WithArgumentList(argumentList: rebuilt)
            : visited);
    }
    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node) {
        var visited = (ImplicitObjectCreationExpressionSyntax)base.VisitImplicitObjectCreationExpression(node: node)!;

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
        var parameters = method.Parameters;

        if ((arguments.Count != parameters.Length)
            || parameters.Any(predicate: static parameter => parameter.IsParams)
            || arguments.Any(predicate: static argument => (argument.NameColon is not null))) {
            return null;
        }

        // Trivia is reassigned by SLOT while the arguments move — separators included — so a comment
        // written above one argument, or after one argument's comma, would end up documenting whichever
        // argument lands in that slot. Leave the call positional.
        if (RewriteShaping.IsAnnotated(list: arguments)) {
            return null;
        }

        // Naming preserves written positions, but the alphabetical SORT moves them. When that move is
        // real AND any argument is side-effecting, leave the call positional — C# evaluates arguments
        // left-to-right, so reordering would change evaluation order.
        var parameterNames = parameters.Select(selector: static parameter => parameter.Name);

        if (!parameterNames.SequenceEqual(second: parameterNames.OrderBy(keySelector: static name => name, comparer: StringComparer.Ordinal))
            && arguments.Any(predicate: static argument => ExpressionSafety.HasSideEffect(expression: argument.Expression))) {
            return null;
        }

        // Content (name + expression) is built per ORIGINAL position, then reordered; the per-slot trivia
        // is reassigned afterwards so the call's existing single-line or one-argument-per-line layout
        // survives the reorder unchanged. An out/ref/in keyword is carried with its argument (named args
        // allow it: `value: out x`).
        var entries = new (string Name, ArgumentSyntax Argument)[arguments.Count];

        for (var index = 0; (index < arguments.Count); index++) {
            var argument = arguments[index];
            var nameColon = SyntaxFactory
                .NameColon(name: SyntaxFactory.IdentifierName(name: parameters[index].Name))
                .WithColonToken(colonToken: SyntaxFactory.Token(kind: SyntaxKind.ColonToken).WithTrailingTrivia(trivia: SyntaxFactory.Space));
            var refKind = (argument.RefKindKeyword.IsKind(kind: SyntaxKind.None)
                ? default
                : argument.RefKindKeyword.WithLeadingTrivia().WithTrailingTrivia(SyntaxFactory.Space));
            var bareExpression = argument.Expression.WithoutLeadingTrivia().WithoutTrailingTrivia();

            entries[index] = (parameters[index].Name, SyntaxFactory.Argument(nameColon: nameColon, refKindKeyword: refKind, expression: bareExpression));
        }

        var ordered = entries
            .OrderBy(keySelector: static entry => entry.Name, comparer: StringComparer.Ordinal)
            .Select(selector: static entry => entry.Argument)
            .ToArray();

        return visitedList.WithArguments(arguments: RewriteShaping.ReorderInPlace(original: arguments, ordered: ordered));
    }
}
