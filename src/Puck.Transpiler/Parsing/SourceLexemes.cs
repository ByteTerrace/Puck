using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.Transpiler.Parsing;

/// <summary>Shared lexical boundaries for preflight, formatting, and editor services.</summary>
public static class SourceLexemes {
    /// <summary>Trims trailing whitespace and comments from a recorded syntax span, preserving strings and interior comments.</summary>
    /// <param name="source">The source that owns the span.</param>
    /// <param name="span">The recorded span, which may include trivia consumed by lookahead.</param>
    /// <returns>The span ending at its last syntax token.</returns>
    public static SourceSpan ContentSpan(string source, SourceSpan span) {
        ArgumentNullException.ThrowIfNull(argument: source);
        var start = Math.Clamp(value: span.Offset, min: 0, max: source.Length);
        var end = Math.Clamp(value: (span.Offset + span.Length), min: start, max: source.Length);
        var last = start;
        var index = start;

        while (index < end) {
            var lexeme = End(offset: index, source: source);

            if (lexeme > index) {
                if (source[index] is '"' or '`' or '$') { last = Math.Min(val1: lexeme, val2: end); }
                index = lexeme;
                continue;
            }
            if (!char.IsWhiteSpace(c: source[index])) { last = (index + 1); }
            index++;
        }
        return span with { Offset = start, Length = (last - start) };
    }

    internal static void Validate(string source) {
        if (source.Length > DocumentEvaluationBudget.TextLimit) {
            throw Refuse(
                message: "The source exceeds the four-million-character limit.",
                offset: 0
            );
        }
        var depth = 0;

        for (var index = 0; (index < source.Length); ++index) {
            var end = End(
                offset: index,
                source: source
            );

            if (end > index) { index = (end - 1); continue; }
            if (source[index] is '(' or '[' or '{') {
                if (++depth > DocumentEvaluationBudget.DepthLimit) {
                    throw Refuse(
                        message: "Source nesting exceeds 64 levels.",
                        offset: index
                    );
                }
            } else if (source[index] is ')' or ']' or '}') {
                depth = Math.Max(
                val1: 0,
                val2: (depth - 1)
            );
            }
        }
        PuckParseException Refuse(string message, int offset) {
            var line = 1;
            var column = 1;

            for (var index = 0; (index < offset); ++index) {
                if (source[index] == '\n') { ++line; column = 1; } else { ++column; }
            }
            return new PuckParseException(
                message,
                offset,
                line,
                column
            ) { Code = PuckDiagnosticCodes.EvaluationLimit };
        }
    }

    /// <summary>Finds the exclusive end of a string, backquoted identifier, or comment at an offset.</summary>
    /// <param name="source">The source text.</param>
    /// <param name="offset">An index within the source text.</param>
    /// <returns>The exclusive end, or the original offset when no quoted token or comment starts there. A plain
    /// string that never closes ends with its line, which is as far as the grammar lets one run
    /// (<see cref="PuckStrings.TryRead"/>); any other unterminated token extends to the end of the source.</returns>
    public static int End(string source, int offset) => End(source, offset, 0);

    private static int End(string source, int offset, int nesting) {
        var start = offset;

        if (
            (source[offset] == '$') &&
            ((offset + 1) < source.Length) &&
            (source[(offset + 1)] == '"')
        ) { ++offset; }
        if (source[offset] == '"') {
            var interpolated = offset != start;
            var fence = source.AsSpan(offset).StartsWith("\"\"\"") ? 3 : 1;
            for (offset += fence; offset < source.Length; offset++) {
                if (interpolated && source[offset] == '{') {
                    if (offset + 1 < source.Length && source[offset + 1] == '{') { offset++; continue; }
                    var close = HoleEnd(source, offset + 1, nesting + 1);
                    if (close < 0) { return source.Length; }
                    offset = close;
                } else if (fence == 1 && source[offset] == '\\') { offset++; }
                else if (source[offset] == '"' && (fence == 1 || source.AsSpan(offset).StartsWith("\"\"\""))) { return offset + fence; }
                else if (fence == 1 && !interpolated && source[offset] is '\n' or '\r') { return offset; }
            }
            return source.Length;
        }
        if (source[offset] == '`') {
            var close = source.IndexOf(
                startIndex: (offset + 1),
                value: '`'
            );

            return ((close < 0)
                ? source.Length
                : (close + 1)
            );
        }
        if (source.AsSpan(start: offset).StartsWith(value: "//")) {
            var close = source.IndexOf(
                startIndex: (offset + 2),
                value: '\n'
            );

            return ((close < 0)
                ? source.Length
                : close
            );
        }
        if (source.AsSpan(start: offset).StartsWith(value: "/*")) {
            var close = source.IndexOf(
                comparisonType: StringComparison.Ordinal,
                startIndex: (offset + 2),
                value: "*/"
            );

            return ((close < 0)
                ? source.Length
                : (close + 2)
            );
        }
        return start;
    }

    /// <summary>Finds a hole's closing brace, skipping nested strings, comments and balanced braces.</summary>
    /// <param name="source">The text containing the hole.</param>
    /// <param name="start">The index just after its opening brace.</param>
    /// <returns>The closing brace's index, or -1 when it is missing.</returns>
    public static int HoleEnd(string source, int start) => HoleEnd(source, start, 0);

    private static int HoleEnd(string source, int start, int nesting) {
        if (nesting >= DocumentEvaluationBudget.DepthLimit) {
            throw new PuckParseException("Source nesting exceeds 64 levels.", start, 1 + source.AsSpan(0, start).Count('\n'), start - source.LastIndexOf('\n', start - 1)) {
                Code = PuckDiagnosticCodes.EvaluationLimit,
            };
        }
        var depth = 0;
        for (var index = start; index < source.Length; index++) {
            var end = End(source, index, nesting);
            if (end > index) { index = end - 1; continue; }
            if (source[index] == '{') { depth++; }
            else if (source[index] == '}') {
                if (depth == 0) { return index; }
                depth--;
            }
        }
        return -1;
    }
}
