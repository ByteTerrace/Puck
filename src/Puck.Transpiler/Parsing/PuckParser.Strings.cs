using Parlot.Fluent;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Parsing;

// The string forms, following C#'s: `"…"` is the plain literal, `"""…"""` is raw (no escapes, optionally
// multi-line with the closing fence setting the indent to strip), and a `$` prefix turns either into an
// interpolation whose `{…}` holes are ordinary expressions. A plain `"…"` never interpolates, so every string
// written before this reads exactly as it did.
public static partial class PuckParser {
    private const string RawFence = "\"\"\"";

    /// <summary>Reads a string in any of its forms, yielding an expression node: a literal when nothing
    /// interpolates, an <see cref="InterpolatedStringNode"/> when something does.</summary>
    private static bool TryReadStringExpression(ParseContext context, out ExpressionNode? node) {
        SkipWhiteSpace(context);

        var cursor = context.Scanner.Cursor;
        var buffer = context.Scanner.Buffer;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(buffer, startOffset);
        var interpolating = (cursor.Current == '$');
        var quoteOffset = (interpolating ? (startOffset + 1) : startOffset);

        node = null;

        if (quoteOffset >= buffer.Length || buffer[quoteOffset] != '"') {
            return false;
        }

        string text;

        if (Matches(buffer, quoteOffset, RawFence)) {
            if (!TryReadRawString(context, quoteOffset, out text)) {
                throw CreateException(context, "Unterminated raw string literal");
            }
        } else if (interpolating) {
            cursor.Advance();

            if (!TryReadString(context, out text)) {
                throw CreateException(context, "Malformed interpolated string literal");
            }
        } else if (!TryReadString(context, out text)) {
            return false;
        }

        var span = new SourceSpan(startOffset, (cursor.Offset - startOffset), line, col);

        node = (interpolating
            ? BuildInterpolation(text, span)
            : new LiteralExpressionNode(Value: text, Unit: null, Offset: span.Offset, Length: span.Length, Line: line, Column: col));

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
        var closeOffset = buffer.IndexOf(RawFence, contentStart, StringComparison.Ordinal);

        text = string.Empty;

        if (closeOffset < 0) {
            return false;
        }

        var raw = buffer[contentStart..closeOffset];

        cursor.ResetPosition(new Parlot.TextPosition((closeOffset + RawFence.Length), 0, 0));
        SkipWhiteSpace(context);

        var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        if (lines.Length < 2 || lines[0].Trim().Length != 0 || lines[^1].Trim().Length != 0) {
            text = raw;

            return true;
        }

        var indent = lines[^1];
        var body = lines[1..^1];

        for (var index = 0; (index < body.Length); ++index) {
            body[index] = (body[index].StartsWith(indent, StringComparison.Ordinal) ? body[index][indent.Length..] : body[index].TrimStart());
        }

        text = string.Join('\n', body);

        return true;
    }

    // Splits interpolation text into its literal runs and its holes. `{{` and `}}` stand for one brace each, so a
    // literal brace never needs a different string form.
    private static ExpressionNode BuildInterpolation(string text, SourceSpan span) {
        var segments = new List<InterpolationSegment>();
        var literal = new System.Text.StringBuilder();

        for (var index = 0; (index < text.Length); ++index) {
            var c = text[index];

            if ((c == '{') && ((index + 1) < text.Length) && (text[index + 1] == '{')) {
                literal.Append('{');
                index++;

                continue;
            }

            if ((c == '}') && ((index + 1) < text.Length) && (text[index + 1] == '}')) {
                literal.Append('}');
                index++;

                continue;
            }

            if (c != '{') {
                literal.Append(c);

                continue;
            }

            var close = FindHoleEnd(text, (index + 1));

            if (close < 0) {
                throw new PuckParseException("Unterminated '{' in an interpolated string", span.Offset, span.Line, span.Column) {
                    Code = PuckDiagnosticCodes.Syntax,
                };
            }

            if (literal.Length > 0) {
                segments.Add(new InterpolationSegment.Literal(Text: literal.ToString()));
                literal.Clear();
            }

            segments.Add(new InterpolationSegment.Hole(Expression: ParseExpression(text[(index + 1)..close])));
            index = close;
        }

        if (literal.Length > 0) {
            segments.Add(new InterpolationSegment.Literal(Text: literal.ToString()));
        }

        return new InterpolatedStringNode(Segments: segments, Offset: span.Offset, Length: span.Length, Line: span.Line, Column: span.Column);
    }

    // The matching '}' for a hole, counting nesting and skipping over any string the expression itself carries.
    private static int FindHoleEnd(string text, int start) {
        var depth = 0;

        for (var index = start; (index < text.Length); ++index) {
            var c = text[index];

            if (c == '"') {
                index++;

                while ((index < text.Length) && (text[index] != '"')) {
                    index += ((text[index] == '\\') ? 2 : 1);
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
        ((offset + token.Length) <= buffer.Length) && string.CompareOrdinal(buffer, offset, token, 0, token.Length) == 0;
}
