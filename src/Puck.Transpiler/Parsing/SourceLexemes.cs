using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.Transpiler.Parsing;

/// <summary>Shared lexical boundaries for preflight, formatting, and editor services.</summary>
public static class SourceLexemes {
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
    /// <returns>The exclusive end, or the original offset when no quoted token or comment starts there. An unterminated token extends to the end of the source.</returns>
    public static int End(string source, int offset) {
        var start = offset;

        if (
            (source[offset] == '$') &&
            ((offset + 1) < source.Length) &&
            (source[(offset + 1)] == '"')
        ) { ++offset; }
        if (source[offset] == '"') {
            if (source.AsSpan(start: offset).StartsWith(value: "\"\"\"")) {
                var close = source.IndexOf(
                    comparisonType: StringComparison.Ordinal,
                    startIndex: (offset + 3),
                    value: "\"\"\""
                );

                return ((close < 0)
                    ? source.Length
                    : (close + 3)
                );
            }
            for (++offset; (offset < source.Length); ++offset) {
                if (source[offset] == '\\') { ++offset; } else if (source[offset] == '"') { return (offset + 1); }
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
}
