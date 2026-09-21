using Puck.Transpiler.Ast;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Lowering;
using Syntax = Puck.State.ExpressionSpelling;

namespace Puck.Transpiler.Parsing;

public static partial class PuckParser {
    // A block member is classified by its vocabulary at lowering time. Project the compile-time syntax into
    // operand syntax structurally; printing supplies diagnostics and formatting only, never binding semantics.
    /// <summary>Converts a compile-time expression tree into unresolved operand syntax without parsing text.</summary>
    /// <param name="expression">The member value already parsed by the compile-time grammar.</param>
    /// <param name="form">The vocabulary's role for the member.</param>
    /// <returns>The operand tree, or an operand carrying a syntax refusal for an unsupported shape.</returns>
    public static OperandExpressionNode CreateOperand(ExpressionNode expression, DocumentValueForm form) {
        ArgumentNullException.ThrowIfNull(expression);
        var atoms = new List<InterpolatedStringNode>();
        var error = string.Empty;
        var syntax = Convert(node: expression);
        var text = PuckPrinter.PrintExpression(expression);
        var spans = ScanAtomSpans(text: text);
        var operands = new OperandAtom[atoms.Count];
        for (var index = 0; (index < atoms.Count); index++) {
            operands[index] = new OperandAtom(spans[index].Start, spans[index].Length, atoms[index]);
        }
        return new OperandExpressionNode(text, form, operands, expression.Offset, expression.Length, expression.Line, expression.Column) {
            Syntax = ((error.Length == 0) ? syntax : null),
            SyntaxError = error,
        };

        Syntax.SyntaxNode Convert(ExpressionNode node) {
            var converted = ConvertCore(node: node);
            return (node.Parenthesized ? new Syntax.SourceGroup(converted) : converted);
        }
        Syntax.SyntaxNode ConvertCore(ExpressionNode node) {
            switch (node) {
                case LiteralExpressionNode { Value: null }:
                    return new Syntax.SourceName("null");
                case LiteralExpressionNode { Unit: null, Value: string value }:
                    return new Syntax.SourceString(value);
                case LiteralExpressionNode { Unit: null, Value: bool value }:
                    return new Syntax.Literal((value ? 1 : 0));
                case LiteralExpressionNode { Unit: null, Value: long or ulong or int or decimal or double } literal:
                    try {
                        return new Syntax.Literal(DecimalValues.FromLiteral(literal: literal));
                    } catch (Exception exception) when ((exception is OverflowException or FormatException)) {
                        error = "The number lies outside the operand decimal range";
                        return new Syntax.Literal(0);
                    }
                case IdentifierExpressionNode name:
                    return Name(text: name.Name);
                case InterpolatedStringNode atom:
                    atoms.Add(item: atom);
                    return new Syntax.SourceAtom((atoms.Count - 1));
                case MemberAccessExpressionNode access:
                    return new Syntax.SourceAccess(Convert(node: access.Target), access.Member);
                case IndexExpressionNode index:
                    return new Syntax.SourceIndex(Convert(node: index.Target), Convert(node: index.Index));
                case UnaryExpressionNode unary:
                    return new Syntax.Unary(unary.Operator, Convert(node: unary.Operand));
                case BinaryExpressionNode binary:
                    return new Syntax.Binary(binary.Operator, Convert(node: binary.Left), Convert(node: binary.Right));
                case CallExpressionNode call:
                    return new Syntax.SourceCall(call.Name, [.. call.Arguments.Select(argument => new Syntax.SourceArgument(argument.Name, Convert(node: argument.Value)))]);
                case LambdaExpressionNode { Parameters.Count: 1 } lambda:
                    return new Syntax.SourceLambda(lambda.Parameters[0], Convert(node: lambda.Body));
                case OperandExpressionNode { Syntax: { } tree } operand:
                    var offset = atoms.Count;
                    atoms.AddRange(collection: operand.Atoms.Select(selector: static atom => atom.Value));
                    return MapAtoms(map: index => (index + offset), node: tree);
                default:
                    error = $"A {node.GetType().Name} is not an operand expression";
                    return new Syntax.Literal(0);
            }
        }
    }

    internal static OperandExpressionNode RebuildOperand(OperandExpressionNode original, Syntax.SyntaxNode syntax, IReadOnlyList<InterpolatedStringNode> atoms) {
        var ordered = new List<InterpolatedStringNode>();
        syntax = MapAtoms(syntax, index => {
            ordered.Add(item: atoms[index]);
            return (ordered.Count - 1);
        });
        var text = Syntax.PrintSyntax(syntax, index => PuckPrinter.PrintExpression(ordered[index]));
        var spans = ScanAtomSpans(text);
        var rebuilt = new OperandAtom[ordered.Count];
        for (var index = 0; (index < rebuilt.Length); index++) {
            rebuilt[index] = new OperandAtom(spans[index].Start, spans[index].Length, ordered[index]);
        }
        return original with { Syntax = syntax, Text = text, Atoms = rebuilt, SyntaxError = string.Empty };
    }

    private static Syntax.SyntaxNode Name(string text) {
        if ((text.Length >= 2) && (text[0] == '`') && (text[^1] == '`')) {
            return new Syntax.SourceName(text[1..^1], Quoted: true);
        }
        if (text.StartsWith(value: '$') || !text.Contains(value: '.')) { return new Syntax.SourceName(text); }
        var parts = text.Split('.');
        Syntax.SyntaxNode result = new Syntax.SourceName(parts[0]);
        for (var index = 1; (index < parts.Length); index++) {
            result = new Syntax.SourceAccess(result, parts[index]);
        }
        return result;
    }

    private static Syntax.SyntaxNode MapAtoms(Syntax.SyntaxNode node, Func<int, int> map) => node switch {
        Syntax.SourceAtom atom => atom with { Index = map(atom.Index) },
        Syntax.SourceGroup group => group with { Value = MapAtoms(group.Value, map) },
        Syntax.SourceIndex index => index with { Target = MapAtoms(index.Target, map), Index = MapAtoms(index.Index, map) },
        Syntax.SourceAccess access => access with { Target = MapAtoms(access.Target, map) },
        Syntax.Unary unary => unary with { Operand = MapAtoms(unary.Operand, map) },
        Syntax.Binary binary => binary with { Left = MapAtoms(binary.Left, map), Right = MapAtoms(binary.Right, map) },
        Syntax.Ternary ternary => ternary with { Condition = MapAtoms(ternary.Condition, map), WhenTrue = MapAtoms(ternary.WhenTrue, map), WhenFalse = MapAtoms(ternary.WhenFalse, map) },
        Syntax.SourceCall call => call with { Arguments = [.. call.Arguments.Select(argument => argument with { Value = MapAtoms(argument.Value, map) })] },
        Syntax.SourceLambda lambda => lambda with { Body = MapAtoms(lambda.Body, map) },
        _ => node,
    };
}
