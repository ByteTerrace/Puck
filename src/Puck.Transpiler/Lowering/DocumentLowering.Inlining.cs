using Puck.Transpiler.Ast;
using Puck.Transpiler.Rewriting;

namespace Puck.Transpiler.Lowering;

public static partial class DocumentLowering {
    /// <summary>Returns an expression with every call of a value function (<see cref="FunctionNamed"/>) written out: the
    /// function's body with each parameter replaced by its argument's expression, or its default's. A vocabulary that
    /// spells an expression for a machine to evaluate reads a function as the expression an author would paste in its
    /// place, and still evaluates whatever part of it is known at compile time. Names the body reads that are not its
    /// parameters resolve where the call stands. A call the function makes of itself, directly or through another, is
    /// refused (<see cref="ExpandFunction"/>) and stands for zero.</summary>
    /// <param name="expression">The expression to inline; it is not modified.</param>
    /// <param name="scope">The lowering scope, whose constants declare the functions and whose diagnostics take a
    /// refusal.</param>
    /// <returns>The inlined expression; <paramref name="expression"/> itself when it calls no function.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> or <paramref name="scope"/> is
    /// <see langword="null"/>.</exception>
    public static ExpressionNode InlineFunctions(ExpressionNode expression, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(argument: expression);
        ArgumentNullException.ThrowIfNull(argument: scope);

        return new FunctionInliner(scope: scope).Inline(expression: expression);
    }

    private sealed class FunctionInliner(DocumentScope scope) : PuckSyntaxRewriter {
        public ExpressionNode Inline(ExpressionNode expression) => RewriteExpression(expression: expression);

        protected override ExpressionNode RewriteExpression(ExpressionNode expression) {
            if ((expression is not CallExpressionNode call) || (FunctionNamed(name: call.Name, scope: scope) is not { } function)) {
                return base.RewriteExpression(expression: expression);
            }

            var refused = new LiteralExpressionNode(Value: 0m, Offset: call.Offset, Length: call.Length, Line: call.Line, Column: call.Column);

            if (BindFunctionArguments(argumentNames: [.. call.Arguments.Select(selector: static argument => argument.Name)], function: function, name: call.Name, scope: scope, span: call.Span) is not { } bound) {
                return refused;
            }

            return ExpandFunction(
                expand: () => {
                    var arguments = new Dictionary<string, ExpressionNode>(comparer: StringComparer.Ordinal);

                    for (var at = 0; (at < bound.Length); at++) {
                        arguments[function.Parameters[at].Name] = RewriteExpression(expression: ((bound[at] >= 0)
                            ? call.Arguments[bound[at]].Value
                            : function.Parameters[at].DefaultValue!));
                    }

                    return RewriteExpression(expression: new ParameterSubstitution(arguments: arguments).Apply(expression: function.Body));
                },
                name: call.Name,
                refused: (ExpressionNode)refused,
                scope: scope,
                span: call.Span
            );
        }
    }

    // Replaces each reading of a parameter with its argument. A name a node binds for a child (SyntaxScopes.Binds), a
    // lambda's or a let's, is that binding inside the child, so the child is rewritten without it.
    private sealed class ParameterSubstitution(IReadOnlyDictionary<string, ExpressionNode> arguments) : PuckSyntaxRewriter {
        public ExpressionNode Apply(ExpressionNode expression) => RewriteExpression(expression: expression);

        protected override PuckSyntaxRewriter Within(SyntaxNode parent, SyntaxNode child) {
            var shadowed = SyntaxScopes.Binds(child: child, node: parent).Select(selector: static binder => binder.Name).Where(predicate: arguments.ContainsKey).ToArray();

            return ((shadowed.Length == 0)
                ? this
                : new ParameterSubstitution(arguments: arguments.Where(predicate: pair => !shadowed.Contains(value: pair.Key)).ToDictionary(comparer: StringComparer.Ordinal)));
        }
        protected override ExpressionNode RewriteExpression(ExpressionNode expression) => (((expression is IdentifierExpressionNode name) && arguments.TryGetValue(key: name.Name, value: out var argument))
            ? argument
            : base.RewriteExpression(expression: expression));
    }
}
