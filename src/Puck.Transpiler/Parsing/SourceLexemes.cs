using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.Transpiler.Parsing;

// Shared lexical boundaries for preflight and formatting. String contents never participate in structural edits.
internal static class SourceLexemes {
    internal static int End(string source, int offset) {
        var start = offset;
        if (source[offset] == '$' && offset + 1 < source.Length && source[offset + 1] == '"') { ++offset; }
        if (source[offset] == '"') {
            if (source.AsSpan(offset).StartsWith("\"\"\"")) {
                var close = source.IndexOf("\"\"\"", offset + 3, StringComparison.Ordinal);
                return close < 0 ? source.Length : close + 3;
            }
            for (++offset; offset < source.Length; ++offset) {
                if (source[offset] == '\\') { ++offset; }
                else if (source[offset] == '"') { return offset + 1; }
            }
            return source.Length;
        }
        if (source[offset] == '`') {
            var close = source.IndexOf('`', offset + 1);
            return close < 0 ? source.Length : close + 1;
        }
        if (source.AsSpan(offset).StartsWith("//")) {
            var close = source.IndexOf('\n', offset + 2);
            return close < 0 ? source.Length : close;
        }
        if (source.AsSpan(offset).StartsWith("/*")) {
            var close = source.IndexOf("*/", offset + 2, StringComparison.Ordinal);
            return close < 0 ? source.Length : close + 2;
        }
        return start;
    }

    internal static void Validate(string source) {
        if (source.Length > DocumentEvaluationBudget.TextLimit) {
            throw Refuse("The source exceeds the four-million-character limit.", 0);
        }
        var depth = 0;
        for (var index = 0; index < source.Length; ++index) {
            var end = End(source, index);
            if (end > index) { index = end - 1; continue; }
            if (source[index] is '(' or '[' or '{') {
                if (++depth > DocumentEvaluationBudget.DepthLimit) {
                    throw Refuse("Source nesting exceeds 64 levels.", index);
                }
            } else if (source[index] is ')' or ']' or '}') { depth = Math.Max(0, depth - 1); }
        }
        PuckParseException Refuse(string message, int offset) {
            var line = 1;
            var column = 1;
            for (var index = 0; index < offset; ++index) {
                if (source[index] == '\n') { ++line; column = 1; } else { ++column; }
            }
            return new PuckParseException(message, offset, line, column) { Code = PuckDiagnosticCodes.EvaluationLimit };
        }
    }
}
