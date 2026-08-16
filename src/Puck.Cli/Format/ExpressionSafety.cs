using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format;

// Reorder-safety for the passes that alphabetize EVALUATED expressions (named-args, init-order,
// member-order). C# evaluates call arguments and object-initializer values in written order, so moving
// an expression that has a side effect — or that reads state another element mutates — changes behavior.
// An element is treated as side-effecting if its subtree holds a call, object creation, await,
// assignment, indexer access, or ++/--. A semantic caller can additionally identify property reads:
// getters are arbitrary code and cannot be assumed to commute. Pure field/local reads commute, so a
// group with none of these is safe to reorder; otherwise the pass leaves it in source order.
internal static class ExpressionSafety {
    public static bool HasSideEffect(SyntaxNode expression, SemanticModel? model = null) => expression.DescendantNodesAndSelf().Any(predicate: node =>
        ((node is InvocationExpressionSyntax
            or ObjectCreationExpressionSyntax
            or ImplicitObjectCreationExpressionSyntax
            or AwaitExpressionSyntax
            or AssignmentExpressionSyntax
            or ElementAccessExpressionSyntax)
        || ((model is not null) && (model.GetSymbolInfo(node: node).Symbol is IPropertySymbol))
        || node.IsKind(kind: SyntaxKind.PreIncrementExpression)
        || node.IsKind(kind: SyntaxKind.PreDecrementExpression)
        || node.IsKind(kind: SyntaxKind.PostIncrementExpression)
        || node.IsKind(kind: SyntaxKind.PostDecrementExpression)));
}
