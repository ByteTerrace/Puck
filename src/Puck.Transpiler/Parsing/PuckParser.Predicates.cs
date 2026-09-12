using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Parsing;

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

    private static PredicateNode ParseNotGate(ParseContext context, DiagnosticBag? diagnostics, int depth = 0) {
        if (depth >= 64) { throw CreateException(context, "Negated gates nest at most 64 levels"); }
        SkipWhiteSpace(context);
        var start = context.Scanner.Cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, start);

        if (TryMatchKeyword(context, "not")) {
            var operand = ParseNotGate(context, diagnostics, depth + 1);
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

        if (TryParseCallPredicate(context, out var call) && (call is not null)) {
            return call;
        }

        return ParseComparisonPredicate(context, diagnostics);
    }

    // A bare `name(...)` gate, taken only when nothing comparison-shaped follows it: `min(a, b) == 3` is a
    // comparison whose left operand happens to be a call, and reading the call as the whole gate would swallow the
    // comparator. The scan is speculative for that reason and rewinds when it guesses wrong -- including when the
    // arguments fail to parse as expressions at all, since a comparison operand is opaque text handed to
    // ExpressionSpelling rather than an expression this parser reads.
    private static bool TryParseCallPredicate(ParseContext context, out CallPredicateNode? predicate) {
        predicate = null;

        var cursor = context.Scanner.Cursor;
        var savedPosition = cursor.Position;
        var start = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, start);

        if (!TryReadIdentifier(context, out var name)) {
            cursor.ResetPosition(savedPosition);

            return false;
        }

        SkipWhiteSpace(context);

        if (cursor.Current != '(') {
            cursor.ResetPosition(savedPosition);

            return false;
        }

        CallExpressionNode call;

        try {
            call = ParseCallExpression(context, name, start, line, col);
        }
        catch (PuckParseException) {
            cursor.ResetPosition(savedPosition);

            return false;
        }

        SkipWhiteSpace(context);

        if (LongestMatchingPunctuation(context.Scanner.Buffer, cursor.Offset, ComparatorPunctuation) is not null) {
            cursor.ResetPosition(savedPosition);

            return false;
        }

        predicate = new CallPredicateNode(Call: call, Offset: start, Length: (cursor.Offset - start), Line: line, Column: col);

        return true;
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
            diagnostics?.ReportError(PuckDiagnosticCodes.ChainedComparison, "chained comparisons are not supported - join two comparisons with 'and'", new SourceSpan(cursor.Offset, 1, rightLine, rightCol));
            SkipToEndOfStatement(context);
        }

        // `kind` becomes a field of THIS comparison's own node, never of the `and`/`or` chain it may sit inside —
        // AndGate/OrGate only ever combine already-built PredicateNodes, so a suffix parsed here can never leak onto
        // a sibling operand no matter which one in the chain carries it or where the chain ends. `WorldDecompiler`
        // still wraps a printed `Int` annotation in parens (`(left cmp right : Int)`) so a READER can see that same
        // binding, since the suffix would otherwise sit at the tail of the whole printed chain.
        string? kind = null;
        SkipWhiteSpace(context);
        if (TryMatchKeyword(context, "as")) {
            SkipWhiteSpace(context);
            var (kindLine, kindCol) = GetLineAndColumn(context.Scanner.Buffer, cursor.Offset);
            kind = TryMatchKindKeyword(context);
            if (kind is null) {
                diagnostics?.ReportError(PuckDiagnosticCodes.UnknownKindAnnotation, $"expected {DescribeAdmittedKinds()} after 'as'", new SourceSpan(cursor.Offset, 1, kindLine, kindCol));
                SkipToEndOfStatement(context);
            }
        } else if (TryStripTrailingColonKind(rightText, out var stripped, out var strippedKind)) {
            rightText = stripped;
            kind = strippedKind;
        } else if (TrailingColonWord(rightText) is { } unknownKind) {
            // The `: Kind` spelling is what the decompiler writes and what the editor snippets teach, so a wrong word
            // after the colon gets the same named diagnostic the `as Kind` spelling does rather than falling through
            // to a parse failure about a stray colon.
            diagnostics?.ReportError(PuckDiagnosticCodes.UnknownKindAnnotation, $"expected {DescribeAdmittedKinds()} after ':', found '{unknownKind}'", rightSpan);
            rightText = rightText[..rightText.LastIndexOf(':')].TrimEnd();
        }

        ValidateOperandText(rightText, rightSpan, diagnostics);

        var len = cursor.Offset - start;
        return new ComparisonPredicateNode(LeftText: leftText, Comparator: comparator, RightText: rightText, Kind: kind, Offset: start, Length: len, Line: line, Column: col);
    }
}
