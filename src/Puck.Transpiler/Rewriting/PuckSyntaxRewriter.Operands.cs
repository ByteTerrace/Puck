using Puck.Transpiler.Lowering;
using Operand = Puck.State.ExpressionSpelling;

namespace Puck.Transpiler.Rewriting;

public abstract partial class PuckSyntaxRewriter {
    /// <summary>Rewrites a parsed operand tree. Bracket contents evaluate; dot members name literal keys.</summary>
    /// <param name="node">The operand syntax.</param>
    /// <param name="form">The position occupied by the syntax.</param>
    /// <returns>The rewritten syntax. The default implementation descends into its children.</returns>
    protected virtual Operand.SyntaxNode RewriteOperandSyntax(Operand.SyntaxNode node, DocumentValueForm form) {
        var rewritten = node switch {
            Operand.SourceGroup group => group with { Value = RewriteOperandSyntax(group.Value, DocumentValueForm.Expression) },
            Operand.SourceIndex index => index with {
                Target = RewriteOperandSyntax(index.Target, DocumentValueForm.Name),
                Index = RewriteOperandSyntax(index.Index, DocumentValueForm.Expression),
            },
            Operand.SourceAccess access => access with { Target = RewriteOperandSyntax(access.Target, DocumentValueForm.Name) },
            Operand.Unary unary => unary with { Operand = RewriteOperandSyntax(unary.Operand, DocumentValueForm.Expression) },
            Operand.Binary binary => binary with {
                Left = RewriteOperandSyntax(binary.Left, DocumentValueForm.Expression),
                Right = RewriteOperandSyntax(binary.Right, DocumentValueForm.Expression),
            },
            Operand.Ternary ternary => ternary with {
                Condition = RewriteOperandSyntax(ternary.Condition, DocumentValueForm.Expression),
                WhenTrue = RewriteOperandSyntax(ternary.WhenTrue, DocumentValueForm.Expression),
                WhenFalse = RewriteOperandSyntax(ternary.WhenFalse, DocumentValueForm.Expression),
            },
            Operand.SourceLambda lambda => lambda with { Body = RewriteOperandSyntax(lambda.Body, DocumentValueForm.Expression) },
            Operand.SourceCall call => call with { Arguments = RewriteArguments(arguments: call.Arguments) },
            _ => node,
        };

        return (Equals(objA: node, objB: rewritten) ? node : rewritten);
    }

    private IReadOnlyList<Operand.SourceArgument> RewriteArguments(IReadOnlyList<Operand.SourceArgument> arguments) {
        Operand.SourceArgument[]? rewritten = null;

        for (var index = 0; (index < arguments.Count); index++) {
            var argument = arguments[index];
            var value = RewriteOperandSyntax(argument.Value, DocumentValueForm.Expression);

            if (!Equals(objA: value, objB: argument.Value)) {
                rewritten ??= arguments.ToArray();
                rewritten[index] = argument with { Value = value };
            }
        }
        return (rewritten ?? arguments);
    }
}
