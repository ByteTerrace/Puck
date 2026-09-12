using Parlot.Fluent;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Parsing;

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
    /// (without consuming) at a top-level ')', ',', ';', '{', '}', a comment opener ('//' or '/*'), a newline, or
    /// end of input. When <paramref
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

            // '{' stops for the same reason '}' does: no operand grammar contains one, and an `if`/`repeat` gate
            // is followed immediately by its block body.
            if (c is ',' or ';' or '{' or '}' or '\n' or '\r') {
                break;
            }

            // The same comment terminators AtEndOfLogicalStatement recognizes: an operand span ends where a
            // trailing comment begins, so a `when`/`bind` line accepts one the way an effect line already does.
            if (c == '/' && (cursor.Offset + 1) < buffer.Length && buffer[cursor.Offset + 1] is '/' or '*') {
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
        foreach (var candidate in PuckDslVocabulary.ComparisonKindNames) {
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
        diagnostics?.ReportError(PuckDiagnosticCodes.OperandParse, $"'{text}' {error}", span);
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
            diagnostics?.ReportError(PuckDiagnosticCodes.OperandParse, $"'{text}' {error}", span);
            return new RowRefNode(text, null, span.Offset, span.Length, span.Line, span.Column);
        }
        if (tokens.Count != 1 || tokens[0] is not ValueToken.State state) {
            diagnostics?.ReportError(PuckDiagnosticCodes.RowReferenceExpected, $"expected a single state-row reference here, not an expression ('{text}')", span);
            return new RowRefNode(text, null, span.Offset, span.Length, span.Line, span.Column);
        }
        return new RowRefNode(state.Name, state.Key, span.Offset, span.Length, span.Line, span.Column);
    }

    /// <summary>Reads and eagerly resolves a row reference in the unconditional contexts (<c>countdown</c>,
    /// <c>remove</c>, <c>schedule</c>) where nothing else could follow the keyword. The whole operand span is
    /// captured, not just a name and its brackets, so text that is an expression rather than a row reference is
    /// named as such (PUCK003) instead of tripping a generic syntax error further along the line.</summary>
    private static bool TryReadRowRefOperand(ParseContext context, DiagnosticBag? diagnostics, IReadOnlySet<string>? stopKeywords, out RowRefNode? rowRef) {
        var cursor = context.Scanner.Cursor;
        SkipWhiteSpace(context);
        var start = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, start);
        var text = ScanOperandSpan(context, stopKeywords, stopAtComparator: false, out _);

        if (text.Length == 0) {
            rowRef = null;
            return false;
        }

        rowRef = ResolveRowRef(text, new SourceSpan(start, cursor.Offset - start, line, col), diagnostics);
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
    /// The walk itself is <c>ExpressionSpelling.ScanBareName</c>'s — the owner of that grammar — never a copy of it
    /// (rule 8).</summary>
    private static bool TryReadExtendedName(ParseContext context, out string name) {
        var buffer = context.Scanner.Buffer;
        var cursor = context.Scanner.Cursor;
        var start = cursor.Offset;
        var length = ExpressionSpelling.ScanBareName(text: buffer, start: start);

        if (length == 0) {
            name = string.Empty;
            return false;
        }

        name = buffer[start..(start + length)];
        cursor.Advance(length);
        return true;
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

    // Whether the next identifier-or-string token after `offset` sits on a later line indented deeper than
    // `keywordColumn` (1-based) — the continuation test a newline-terminated statement list needs to tell its own
    // wrapped operands apart from the next statement at the same nesting level.
    private static bool ContinuesOnDeeperIndentedLine(string buffer, int offset, int keywordColumn) {
        var i = offset;

        while (i < buffer.Length) {
            var c = buffer[i];

            if (char.IsWhiteSpace(c)) {
                i++;
                continue;
            }
            if (c == '/' && (i + 1) < buffer.Length && buffer[i + 1] == '/') {
                while (i < buffer.Length && buffer[i] is not ('\r' or '\n')) {
                    i++;
                }
                continue;
            }
            if (c == '/' && (i + 1) < buffer.Length && buffer[i + 1] == '*') {
                var close = buffer.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = (close < 0) ? buffer.Length : (close + 2);
                continue;
            }
            break;
        }

        if (i >= buffer.Length || !(char.IsLetter(buffer[i]) || buffer[i] is '_' or '$' or '"')) {
            return false;
        }

        var lineStart = i;
        while (lineStart > 0 && buffer[lineStart - 1] is not ('\r' or '\n')) {
            lineStart--;
        }

        return ((i - lineStart) + 1) > keywordColumn;
    }
    /// <summary>Matches one cell-kind keyword a <c>: Kind</c>/<c>as Kind</c> annotation or a <c>bind</c> admits,
    /// spelled from the enum through <see cref="PuckDslVocabulary.ComparisonKindNames"/>. Returns
    /// <see langword="null"/>, consuming nothing, when the next word is not one.</summary>
    private static string? TryMatchKindKeyword(ParseContext context) {
        foreach (var candidate in PuckDslVocabulary.ComparisonKindNames) {
            if (TryMatchKeyword(context, candidate)) {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Returns the bare word a trailing <c>: word</c> names, or <see langword="null"/> when
    /// <paramref name="text"/> does not end in one. Used to tell a mis-spelled kind annotation apart from a colon
    /// that belongs to the operand itself (a ternary, a reserved channel segment).</summary>
    private static string? TrailingColonWord(string text) {
        var trimmed = text.TrimEnd();
        var colon = trimmed.LastIndexOf(':');
        if (colon < 0 || colon == (trimmed.Length - 1)) {
            return null;
        }
        var word = trimmed[(colon + 1)..].TrimStart();
        if (word.Length == 0 || !char.IsLetter(word[0])) {
            return null;
        }
        foreach (var c in word) {
            if (!IsNameContinuationCharacter(c)) {
                return null;
            }
        }
        // A reserved channel carries its own colons inside one unbroken name ("$cell:row:key"); only a colon that
        // stands apart from the text before it can be a kind annotation.
        return char.IsWhiteSpace(trimmed[colon - 1]) ? word : null;
    }

    /// <summary>Names the cell kinds an annotation admits, for a diagnostic message.</summary>
    private static string DescribeAdmittedKinds() =>
        string.Join(" or ", PuckDslVocabulary.ComparisonKindNames.Select(static name => $"'{name}'"));

    /// <summary>Advances to the end of the current statement — the next newline, <c>;</c>, <c>,</c> or the enclosing
    /// <c>}</c>, whichever comes first, without consuming the terminator. A diagnostic that has already named the
    /// real defect resynchronizes here so the rest of the statement cannot raise a second, misleading one.</summary>
    private static void SkipToEndOfStatement(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var depth = 0;
        while (!cursor.Eof) {
            var c = cursor.Current;
            if (c is '(' or '[') {
                depth++;
            } else if (c is ')' or ']') {
                if (depth == 0) {
                    return;
                }
                depth--;
            } else if (depth == 0 && c is '\n' or '\r' or ';' or ',' or '}') {
                return;
            }
            cursor.Advance();
        }
    }
}
