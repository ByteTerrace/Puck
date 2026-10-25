using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Syntax = Puck.State.ExpressionSpelling;

namespace Puck.Transpiler.Parsing;

// Gates use the ordinary expression parser. Their comparison tree is an emitter input, not another grammar.
public static partial class PuckParser {
    /// <summary><c>when Gate</c>.</summary>
    private static WhenStatementNode ParseWhenStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var predicate = ParseGate(
            context: context,
            diagnostics: diagnostics
        );
        var len = (context.Scanner.Cursor.Offset - startOffset);

        return new WhenStatementNode(
            Column: col,
            Length: len,
            Line: line,
            Offset: startOffset,
            Predicate: predicate
        );
    }
    private static PredicateNode ParseGate(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;
        var start = cursor.Offset;
        var (line, column) = GetLineAndColumn(context.Scanner.Buffer, start);
        var text = ScanOperandSpan(context, stopKeywords: null, stopAtComparator: false, out _);
        var operand = ValidatedOperand(diagnostics: diagnostics, span: new SourceSpan(start, cursor.Offset - start, line, column), text: text);
        if (operand.Syntax is not null) { return CreateGate(operand); }
        return new ComparisonPredicateNode(operand, "!=", CreateOperand(text: "0", form: DocumentValueForm.Expression));
    }
    /// <summary>Classifies an ordinary expression as a gate without a second expression grammar.</summary>
    /// <param name="operand">The parsed expression.</param>
    /// <returns>The expression's comparison and truth structure.</returns>
    public static PredicateNode CreateGate(OperandExpressionNode operand) {
        ArgumentNullException.ThrowIfNull(operand);
        return GateExpression(operand, operand.Syntax ?? throw new ArgumentException("A gate requires parsed expression syntax", nameof(operand)));
    }
    private static PredicateNode GateExpression(OperandExpressionNode original, Syntax.SyntaxNode node) {
        OperandExpressionNode Operand(Syntax.SyntaxNode value) => RebuildOperand(original, value,
            original.Atoms.Select(static atom => atom.Value).ToArray());
        while (node is Syntax.SourceGroup group) { node = group.Value; }
        return node switch {
            Syntax.Binary { Operator: "and" } binary => new AndPredicateNode(
                Operands: Flatten(binary, "and"), Offset: original.Offset, Length: original.Length, Line: original.Line, Column: original.Column),
            Syntax.Binary { Operator: "or" } binary => new OrPredicateNode(
                Operands: Flatten(binary, "or"), Offset: original.Offset, Length: original.Length, Line: original.Line, Column: original.Column),
            Syntax.Unary { Operator: "not" } unary => new NotPredicateNode(GateExpression(original, unary.Operand)),
            Syntax.Binary { Operator: "==" or "!=" or "<" or "<=" or ">" or ">=" } binary =>
                new ComparisonPredicateNode(Operand(binary.Left), binary.Operator, Operand(binary.Right),
                    Offset: original.Offset, Length: original.Length, Line: original.Line, Column: original.Column),
            Syntax.SourceCall { Name: "key" } => new CallPredicateNode((CallExpressionNode)ParseExpression(source: Operand(node).Text)),
            _ => new ComparisonPredicateNode(Operand(node), "!=", CreateOperand(text: "0", form: DocumentValueForm.Expression),
                Offset: original.Offset, Length: original.Length, Line: original.Line, Column: original.Column),
        };

        IReadOnlyList<PredicateNode> Flatten(Syntax.Binary binary, string operation) {
            var values = new List<PredicateNode>();
            Add(binary);
            return values;
            void Add(Syntax.SyntaxNode child) {
                if (child is Syntax.Binary nested && nested.Operator == operation) { Add(nested.Left); Add(nested.Right); }
                else { values.Add(GateExpression(original, child)); }
            }
        }
    }
}
