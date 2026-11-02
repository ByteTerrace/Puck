using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Syntax = Puck.State.ExpressionSpelling;

namespace Puck.World.Transpiler.Validation;

public static partial class PuckLinter {
    // Return a conditional-arm division hazard while collecting names in the same walk.
    private static bool CollectOperandReferences(Syntax.SyntaxNode node, DocumentValueForm form, HashSet<string> references, string? binder = null, bool conditionalArm = false) {
        var hazard = false;
        switch (node) {
            case Syntax.SourceName { Quoted: false } name when ((form != DocumentValueForm.Key) && (name.Name != binder)):
                references.Add(item: name.Name);
                break;
            case Syntax.SourceGroup group: hazard |= CollectOperandReferences(group.Value, DocumentValueForm.Expression, references, binder, conditionalArm); break;
            case Syntax.SourceAccess access:
                if (QualifiedName.From(syntax: access) is { } qualified) { references.Add(item: qualified.ToString()); }
                hazard |= CollectOperandReferences(access.Target, DocumentValueForm.Name, references, binder, conditionalArm);
                break;
            case Syntax.SourceIndex index:
                hazard |= CollectOperandReferences(index.Target, DocumentValueForm.Name, references, binder, conditionalArm);
                hazard |= CollectOperandReferences(index.Index, DocumentValueForm.Expression, references, binder, conditionalArm);
                break;
            case Syntax.Unary unary: hazard |= CollectOperandReferences(unary.Operand, DocumentValueForm.Expression, references, binder, conditionalArm); break;
            case Syntax.Binary binary:
                var divisor = binary.Right;
                while (divisor is Syntax.SourceGroup grouped) { divisor = grouped.Value; }
                hazard |= conditionalArm && (binary.Operator is "/" or "%") && divisor is not Syntax.Literal { Value: not 0 };
                hazard |= CollectOperandReferences(binary.Left, DocumentValueForm.Expression, references, binder, conditionalArm);
                hazard |= CollectOperandReferences(binary.Right, DocumentValueForm.Expression, references, binder, conditionalArm);
                break;
            case Syntax.Ternary ternary:
                hazard |= CollectOperandReferences(ternary.Condition, DocumentValueForm.Expression, references, binder, conditionalArm);
                hazard |= CollectOperandReferences(ternary.WhenTrue, DocumentValueForm.Expression, references, binder, conditionalArm: true);
                hazard |= CollectOperandReferences(ternary.WhenFalse, DocumentValueForm.Expression, references, binder, conditionalArm: true);
                break;
            case Syntax.SourceCall call:
                foreach (var argument in call.Arguments) {
                    hazard |= CollectOperandReferences(argument.Value, DocumentValueForm.Expression, references, binder, conditionalArm);
                }
                break;
            case Syntax.SourceLambda lambda: hazard |= CollectOperandReferences(lambda.Body, DocumentValueForm.Expression, references, lambda.Binder, conditionalArm); break;
        }
        return hazard;
    }
}
