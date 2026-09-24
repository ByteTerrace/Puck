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
    private static readonly HashSet<string> ComparisonStopOperators = new(comparer: StringComparer.Ordinal) { "==", "!=", "<=", ">=", "<", ">" };
    private static readonly HashSet<string> GateReservedWords = new(comparer: StringComparer.Ordinal) { "and", "or", "not", "as" };

    /// <summary>Scans one contiguous span of opaque operand text: brackets and parentheses are tracked as one
    /// combined depth counter, a backquoted span and a string literal are passed through uninterpreted, and scanning always stops
    /// (without consuming) at a top-level ')', ',', ';', '{', '}', a comment opener ('//' or '/*'), a newline, or
    /// end of input. When <paramref
    /// name="stopKeywords"/> names a bare word found at a word boundary, scanning also stops there. When <paramref
    /// name="stopAtComparator"/>, a top-level comparison operator (from <see cref="ComparisonStopOperators"/>) also
    /// stops the scan without being consumed — <paramref name="sawComparator"/> reports whether that happened, so a
    /// second stop of this kind (chained comparisons) is diagnosable by the caller.</summary>
    private static string ScanOperandSpan(ParseContext context, IReadOnlySet<string>? stopKeywords, bool stopAtComparator, out bool sawComparator) {
        var cursor = context.Scanner.Cursor;
        var buffer = context.Scanner.Buffer;

        SkipWhiteSpace(context: context);
        var start = cursor.Offset;
        var depth = 0;

        sawComparator = false;

        while (!cursor.Eof) {
            var c = cursor.Current;

            if (c == '`') {
                cursor.Advance();
                while (
                    !cursor.Eof &&
                    (cursor.Current != '`')
                ) {
                    cursor.Advance();
                }
                if (!cursor.Eof) {
                    cursor.Advance();
                }
                continue;
            }

            // An interpolated string is an atom of the operand, and its holes hold brackets and braces of their own.
            if ((c is '"' or '$') && (StringLiteralEnd(buffer: buffer, offset: cursor.Offset) is var closed and > 0)) {
                cursor.Advance(count: (closed - cursor.Offset));
                continue;
            }

            if (c is '(' or '[') {
                depth++;
                cursor.Advance();
                continue;
            }
            if (c is ')' or ']') {
                if (
                    (c == ')') &&
                    (depth == 0)
                ) {
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
            // trailing comment begins, so a `when`/`local` line accepts one the way an effect line already does.
            if (
                (c == '/') &&
                ((cursor.Offset + 1) < buffer.Length) &&
                (buffer[(cursor.Offset + 1)] is '/' or '*')
            ) {
                break;
            }

            if (stopAtComparator) {
                var matched = LongestMatchingPunctuation(
                    buffer,
                    cursor.Offset,
                    ComparatorPunctuation
                );

                if (matched is not null) {
                    if (ComparisonStopOperators.Contains(item: matched)) {
                        sawComparator = true;
                        break;
                    }
                    cursor.Advance(count: matched.Length);
                    continue;
                }
            }

            if (
                (stopKeywords is not null) &&
                (MatchingWordAt(
                buffer,
                cursor.Offset,
                stopKeywords
            ) is not null)
            ) {
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
        var matched = LongestMatchingPunctuation(
            context.Scanner.Buffer,
            cursor.Offset,
            ComparatorPunctuation
        );

        if (
            (matched is null) ||
            !ComparisonStopOperators.Contains(item: matched)
        ) {
            throw CreateException(
                context: context,
                message: "Expected a comparison operator ('==', '!=', '<', '<=', '>', '>=')"
            );
        }
        cursor.Advance(count: matched.Length);
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
            if (!trimmed.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: candidate
            )) {
                continue;
            }
            var beforeWord = (trimmed.Length - candidate.Length);

            if (
                (beforeWord > 0) &&
                IdentifierSpelling.IsNameCharacter(character: trimmed[(beforeWord - 1)])
            ) {
                continue;
            }
            var head = trimmed[..beforeWord].TrimEnd();

            if (!head.EndsWith(value: ':')) {
                continue;
            }
            exprText = head[..^1].TrimEnd();
            kind = candidate;
            return true;
        }
        return false;
    }
    private static OperandExpressionNode ValidatedOperand(string text, SourceSpan span, DiagnosticBag? diagnostics) =>
        ValidateOperand(diagnostics: diagnostics, operand: CreateOperand(span: span, text: text));
    // Reports an operand's refusal once, here, and returns the operand marked as reported when it was.
    private static OperandExpressionNode ValidateOperand(OperandExpressionNode operand, DiagnosticBag? diagnostics) {
        var respelled = ExpressionSpelling.ToSourceDialect(text: operand.Text);

        if (!string.Equals(a: respelled, b: operand.Text, comparisonType: StringComparison.Ordinal)) {
            diagnostics?.ReportError(code: PuckDiagnosticCodes.OperandColonChannel,
                message: $"'{operand.Text}' spells a reserved channel with colons; write '{respelled}'", span: operand.Span);
        } else if ((operand.Syntax is null) && (diagnostics is not null)) {
            diagnostics.ReportError(code: PuckDiagnosticCodes.OperandParse,
                message: $"'{operand.Text}' {operand.SyntaxError}", span: operand.Span);

            return (operand with { SyntaxErrorReported = true });
        }

        return operand;
    }
    /// <summary>Reads a row reference span — a name (extended for reserved <c>$</c>-channels and a folded
    /// <c>$zones[...]</c> selector, §9-A8) followed by zero or more immediately adjacent, balanced <c>[...]</c>
    /// groups — without resolving or validating it. Used where the caller must look ahead (e.g. to see whether
    /// '=' or '+=' follows) before committing to a row-reference interpretation, so no diagnostic is reported on a
    /// span the caller ends up abandoning.</summary>
    private static bool TryReadRowRefSpanRaw(ParseContext context, out string text, out SourceSpan span) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        var start = cursor.Offset;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: start
        );

        if (!TryReadName(admitted: NameForms.Extended, context: context, spelling: out _, text: out _)) {
            text = string.Empty;
            span = default;
            return false;
        }

        while (!cursor.Eof) {
            if (cursor.Current == '[') {
                var depth = 0;

                do {
                    if (cursor.Current == '[') {
                        depth++;
                    } else if (cursor.Current == ']') {
                        depth--;
                    }
                    cursor.Advance();
                } while (!cursor.Eof && (depth > 0));
            } else if (cursor.Current == '.') {
                var dotPos = cursor.Position;

                cursor.Advance();
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out _)) {
                    cursor.ResetPosition(position: dotPos);
                    break;
                }
            } else {
                break;
            }
        }

        text = context.Scanner.Buffer[start..cursor.Offset];
        span = new SourceSpan(
            start,
            (cursor.Offset - start),
            line,
            col
        );
        return true;
    }
    /// <summary>Resolves a row-reference span read by <see cref="TryReadRowRefSpanRaw"/> through
    /// <c>ExpressionSpelling.TryParse</c>, requiring exactly one <c>State</c> token (PUCK003 otherwise; PUCK002 when
    /// the span does not even parse). The token's own <c>Name</c>/<c>Key</c> become the result — the parser never
    /// re-derives that split itself.</summary>
    private static RowRefNode ResolveRowRef(string text, SourceSpan span, DiagnosticBag? diagnostics) {
        // `row[$"…"]` computes its key at lowering, as the same atom does in a read; the row is resolved alone.
        if (
            (text.IndexOf(comparisonType: StringComparison.Ordinal, value: "[$\"") is var open and > 0) &&
            text.EndsWith(comparisonType: StringComparison.Ordinal, value: "\"]") &&
            (text.IndexOf(value: '[') == open)
        ) {
            var keyText = text[(open + 1)..^1];
            var row = ResolveRowRef(
                diagnostics: diagnostics,
                span: span,
                text: text[..open]
            );

            if (TryParseKeyAtom(atom: out var atom, text: keyText)) {
                return new RowRefNode(
                    row.Name,
                    keyText,
                    span.Offset,
                    span.Length,
                    span.Line,
                    span.Column,
                    KeyAtom: atom
                );
            }
        }
        if (text.Contains(value: "].")) {
            return new RowRefNode(
                text,
                null,
                span.Offset,
                span.Length,
                span.Line,
                span.Column
            );
        }
        // A name a nested module instance declares, `outer.inner.name`: the scope reads the chain through its
        // instances (DocumentScope.TryQualify), which is lowering's to resolve or refuse.
        if ((QualifiedName.Parse(text: text) is { Segments.Count: > 2 } nested) && nested.Segments.All(predicate: static part => IdentifierSpelling.IsName(text: part))) {
            return new RowRefNode(
                nested.Head,
                nested.Tail,
                span.Offset,
                span.Length,
                span.Line,
                span.Column,
                FieldAccess: true
            );
        }

        if (!ExpressionSpelling.TryParse(
            error: out var error,
            program: out var parsed,
            text: text
        )) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.OperandParse,
                message: $"'{text}' {error}",
                span: span
            );
            return new RowRefNode(
                text,
                null,
                span.Offset,
                span.Length,
                span.Line,
                span.Column
            );
        }
        if (parsed.Instructions is not [{ Payload: InstructionPayload.State state }]) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.RowReferenceExpected,
                message: $"expected a single state-row reference here, not an expression ('{text}')",
                span: span
            );
            return new RowRefNode(
                text,
                null,
                span.Offset,
                span.Length,
                span.Line,
                span.Column
            );
        }
        // A dotted row reference is contextual source sugar. ExpressionSpelling preserves it as a structurally
        // typed lexical pool field so expression IR prints and parses bijectively; the world emitter still needs
        // the original two pieces here so its active binding scope can distinguish `piece.rank` from the existing
        // `row.key` spelling. Static `pool[slot].field` references took the early path above and remain indivisible.
        if ((state.Name.PoolField is { Binding: { } binding, Pool: null, Slot: null } poolField) && (state.Key is null)) {
            return new RowRefNode(
                binding,
                poolField.Field,
                span.Offset,
                span.Length,
                span.Line,
                span.Column,
                FieldAccess: true
            );
        }
        return new RowRefNode(
            state.Name.Spelling,
            state.Key?.Spelling,
            span.Offset,
            span.Length,
            span.Line,
            span.Column
        );
    }
    // A key that does not read as one interpolated string falls back to the ordinary resolution, which reports it.
    private static bool TryParseKeyAtom(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out InterpolatedStringNode? atom) {
        try {
            atom = (ParseExpression(source: text) as InterpolatedStringNode);
        } catch (PuckParseException) {
            atom = null;
        }

        return (atom is not null);
    }
    /// <summary>Reads and eagerly resolves a row reference in the unconditional contexts (<c>remove</c>,
    /// <c>schedule</c>) where nothing else could follow the keyword. The whole operand span is
    /// captured, not just a name and its brackets, so text that is an expression rather than a row reference is
    /// named as such (PUCK003) instead of tripping a generic syntax error further along the line.</summary>
    private static bool TryReadRowRefOperand(ParseContext context, DiagnosticBag? diagnostics, IReadOnlySet<string>? stopKeywords, out RowRefNode? rowRef) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        var start = cursor.Offset;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: start
        );
        var text = ScanOperandSpan(
            context,
            stopKeywords,
            stopAtComparator: false,
            out _
        );

        if (text.Length == 0) {
            rowRef = null;
            return false;
        }

        rowRef = ResolveRowRef(
            text,
            new SourceSpan(
                start,
                (cursor.Offset - start),
                line,
                col
            ),
            diagnostics
        );
        return true;
    }
    private static bool TryReadBackquotedName(ParseContext context, out string name) {
        var cursor = context.Scanner.Cursor;

        if (
            cursor.Eof ||
            (cursor.Current != '`')
        ) {
            name = string.Empty;
            return false;
        }
        cursor.Advance();
        var start = cursor.Offset;

        while (
            !cursor.Eof &&
            (cursor.Current != '`')
        ) {
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
    private static string? LongestMatchingPunctuation(string buffer, int offset, string[] candidates) {
        foreach (var candidate in candidates) {
            if (
                ((buffer.Length - offset) >= candidate.Length) &&
                (string.CompareOrdinal(
                buffer,
                offset,
                candidate,
                0,
                candidate.Length
            ) == 0)
            ) {
                return candidate;
            }
        }
        return null;
    }
    // A bare-word match at `offset`: the preceding character (when any) and the character right after the
    // candidate must both be outside a name, so "android" never spuriously matches "and".
    private static string? MatchingWordAt(string buffer, int offset, IReadOnlySet<string> candidates) {
        if (
            (offset > 0) &&
            IdentifierSpelling.IsNameCharacter(character: buffer[(offset - 1)])
        ) {
            return null;
        }
        foreach (var candidate in candidates) {
            if ((buffer.Length - offset) < candidate.Length) {
                continue;
            }
            if (string.CompareOrdinal(
                buffer,
                offset,
                candidate,
                0,
                candidate.Length
            ) != 0) {
                continue;
            }
            var after = (offset + candidate.Length);

            if (
                (after < buffer.Length) &&
                IdentifierSpelling.IsPart(character: buffer[after])
            ) {
                continue;
            }
            return candidate;
        }
        return null;
    }
    // Whether nothing else follows `identifier` on its own logical line (only spaces/tabs, then a newline, a line
    // comment, '}', ';', ',', or end of input) — used to recognize a bare keyword statement (e.g. `solid`, §4.2)
    // without the general whitespace skip first erasing the line boundary a newline-terminated grammar depends on.
    private static bool AtEndOfLogicalStatement(string buffer, int offset) {
        var i = offset;

        while (
            (i < buffer.Length) &&
            ((buffer[i] == ' ') || (buffer[i] == '\t'))
        ) {
            i++;
        }
        if (i >= buffer.Length) {
            return true;
        }
        if (buffer[i] is '\r' or '\n' or '}' or ';' or ',') {
            return true;
        }
        return (
            ((i + 1) < buffer.Length) &&
            (buffer[i] == '/') &&
            (buffer[(i + 1)] == '/')
        );
    }
    // Whether the next identifier-or-string token after `offset` sits on a later line indented deeper than
    // `keywordColumn` (1-based) — the continuation test a newline-terminated statement list needs to tell its own
    // wrapped operands apart from the next statement at the same nesting level.
    private static bool ContinuesOnDeeperIndentedLine(string buffer, int offset, int keywordColumn) {
        var i = offset;

        while (i < buffer.Length) {
            var c = buffer[i];

            if (char.IsWhiteSpace(c: c)) {
                i++;
                continue;
            }
            if (
                (c == '/') &&
                ((i + 1) < buffer.Length) &&
                (buffer[(i + 1)] == '/')
            ) {
                while (
                    (i < buffer.Length) &&
                    (buffer[i] is not ('\r' or '\n'))
                ) {
                    i++;
                }
                continue;
            }
            if (
                (c == '/') &&
                ((i + 1) < buffer.Length) &&
                (buffer[(i + 1)] == '*')
            ) {
                var close = buffer.IndexOf(
                    comparisonType: StringComparison.Ordinal,
                    startIndex: (i + 2),
                    value: "*/"
                );

                i = ((close < 0)
                    ? buffer.Length
                    : (close + 2)
                );
                continue;
            }
            break;
        }

        if (
            (i >= buffer.Length) ||
            !(IdentifierSpelling.IsStart(character: buffer[i]) || (buffer[i] is IdentifierSpelling.Sigil or '"'))
        ) {
            return false;
        }

        var lineStart = i;

        while (
            (lineStart > 0) &&
            (buffer[(lineStart - 1)] is not ('\r' or '\n'))
        ) {
            lineStart--;
        }

        return (((i - lineStart) + 1) > keywordColumn);
    }
    /// <summary>Matches one cell-kind keyword a <c>: Kind</c>/<c>as Kind</c> annotation or a <c>local</c> admits,
    /// spelled from the enum through <see cref="PuckDslVocabulary.ComparisonKindNames"/>. Returns
    /// <see langword="null"/>, consuming nothing, when the next word is not one.</summary>
    private static string? TryMatchKindKeyword(ParseContext context) {
        foreach (var candidate in PuckDslVocabulary.ComparisonKindNames) {
            if (TryMatchKeyword(
                context: context,
                keyword: candidate
            )) {
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
        var colon = trimmed.LastIndexOf(value: ':');

        if (
            (colon < 0) ||
            (colon == (trimmed.Length - 1))
        ) {
            return null;
        }
        var word = trimmed[(colon + 1)..].TrimStart();

        if (!IdentifierSpelling.IsIdentifier(text: word)) {
            return null;
        }
        // A reserved channel carries its own colons inside one unbroken name ("$cell:row:key"); only a colon that
        // stands apart from the text before it can be a kind annotation.
        return (char.IsWhiteSpace(c: trimmed[(colon - 1)])
            ? word
            : null
        );
    }
    /// <summary>Names the cell kinds an annotation admits, for a diagnostic message.</summary>
    private static string DescribeAdmittedKinds() =>
        string.Join(
            separator: " or ",
            values: PuckDslVocabulary.ComparisonKindNames.Select(selector: static name => $"'{name}'")
        );
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
            } else if (
                (depth == 0) &&
                (c is '\n' or '\r' or ';' or ',' or '}')
            ) {
                return;
            }
            cursor.Advance();
        }
    }
}
