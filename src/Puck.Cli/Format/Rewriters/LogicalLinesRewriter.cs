using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The logical-line normalizer (the `logical-lines` pass): a multi-operand `&&`/`||` chain that delimits
// an `if`/`while` condition or a parenthesized `return` value is laid out one operand per line, the
// operator TRAILING each line, with the enclosing `(` and `)` hanging on their own lines —
//   if (
//       (0 == deviceHandle) ||
//       (0 == pipelineHandle)
//   ) {
//   return (
//       (a is not null) &&
//       (b is not null)
//   );
// For if/while the keyword's own parentheses are the delimiter (the condition is left bare by
// paren-clarity); for return the value is an explicit ParenthesizedExpression (paren-clarity wraps a
// logical return). Operand inner trivia is preserved and only the outer layout trivia is reset, so a
// second run reproduces the same shape — the pass is idempotent. A chain carrying a comment or
// #directive in one of those reset slots is left exactly as authored. A same-operator chain
// (`a || b || c`) flattens to a single group; mixed precedence (`a && b || c`) keeps the tighter `&&`
// inline on its operand line.
internal sealed class LogicalLinesRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node) =>
        LayoutCondition(original: node, visited: ((IfStatementSyntax)base.VisitIfStatement(node: node)!));
    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node) =>
        LayoutCondition(original: node, visited: ((WhileStatementSyntax)base.VisitWhileStatement(node: node)!));
    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node) {
        var visited = ((ReturnStatementSyntax)base.VisitReturnStatement(node: node)!);

        if ((visited.Expression is not ParenthesizedExpressionSyntax paren)
            || (paren.Expression is not BinaryExpressionSyntax binary)
            || !IsLogical(binary: binary)
            || IsAnnotated(openParen: paren.OpenParenToken, binary: binary, closeParen: paren.CloseParenToken)) {
            return visited;
        }

        var indent = LineIndentAt(node: node, position: node.ReturnKeyword.SpanStart);
        var laidOut = paren
            .WithOpenParenToken(openParenToken: paren.OpenParenToken.WithTrailingTrivia())
            .WithExpression(expression: Layout(binary: binary, innerIndent: (indent + "    ")))
            .WithCloseParenToken(closeParenToken: paren.CloseParenToken.WithLeadingTrivia(RewriteShaping.EndOfLine, SyntaxFactory.Whitespace(text: indent)));

        return visited.WithExpression(expression: laidOut);
    }

    private static bool IsLogical(BinaryExpressionSyntax binary) =>
        (binary.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression);
    private static SyntaxNode LayoutCondition(SyntaxNode original, SyntaxNode visited) {
        var (condition, openParen, closeParen, keyword) = visited switch {
            IfStatementSyntax ifStatement => (ifStatement.Condition, ifStatement.OpenParenToken, ifStatement.CloseParenToken, ((IfStatementSyntax)original).IfKeyword),
            WhileStatementSyntax whileStatement => (whileStatement.Condition, whileStatement.OpenParenToken, whileStatement.CloseParenToken, ((WhileStatementSyntax)original).WhileKeyword),
            _ => throw new ArgumentException(message: "A logical condition must belong to an if or while statement.", paramName: nameof(visited)),
        };

        if ((condition is not BinaryExpressionSyntax binary)
            || !IsLogical(binary: binary)
            || IsAnnotated(binary: binary, closeParen: closeParen, openParen: openParen)) {
            return visited;
        }

        var indent = LineIndentAt(node: original, position: keyword.SpanStart);
        var laidOut = Layout(binary: binary, innerIndent: (indent + "    "));
        var opened = openParen.WithTrailingTrivia();
        var closed = closeParen.WithLeadingTrivia(RewriteShaping.EndOfLine, SyntaxFactory.Whitespace(text: indent));

        return visited switch {
            IfStatementSyntax ifStatement => ifStatement.WithOpenParenToken(openParenToken: opened).WithCondition(condition: laidOut).WithCloseParenToken(closeParenToken: closed),
            WhileStatementSyntax whileStatement => whileStatement.WithOpenParenToken(openParenToken: opened).WithCondition(condition: laidOut).WithCloseParenToken(closeParenToken: closed),
            _ => visited,
        };
    }
    // True when a trivia slot the layout resets carries prose or a directive: the delimiting parentheses'
    // inner faces, or the outer trivia of any operand or operator in the chain. The rebuild reissues all
    // of them, so an annotated chain would lose the annotation outright — and the write guard only counts
    // parse errors, so the loss would be written. Leave such a chain exactly as authored.
    private static bool IsAnnotated(SyntaxToken openParen, BinaryExpressionSyntax binary, SyntaxToken closeParen) {
        if (RewriteShaping.HasCommentOrDirective(trivia: openParen.TrailingTrivia)
            || RewriteShaping.HasCommentOrDirective(trivia: closeParen.LeadingTrivia)) {
            return true;
        }

        var operands = new List<ExpressionSyntax>();
        var operators = new List<SyntaxToken>();

        Flatten(binary: binary, kind: binary.Kind(), operands: operands, operators: operators);

        return (operands.Any(predicate: static operand => RewriteShaping.IsAnnotated(node: operand))
            || operators.Any(predicate: static operatorToken => RewriteShaping.IsAnnotated(token: operatorToken)));
    }
    // Rebuilds a same-operator logical chain with each operand on its own indented line and the operator
    // hugging the end of the previous operand's line.
    private static ExpressionSyntax Layout(BinaryExpressionSyntax binary, string innerIndent) {
        var kind = binary.Kind();
        var operands = new List<ExpressionSyntax>();
        var operators = new List<SyntaxToken>();

        Flatten(binary: binary, kind: kind, operands: operands, operators: operators);

        var operandLead = new[] { RewriteShaping.EndOfLine, SyntaxFactory.Whitespace(text: innerIndent) };
        var result = operands[0].WithLeadingTrivia(trivia: operandLead).WithTrailingTrivia();

        for (var index = 0; (index < operators.Count); index++) {
            var operatorToken = operators[index].WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia();
            var right = operands[(index + 1)].WithLeadingTrivia(trivia: operandLead).WithTrailingTrivia();

            result = SyntaxFactory.BinaryExpression(kind: kind, left: result, operatorToken: operatorToken, right: right);
        }

        return result;
    }
    // Collects the operands and operators of a left-associative same-operator chain in source order (the
    // right operand is never the same operator unless re-parenthesized, in which case it is a
    // ParenthesizedExpression and stays a single operand).
    private static void Flatten(BinaryExpressionSyntax binary, SyntaxKind kind, List<ExpressionSyntax> operands, List<SyntaxToken> operators) {
        if ((binary.Left is BinaryExpressionSyntax leftBinary) && (leftBinary.Kind() == kind)) {
            Flatten(binary: leftBinary, kind: kind, operands: operands, operators: operators);
        } else {
            operands.Add(item: binary.Left);
        }

        operators.Add(item: binary.OperatorToken);
        operands.Add(item: binary.Right);
    }
    private static string LineIndentAt(SyntaxNode node, int position) {
        var line = node.SyntaxTree!.GetText().Lines.GetLineFromPosition(position: position).ToString();

        return line[..(line.Length - line.TrimStart().Length)];
    }
}
