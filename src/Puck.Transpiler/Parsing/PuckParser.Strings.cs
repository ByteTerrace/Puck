using Parlot.Fluent;
using Puck.State;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Parsing;

// The string forms, following C#'s: `"…"` is the plain literal, `"""…"""` is raw (no escapes, optionally
// multi-line with the closing fence setting the indent to strip), and a `$` prefix turns either into an
// interpolation whose `{…}` holes are ordinary expressions. A plain `"…"` never interpolates, so every string
// written before this reads exactly as it did.
public static partial class PuckParser {
    private const string RawFence = "\"\"\"";

    /// <summary>The spellings a name read admits. A read takes the first form the text actually starts with, and
    /// fails without consuming anything when that form is not among the admitted ones.</summary>
    [Flags]
    private enum NameForms {
        /// <summary>A bare identifier: letters, digits, <c>_</c>, and <c>$</c>.</summary>
        Identifier = 1,
        /// <summary>A quoted string, plain or raw.</summary>
        String = 2,
        /// <summary>A <c>$"…"</c> string, whose holes read as expressions.</summary>
        Interpolated = 4,
        /// <summary>A reserved-channel name, on <c>ExpressionSpelling</c>'s own terms.</summary>
        Extended = 8,
    }
    /// <summary>How a name was written, for a printer that must write it back the same way.</summary>
    /// <param name="Quoted">Whether the name arrived as a quoted string rather than as a bare word.</param>
    /// <param name="Expression">The node a quoted or interpolated read also yields, or <see langword="null"/>.</param>
    private readonly record struct NameSpelling(bool Quoted, ExpressionNode? Expression);

    /// <summary>Reads a name in whichever of its spellings <paramref name="admitted"/> allows: a bare identifier, a
    /// reserved-channel name, a quoted string (plain or raw), or a <c>$"…"</c> interpolation.</summary>
    /// <param name="context">The parse context.</param>
    /// <param name="admitted">The spellings this position accepts.</param>
    /// <param name="text">The name's own text, decoded; the raw interpolation body for an interpolated read.</param>
    /// <param name="spelling">How the name was written.</param>
    /// <returns><see langword="true"/> when a name was read.</returns>
    private static bool TryReadName(ParseContext context, NameForms admitted, out string text, out NameSpelling spelling) {
        SkipWhiteSpace(context: context);

        var cursor = context.Scanner.Cursor;
        var buffer = context.Scanner.Buffer;

        text = string.Empty;
        spelling = default;

        if (cursor.Eof) {
            return false;
        }

        var startPosition = cursor.Position;
        var startOffset = cursor.Offset;
        var interpolating = (
            (cursor.Current == '$') &&
            ((startOffset + 1) < buffer.Length) &&
            (buffer[(startOffset + 1)] == '"')
        );

        if (interpolating || (cursor.Current == '"')) {
            if ((admitted & (interpolating
                ? NameForms.Interpolated
                : NameForms.String
            )) == 0) {
                return false;
            }

            var (line, col) = GetLineAndColumn(
                buffer: buffer,
                offset: startOffset
            );
            var quoteOffset = (interpolating
                ? (startOffset + 1)
                : startOffset
            );

            if (Matches(
                buffer: buffer,
                offset: quoteOffset,
                token: RawFence
            )) {
                if (!TryReadRawString(
                    context: context,
                    quoteOffset: quoteOffset,
                    text: out text
                )) {
                    throw CreateException(
                        context: context,
                        message: "Unterminated raw string literal"
                    );
                }
            } else {
                if (!PuckStrings.TryRead(
                    end: out var closed,
                    error: out var failure,
                    offset: quoteOffset,
                    source: buffer,
                    value: out text
                )) {
                    if (interpolating) {
                        throw CreateException(
                            context: context,
                            message: $"Malformed interpolated string literal: {failure}"
                        );
                    }
                    cursor.ResetPosition(position: startPosition);
                    throw CreateException(
                        context: context,
                        message: $"Malformed string literal: {failure}"
                    );
                }
                cursor.Advance(count: (closed - startOffset));
            }

            var span = new SourceSpan(
                startOffset,
                (cursor.Offset - startOffset),
                line,
                col
            );

            var fenced = Matches(
                buffer: buffer,
                offset: quoteOffset,
                token: RawFence
            );

            spelling = new NameSpelling(
                Expression: (interpolating
                    ? (BuildInterpolation(
                        span: span,
                        text: text
                    ) with { RawFenced = fenced })
                    : new LiteralExpressionNode(
                        Value: text,
                        Unit: null,
                        Offset: span.Offset,
                        Length: span.Length,
                        Line: line,
                        Column: col
                    ) { RawFenced = fenced }),
                Quoted: true
            );

            return true;
        }

        // The reserved-channel walk is ExpressionSpelling's own — `:name` continuations, signed `:-N` segments, and
        // a folded `$zones[...]` group — never a copy of it, and it subsumes the plain identifier it starts from.
        if ((admitted & NameForms.Extended) != 0) {
            var length = ExpressionSpelling.ScanBareName(
                start: startOffset,
                text: buffer
            );

            if (length == 0) {
                return false;
            }

            text = buffer[startOffset..(startOffset + length)];
            cursor.Advance(count: length);

            return true;
        }

        if ((admitted & NameForms.Identifier) == 0) {
            return false;
        }

        var first = cursor.Current;

        if (
            !char.IsLetter(c: first) &&
            (first != '_') &&
            (first != '$')
        ) {
            return false;
        }

        cursor.Advance();
        while (!cursor.Eof) {
            var current = cursor.Current;

            if (
                !char.IsLetterOrDigit(c: current) &&
                (current != '_') &&
                (current != '$')
            ) {
                break;
            }
            cursor.Advance();
        }
        text = buffer.Substring(
            length: (cursor.Offset - startOffset),
            startIndex: startOffset
        );

        return true;
    }
    // A raw string runs to its closing fence with no escape processing at all. When the opening fence ends its own
    // line and the closing fence begins its own line, the closing fence's indentation is stripped from every
    // content line and the bracketing newlines are dropped -- so the text reads at the indentation it is written
    // at, without carrying the surrounding code's.
    private static bool TryReadRawString(ParseContext context, int quoteOffset, out string text) {
        var cursor = context.Scanner.Cursor;
        var buffer = context.Scanner.Buffer;
        var contentStart = (quoteOffset + RawFence.Length);
        var closeOffset = buffer.IndexOf(
            comparisonType: StringComparison.Ordinal,
            startIndex: contentStart,
            value: RawFence
        );

        text = string.Empty;

        if (closeOffset < 0) {
            return false;
        }

        var raw = buffer[contentStart..closeOffset];

        cursor.ResetPosition(position: new Parlot.TextPosition(
            (closeOffset + RawFence.Length),
            0,
            0
        ));
        SkipWhiteSpace(context: context);

        var lines = raw.Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: "\n",
            oldValue: "\r\n"
        ).Split('\n');

        if (
            (lines.Length < 2) ||
            (lines[0].Trim().Length != 0) ||
            (lines[^1].Trim().Length != 0)
        ) {
            text = raw;

            return true;
        }

        var indent = lines[^1];
        var body = lines[1..^1];

        for (var index = 0; (index < body.Length); ++index) {
            body[index] = (body[index].StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: indent
            )
                ? body[index][indent.Length..]
                : body[index].TrimStart()
            );
        }

        text = string.Join(
            '\n',
            body
        );

        return true;
    }
    // Splits interpolation text into its literal runs and its holes. `{{` and `}}` stand for one brace each, so a
    // literal brace never needs a different string form.
    private static InterpolatedStringNode BuildInterpolation(string text, SourceSpan span) {
        var segments = new List<InterpolationSegment>();
        var literal = new System.Text.StringBuilder();

        for (var index = 0; (index < text.Length); ++index) {
            var c = text[index];

            if (
                (c == '{') &&
                ((index + 1) < text.Length) &&
                (text[(index + 1)] == '{')
            ) {
                literal.Append(value: '{');
                index++;

                continue;
            }

            if (
                (c == '}') &&
                ((index + 1) < text.Length) &&
                (text[(index + 1)] == '}')
            ) {
                literal.Append(value: '}');
                index++;

                continue;
            }

            if (c != '{') {
                literal.Append(value: c);

                continue;
            }

            var close = FindHoleEnd(
                start: (index + 1),
                text: text
            );

            if (close < 0) {
                throw new PuckParseException(
                    "Unterminated '{' in an interpolated string",
                    span.Offset,
                    span.Line,
                    span.Column
                ) {
                    Code = PuckDiagnosticCodes.Syntax,
                };
            }

            if (literal.Length > 0) {
                segments.Add(item: new InterpolationSegment.Literal(Text: literal.ToString()));
                literal.Clear();
            }

            segments.Add(item: new InterpolationSegment.Hole(Expression: ParseExpression(source: text[(index + 1)..close])));
            index = close;
        }

        if (literal.Length > 0) {
            segments.Add(item: new InterpolationSegment.Literal(Text: literal.ToString()));
        }

        return new InterpolatedStringNode(
            Segments: segments,
            Offset: span.Offset,
            Length: span.Length,
            Line: span.Line,
            Column: span.Column
        );
    }
    // The matching '}' for a hole, counting nesting and skipping over any string the expression itself carries.
    private static int FindHoleEnd(string text, int start) {
        var depth = 0;

        for (var index = start; (index < text.Length); ++index) {
            var c = text[index];

            if (c == '"') {
                index++;

                while (
                    (index < text.Length) &&
                    (text[index] != '"')
                ) {
                    index += ((text[index] == '\\')
                        ? 2
                        : 1
                    );
                }

                continue;
            }

            if (c is '{' or '[' or '(') {
                depth++;
            } else if (c is ']' or ')') {
                depth--;
            } else if (c == '}') {
                if (depth == 0) {
                    return index;
                }

                depth--;
            }
        }

        return -1;
    }
    private static bool Matches(string buffer, int offset, string token) =>
        (((offset + token.Length) <= buffer.Length) && (string.CompareOrdinal(
            buffer,
            offset,
            token,
            0,
            token.Length
        ) == 0));
}
