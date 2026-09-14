using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Parsing;

// The gate grammar: WhenStatement -> Gate -> OrGate -> AndGate -> NotGate -> Atom -> Comparison. `and`/`or`/`not`/
// `as`/`when` are reserved only inside this production family; nowhere else does the parser treat them specially.
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
    private static PredicateNode ParseGate(ParseContext context, DiagnosticBag? diagnostics) => ParseOrGate(
        context: context,
        diagnostics: diagnostics
    );
    private static PredicateNode ParseOrGate(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context: context);
        var start = context.Scanner.Cursor.Offset;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: start
        );

        var operands = new List<PredicateNode> { ParseAndGate(
            context: context,
            diagnostics: diagnostics
        ) };

        while (true) {
            SkipWhiteSpace(context: context);
            if (!TryMatchKeyword(
                context: context,
                keyword: "or"
            )) {
                break;
            }
            operands.Add(item: ParseAndGate(
                context: context,
                diagnostics: diagnostics
            ));
        }

        if (operands.Count == 1) {
            return operands[0];
        }
        var len = (context.Scanner.Cursor.Offset - start);

        return new OrPredicateNode(
            Column: col,
            Length: len,
            Line: line,
            Offset: start,
            Operands: operands
        );
    }
    private static PredicateNode ParseAndGate(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context: context);
        var start = context.Scanner.Cursor.Offset;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: start
        );

        var operands = new List<PredicateNode> { ParseNotGate(
            context,
            diagnostics
        ) };

        while (true) {
            SkipWhiteSpace(context: context);
            if (!TryMatchKeyword(
                context: context,
                keyword: "and"
            )) {
                break;
            }
            operands.Add(item: ParseNotGate(
                context,
                diagnostics
            ));
        }

        if (operands.Count == 1) {
            return operands[0];
        }
        var len = (context.Scanner.Cursor.Offset - start);

        return new AndPredicateNode(
            Column: col,
            Length: len,
            Line: line,
            Offset: start,
            Operands: operands
        );
    }
    private static PredicateNode ParseNotGate(ParseContext context, DiagnosticBag? diagnostics, int depth = 0) {
        if (depth >= 64) { throw CreateException(
            context: context,
            message: "Negated gates nest at most 64 levels"
        ); }
        SkipWhiteSpace(context: context);
        var start = context.Scanner.Cursor.Offset;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: start
        );

        if (TryMatchKeyword(
            context: context,
            keyword: "not"
        )) {
            var operand = ParseNotGate(
                context: context,
                depth: (depth + 1),
                diagnostics: diagnostics
            );
            var len = (context.Scanner.Cursor.Offset - start);

            return new NotPredicateNode(
                Column: col,
                Length: len,
                Line: line,
                Offset: start,
                Operand: operand
            );
        }

        return ParseAtom(
            context: context,
            diagnostics: diagnostics
        );
    }
    private static PredicateNode ParseAtom(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;

        if (cursor.Current == '(') {
            cursor.Advance();
            var inner = ParseGate(
                context: context,
                diagnostics: diagnostics
            );

            SkipWhiteSpace(context: context);
            if (!TryConsume(
                c: ')',
                context: context
            )) {
                throw CreateException(
                    context: context,
                    message: "Expected ')' closing parenthesized gate"
                );
            }
            return inner;
        }

        if (
            TryParseCallPredicate(
            context: context,
            predicate: out var call
        ) &&
            (call is not null)
        ) {
            return call;
        }

        return ParseComparisonPredicate(
            context: context,
            diagnostics: diagnostics
        );
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

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: start
        );

        if (!TryReadIdentifier(
            context: context,
            identifier: out var name
        )) {
            cursor.ResetPosition(position: savedPosition);

            return false;
        }

        SkipWhiteSpace(context: context);

        if (cursor.Current != '(') {
            cursor.ResetPosition(position: savedPosition);

            return false;
        }

        CallExpressionNode call;

        try {
            call = ParseCallExpression(
                col: col,
                context: context,
                functionName: name,
                line: line,
                startOffset: start
            );
        } catch (PuckParseException) {
            cursor.ResetPosition(position: savedPosition);

            return false;
        }

        SkipWhiteSpace(context: context);

        if (LongestMatchingPunctuation(
            context.Scanner.Buffer,
            cursor.Offset,
            ComparatorPunctuation
        ) is not null) {
            cursor.ResetPosition(position: savedPosition);

            return false;
        }

        predicate = new CallPredicateNode(
            Call: call,
            Offset: start,
            Length: (cursor.Offset - start),
            Line: line,
            Column: col
        );

        return true;
    }
    private static PredicateNode ParseComparisonPredicate(ParseContext context, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        var start = cursor.Offset;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: start
        );

        var leftStart = cursor.Offset;

        var (leftLine, leftCol) = (line, col);
        var leftText = ScanOperandSpan(
            context,
            GateReservedWords,
            stopAtComparator: true,
            out var leftSawComparator
        );
        var leftSpan = new SourceSpan(
            leftStart,
            (cursor.Offset - leftStart),
            leftLine,
            leftCol
        );

        if (!leftSawComparator) {
            throw CreateException(
                context: context,
                message: $"Expected a comparison operator after '{leftText}' in a 'when' gate"
            );
        }
        ValidateOperandText(
            diagnostics: diagnostics,
            span: leftSpan,
            text: leftText
        );

        var comparator = ConsumeComparatorToken(context: context);

        var rightStart = cursor.Offset;

        var (rightLine, rightCol) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: rightStart
        );
        var rightText = ScanOperandSpan(
            context,
            GateReservedWords,
            stopAtComparator: true,
            out var rightSawComparator
        );
        var rightSpan = new SourceSpan(
            rightStart,
            (cursor.Offset - rightStart),
            rightLine,
            rightCol
        );

        if (rightSawComparator) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.ChainedComparison,
                message: "chained comparisons are not supported - join two comparisons with 'and'",
                span: new SourceSpan(
                    cursor.Offset,
                    1,
                    rightLine,
                    rightCol
                )
            );
            SkipToEndOfStatement(context: context);
        }

        // `kind` becomes a field of THIS comparison's own node, never of the `and`/`or` chain it may sit inside —
        // AndGate/OrGate only ever combine already-built PredicateNodes, so a suffix parsed here can never leak onto
        // a sibling operand no matter which one in the chain carries it or where the chain ends. `WorldDecompiler`
        // still wraps a printed `Int` annotation in parens (`(left cmp right : Int)`) so a READER can see that same
        // binding, since the suffix would otherwise sit at the tail of the whole printed chain.
        string? kind = null;

        SkipWhiteSpace(context: context);
        if (TryMatchKeyword(
            context: context,
            keyword: "as"
        )) {
            SkipWhiteSpace(context: context);
            var (kindLine, kindCol) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: cursor.Offset
            );
            kind = TryMatchKindKeyword(context: context);
            if (kind is null) {
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.UnknownKindAnnotation,
                    message: $"expected {DescribeAdmittedKinds()} after 'as'",
                    span: new SourceSpan(
                        cursor.Offset,
                        1,
                        kindLine,
                        kindCol
                    )
                );
                SkipToEndOfStatement(context: context);
            }
        } else if (TryStripTrailingColonKind(
            exprText: out var stripped,
            kind: out var strippedKind,
            text: rightText
        )) {
            rightText = stripped;
            kind = strippedKind;
        } else if (TrailingColonWord(text: rightText) is { } unknownKind) {
            // The `: Kind` spelling is what the decompiler writes and what the editor snippets teach, so a wrong word
            // after the colon gets the same named diagnostic the `as Kind` spelling does rather than falling through
            // to a parse failure about a stray colon.
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.UnknownKindAnnotation,
                message: $"expected {DescribeAdmittedKinds()} after ':', found '{unknownKind}'",
                span: rightSpan
            );
            rightText = rightText[..rightText.LastIndexOf(value: ':')].TrimEnd();
        }

        ValidateOperandText(
            diagnostics: diagnostics,
            span: rightSpan,
            text: rightText
        );

        var len = (cursor.Offset - start);

        return new ComparisonPredicateNode(
            Column: col,
            Comparator: comparator,
            Kind: kind,
            LeftText: leftText,
            Length: len,
            Line: line,
            Offset: start,
            RightText: rightText
        );
    }
}
