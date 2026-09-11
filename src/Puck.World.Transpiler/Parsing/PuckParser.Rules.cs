using Parlot.Fluent;
using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Parsing;

// `rule "name" { }` and everything inside it (§2, §3): bind/decision/option/interrupt/onNoChoice, and the effect
// statements (set/add/push/countdown/remove/schedule/transform/transaction). These bodies are parsed by a dedicated
// dispatcher rather than the shared ParseStatement, because `identifier[...]` already means an inline array
// property there (§2.2) — the two productions never share a parse context, so no backtracking is needed to
// disambiguate them.
public static partial class PuckParser {
    private static RuleBlockNode ParseRuleBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;
        SkipWhiteSpace(context);
        if (!TryReadString(context, out var name)) {
            var (nLine, nCol) = GetLineAndColumn(context.Scanner.Buffer, cursor.Offset);
            diagnostics?.ReportError("PUCK011", "Expected a quoted name after 'rule'", new SourceSpan(cursor.Offset, 1, nLine, nCol));
            name = string.Empty;
        }

        SkipWhiteSpace(context);
        if (!TryConsume(context, '{')) {
            throw CreateException(context, $"Expected '{{' starting body for rule '{name}'");
        }

        var statements = new List<StatementNode>();
        var sawWhen = false;
        var sawEffect = false;
        SkipWhiteSpace(context);
        while (!cursor.Eof && cursor.Current != '}') {
            var stmt = ParseRuleBodyStatement(context, diagnostics);
            if (stmt is WhenStatementNode) {
                if (sawWhen) {
                    var span = new SourceSpan(stmt.Offset, stmt.Length, stmt.Line, stmt.Column);
                    diagnostics?.ReportError("PUCK012", $"rule '{name}' has more than one 'when' clause", span);
                } else {
                    sawWhen = true;
                }
            }
            if (stmt is EffectStatementNode or ExpressionStatementNode) {
                sawEffect = true;
            }
            statements.Add(stmt);
            ConsumeSeparator(context);
            SkipWhiteSpace(context);
        }

        if (!TryConsume(context, '}')) {
            throw CreateException(context, $"Expected '}}' closing body for rule '{name}'");
        }

        if (!sawEffect) {
            diagnostics?.ReportError("PUCK026", $"rule '{name}' carries no effect statements", new SourceSpan(startOffset, cursor.Offset - startOffset, line, col));
        }

        var len = cursor.Offset - startOffset;
        return new RuleBlockNode(name, statements, startOffset, len, line, col);
    }

    private static StatementNode ParseRuleBodyStatement(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        if (TryMatchKeyword(context, "when")) {
            return ParseWhenStatement(context, startOffset, line, col, diagnostics);
        }
        if (TryMatchKeyword(context, "bind")) {
            return ParseBindStatement(context, startOffset, line, col, diagnostics);
        }
        if (TryMatchKeyword(context, "decision")) {
            return ParseDecisionBlock(context, startOffset, line, col, diagnostics);
        }

        var effect = ParseEffectStatement(context, diagnostics);
        if (effect is not null) {
            return effect;
        }

        var property = TryParseGenericProperty(context, diagnostics);
        if (property is not null) {
            return property;
        }

        throw CreateException(context, $"Unexpected token '{cursor.Current}' inside rule body");
    }

    private static BindStatementNode ParseBindStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;
        SkipWhiteSpace(context);
        if (!TryReadIdentifier(context, out var name)) {
            throw CreateException(context, "Expected a name after 'bind'");
        }

        SkipWhiteSpace(context);
        string? kind = null;
        if (TryConsume(context, ':') || TryMatchKeyword(context, "as")) {
            SkipWhiteSpace(context);
            if (TryMatchKeyword(context, "Int")) {
                kind = "Int";
            } else if (TryMatchKeyword(context, "Fixed")) {
                kind = "Fixed";
            }
        }
        if (kind is null) {
            var (kLine, kCol) = GetLineAndColumn(context.Scanner.Buffer, cursor.Offset);
            diagnostics?.ReportError("PUCK006", $"'bind {name}' is missing its required ': Int' or ': Fixed' kind annotation", new SourceSpan(cursor.Offset, 1, kLine, kCol));
            kind = "Int";
        }

        SkipWhiteSpace(context);
        if (!TryConsume(context, '=')) {
            var (eLine, eCol) = GetLineAndColumn(context.Scanner.Buffer, cursor.Offset);
            diagnostics?.ReportError("PUCK007", $"'bind {name}' is missing its '=' initializer", new SourceSpan(cursor.Offset, 1, eLine, eCol));
            var missingLen = cursor.Offset - startOffset;
            return new BindStatementNode(name, kind, string.Empty, startOffset, missingLen, line, col);
        }

        var opStart = cursor.Offset;
        var (oLine, oCol) = GetLineAndColumn(context.Scanner.Buffer, opStart);
        var text = ScanOperandSpan(context, stopKeywords: null, stopAtComparator: false, out _);
        var span = new SourceSpan(opStart, cursor.Offset - opStart, oLine, oCol);
        if (text.Length == 0) {
            diagnostics?.ReportError("PUCK007", $"'bind {name}' initializer is empty", span);
        } else {
            ValidateOperandText(text, span, diagnostics);
        }

        var len = cursor.Offset - startOffset;
        return new BindStatementNode(name, kind, text, startOffset, len, line, col);
    }

    private static DecisionBlockNode ParseDecisionBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;
        SkipWhiteSpace(context);
        if (!TryConsume(context, '{')) {
            throw CreateException(context, "Expected '{' starting 'decision' body");
        }

        var statements = new List<StatementNode>();
        var sawPeriodSeconds = false;
        SkipWhiteSpace(context);
        while (!cursor.Eof && cursor.Current != '}') {
            var stmt = ParseDecisionBodyStatement(context, diagnostics);
            if (stmt is PropertyNode { Name: "periodSeconds" }) {
                sawPeriodSeconds = true;
            }
            statements.Add(stmt);
            ConsumeSeparator(context);
            SkipWhiteSpace(context);
        }

        if (!TryConsume(context, '}')) {
            throw CreateException(context, "Expected '}' closing 'decision' body");
        }

        if (!sawPeriodSeconds) {
            diagnostics?.ReportError("PUCK029", "'decision' is missing its required 'periodSeconds'", new SourceSpan(startOffset, cursor.Offset - startOffset, line, col));
        }

        var len = cursor.Offset - startOffset;
        return new DecisionBlockNode(statements, startOffset, len, line, col);
    }

    private static StatementNode ParseDecisionBodyStatement(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        if (TryMatchKeyword(context, "option")) {
            return ParseOptionBlock(context, startOffset, line, col, diagnostics);
        }
        if (TryMatchKeyword(context, "interrupt")) {
            var predicate = ParseGate(context, diagnostics);
            var len = cursor.Offset - startOffset;
            return new InterruptStatementNode(predicate, startOffset, len, line, col);
        }
        if (TryMatchKeyword(context, "onNoChoice")) {
            SkipWhiteSpace(context);
            if (!TryConsume(context, '{')) {
                throw CreateException(context, "Expected '{' starting 'onNoChoice' body");
            }
            var effects = ParseEffectStatementList(context, diagnostics);
            if (!TryConsume(context, '}')) {
                throw CreateException(context, "Expected '}' closing 'onNoChoice' body");
            }
            var len = cursor.Offset - startOffset;
            return new OnNoChoiceBlockNode(effects, startOffset, len, line, col);
        }

        var property = TryParseGenericProperty(context, diagnostics);
        if (property is not null) {
            return property;
        }

        throw CreateException(context, $"Unexpected token '{cursor.Current}' inside 'decision' body");
    }

    private static OptionBlockNode ParseOptionBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;
        SkipWhiteSpace(context);
        if (!TryReadString(context, out var name)) {
            var (nLine, nCol) = GetLineAndColumn(context.Scanner.Buffer, cursor.Offset);
            diagnostics?.ReportError("PUCK013", "Expected a quoted name after 'option'", new SourceSpan(cursor.Offset, 1, nLine, nCol));
            name = string.Empty;
        }

        SkipWhiteSpace(context);
        if (!TryConsume(context, '{')) {
            throw CreateException(context, $"Expected '{{' starting body for option '{name}'");
        }

        var statements = new List<StatementNode>();
        var sawScore = false;
        SkipWhiteSpace(context);
        while (!cursor.Eof && cursor.Current != '}') {
            var stmt = ParseOptionBodyStatement(context, diagnostics);
            if (stmt is ScoreStatementNode) {
                sawScore = true;
            }
            statements.Add(stmt);
            ConsumeSeparator(context);
            SkipWhiteSpace(context);
        }

        if (!TryConsume(context, '}')) {
            throw CreateException(context, $"Expected '}}' closing body for option '{name}'");
        }

        if (!sawScore) {
            diagnostics?.ReportError("PUCK013", $"option '{name}' is missing its required 'score'", new SourceSpan(startOffset, cursor.Offset - startOffset, line, col));
        }

        var len = cursor.Offset - startOffset;
        return new OptionBlockNode(name, statements, startOffset, len, line, col);
    }

    private static StatementNode ParseOptionBodyStatement(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        if (TryMatchKeyword(context, "when")) {
            return ParseWhenStatement(context, startOffset, line, col, diagnostics);
        }
        if (TryMatchKeyword(context, "score")) {
            SkipWhiteSpace(context);
            TryConsume(context, ':');
            TryConsume(context, '=');
            var opStart = cursor.Offset;
            var (oLine, oCol) = GetLineAndColumn(context.Scanner.Buffer, opStart);
            var text = ScanOperandSpan(context, stopKeywords: null, stopAtComparator: false, out _);
            var span = new SourceSpan(opStart, cursor.Offset - opStart, oLine, oCol);
            ValidateOperandText(text, span, diagnostics);
            var len = cursor.Offset - startOffset;
            return new ScoreStatementNode(text, startOffset, len, line, col);
        }

        var effect = ParseEffectStatement(context, diagnostics);
        if (effect is not null) {
            return effect;
        }

        var property = TryParseGenericProperty(context, diagnostics);
        if (property is not null) {
            return property;
        }

        throw CreateException(context, $"Unexpected token '{cursor.Current}' inside option body");
    }

    /// <summary>Tries to parse one effect statement (§2): <c>push</c>, <c>countdown</c>, <c>remove</c>,
    /// <c>schedule</c>, <c>transform</c>, <c>transaction</c>, a <c>row[key] (= | +=) rhs</c> cell assignment, or a
    /// call-form statement (<c>generate(...)</c> and every <c>Puck.World.Schema</c> extension arm). Returns
    /// <see langword="null"/> — consuming nothing — when none of these match, so the caller can fall back to an
    /// ordinary property.</summary>
    private static StatementNode? ParseEffectStatement(ParseContext context, DiagnosticBag? diagnostics, bool insideTransaction = false) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        if (TryMatchKeyword(context, "push")) {
            SkipWhiteSpace(context);
            if (!TryReadIdentifier(context, out var rowName)) {
                throw CreateException(context, "Expected a row name after 'push'");
            }
            SkipWhiteSpace(context);
            if (!TryConsume(context, '=')) {
                throw CreateException(context, $"Expected '=' after 'push {rowName}'");
            }
            var rhs = ParseRhs(context, diagnostics, allowText: false, allowSeconds: false, ownerForDiagnostic: "push");
            var len = cursor.Offset - startOffset;
            return new PushStatementNode(rowName, rhs, startOffset, len, line, col);
        }

        if (TryMatchKeyword(context, "countdown")) {
            if (!TryReadRowRef(context, diagnostics, out var target) || target is null) {
                throw CreateException(context, "Expected a state row reference after 'countdown'");
            }
            var len = cursor.Offset - startOffset;
            return new CountdownStatementNode(target, startOffset, len, line, col);
        }

        if (TryMatchKeyword(context, "remove")) {
            if (!TryReadRowRef(context, diagnostics, out var target) || target is null) {
                throw CreateException(context, "Expected a state row reference after 'remove'");
            }
            var len = cursor.Offset - startOffset;
            return new RemoveCellStatementNode(target, startOffset, len, line, col);
        }

        if (TryMatchKeyword(context, "schedule")) {
            if (!TryReadRowRef(context, diagnostics, out var target) || target is null) {
                throw CreateException(context, "Expected a state row reference after 'schedule'");
            }
            SkipWhiteSpace(context);
            if (!TryMatchKeyword(context, "in")) {
                throw CreateException(context, "Expected 'in' after 'schedule <row>'");
            }
            SkipWhiteSpace(context);
            var literal = ParseNumberWithOptionalUnit(context);
            if (literal.Unit != "s") {
                diagnostics?.ReportError("PUCK010", "'schedule ... in' requires a number carrying the 's' suffix", new SourceSpan(literal.Offset, literal.Length, literal.Line, literal.Column));
            }
            var delay = LiteralToDecimal(literal.Value);
            var len = cursor.Offset - startOffset;
            return new ScheduleStatementNode(target, delay, startOffset, len, line, col);
        }

        if (TryMatchKeyword(context, "transform")) {
            SkipWhiteSpace(context);
            if (!TryReadIdentifier(context, out var rowName)) {
                throw CreateException(context, "Expected a local name after 'transform'");
            }
            SkipWhiteSpace(context);
            if (!TryConsume(context, '=')) {
                throw CreateException(context, $"Expected '=' after 'transform {rowName}'");
            }
            SkipWhiteSpace(context);
            var callStart = cursor.Offset;
            var (callLine, callCol) = GetLineAndColumn(context.Scanner.Buffer, callStart);
            if (!TryReadIdentifier(context, out var callName)) {
                throw CreateException(context, "Expected a transform call after '='");
            }
            SkipWhiteSpace(context);
            if (cursor.Current != '(') {
                throw CreateException(context, $"Expected '(' after transform name '{callName}'");
            }
            var call = ParseCallExpression(context, callName, callStart, callLine, callCol);
            var len = cursor.Offset - startOffset;
            return new TransformStatementNode(rowName, call, startOffset, len, line, col);
        }

        if (TryMatchKeyword(context, "transaction")) {
            return ParseTransactionStatement(context, startOffset, line, col, diagnostics, insideTransaction);
        }

        var savedPosition = cursor.Position;

        if (TryReadRowRefSpanRaw(context, out var rowRefText, out var rowRefSpan)) {
            SkipWhiteSpace(context);
            if (cursor.Current == '+' && cursor.PeekNext() == '=') {
                cursor.Advance(2);
                var target = ResolveRowRef(rowRefText, rowRefSpan, diagnostics);
                var rhs = ParseRhs(context, diagnostics, allowText: false, allowSeconds: true, ownerForDiagnostic: "addState");
                var len = cursor.Offset - startOffset;
                return new AddCellStatementNode(target, rhs, startOffset, len, line, col);
            }
            if (cursor.Current == '=') {
                cursor.Advance();
                var target = ResolveRowRef(rowRefText, rowRefSpan, diagnostics);
                var rhs = ParseRhs(context, diagnostics, allowText: true, allowSeconds: true, ownerForDiagnostic: "setState");
                var len = cursor.Offset - startOffset;
                return new SetCellStatementNode(target, rhs, startOffset, len, line, col);
            }
            cursor.ResetPosition(savedPosition);
        }

        if (TryReadIdentifierOrString(context, out var callId)) {
            SkipWhiteSpace(context);
            if (cursor.Current == '(') {
                var call = ParseCallExpression(context, callId, startOffset, line, col);
                var len = cursor.Offset - startOffset;
                return new ExpressionStatementNode(call, startOffset, len, line, col);
            }
            cursor.ResetPosition(savedPosition);
            return null;
        }

        return null;
    }

    private static List<StatementNode> ParseEffectStatementList(ParseContext context, DiagnosticBag? diagnostics, bool insideTransaction = false) {
        var statements = new List<StatementNode>();
        var cursor = context.Scanner.Cursor;
        SkipWhiteSpace(context);
        while (!cursor.Eof && cursor.Current != '}') {
            var stmt = ParseEffectStatement(context, diagnostics, insideTransaction);
            if (stmt is null) {
                throw CreateException(context, $"Expected an effect statement, found '{cursor.Current}'");
            }
            statements.Add(stmt);
            ConsumeSeparator(context);
            SkipWhiteSpace(context);
        }
        return statements;
    }

    private static TransactionStatementNode ParseTransactionStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, bool insideTransaction = false) {
        var cursor = context.Scanner.Cursor;

        if (insideTransaction) {
            diagnostics?.ReportError("PUCK019", "nested 'transaction' is not supported", new SourceSpan(startOffset, "transaction".Length, line, col));
        }

        SkipWhiteSpace(context);
        if (!TryConsume(context, '{')) {
            throw CreateException(context, "Expected '{' starting 'transaction' body");
        }
        var mainEffects = ParseEffectStatementList(context, diagnostics, insideTransaction: true);
        if (!TryConsume(context, '}')) {
            throw CreateException(context, "Expected '}' closing 'transaction' body");
        }

        IReadOnlyList<StatementNode>? onFailureEffects = null;
        SkipWhiteSpace(context);
        if (TryMatchKeyword(context, "onFailure")) {
            SkipWhiteSpace(context);
            if (!TryConsume(context, '{')) {
                throw CreateException(context, "Expected '{' starting 'onFailure' body");
            }
            onFailureEffects = ParseEffectStatementList(context, diagnostics, insideTransaction: true);
            if (!TryConsume(context, '}')) {
                throw CreateException(context, "Expected '}' closing 'onFailure' body");
            }

            SkipWhiteSpace(context);
            if (TryMatchKeyword(context, "onFailure")) {
                var (dLine, dCol) = GetLineAndColumn(context.Scanner.Buffer, cursor.Offset);
                diagnostics?.ReportError("PUCK014", "a 'transaction' may carry at most one 'onFailure' block", new SourceSpan(cursor.Offset, 9, dLine, dCol));
                SkipWhiteSpace(context);
                if (TryConsume(context, '{')) {
                    ParseEffectStatementList(context, diagnostics, insideTransaction: true);
                    TryConsume(context, '}');
                }
            }
        }

        var len = cursor.Offset - startOffset;
        return new TransactionStatementNode(mainEffects, onFailureEffects, startOffset, len, line, col);
    }

    /// <summary>Parses a <c>setState</c>/<c>addState</c>/<c>push</c> right-hand side (§2.3): a string literal, a
    /// number carrying the <c>s</c> unit and nothing else on the statement, or opaque operand text. Reports PUCK009
    /// when the shape reached is not one <paramref name="ownerForDiagnostic"/>'s own JSON fields can carry.</summary>
    private static RhsNode ParseRhs(ParseContext context, DiagnosticBag? diagnostics, bool allowText, bool allowSeconds, string ownerForDiagnostic) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, cursor.Offset);

        if (cursor.Current == '"') {
            var start = cursor.Offset;
            if (!TryReadString(context, out var text)) {
                throw CreateException(context, "Malformed string literal");
            }
            var span = new SourceSpan(start, cursor.Offset - start, line, col);
            if (!allowText) {
                diagnostics?.ReportError("PUCK009", $"a string literal is not valid here — '{ownerForDiagnostic}' carries no Text field", span);
            }
            return new RhsTextNode(text, start, span.Length, line, col);
        }

        if (char.IsDigit(cursor.Current) || ((cursor.Current is '-' or '+') && (char.IsDigit(cursor.PeekNext()) || cursor.PeekNext() == '.'))) {
            var saved = cursor.Position;
            var literal = ParseNumberWithOptionalUnit(context);
            if (AtEndOfLogicalStatement(context.Scanner.Buffer, cursor.Offset)) {
                if (literal.Unit == "s") {
                    if (!allowSeconds) {
                        diagnostics?.ReportError("PUCK009", $"a seconds literal is not valid here — '{ownerForDiagnostic}' carries no ValueSeconds field", new SourceSpan(literal.Offset, literal.Length, literal.Line, literal.Column));
                    }
                    return new RhsSecondsNode(LiteralToDecimal(literal.Value), literal.Offset, literal.Length, literal.Line, literal.Column);
                }
                if (literal.Unit is null) {
                    var text = context.Scanner.Buffer[literal.Offset..(literal.Offset + literal.Length)];
                    return new RhsOperandNode(text, literal.Offset, literal.Length, literal.Line, literal.Column);
                }
            }
            cursor.ResetPosition(saved);
        }

        var opStart = cursor.Offset;
        var (oLine, oCol) = GetLineAndColumn(context.Scanner.Buffer, opStart);
        var opText = ScanOperandSpan(context, stopKeywords: null, stopAtComparator: false, out _);
        var opSpan = new SourceSpan(opStart, cursor.Offset - opStart, oLine, oCol);
        ValidateOperandText(opText, opSpan, diagnostics);
        return new RhsOperandNode(opText, opStart, opSpan.Length, oLine, oCol);
    }

    /// <summary>Reads one ordinary <c>name: value</c>/<c>name = value</c>/<c>name [ ... ]</c>/<c>name { ... }</c>
    /// property — the DSL's existing generic property grammar, plus a backquoted name (for e.g. an authored
    /// <c>`$replace`: true</c> basis-merge override, which is not itself new sugar — §0). Returns
    /// <see langword="null"/> — consuming nothing — when the next token is not a name at all.</summary>
    private static PropertyNode? TryParseGenericProperty(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        string name;
        if (cursor.Current == '`') {
            if (!TryReadBackquotedName(context, out name)) {
                return null;
            }
        } else if (!TryReadIdentifier(context, out name)) {
            return null;
        }

        SkipWhiteSpace(context);
        if (cursor.Current is ':' or '=') {
            cursor.Advance();
            var value = ParseExpression(context);
            var len = cursor.Offset - startOffset;
            return new PropertyNode(name, value, startOffset, len, line, col);
        }
        if (cursor.Current == '[') {
            var arr = ParseArrayExpression(context);
            var len = cursor.Offset - startOffset;
            return new PropertyNode(name, arr, startOffset, len, line, col);
        }
        if (cursor.Current == '{') {
            var obj = ParseObjectExpression(context);
            var len = cursor.Offset - startOffset;
            return new PropertyNode(name, obj, startOffset, len, line, col);
        }

        cursor.ResetPosition(saved);
        return null;
    }
}
