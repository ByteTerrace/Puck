using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format;

// Reorder-safety for the passes that alphabetize EVALUATED expressions (named-args, init-order,
// member-order). C# evaluates call arguments and object-initializer values in written order, so moving
// an expression that has a side effect — or that reads state another element mutates — changes behavior.
// An element is treated as side-effecting if its subtree holds a call, object creation, await,
// assignment, indexer access, or ++/--; those cover the observable-order hazards without a semantic
// model. Pure reads commute, so a group with none of these is safe to reorder; otherwise the pass leaves
// it in source order.
internal static class ExpressionSafety {
    public static bool HasSideEffect(SyntaxNode expression) => expression.DescendantNodesAndSelf().Any(predicate: static node =>
        ((node is InvocationExpressionSyntax
            or ObjectCreationExpressionSyntax
            or ImplicitObjectCreationExpressionSyntax
            or AwaitExpressionSyntax
            or AssignmentExpressionSyntax
            or ElementAccessExpressionSyntax)
        || node.IsKind(kind: SyntaxKind.PreIncrementExpression)
        || node.IsKind(kind: SyntaxKind.PreDecrementExpression)
        || node.IsKind(kind: SyntaxKind.PostIncrementExpression)
        || node.IsKind(kind: SyntaxKind.PostDecrementExpression)));
}
