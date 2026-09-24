using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Parsing;

// `test "name" [with module(arguments)] { given { } when { } expect { } }`. The three blocks are written in that
// order and each at most once; what each admits is exactly what a generated test world can carry today, so a
// spelling the engine cannot honour is refused here by name rather than lowered into a world that would ignore it.
public static partial class PuckParser {
    // The seat label a `when` step acts as. A step never acts as the console: the console is trusted at every gate,
    // so a step under it proves nothing about authority.
    private const string SeatPrefix = "seat";

    private static bool TryMatchTestKeyword(ParseContext context) =>
        TryMatchKeywordFollowedByName(admitted: NameForms.String, context: context, keyword: "test");
    // `world {` at the head of a line inside `given`, `when` or `expect`: a run of lines addressed to one world of
    // the composition the test stands at the root of. A bare word followed by '{' is the whole signal; anything
    // else leaves the cursor where it was, so a `seat1:` step and a `row = literal` cell read as they always did.
    private static bool TryMatchTestWorldHeader(ParseContext context, out string world, out SourceSpan span) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;
        var start = cursor.Offset;

        var (line, column) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: start
        );

        world = string.Empty;
        span = new SourceSpan(Column: column, Length: 0, Line: line, Offset: start);

        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var name)) {
            cursor.ResetPosition(position: saved);

            return false;
        }

        _ = SkipSpacesOnLine(context: context);
        if (!TryConsume(c: '{', context: context)) {
            cursor.ResetPosition(position: saved);

            return false;
        }

        span = new SourceSpan(start, (cursor.Offset - start), line, column);
        world = name;

        return true;
    }
    // A world block written inside another one. The inner block is parsed and discarded so the enclosing block's own
    // closing brace still lines up.
    private static void RefuseNestedTestWorldBlock(ParseContext context, DiagnosticBag? diagnostics, string world, string outer, string keyword, SourceSpan span) {
        diagnostics?.ReportError(
            code: PuckDiagnosticCodes.TestStepInadmissible,
            message: $"'{world}' opens a world block inside '{outer}' — a test's '{keyword}' block addresses one world per block, and a composition is one world deep",
            span: span
        );
    }
    private static bool TryHandleNestedTestWorldBlock(
        ParseContext context,
        DiagnosticBag? diagnostics,
        string keyword,
        string? outer,
        Action<ParseContext, string> skipNested
    ) {
        if (!TryMatchTestWorldHeader(
            context: context,
            span: out var span,
            world: out var nested
        )) {
            return false;
        }

        RefuseNestedTestWorldBlock(
            context: context,
            diagnostics: diagnostics,
            keyword: keyword,
            outer: (outer ?? string.Empty),
            span: span,
            world: nested
        );
        skipNested(context, nested);
        _ = TryConsume(c: '}', context: context);
        ConsumeSeparator(context: context);
        SkipWhiteSpace(context: context);

        return true;
    }
    private static TestGivenNode? ParseTestGivenCell(ParseContext context, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;
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

            return null;
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

            return null;
        }

        var value = ParseRhs(
            context,
            diagnostics,
            allowText: true,
            allowSeconds: false,
            ownerForDiagnostic: "a test's given block"
        );
        var cell = new TestGivenNode(
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
        );

        ConsumeSeparator(context: context);
        SkipWhiteSpace(context: context);

        return cell;
    }
    private static IReadOnlyList<TestGivenNode> ParseTestGivenCells(ParseContext context, DiagnosticBag? diagnostics, string outer) {
        var cursor = context.Scanner.Cursor;
        var cells = new List<TestGivenNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            if (TryHandleNestedTestWorldBlock(
                context: context,
                diagnostics: diagnostics,
                keyword: "given",
                outer: outer,
                skipNested: static (ctx, n) => _ = ParseTestGivenCells(context: ctx, diagnostics: null, outer: n)
            )) {
                continue;
            }

            if (ParseTestGivenCell(
                context: context,
                diagnostics: diagnostics
            ) is { } cell) {
                cells.Add(item: cell);
            }
        }

        return cells;
    }
    private static TestGivenBlockNode ParseTestGivenBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: "Expected '{' starting a test's 'given' block");
        }

        var items = new List<TestGivenItemNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            if (TryMatchTestWorldHeader(
                context: context,
                span: out var header,
                world: out var world
            )) {
                var cells = ParseTestGivenCells(
                    context: context,
                    diagnostics: diagnostics,
                    outer: world
                );

                if (!TryConsume(c: '}', context: context)) {
                    throw CreateException(context: context, message: $"Expected '}}' closing the world block '{world}' inside a test's 'given' block");
                }
                items.Add(item: new TestGivenWorldNode(
                    Cells: cells,
                    Column: header.Column,
                    Length: (cursor.Offset - header.Offset),
                    Line: header.Line,
                    Offset: header.Offset,
                    World: world
                ));
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);

                continue;
            }

            if (ParseTestGivenCell(
                context: context,
                diagnostics: diagnostics
            ) is { } cell) {
                items.Add(item: cell);
            }
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: "Expected '}' closing a test's 'given' block");
        }

        return new TestGivenBlockNode(
            Cells: items,
            Column: col,
            Length: (cursor.Offset - startOffset),
            Line: line,
            Offset: startOffset
        );
    }
    // One line of a `when` block. `ticks` is the grid's own cursor and belongs to the whole run, so it is admitted
    // only outside a world block.
    private static TestStepNode? ParseTestWhenStep(ParseContext context, DiagnosticBag? diagnostics, string? world) {
        var cursor = context.Scanner.Cursor;
        var stepStart = cursor.Offset;

        var (stepLine, stepColumn) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: stepStart
        );

        TestStepNode? step = null;

        if (TryMatchKeyword(context: context, keyword: "ticks")) {
            SkipWhiteSpace(context: context);

            var literal = (char.IsDigit(c: cursor.Current)
                ? ParseNumberWithOptionalUnit(context: context)
                : null
            );

            if (world is { } addressed) {
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.TestStepInadmissible,
                    message: $"'ticks' stands inside the world block '{addressed}' — one tick grid carries the whole composed run, so a 'ticks' step is written outside every world block",
                    span: new SourceSpan(stepStart, (cursor.Offset - stepStart), stepLine, stepColumn)
                );
            } else if ((literal is { Value: long ticks, Unit: null }) && (ticks > 0L)) {
                step = new TestTicksStepNode(
                    Column: stepColumn,
                    Length: (cursor.Offset - stepStart),
                    Line: stepLine,
                    Offset: stepStart,
                    Ticks: ticks
                );
            } else {
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.TestStepInadmissible,
                    message: "'ticks' carries a whole positive number of simulation ticks and no unit — a test's tick grid is counted in ticks, since a duration cannot say which tick a step lands on",
                    span: new SourceSpan(stepStart, (cursor.Offset - stepStart), stepLine, stepColumn)
                );
            }
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);

            return step;
        }

        if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var actor)) {
            SkipWhiteSpace(context: context);

            // `seat<n> refused ["text"]: <command line>` declares the step's outcome: the world must say no, and
            // the recorded refusal must contain the text when one is written.
            var refused = TryMatchKeyword(context: context, keyword: "refused");
            string? refusal = null;

            if (refused) {
                SkipWhiteSpace(context: context);
                if (TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out var wanted)) {
                    refusal = wanted;
                    SkipWhiteSpace(context: context);
                }
            }
            if (!TryConsume(c: ':', context: context)) {
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.TestStepInadmissible,
                    message: $"'{actor}{ReadRestOfLine(context: context)}' is not a 'when' step — a test's when block carries 'ticks <n>', 'seat<n>: <command line>', 'seat<n> refused [\"text\"]: <command line>', and 'world {{ … }}' addressing one world of a composition",
                    span: new SourceSpan(stepStart, (cursor.Offset - stepStart), stepLine, stepColumn)
                );
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);

                return null;
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
                step = new TestSeatStepNode(
                    Column: stepColumn,
                    Command: command,
                    Length: (cursor.Offset - stepStart),
                    Line: stepLine,
                    Offset: stepStart,
                    Refusal: refusal,
                    Refused: refused,
                    Seat: seat
                );
            }
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);

            return step;
        }

        diagnostics?.ReportError(
            code: PuckDiagnosticCodes.TestStepInadmissible,
            message: $"'{ReadRestOfLine(context: context)}' is not a 'when' step — a test's when block carries 'ticks <n>', 'seat<n>: <command line>', and 'world {{ … }}' addressing one world of a composition",
            span: new SourceSpan(stepStart, (cursor.Offset - stepStart), stepLine, stepColumn)
        );
        ConsumeSeparator(context: context);
        SkipWhiteSpace(context: context);

        return null;
    }
    private static IReadOnlyList<TestSeatStepNode> ParseTestWhenSteps(ParseContext context, DiagnosticBag? diagnostics, string outer) {
        var cursor = context.Scanner.Cursor;
        var steps = new List<TestSeatStepNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            if (TryHandleNestedTestWorldBlock(
                context: context,
                diagnostics: diagnostics,
                keyword: "when",
                outer: outer,
                skipNested: static (ctx, n) => _ = ParseTestWhenSteps(context: ctx, diagnostics: null, outer: n)
            )) {
                continue;
            }

            if (ParseTestWhenStep(
                context: context,
                diagnostics: diagnostics,
                world: outer
            ) is TestSeatStepNode seat) {
                steps.Add(item: seat);
            }
        }

        return steps;
    }
    private static TestWhenBlockNode ParseTestWhenBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: "Expected '{' starting a test's 'when' block");
        }

        var steps = new List<TestStepNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            if (TryMatchTestWorldHeader(
                context: context,
                span: out var header,
                world: out var world
            )) {
                var addressed = ParseTestWhenSteps(
                    context: context,
                    diagnostics: diagnostics,
                    outer: world
                );

                if (!TryConsume(c: '}', context: context)) {
                    throw CreateException(context: context, message: $"Expected '}}' closing the world block '{world}' inside a test's 'when' block");
                }
                steps.Add(item: new TestWhenWorldNode(
                    Column: header.Column,
                    Length: (cursor.Offset - header.Offset),
                    Line: header.Line,
                    Offset: header.Offset,
                    Steps: addressed,
                    World: world
                ));
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);

                continue;
            }

            if (ParseTestWhenStep(
                context: context,
                diagnostics: diagnostics,
                world: null
            ) is { } step) {
                steps.Add(item: step);
            }
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
    private static TestExpectationNode ParseTestExpectation(ParseContext context, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;
        var expectStart = cursor.Offset;

        var (expectLine, expectColumn) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: expectStart
        );
        var predicate = ParseGate(
            context: context,
            diagnostics: diagnostics
        );
        var expectation = new TestExpectationNode(
            Column: expectColumn,
            Length: (cursor.Offset - expectStart),
            Line: expectLine,
            Offset: expectStart,
            Predicate: predicate
        );

        ConsumeSeparator(context: context);
        SkipWhiteSpace(context: context);

        // A gate reader that consumed nothing would spin here forever on a line it cannot read; the gate itself
        // has already reported why.
        if (cursor.Offset == expectStart) {
            _ = ReadRestOfLine(context: context);
            SkipWhiteSpace(context: context);
        }

        return expectation;
    }
    private static IReadOnlyList<TestExpectationNode> ParseTestExpectations(ParseContext context, DiagnosticBag? diagnostics, string outer) {
        var cursor = context.Scanner.Cursor;
        var expectations = new List<TestExpectationNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            if (TryHandleNestedTestWorldBlock(
                context: context,
                diagnostics: diagnostics,
                keyword: "expect",
                outer: outer,
                skipNested: static (ctx, n) => _ = ParseTestExpectations(context: ctx, diagnostics: null, outer: n)
            )) {
                continue;
            }
            expectations.Add(item: ParseTestExpectation(
                context: context,
                diagnostics: diagnostics
            ));
        }

        return expectations;
    }
    private static TestExpectBlockNode ParseTestExpectBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: "Expected '{' starting a test's 'expect' block");
        }

        var items = new List<TestExpectItemNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            if (TryMatchTestWorldHeader(
                context: context,
                span: out var header,
                world: out var world
            )) {
                var addressed = ParseTestExpectations(
                    context: context,
                    diagnostics: diagnostics,
                    outer: world
                );

                if (!TryConsume(c: '}', context: context)) {
                    throw CreateException(context: context, message: $"Expected '}}' closing the world block '{world}' inside a test's 'expect' block");
                }
                items.Add(item: new TestExpectWorldNode(
                    Column: header.Column,
                    Expectations: addressed,
                    Length: (cursor.Offset - header.Offset),
                    Line: header.Line,
                    Offset: header.Offset,
                    World: world
                ));
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);

                continue;
            }
            items.Add(item: ParseTestExpectation(
                context: context,
                diagnostics: diagnostics
            ));
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: "Expected '}' closing a test's 'expect' block");
        }

        return new TestExpectBlockNode(
            Column: col,
            Expectations: items,
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

        CallExpressionNode? subject = null;

        if (TryMatchKeyword(context: context, keyword: "with")) {
            subject = ParseTestSubject(context: context);
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

        if ((expect is null) || !expect.Lines.Any()) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.TestShapeInadmissible,
                message: $"test '{name}' states no expectation — a test's 'expect' block carries one gate expression per line, and a test with none answers nothing",
                span: new SourceSpan(Column: col, Length: length, Line: line, Offset: startOffset)
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
            Subject: subject,
            When: when
        );
    }
    // `with module(arguments)` — the same dotted module name and argument list a `use` carries, so the subject of a
    // test and the subject of an instantiation are one spelling.
    private static CallExpressionNode ParseTestSubject(ParseContext context) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);

        var callStart = cursor.Offset;

        var (callLine, callColumn) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: callStart
        );

        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var moduleName)) {
            throw CreateException(context: context, message: "Expected a module name after 'with'");
        }

        moduleName = ReadDottedIdentifier(
            context: context,
            expectedAfterDotMessage: "Expected a module name after '.'",
            initialName: moduleName
        );
        SkipWhiteSpace(context: context);

        return ParseCallExpression(
            col: callColumn,
            context: context,
            functionName: moduleName,
            line: callLine,
            startOffset: callStart
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
