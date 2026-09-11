using Parlot.Fluent;
using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Parsing;

// The gate grammar: WhenStatement -> Gate -> OrGate -> AndGate -> NotGate -> Atom -> Comparison. `and`/`or`/`not`/
// `as`/`when` are reserved only inside this production family; nowhere else does the parser treat them specially.
public static partial class PuckParser {
    /// <summary><c>when Gate</c>.</summary>
    private static WhenStatementNode ParseWhenStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var predicate = ParseGate(context, diagnostics);
        var len = context.Scanner.Cursor.Offset - startOffset;
        return new WhenStatementNode(Predicate: predicate, Offset: startOffset, Length: len, Line: line, Column: col);
    }

    private static PredicateNode ParseGate(ParseContext context, DiagnosticBag? diagnostics) => ParseOrGate(context, diagnostics);

    private static PredicateNode ParseOrGate(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context);
        var start = context.Scanner.Cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, start);

        var operands = new List<PredicateNode> { ParseAndGate(context, diagnostics) };
        while (true) {
            SkipWhiteSpace(context);
            if (!TryMatchKeyword(context, "or")) {
                break;
            }
            operands.Add(ParseAndGate(context, diagnostics));
        }

        if (operands.Count == 1) {
            return operands[0];
        }
        var len = context.Scanner.Cursor.Offset - start;
        return new OrPredicateNode(Operands: operands, Offset: start, Length: len, Line: line, Column: col);
    }

    private static PredicateNode ParseAndGate(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context);
        var start = context.Scanner.Cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, start);

        var operands = new List<PredicateNode> { ParseNotGate(context, diagnostics) };
        while (true) {
            SkipWhiteSpace(context);
            if (!TryMatchKeyword(context, "and")) {
                break;
            }
            operands.Add(ParseNotGate(context, diagnostics));
        }

        if (operands.Count == 1) {
            return operands[0];
        }
        var len = context.Scanner.Cursor.Offset - start;
        return new AndPredicateNode(Operands: operands, Offset: start, Length: len, Line: line, Column: col);
    }

    private static PredicateNode ParseNotGate(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context);
        var start = context.Scanner.Cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, start);

        if (TryMatchKeyword(context, "not")) {
            var operand = ParseNotGate(context, diagnostics);
            var len = context.Scanner.Cursor.Offset - start;
            return new NotPredicateNode(Operand: operand, Offset: start, Length: len, Line: line, Column: col);
        }

        return ParseAtom(context, diagnostics);
    }

    private static PredicateNode ParseAtom(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;

        if (cursor.Current == '(') {
            cursor.Advance();
            var inner = ParseGate(context, diagnostics);
            SkipWhiteSpace(context);
            if (!TryConsume(context, ')')) {
                throw CreateException(context, "Expected ')' closing parenthesized gate");
            }
            return inner;
        }

        return ParseComparisonPredicate(context, diagnostics);
    }

    private static PredicateNode ParseComparisonPredicate(ParseContext context, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;
        SkipWhiteSpace(context);
        var start = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, start);

        var leftStart = cursor.Offset;
        var (leftLine, leftCol) = (line, col);
        var leftText = ScanOperandSpan(context, GateReservedWords, stopAtComparator: true, out var leftSawComparator);
        var leftSpan = new SourceSpan(leftStart, cursor.Offset - leftStart, leftLine, leftCol);
        if (!leftSawComparator) {
            throw CreateException(context, $"Expected a comparison operator after '{leftText}' in a 'when' gate");
        }
        ValidateOperandText(leftText, leftSpan, diagnostics);

        var comparator = ConsumeComparatorToken(context);

        var rightStart = cursor.Offset;
        var (rightLine, rightCol) = GetLineAndColumn(context.Scanner.Buffer, rightStart);
        var rightText = ScanOperandSpan(context, GateReservedWords, stopAtComparator: true, out var rightSawComparator);
        var rightSpan = new SourceSpan(rightStart, cursor.Offset - rightStart, rightLine, rightCol);
        if (rightSawComparator) {
            diagnostics?.ReportError("PUCK004", "chained comparisons are not supported — join two comparisons with 'and'", new SourceSpan(cursor.Offset, 1, rightLine, rightCol));
        }

        string? kind = null;
        SkipWhiteSpace(context);
        if (TryMatchKeyword(context, "as")) {
            SkipWhiteSpace(context);
            var (kindLine, kindCol) = GetLineAndColumn(context.Scanner.Buffer, cursor.Offset);
            if (TryMatchKeyword(context, "Int")) {
                kind = "Int";
            } else if (TryMatchKeyword(context, "Fixed")) {
                kind = "Fixed";
            } else {
                diagnostics?.ReportError("PUCK005", "expected 'Int' or 'Fixed' after 'as'", new SourceSpan(cursor.Offset, 1, kindLine, kindCol));
            }
        } else if (TryStripTrailingColonKind(rightText, out var stripped, out var strippedKind)) {
            rightText = stripped;
            kind = strippedKind;
        }

        ValidateOperandText(rightText, rightSpan, diagnostics);

        var len = cursor.Offset - start;
        return new ComparisonPredicateNode(LeftText: leftText, Comparator: comparator, RightText: rightText, Kind: kind, Offset: start, Length: len, Line: line, Column: col);
    }
}
