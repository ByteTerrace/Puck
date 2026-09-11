using Parlot.Fluent;
using Puck.State;
using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Parsing;

// The opaque-span operand scanner (§1.2 of the sugar wave) and the row-reference reader (§2.2) built on it. Both
// hand raw text to Puck.State.ExpressionSpelling.TryParse rather than re-implementing any part of its grammar
// (rule 8): the scanner assigns no meaning to the characters it passes over, only tracking bracket/backquote depth
// and the handful of stop conditions §1.2 names.
public static partial class PuckParser {
    private static readonly string[] ComparatorPunctuation = [">>>", "<<", ">>", "==", "!=", "<=", ">=", "<", ">"];
    private static readonly HashSet<string> ComparisonStopOperators = new(StringComparer.Ordinal) { "==", "!=", "<=", ">=", "<", ">" };
    private static readonly HashSet<string> GateReservedWords = new(StringComparer.Ordinal) { "and", "or", "not", "as" };

    /// <summary>Scans one contiguous span of opaque operand text: brackets and parentheses are tracked as one
    /// combined depth counter, a backquoted span is passed through uninterpreted, and scanning always stops
    /// (without consuming) at a top-level ')', ',', ';', '}', a newline, or end of input. When <paramref
    /// name="stopKeywords"/> names a bare word found at a word boundary, scanning also stops there. When <paramref
    /// name="stopAtComparator"/>, a top-level comparison operator (from <see cref="ComparisonStopOperators"/>) also
    /// stops the scan without being consumed — <paramref name="sawComparator"/> reports whether that happened, so a
    /// second stop of this kind (chained comparisons) is diagnosable by the caller.</summary>
    private static string ScanOperandSpan(ParseContext context, IReadOnlySet<string>? stopKeywords, bool stopAtComparator, out bool sawComparator) {
        var cursor = context.Scanner.Cursor;
        var buffer = context.Scanner.Buffer;
        SkipWhiteSpace(context);
        var start = cursor.Offset;
        var depth = 0;
        sawComparator = false;

        while (!cursor.Eof) {
            var c = cursor.Current;

            if (c == '`') {
                cursor.Advance();
                while (!cursor.Eof && cursor.Current != '`') {
                    cursor.Advance();
                }
                if (!cursor.Eof) {
                    cursor.Advance();
                }
                continue;
            }

            if (c is '(' or '[') {
                depth++;
                cursor.Advance();
                continue;
            }
            if (c is ')' or ']') {
                if (c == ')' && depth == 0) {
                    break;
                }
                if (depth > 0) {
                    depth--;
                }
                cursor.Advance();
                continue;
            }

            if (depth > 0) {
                cursor.Advance();
                continue;
            }

            if (c is ',' or ';' or '}' or '\n' or '\r') {
                break;
            }

            if (stopAtComparator) {
                var matched = LongestMatchingPunctuation(buffer, cursor.Offset, ComparatorPunctuation);
                if (matched is not null) {
                    if (ComparisonStopOperators.Contains(matched)) {
                        sawComparator = true;
                        break;
                    }
                    cursor.Advance(matched.Length);
                    continue;
                }
            }

            if (stopKeywords is not null && MatchingWordAt(buffer, cursor.Offset, stopKeywords) is not null) {
                break;
            }

            cursor.Advance();
        }

        return buffer[start..cursor.Offset].Trim();
    }

    /// <summary>Reads the comparator token a right-scan stopped on (one of <see cref="ComparisonStopOperators"/>),
    /// advancing past it. The caller must only invoke this after <see cref="ScanOperandSpan"/> reported
    /// <c>sawComparator</c>.</summary>
    private static string ConsumeComparatorToken(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var matched = LongestMatchingPunctuation(context.Scanner.Buffer, cursor.Offset, ComparatorPunctuation);
        if (matched is null || !ComparisonStopOperators.Contains(matched)) {
            throw CreateException(context, "Expected a comparison operator ('==', '!=', '<', '<=', '>', '>=')");
        }
        cursor.Advance(matched.Length);
        return matched;
    }

    // Strips a trailing ": Int"/": Fixed" kind suffix a right-operand scan swallowed whole (':' is not itself a stop
    // token — it is legitimate inside a ternary and inside a "$name:segment" reserved channel, so the suffix is
    // recognized only after the fact, as an exact trailing pattern). The "as Int"/"as Fixed" spelling never reaches
    // here: "as" is a gate-reserved word (GateReservedWords), so the scan already stops there and the caller reads
    // it directly (see ParseComparisonPredicate).
    private static bool TryStripTrailingColonKind(string text, out string exprText, out string? kind) {
        exprText = text;
        kind = null;
        var trimmed = text.TrimEnd();
        foreach (var candidate in (string[])["Int", "Fixed"]) {
            if (!trimmed.EndsWith(candidate, StringComparison.Ordinal)) {
                continue;
            }
            var beforeWord = trimmed.Length - candidate.Length;
            if (beforeWord > 0 && IsNameContinuationCharacter(trimmed[beforeWord - 1])) {
                continue;
            }
            var head = trimmed[..beforeWord].TrimEnd();
            if (!head.EndsWith(':')) {
                continue;
            }
            exprText = head[..^1].TrimEnd();
            kind = candidate;
            return true;
        }
        return false;
    }

    /// <summary>Validates opaque operand text through <c>ExpressionSpelling.TryParse</c>, reporting PUCK002 at
    /// <paramref name="span"/> on failure. Never re-implements the expression grammar — the parse result itself is
    /// the only thing consulted (rule 8).</summary>
    private static bool ValidateOperandText(string text, SourceSpan span, DiagnosticBag? diagnostics) {
        if (ExpressionSpelling.TryParse(text, out _, out var error)) {
            return true;
        }
        diagnostics?.ReportError("PUCK002", $"'{text}' {error}", span);
        return false;
    }

    /// <summary>Reads a row reference span — a name (extended for reserved <c>$</c>-channels and a folded
    /// <c>$zones[...]</c> selector, §9-A8) followed by zero or more immediately adjacent, balanced <c>[...]</c>
    /// groups — without resolving or validating it. Used where the caller must look ahead (e.g. to see whether
    /// '=' or '+=' follows) before committing to a row-reference interpretation, so no diagnostic is reported on a
    /// span the caller ends up abandoning.</summary>
    private static bool TryReadRowRefSpanRaw(ParseContext context, out string text, out SourceSpan span) {
        var cursor = context.Scanner.Cursor;
        SkipWhiteSpace(context);
        var start = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, start);

        if (!TryReadExtendedName(context, out _)) {
            text = string.Empty;
            span = default;
            return false;
        }

        while (!cursor.Eof && cursor.Current == '[') {
            var depth = 0;
            do {
                if (cursor.Current == '[') {
                    depth++;
                } else if (cursor.Current == ']') {
                    depth--;
                }
                cursor.Advance();
            } while (!cursor.Eof && depth > 0);
        }

        text = context.Scanner.Buffer[start..cursor.Offset];
        span = new SourceSpan(start, cursor.Offset - start, line, col);
        return true;
    }

    /// <summary>Resolves a row-reference span read by <see cref="TryReadRowRefSpanRaw"/> through
    /// <c>ExpressionSpelling.TryParse</c>, requiring exactly one <c>State</c> token (PUCK003 otherwise; PUCK002 when
    /// the span does not even parse). The token's own <c>Name</c>/<c>Key</c> become the result — the parser never
    /// re-derives that split itself.</summary>
    private static RowRefNode ResolveRowRef(string text, SourceSpan span, DiagnosticBag? diagnostics) {
        if (!ExpressionSpelling.TryParse(text, out var tokens, out var error)) {
            diagnostics?.ReportError("PUCK002", $"'{text}' {error}", span);
            return new RowRefNode(text, null, span.Offset, span.Length, span.Line, span.Column);
        }
        if (tokens.Count != 1 || tokens[0] is not ValueToken.State state) {
            diagnostics?.ReportError("PUCK003", $"expected a single state-row reference here, not an expression ('{text}')", span);
            return new RowRefNode(text, null, span.Offset, span.Length, span.Line, span.Column);
        }
        return new RowRefNode(state.Name, state.Key, span.Offset, span.Length, span.Line, span.Column);
    }

    /// <summary>Reads and eagerly resolves a row reference, for the unconditional contexts (<c>countdown</c>,
    /// <c>remove</c>, <c>schedule</c>) where nothing else could follow the keyword.</summary>
    private static bool TryReadRowRef(ParseContext context, DiagnosticBag? diagnostics, out RowRefNode? rowRef) {
        if (!TryReadRowRefSpanRaw(context, out var text, out var span)) {
            rowRef = null;
            return false;
        }
        rowRef = ResolveRowRef(text, span, diagnostics);
        return true;
    }

    private static bool TryReadBackquotedName(ParseContext context, out string name) {
        var cursor = context.Scanner.Cursor;
        if (cursor.Eof || cursor.Current != '`') {
            name = string.Empty;
            return false;
        }
        cursor.Advance();
        var start = cursor.Offset;
        while (!cursor.Eof && cursor.Current != '`') {
            cursor.Advance();
        }
        if (cursor.Eof) {
            name = string.Empty;
            return false;
        }
        name = context.Scanner.Buffer[start..cursor.Offset];
        cursor.Advance();
        return (name.Length > 0);
    }

    private static decimal LiteralToDecimal(object? value) => value switch {
        decimal m => m,
        long l => l,
        ulong ul => ul,
        double d => (decimal)d,
        int i => i,
        _ => 0m,
    };

    /// <summary>Reads a name on the same terms <c>ExpressionSpelling</c>'s own lexer does for a reserved channel:
    /// letters/digits/<c>_</c>/<c>$</c>/<c>.</c>, plus, once the name starts with <c>$</c>, <c>:name</c>
    /// continuations, signed <c>:-N</c> segments, and a <c>$zones[...]</c> group folded whole into the name (§9-A8).
    /// Duplicated from <c>ExpressionSpelling</c>'s private lexer (Puck.State is outside this stage's file scope —
    /// see the parser-owner notes) rather than exposed there; keep the two in sync by hand.</summary>
    private static bool TryReadExtendedName(ParseContext context, out string name) {
        var buffer = context.Scanner.Buffer;
        var cursor = context.Scanner.Cursor;
        var start = cursor.Offset;

        if (start >= buffer.Length) {
            name = string.Empty;
            return false;
        }

        var first = buffer[start];
        if (!char.IsLetter(first) && first != '_' && first != '$') {
            name = string.Empty;
            return false;
        }

        var reserved = (first == '$');
        var pos = start + 1;

        while (pos < buffer.Length) {
            var c = buffer[pos];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '.') {
                pos++;
                continue;
            }
            if (reserved && c == ':' && pos + 1 < buffer.Length) {
                var next = buffer[pos + 1];
                if (char.IsLetterOrDigit(next) || next == '_' || next == '$' || next == '.') {
                    pos++;
                    continue;
                }
                if (next == '-' && pos + 2 < buffer.Length && char.IsAsciiDigit(buffer[pos + 2])) {
                    pos += 2;
                    continue;
                }
            }
            if (reserved && c == '[') {
                var close = ReservedLiveZoneIndexEnd(buffer, start, pos);
                if (close > 0) {
                    pos = close + 1;
                    continue;
                }
            }
            break;
        }

        name = buffer[start..pos];
        cursor.Advance(pos - start);
        return true;
    }

    // Mirrors ExpressionSpelling's private LiveZoneIndexEnd: a "$zones[" segment folds its whole bracketed index —
    // colons, nested brackets and all — into the enclosing reserved name. Returns the closing bracket's offset, or
    // -1 when the bracket at `bracket` does not open a live-zone index or never closes.
    private static int ReservedLiveZoneIndexEnd(string text, int start, int bracket) {
        const string livezonePrefix = "$zones[";
        var prefixLength = livezonePrefix.Length - 1;
        if (text[bracket] != '[' || (bracket - start) < prefixLength) {
            return -1;
        }
        if (string.CompareOrdinal(text, bracket - prefixLength, livezonePrefix, 0, prefixLength) != 0) {
            return -1;
        }
        if ((bracket - prefixLength) != start && text[bracket - prefixLength - 1] != ':') {
            return -1;
        }
        var depth = 0;
        for (var index = bracket; index < text.Length; index++) {
            if (text[index] == '[') {
                depth++;
            } else if (text[index] == ']' && --depth == 0) {
                return index;
            }
        }
        return -1;
    }

    private static string? LongestMatchingPunctuation(string buffer, int offset, string[] candidates) {
        foreach (var candidate in candidates) {
            if (buffer.Length - offset >= candidate.Length && string.CompareOrdinal(buffer, offset, candidate, 0, candidate.Length) == 0) {
                return candidate;
            }
        }
        return null;
    }

    // A bare-word match at `offset`: the preceding character (when any) and the character right after the
    // candidate must both be outside a name, so "android" never spuriously matches "and".
    private static string? MatchingWordAt(string buffer, int offset, IReadOnlySet<string> candidates) {
        if (offset > 0 && IsNameContinuationCharacter(buffer[offset - 1])) {
            return null;
        }
        foreach (var candidate in candidates) {
            if (buffer.Length - offset < candidate.Length) {
                continue;
            }
            if (string.CompareOrdinal(buffer, offset, candidate, 0, candidate.Length) != 0) {
                continue;
            }
            var after = offset + candidate.Length;
            if (after < buffer.Length && IsNameContinuationCharacter(buffer[after])) {
                continue;
            }
            return candidate;
        }
        return null;
    }

    private static bool IsNameContinuationCharacter(char c) => (char.IsLetterOrDigit(c) || c == '_' || c == '$');

    // Whether nothing else follows `identifier` on its own logical line (only spaces/tabs, then a newline, a line
    // comment, '}', ';', ',', or end of input) — used to recognize a bare keyword statement (e.g. `solid`, §4.2)
    // without the general whitespace skip first erasing the line boundary a newline-terminated grammar depends on.
    private static bool AtEndOfLogicalStatement(string buffer, int offset) {
        var i = offset;
        while (i < buffer.Length && (buffer[i] == ' ' || buffer[i] == '\t')) {
            i++;
        }
        if (i >= buffer.Length) {
            return true;
        }
        if (buffer[i] is '\r' or '\n' or '}' or ';' or ',') {
            return true;
        }
        return (i + 1 < buffer.Length && buffer[i] == '/' && buffer[i + 1] == '/');
    }
}
