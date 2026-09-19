using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Parsing;

// `test "name" { given { } when { } expect { } }`. The three blocks are written in that order and each at most
// once; what each admits is exactly what a generated test world can carry today, so a spelling the engine cannot
// honour is refused here by name rather than lowered into a world that would ignore it.
public static partial class PuckParser {
    // The seat label a `when` step acts as. A step never acts as the console: the console is trusted at every gate,
    // so a step under it proves nothing about authority.
    private const string SeatPrefix = "seat";

    private static bool TryMatchTestKeyword(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (TryMatchKeyword(context: context, keyword: "test") && SkipSpacesOnLine(context: context)) {
            if (TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out _)) {
                cursor.ResetPosition(position: saved);

                return TryMatchKeyword(context: context, keyword: "test");
            }
        }

        cursor.ResetPosition(position: saved);

        return false;
    }
    private static TestGivenBlockNode ParseTestGivenBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: "Expected '{' starting a test's 'given' block");
        }

        var cells = new List<TestGivenNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            var cellStart = cursor.Offset;

            var (cellLine, cellColumn) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: cellStart
            );

            if (!TryReadRowRefSpanRaw(
                context: context,
                span: out var rowSpan,
                text: out var rowText
            )) {
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.TestStepInadmissible,
                    message: $"'{ReadRestOfLine(context: context)}' is not a 'given' line — a test's given block writes initial cells, one per line, as 'row = literal' or 'row[key] = literal'",
                    span: new SourceSpan(cellStart, (cursor.Offset - cellStart), cellLine, cellColumn)
                );
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);

                continue;
            }

            SkipWhiteSpace(context: context);
            if (!TryConsume(c: '=', context: context)) {
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.TestStepInadmissible,
                    message: $"'{rowText}' carries no '=' — a test's given block writes initial cells as 'row = literal', and no other effect spelling is admitted there",
                    span: new SourceSpan(cellStart, (cursor.Offset - cellStart), cellLine, cellColumn)
                );
                _ = ReadRestOfLine(context: context);
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);

                continue;
            }

            var value = ParseRhs(
                context,
                diagnostics,
                allowText: true,
                allowSeconds: false,
                ownerForDiagnostic: "a test's given block"
            );

            cells.Add(item: new TestGivenNode(
                Column: cellColumn,
                Length: (cursor.Offset - cellStart),
                Line: cellLine,
                Offset: cellStart,
                Target: ResolveRowRef(
                diagnostics: diagnostics,
                span: rowSpan,
                text: rowText
            ),
                Value: value
            ));
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: "Expected '}' closing a test's 'given' block");
        }

        return new TestGivenBlockNode(
            Cells: cells,
            Column: col,
            Length: (cursor.Offset - startOffset),
            Line: line,
            Offset: startOffset
        );
    }
    private static TestWhenBlockNode ParseTestWhenBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: "Expected '{' starting a test's 'when' block");
        }

        var steps = new List<TestStepNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            var stepStart = cursor.Offset;

            var (stepLine, stepColumn) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: stepStart
            );

            if (TryMatchKeyword(context: context, keyword: "ticks")) {
                SkipWhiteSpace(context: context);

                var literal = (char.IsDigit(c: cursor.Current)
                    ? ParseNumberWithOptionalUnit(context: context)
                    : null
                );

                if ((literal is { Value: long ticks, Unit: null }) && (ticks > 0L)) {
                    steps.Add(item: new TestTicksStepNode(
                        Column: stepColumn,
                        Length: (cursor.Offset - stepStart),
                        Line: stepLine,
                        Offset: stepStart,
                        Ticks: ticks
                    ));
                } else {
                    diagnostics?.ReportError(
                        code: PuckDiagnosticCodes.TestStepInadmissible,
                        message: "'ticks' carries a whole positive number of simulation ticks and no unit — a test's tick grid is counted in ticks, since a duration cannot say which tick a step lands on",
                        span: new SourceSpan(stepStart, (cursor.Offset - stepStart), stepLine, stepColumn)
                    );
                }
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);

                continue;
            }

            if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var actor)) {
                SkipWhiteSpace(context: context);
                if (!TryConsume(c: ':', context: context)) {
                    diagnostics?.ReportError(
                        code: PuckDiagnosticCodes.TestStepInadmissible,
                        message: $"'{actor}{ReadRestOfLine(context: context)}' is not a 'when' step — a test's when block carries 'ticks <n>' and 'seat<n>: <command line>', and nothing else",
                        span: new SourceSpan(stepStart, (cursor.Offset - stepStart), stepLine, stepColumn)
                    );
                    ConsumeSeparator(context: context);
                    SkipWhiteSpace(context: context);

                    continue;
                }

                var command = ReadRestOfLine(context: context);

                if (!TryParseSeatLabel(
                    label: actor,
                    seat: out var seat
                )) {
                    diagnostics?.ReportError(
                        code: PuckDiagnosticCodes.TestStepInadmissible,
                        message: $"'{actor}' is not a seat — a test acts as 'seat1'..'seat4' with what it needs authored in the document's own grants; the console is trusted at every gate, so a step under it would prove nothing about authority",
                        span: new SourceSpan(stepStart, (cursor.Offset - stepStart), stepLine, stepColumn)
                    );
                } else if (command.Length == 0) {
                    diagnostics?.ReportError(
                        code: PuckDiagnosticCodes.TestStepInadmissible,
                        message: $"'{actor}:' carries no command line",
                        span: new SourceSpan(stepStart, (cursor.Offset - stepStart), stepLine, stepColumn)
                    );
                } else {
                    steps.Add(item: new TestSeatStepNode(
                        Column: stepColumn,
                        Command: command,
                        Length: (cursor.Offset - stepStart),
                        Line: stepLine,
                        Offset: stepStart,
                        Seat: seat
                    ));
                }
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);

                continue;
            }

            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.TestStepInadmissible,
                message: $"'{ReadRestOfLine(context: context)}' is not a 'when' step — a test's when block carries 'ticks <n>' and 'seat<n>: <command line>', and nothing else",
                span: new SourceSpan(stepStart, (cursor.Offset - stepStart), stepLine, stepColumn)
            );
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: "Expected '}' closing a test's 'when' block");
        }

        return new TestWhenBlockNode(
            Column: col,
            Length: (cursor.Offset - startOffset),
            Line: line,
            Offset: startOffset,
            Steps: steps
        );
    }
    private static TestExpectBlockNode ParseTestExpectBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: "Expected '{' starting a test's 'expect' block");
        }

        var expectations = new List<TestExpectationNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            var expectStart = cursor.Offset;

            var (expectLine, expectColumn) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: expectStart
            );
            var predicate = ParseGate(
                context: context,
                diagnostics: diagnostics
            );

            expectations.Add(item: new TestExpectationNode(
                Column: expectColumn,
                Length: (cursor.Offset - expectStart),
                Line: expectLine,
                Offset: expectStart,
                Predicate: predicate
            ));
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);

            // A gate reader that consumed nothing would spin here forever on a line it cannot read; the gate itself
            // has already reported why.
            if (cursor.Offset == expectStart) {
                _ = ReadRestOfLine(context: context);
                SkipWhiteSpace(context: context);
            }
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: "Expected '}' closing a test's 'expect' block");
        }

        return new TestExpectBlockNode(
            Column: col,
            Expectations: expectations,
            Length: (cursor.Offset - startOffset),
            Line: line,
            Offset: startOffset
        );
    }
    private static TestDeclarationNode ParseTestDeclaration(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out var name)) {
            throw CreateException(context: context, message: "Expected a quoted test name after 'test'");
        }

        SkipWhiteSpace(context: context);
        if (TryMatchKeyword(context: context, keyword: "with")) {
            var withStart = cursor.Offset;

            _ = ReadRestOfLine(context: context);
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.TestShapeInadmissible,
                message: $"test '{name}' is written 'with' a module, and a test of a module arrives with modules — the module construct and its 'use' do not exist yet, so a test today states a world's own behaviour at that world's root",
                span: new SourceSpan(withStart, (cursor.Offset - withStart), line, col)
            );
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: $"Expected '{{' starting test '{name}'");
        }

        TestGivenBlockNode? given = null;
        TestWhenBlockNode? when = null;
        TestExpectBlockNode? expect = null;

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            var blockStart = cursor.Offset;

            var (blockLine, blockColumn) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: blockStart
            );

            if (TryMatchKeyword(context: context, keyword: "given")) {
                var parsed = ParseTestGivenBlock(
                    col: blockColumn,
                    context: context,
                    diagnostics: diagnostics,
                    line: blockLine,
                    startOffset: blockStart
                );

                RefuseOutOfOrderTestBlock(
                    already: ((given is not null) || (when is not null) || (expect is not null)),
                    diagnostics: diagnostics,
                    keyword: "given",
                    name: name,
                    span: parsed.Span
                );
                given ??= parsed;
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);

                continue;
            }

            if (TryMatchKeyword(context: context, keyword: "when")) {
                var parsed = ParseTestWhenBlock(
                    col: blockColumn,
                    context: context,
                    diagnostics: diagnostics,
                    line: blockLine,
                    startOffset: blockStart
                );

                RefuseOutOfOrderTestBlock(
                    already: ((when is not null) || (expect is not null)),
                    diagnostics: diagnostics,
                    keyword: "when",
                    name: name,
                    span: parsed.Span
                );
                when ??= parsed;
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);

                continue;
            }

            if (TryMatchKeyword(context: context, keyword: "expect")) {
                var parsed = ParseTestExpectBlock(
                    col: blockColumn,
                    context: context,
                    diagnostics: diagnostics,
                    line: blockLine,
                    startOffset: blockStart
                );

                RefuseOutOfOrderTestBlock(
                    already: (expect is not null),
                    diagnostics: diagnostics,
                    keyword: "expect",
                    name: name,
                    span: parsed.Span
                );
                expect ??= parsed;
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);

                continue;
            }

            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.TestShapeInadmissible,
                message: $"test '{name}' carries '{ReadRestOfLine(context: context)}' — a test's body is 'given', 'when' and 'expect', in that order, and nothing else",
                span: new SourceSpan(blockStart, (cursor.Offset - blockStart), blockLine, blockColumn)
            );
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: $"Expected '}}' closing test '{name}'");
        }

        var length = (cursor.Offset - startOffset);

        if (expect is not { Expectations.Count: > 0 }) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.TestShapeInadmissible,
                message: $"test '{name}' states no expectation — a test's 'expect' block carries one gate expression per line, and a test with none answers nothing",
                span: new SourceSpan(startOffset, length, line, col)
            );
        }

        return new TestDeclarationNode(
            Column: col,
            Expect: expect,
            Given: given,
            Length: length,
            Line: line,
            Name: name,
            Offset: startOffset,
            When: when
        );
    }
    private static void RefuseOutOfOrderTestBlock(DiagnosticBag? diagnostics, bool already, string keyword, string name, SourceSpan span) {
        if (already) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.TestShapeInadmissible,
                message: $"test '{name}' writes '{keyword}' twice or out of order — a test's body is 'given', 'when' and 'expect', each at most once, in that order",
                span: span
            );
        }
    }
    private static bool TryParseSeatLabel(string label, out int seat) {
        seat = 0;

        return (
            label.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: SeatPrefix
        ) &&
            int.TryParse(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            result: out seat,
            s: label.AsSpan(start: SeatPrefix.Length),
            style: System.Globalization.NumberStyles.None
        ) &&
            (seat >= 1)
        );
    }
}
