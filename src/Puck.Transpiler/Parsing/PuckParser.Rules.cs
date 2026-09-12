using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Units;

namespace Puck.Transpiler.Parsing;

// `rule "name" { }` and everything inside it (§2, §3): bind/decision/option/interrupt/onNoChoice, and the effect
// statements (set/add/push/countdown/remove/schedule/transform/transaction). These bodies are parsed by a dedicated
// dispatcher rather than the shared ParseStatement, because `identifier[...]` already means an inline array
// property there (§2.2) — the two productions never share a parse context, so no backtracking is needed to
// disambiguate them.
public static partial class PuckParser {
    // Longest-first, so a scan never reads ">>=" as ">" or "<<=" as "<". Plain "=" and "+=" are matched ahead of
    // this table and carry their own statement nodes.
    private static readonly string[] CompoundAssignmentOperators = [">>=", "<<=", "-=", "*=", "/=", "%=", "&=", "|=", "^="];

    // `in` closes a `schedule <row> in <delay>` row reference; nothing else terminates one but the statement itself.
    private static readonly HashSet<string> ScheduleStopKeywords = new(StringComparer.Ordinal) { "in" };

    private static RuleBlockNode ParseRuleBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;
        SkipWhiteSpace(context);
        if (!TryReadString(context, out var name)) {
            var (nLine, nCol) = GetLineAndColumn(context.Scanner.Buffer, cursor.Offset);
            diagnostics?.ReportError(PuckDiagnosticCodes.RuleNameMissing, "Expected a quoted name after 'rule'", new SourceSpan(cursor.Offset, 1, nLine, nCol));
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
                    diagnostics?.ReportError(PuckDiagnosticCodes.DuplicateWhen, $"rule '{name}' has more than one 'when' clause", span);
                } else {
                    sawWhen = true;
                }
            }
            if (stmt is EffectStatementNode or ExpressionStatementNode or DecisionBlockNode) {
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
            diagnostics?.ReportError(PuckDiagnosticCodes.RuleWithoutEffects, $"rule '{name}' carries no effect statements", new SourceSpan(startOffset, cursor.Offset - startOffset, line, col));
        }

        var len = cursor.Offset - startOffset;
        return new RuleBlockNode(name, statements, startOffset, len, line, col);
    }

    // A rule's own wire fields. `mode = Edge` reads exactly like a cell assignment to the effect dispatcher, so
    // these names are routed to the property path first; a state row genuinely called one of them is backquoted.
    private static readonly HashSet<string> RuleBodyPropertyNames = new(StringComparer.Ordinal) {
        "name", "gate", "mode", "forEach", "zones", "bindings", "effects", "decision",
    };

    // Whether the next token is one of `names` used as a property (followed by ':', '=', '{' or '[').
    private static bool AtPropertyNamed(ParseContext context, IReadOnlySet<string> names) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;
        try {
            if (!TryReadIdentifier(context, out var name) || !names.Contains(name)) {
                return false;
            }
            SkipWhiteSpace(context);
            // ':'/'=' only: a '{' after one of these names opens the construct's own block, not a property value.
            return cursor.Current is ':' or '=';
        } finally {
            cursor.ResetPosition(saved);
        }
    }

    private static StatementNode ParseRuleBodyStatement(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        if (AtPropertyNamed(context, RuleBodyPropertyNames)) {
            var ruleProperty = TryParseGenericProperty(context, diagnostics);
            if (ruleProperty is not null) {
                return ruleProperty;
            }
        }

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
            kind = TryMatchKindKeyword(context);
        }
        if (kind is null) {
            var (kLine, kCol) = GetLineAndColumn(context.Scanner.Buffer, cursor.Offset);
            diagnostics?.ReportError(PuckDiagnosticCodes.BindKindMissing, $"'bind {name}' is missing its required ': Int' or ': Fixed' kind annotation", new SourceSpan(cursor.Offset, 1, kLine, kCol));
            kind = "Int";
        }

        SkipWhiteSpace(context);
        if (!TryConsume(context, '=')) {
            var (eLine, eCol) = GetLineAndColumn(context.Scanner.Buffer, cursor.Offset);
            diagnostics?.ReportError(PuckDiagnosticCodes.BindInitializerMissing, $"'bind {name}' is missing its '=' initializer", new SourceSpan(cursor.Offset, 1, eLine, eCol));
            var missingLen = cursor.Offset - startOffset;
            return new BindStatementNode(name, kind, string.Empty, startOffset, missingLen, line, col);
        }

        var opStart = cursor.Offset;
        var (oLine, oCol) = GetLineAndColumn(context.Scanner.Buffer, opStart);
        var text = ScanOperandSpan(context, stopKeywords: null, stopAtComparator: false, out _);
        var span = new SourceSpan(opStart, cursor.Offset - opStart, oLine, oCol);
        if (text.Length == 0) {
            diagnostics?.ReportError(PuckDiagnosticCodes.BindInitializerMissing, $"'bind {name}' initializer is empty", span);
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
            diagnostics?.ReportError(PuckDiagnosticCodes.DecisionPeriodMissing, "'decision' is missing its required 'periodSeconds'", new SourceSpan(startOffset, cursor.Offset - startOffset, line, col));
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
            SkipWhiteSpace(context);
            // `interrupt: <call-form>` is the spelling a predicate the gate grammar cannot carry decompiles to.
            if (cursor.Current is ':' or '=') {
                cursor.Advance();
                var value = ParseExpression(context);
                var propertyLen = cursor.Offset - startOffset;
                return new PropertyNode("interrupt", value, startOffset, propertyLen, line, col);
            }
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
            diagnostics?.ReportError(PuckDiagnosticCodes.DecisionStructure, "Expected a quoted name after 'option'", new SourceSpan(cursor.Offset, 1, nLine, nCol));
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
            diagnostics?.ReportError(PuckDiagnosticCodes.DecisionStructure, $"option '{name}' is missing its required 'score'", new SourceSpan(startOffset, cursor.Offset - startOffset, line, col));
        }

        var len = cursor.Offset - startOffset;
        return new OptionBlockNode(name, statements, startOffset, len, line, col);
    }

    // An option's own wire fields, for the same reason a rule body routes its own (see RuleBodyPropertyNames).
    // `gate:` is the spelling a predicate the `when` grammar cannot carry decompiles to.
    private static readonly HashSet<string> OptionBodyPropertyNames = new(StringComparer.Ordinal) {
        "name", "gate", "neighbors", "effects",
    };

    private static StatementNode ParseOptionBodyStatement(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        if (AtPropertyNamed(context, OptionBodyPropertyNames)) {
            var optionProperty = TryParseGenericProperty(context, diagnostics);
            if (optionProperty is not null) {
                return optionProperty;
            }
        }

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
            if (!TryReadRowRefOperand(context, diagnostics, stopKeywords: null, out var target) || target is null) {
                throw CreateException(context, "Expected a state row reference after 'countdown'");
            }
            var len = cursor.Offset - startOffset;
            return new CountdownStatementNode(target, startOffset, len, line, col);
        }

        if (TryMatchKeyword(context, "remove")) {
            if (!TryReadRowRefOperand(context, diagnostics, stopKeywords: null, out var target) || target is null) {
                throw CreateException(context, "Expected a state row reference after 'remove'");
            }
            var len = cursor.Offset - startOffset;
            return new RemoveCellStatementNode(target, startOffset, len, line, col);
        }

        if (TryMatchKeyword(context, "schedule")) {
            if (!TryReadRowRefOperand(context, diagnostics, ScheduleStopKeywords, out var target) || target is null) {
                throw CreateException(context, "Expected a state row reference after 'schedule'");
            }
            SkipWhiteSpace(context);
            if (!TryMatchKeyword(context, "in")) {
                throw CreateException(context, "Expected 'in' after 'schedule <row>'");
            }
            SkipWhiteSpace(context);
            var literal = ParseNumberWithOptionalUnit(context);
            var delay = LiteralToDecimal(literal.Value);
            if (literal.Unit is null) {
                diagnostics?.ReportError(PuckDiagnosticCodes.ScheduleDelayUnit, "'schedule ... in' requires a number carrying a time unit", new SourceSpan(literal.Offset, literal.Length, literal.Line, literal.Column));
            } else if (UnitConversion.TryConvert(UnitDimension.Seconds, (double)delay, literal.Unit, out var seconds)) {
                delay = (decimal)seconds;
            } else {
                var accepted = string.Join("/", UnitConversion.AcceptedUnits(UnitDimension.Seconds));
                diagnostics?.ReportError(PuckDiagnosticCodes.ScheduleDelayUnit, $"'schedule ... in' accepts {accepted}, not '{literal.Unit}'", new SourceSpan(literal.Offset, literal.Length, literal.Line, literal.Column));
            }
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

        if (TryMatchKeyword(context, "if")) {
            return ParseIfStatement(context, startOffset, line, col, diagnostics, insideTransaction);
        }

        if (TryMatchKeyword(context, "repeat")) {
            return ParseRepeatStatement(context, startOffset, line, col, diagnostics, insideTransaction);
        }

        if (TryMatchKeyword(context, "break")) {
            return new BreakStatementNode(startOffset, (cursor.Offset - startOffset), line, col);
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
            if (LongestMatchingPunctuation(context.Scanner.Buffer, cursor.Offset, CompoundAssignmentOperators) is { } compound) {
                cursor.Advance(compound.Length);
                var target = ResolveRowRef(rowRefText, rowRefSpan, diagnostics);
                var rhs = ParseRhs(context, diagnostics, allowText: false, allowSeconds: false, ownerForDiagnostic: "compound assignment");
                var len = cursor.Offset - startOffset;
                return new CompoundAssignStatementNode(target, compound[..^1], rhs, startOffset, len, line, col);
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

    // `if Gate { ... }`, with an optional `else { ... }` or `else if ...` tail. An `else if` is stored as an Else
    // holding one nested IfStatementNode, so a chain of any length is the same shape as one branch and no consumer
    // needs a separate else-if case.
    private static IfStatementNode ParseIfStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, bool insideTransaction) {
        var cursor = context.Scanner.Cursor;
        var condition = ParseGate(context, diagnostics);

        SkipWhiteSpace(context);

        if (!TryConsume(context, '{')) {
            throw CreateException(context, "Expected '{' starting an 'if' body");
        }

        var thenStatements = ParseEffectStatementList(context, diagnostics, insideTransaction);

        if (!TryConsume(context, '}')) {
            throw CreateException(context, "Expected '}' closing an 'if' body");
        }

        IReadOnlyList<StatementNode>? elseStatements = null;
        var savedPosition = cursor.Position;

        SkipWhiteSpace(context);

        if (TryMatchKeyword(context, "else")) {
            SkipWhiteSpace(context);

            var elseStart = cursor.Offset;
            var (elseLine, elseCol) = GetLineAndColumn(context.Scanner.Buffer, elseStart);

            if (TryMatchKeyword(context, "if")) {
                elseStatements = [ParseIfStatement(context, elseStart, elseLine, elseCol, diagnostics, insideTransaction)];
            } else {
                if (!TryConsume(context, '{')) {
                    throw CreateException(context, "Expected '{' or 'if' after 'else'");
                }

                elseStatements = ParseEffectStatementList(context, diagnostics, insideTransaction);

                if (!TryConsume(context, '}')) {
                    throw CreateException(context, "Expected '}' closing an 'else' body");
                }
            }
        } else {
            // Nothing was consumed by the failed keyword match, but the whitespace skip before it was: rewinding
            // keeps this statement's Length honest and leaves the next statement's own leading trivia alone.
            cursor.ResetPosition(savedPosition);
        }

        return new IfStatementNode(condition, thenStatements, elseStatements, startOffset, (cursor.Offset - startOffset), line, col);
    }

    // `repeat Count as name { ... }`.
    private static RepeatStatementNode ParseRepeatStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, bool insideTransaction) {
        var cursor = context.Scanner.Cursor;
        var count = ParseExpression(context);

        SkipWhiteSpace(context);

        if (!TryMatchKeyword(context, "as")) {
            throw CreateException(context, "Expected 'as <name>' after a 'repeat' count");
        }

        SkipWhiteSpace(context);

        if (!TryReadIdentifier(context, out var index)) {
            throw CreateException(context, "Expected an index name after 'repeat <count> as'");
        }

        SkipWhiteSpace(context);

        if (!TryConsume(context, '{')) {
            throw CreateException(context, "Expected '{' starting a 'repeat' body");
        }

        var body = ParseEffectStatementList(context, diagnostics, insideTransaction);

        if (!TryConsume(context, '}')) {
            throw CreateException(context, "Expected '}' closing a 'repeat' body");
        }

        return new RepeatStatementNode(count, index, body, startOffset, (cursor.Offset - startOffset), line, col);
    }

    private static TransactionStatementNode ParseTransactionStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, bool insideTransaction = false) {
        var cursor = context.Scanner.Cursor;

        if (insideTransaction) {
            diagnostics?.ReportError(PuckDiagnosticCodes.NestedTransaction, "nested 'transaction' is not supported", new SourceSpan(startOffset, "transaction".Length, line, col));
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
                diagnostics?.ReportError(PuckDiagnosticCodes.OnFailureStructure, "a 'transaction' may carry at most one 'onFailure' block", new SourceSpan(cursor.Offset, 9, dLine, dCol));
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
                diagnostics?.ReportError(PuckDiagnosticCodes.EffectRhsShape, $"a string literal is not valid here — '{ownerForDiagnostic}' carries no Text field", span);
            }
            return new RhsTextNode(text, start, span.Length, line, col);
        }

        if (char.IsDigit(cursor.Current) || ((cursor.Current is '-' or '+') && (char.IsDigit(cursor.PeekNext()) || cursor.PeekNext() == '.'))) {
            var saved = cursor.Position;
            var literal = ParseNumberWithOptionalUnit(context);
            if (AtEndOfLogicalStatement(context.Scanner.Buffer, cursor.Offset)) {
                if (literal.Unit == "s") {
                    if (!allowSeconds) {
                        diagnostics?.ReportError(PuckDiagnosticCodes.EffectRhsShape, $"a seconds literal is not valid here — '{ownerForDiagnostic}' carries no ValueSeconds field", new SourceSpan(literal.Offset, literal.Length, literal.Line, literal.Column));
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
