using Puck.Transpiler.Lowering;
using Syntax = Puck.State.ExpressionSpelling;

namespace Puck.World.Transpiler.Validation;

public static partial class PuckLinter {
    private static void CollectOperandReferences(Syntax.SyntaxNode node, DocumentValueForm form, HashSet<string> references, string? binder = null) {
        switch (node) {
            case Syntax.SourceName { Quoted: false } name when ((form != DocumentValueForm.Key) && (name.Name != binder)):
                references.Add(name.Name);
                break;
            case Syntax.SourceGroup group: CollectOperandReferences(group.Value, DocumentValueForm.Expression, references, binder); break;
            case Syntax.SourceAccess access:
                if (Qualified(access) is { } qualified) { references.Add(item: qualified); }
                CollectOperandReferences(access.Target, DocumentValueForm.Name, references, binder);
                break;
            case Syntax.SourceIndex index:
                CollectOperandReferences(index.Target, DocumentValueForm.Name, references, binder);
                CollectOperandReferences(index.Index, DocumentValueForm.Key, references, binder);
                break;
            case Syntax.Unary unary: CollectOperandReferences(unary.Operand, DocumentValueForm.Expression, references, binder); break;
            case Syntax.Binary binary:
                CollectOperandReferences(binary.Left, DocumentValueForm.Expression, references, binder);
                CollectOperandReferences(binary.Right, DocumentValueForm.Expression, references, binder);
                break;
            case Syntax.Ternary ternary:
                CollectOperandReferences(ternary.Condition, DocumentValueForm.Expression, references, binder);
                CollectOperandReferences(ternary.WhenTrue, DocumentValueForm.Expression, references, binder);
                CollectOperandReferences(ternary.WhenFalse, DocumentValueForm.Expression, references, binder);
                break;
            case Syntax.SourceCall call:
                foreach (var argument in call.Arguments) {
                    CollectOperandReferences(argument.Value, DocumentValueForm.Expression, references, binder);
                }
                break;
            case Syntax.SourceLambda lambda: CollectOperandReferences(lambda.Body, DocumentValueForm.Expression, references, lambda.Binder); break;
        }
    }

    private static string? Qualified(Syntax.SyntaxNode node) => node switch {
        Syntax.SourceName { Quoted: false } name => name.Name,
        Syntax.SourceAccess access when (Qualified(access.Target) is { } prefix) => $"{prefix}.{access.Member}",
        _ => null,
    };
}
