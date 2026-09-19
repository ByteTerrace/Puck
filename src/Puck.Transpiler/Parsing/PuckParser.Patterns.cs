using System.Globalization;
using Parlot.Fluent;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Parsing;

// The `pattern` declaration. Its `match:` value is the one place the operand scanner cannot be reused: a repetition
// count is spelled `{m,n}`, and that scanner stops at a top-level '{'. The match text therefore runs to the end of
// its own line, which is also what makes a pattern body's closing '}' unambiguous.
public static partial class PuckParser {
    private static bool TryMatchPatternKeyword(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (
            TryMatchKeyword(
            context: context,
            keyword: "pattern"
        ) &&
            SkipSpacesOnLine(context: context) &&
            TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out _)
        ) {
            cursor.ResetPosition(position: saved);

            return TryMatchKeyword(
                context: context,
                keyword: "pattern"
            );
        }

        cursor.ResetPosition(position: saved);

        return false;
    }
    private static PatternDeclarationNode ParsePatternDeclaration(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var name)) {
            throw CreateException(
                context: context,
                message: "Expected a pattern name after 'pattern'"
            );
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(
            c: ':',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: $"Expected ': Kind' after 'pattern {name}'"
            );
        }

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var kind)) {
            throw CreateException(
                context: context,
                message: $"Expected a value kind after 'pattern {name}:'"
            );
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(
            c: '{',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: $"Expected '{{' starting pattern body for '{name}'"
            );
        }

        var symbols = new List<PatternSymbolDeclarationNode>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        PatternNode? match = null;

        string? attribute = null;
        string? value = null;
        int? maximumStates = null;

        SkipWhiteSpace(context: context);
        while (
            !cursor.Eof &&
            (cursor.Current != '}')
        ) {
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var member)) {
                throw CreateException(
                    context: context,
                    message: $"Expected a pattern member name in '{name}'"
                );
            }

            if (member == "symbols") {
                ParsePatternSymbols(
                    context: context,
                    diagnostics: diagnostics,
                    name: name,
                    seen: seen,
                    symbols: symbols
                );
                SkipWhiteSpace(context: context);

                continue;
            }

            SkipWhiteSpace(context: context);
            if (!TryConsume(
                c: ':',
                context: context
            )) {
                throw CreateException(
                    context: context,
                    message: $"Expected ':' after '{member}' in pattern '{name}'"
                );
            }

            switch (member) {
                case "attribute": {
                        SkipWhiteSpace(context: context);
                        if (!TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out attribute)) {
                            throw CreateException(
                                context: context,
                                message: $"Expected a quoted row name after 'attribute:' in pattern '{name}'"
                            );
                        }

                        break;
                    }
                case "value": {
                        SkipWhiteSpace(context: context);
                        if (!TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out value)) {
                            throw CreateException(
                                context: context,
                                message: $"Expected a quoted expression after 'value:' in pattern '{name}'"
                            );
                        }

                        break;
                    }
                case "maxStates": {
                        SkipWhiteSpace(context: context);

                        var budgetStart = cursor.Offset;

                        while (
                            !cursor.Eof &&
                            char.IsAsciiDigit(c: cursor.Current)
                        ) {
                            cursor.Advance();
                        }
                        if (
                            (cursor.Offset == budgetStart) ||
                            !int.TryParse(
                            provider: CultureInfo.InvariantCulture,
                            result: out var budget,
                            s: context.Scanner.Buffer.AsSpan(start: budgetStart, length: (cursor.Offset - budgetStart))
                        )
                        ) {
                            throw CreateException(
                                context: context,
                                message: $"Expected a whole state budget after 'maxStates:' in pattern '{name}'"
                            );
                        }

                        maximumStates = budget;

                        break;
                    }
                case "match": {
                        var matchStart = cursor.Offset;
                        var matchText = ReadRestOfLine(context: context);

                        var (matchLine, matchCol) = GetLineAndColumn(
                            buffer: context.Scanner.Buffer,
                            offset: matchStart
                        );

                        if (!PatternSpelling.TryParse(
                            error: out var error,
                            node: out match,
                            text: matchText
                        )) {
                            diagnostics?.ReportError(
                                code: PuckDiagnosticCodes.PatternExpression,
                                message: $"pattern '{name}' match '{matchText}' {error}",
                                span: new SourceSpan(
                                    matchStart,
                                    (cursor.Offset - matchStart),
                                    matchLine,
                                    matchCol
                                )
                            );
                            match = new PatternNode.None();
                        }

                        break;
                    }
                default: {
                        throw CreateException(
                            context: context,
                            message: $"Pattern '{name}' carries no member '{member}'"
                        );
                    }
            }

            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(
            c: '}',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: $"Expected '}}' closing pattern body for '{name}'"
            );
        }

        if (match is null) {
            throw CreateException(
                context: context,
                message: $"Pattern '{name}' carries no 'match:' language"
            );
        }

        var length = (cursor.Offset - startOffset);

        return new PatternDeclarationNode(
            Attribute: attribute,
            Column: col,
            Kind: kind,
            Length: length,
            Line: line,
            Match: match,
            MaximumStates: maximumStates,
            Name: name,
            Offset: startOffset,
            Symbols: symbols,
            Value: value
        );
    }
    private static void ParsePatternSymbols(ParseContext context, string name, List<PatternSymbolDeclarationNode> symbols, HashSet<string> seen, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryConsume(
            c: '{',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: $"Expected '{{' starting the symbols block of pattern '{name}'"
            );
        }

        SkipWhiteSpace(context: context);
        while (
            !cursor.Eof &&
            (cursor.Current != '}')
        ) {
            var entryStart = cursor.Offset;

            var (entryLine, entryCol) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: entryStart
            );

            if (
                !TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var symbol) &&
                !TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out symbol)
            ) {
                throw CreateException(
                    context: context,
                    message: $"Expected a symbol name in the symbols block of pattern '{name}'"
                );
            }

            SkipWhiteSpace(context: context);
            if (!TryConsume(
                c: '=',
                context: context
            )) {
                throw CreateException(
                    context: context,
                    message: $"Expected '=' after symbol '{symbol}' in pattern '{name}'"
                );
            }
            if (!TryReadPatternBound(
                bound: out var minimum,
                context: context
            )) {
                throw CreateException(
                    context: context,
                    message: $"Expected a value after 'symbol {symbol} =' in pattern '{name}'"
                );
            }

            var maximum = minimum;

            if (TryConsumeRangeOperator(context: context) && !TryReadPatternBound(
                bound: out maximum,
                context: context
            )) {
                throw CreateException(
                    context: context,
                    message: $"Expected the greatest value of symbol '{symbol}' in pattern '{name}'"
                );
            }

            var span = new SourceSpan(
                entryStart,
                (cursor.Offset - entryStart),
                entryLine,
                entryCol
            );

            if (!seen.Add(item: symbol!)) {
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.DuplicateTypeMember,
                    message: $"Pattern '{name}' declares duplicate symbol '{symbol}'",
                    span: span
                );
            }
            if (maximum < minimum) {
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.PatternExpression,
                    message: $"pattern '{name}' symbol '{symbol}' reads {minimum}..{maximum}, which is not least first",
                    span: span
                );
            }

            symbols.Add(item: new PatternSymbolDeclarationNode(
                Column: entryCol,
                Length: span.Length,
                Line: entryLine,
                Maximum: maximum,
                Minimum: minimum,
                Name: symbol!,
                Offset: entryStart
            ));
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(
            c: '}',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: $"Expected '}}' closing the symbols block of pattern '{name}'"
            );
        }
    }
    // A '..' only closes a range when a value follows it, so `1..2` reads as a band while a bare `1` reads as one
    // value.
    private static bool TryConsumeRangeOperator(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var buffer = context.Scanner.Buffer;

        SkipSpacesOnLine(context: context);
        if (
            ((cursor.Offset + 1) >= buffer.Length) ||
            (buffer[cursor.Offset] != '.') ||
            (buffer[(cursor.Offset + 1)] != '.')
        ) {
            return false;
        }
        cursor.Advance(count: 2);

        return true;
    }
    private static bool TryReadPatternBound(ParseContext context, out decimal bound) {
        var cursor = context.Scanner.Cursor;
        var buffer = context.Scanner.Buffer;

        SkipSpacesOnLine(context: context);

        var start = cursor.Offset;

        if (
            !cursor.Eof &&
            (cursor.Current == '-')
        ) {
            cursor.Advance();
        }
        while (
            !cursor.Eof &&
            char.IsAsciiDigit(c: cursor.Current)
        ) {
            cursor.Advance();
        }
        // A fractional point only belongs to the number when a digit follows it; `1..2` keeps both dots for the
        // range operator.
        if (
            !cursor.Eof &&
            (cursor.Current == '.') &&
            ((cursor.Offset + 1) < buffer.Length) &&
            char.IsAsciiDigit(c: buffer[(cursor.Offset + 1)])
        ) {
            cursor.Advance();
            while (
                !cursor.Eof &&
                char.IsAsciiDigit(c: cursor.Current)
            ) {
                cursor.Advance();
            }
        }

        return decimal.TryParse(
            provider: CultureInfo.InvariantCulture,
            result: out bound,
            s: buffer.AsSpan(start: start, length: (cursor.Offset - start)),
            style: NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
        );
    }
    private static string ReadRestOfLine(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var buffer = context.Scanner.Buffer;

        SkipSpacesOnLine(context: context);

        var start = cursor.Offset;

        while (!cursor.Eof) {
            var c = cursor.Current;

            if (c is '\n' or '\r') {
                break;
            }
            if (
                (c == '/') &&
                ((cursor.Offset + 1) < buffer.Length) &&
                (buffer[(cursor.Offset + 1)] is '/' or '*')
            ) {
                break;
            }
            cursor.Advance();
        }

        return buffer[start..cursor.Offset].TrimEnd();
    }
}
