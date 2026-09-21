using Parlot.Fluent;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Units;

namespace Puck.Transpiler.Parsing;

// `rule "name" { }` and everything inside it (§2, §3): local/decision/option/interrupt/onNoChoice, and the effect
// statements (set/add/push/remove/schedule/transform/transaction). These bodies are parsed by a dedicated
// dispatcher rather than the shared ParseStatement, because `identifier[...]` already means an inline array
// property there (§2.2) — the two productions never share a parse context, so no backtracking is needed to
// disambiguate them.
public static partial class PuckParser {
    // Longest-first, so a scan never reads ">>=" as ">" or "<<=" as "<". Plain "=" and "+=" are matched ahead of
    // this table and carry their own statement nodes.
    private static readonly string[] CompoundAssignmentOperators = [">>=", "<<=", "-=", "*=", "/=", "%=", "&=", "|=", "^="];
    // `in` closes a `schedule <row> in <delay>` row reference; nothing else terminates one but the statement itself.
    private static readonly HashSet<string> ScheduleStopKeywords = new(comparer: StringComparer.Ordinal) { "in" };

    // The names a block declares with `local` at its own depth, read ahead of the block's statements because a gate
    // may read a local declared after it. A local of an enclosing scope stays in force.
    private static HashSet<string> DeclaredLocals(string buffer, int offset) {
        var names = new HashSet<string>(
            collection: (ExpressionSpelling.CurrentLocals ?? ((IReadOnlySet<string>)new HashSet<string>())),
            comparer: StringComparer.Ordinal
        );
        var depth = 0;

        for (var index = offset; (index < buffer.Length); index++) {
            var character = buffer[index];

            if (character == '"') {
                for (index++; ((index < buffer.Length) && (buffer[index] != '"')); index++) {
                    if (buffer[index] == '\\') {
                        index++;
                    }
                }
            } else if ((character == '/') && ((index + 1) < buffer.Length) && (buffer[(index + 1)] == '/')) {
                while ((index < buffer.Length) && (buffer[index] != '\n')) {
                    index++;
                }
            } else if ((character == '/') && ((index + 1) < buffer.Length) && (buffer[(index + 1)] == '*')) {
                var close = buffer.IndexOf(
                    comparisonType: StringComparison.Ordinal,
                    startIndex: (index + 2),
                    value: "*/"
                );

                index = ((close < 0)
                    ? buffer.Length
                    : (close + 1)
                );
            } else if (character == '{') {
                depth++;
            } else if (character == '}') {
                if (--depth <= 0) {
                    break;
                }
            } else if (
                (depth == 1) &&
                (character == 'l') &&
                (string.CompareOrdinal(
                    indexA: index,
                    indexB: 0,
                    length: 6,
                    strA: buffer,
                    strB: "local "
                ) == 0) &&
                ((index == 0) || !(char.IsLetterOrDigit(c: buffer[(index - 1)]) || (buffer[(index - 1)] is '_' or '$' or '.' or ':')))
            ) {
                var start = (index + 6);

                while ((start < buffer.Length) && (buffer[start] == ' ')) {
                    start++;
                }

                var end = start;

                while ((end < buffer.Length) && (char.IsLetterOrDigit(c: buffer[end]) || (buffer[end] == '_'))) {
                    end++;
                }
                if (end > start) {
                    _ = names.Add(item: buffer[start..end]);
                }

                index = end;
            }
        }

        return names;
    }
    private static RuleBlockNode ParseRuleBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        using var locals = ExpressionSpelling.WithLocals(locals: DeclaredLocals(
            buffer: context.Scanner.Buffer,
            offset: context.Scanner.Cursor.Offset
        ));
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);

        ExpressionNode? nameExpression = null;
        var name = string.Empty;

        // `rule $"flip-{i}" { }`: the name is not known until lowering, so it rides the node as an expression
        // instead of a string, exactly as a block header's interpolated name does.
        if (
            (cursor.Current == '$') &&
            (cursor.PeekNext() == '"')
        ) {
            if (!TryReadName(
                admitted: NameForms.Interpolated | NameForms.String,
                context: context,
                spelling: out var ruleName,
                text: out _
            ) || (ruleName.Expression is null)) {
                throw CreateException(context: context, message: "Expected an interpolated name after 'rule'");
            }
            nameExpression = ruleName.Expression;
        } else if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out name)) {
            var (nLine, nCol) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: cursor.Offset
            );
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.RuleNameMissing,
                message: "Expected an identifier or quoted name after 'rule'",
                span: new SourceSpan(
                    cursor.Offset,
                    1,
                    nLine,
                    nCol
                )
            );
            name = string.Empty;
        }

        WhenStatementNode? headerWhen = null;
        var sawWhen = false;
        string? poolForEach = null;
        string? poolBinding = null;

        SkipWhiteSpace(context: context);

        if (TryMatchKeyword(context: context, keyword: "for")) {
            SkipWhiteSpace(context: context);
            if (!TryMatchKeyword(context: context, keyword: "each")) {
                throw CreateException(context: context, message: "Expected 'each <binding> in <pool>' after 'for' in a rule header");
            }
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out poolBinding)) {
                throw CreateException(context: context, message: "Expected a binding after 'for each' in a rule header");
            }
            SkipWhiteSpace(context: context);
            if (!TryMatchKeyword(context: context, keyword: "in")) {
                throw CreateException(context: context, message: "Expected 'in <pool>' after a rule iteration binding");
            }
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out poolForEach)) {
                throw CreateException(context: context, message: "Expected a pool after 'in' in a rule header");
            }
            SkipWhiteSpace(context: context);
        }

        if (TryMatchKeyword(context: context, keyword: "when")) {
            var whenStart = cursor.Offset;

            var (whenLine, whenCol) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: whenStart);
            headerWhen = ParseWhenStatement(
                col: whenCol,
                context: context,
                diagnostics: diagnostics,
                line: whenLine,
                startOffset: whenStart
            );
            sawWhen = true;
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(
            c: '{',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: $"Expected '{{' starting body for rule '{name}'"
            );
        }

        var statements = new List<StatementNode>();

        if (headerWhen is not null) {
            statements.Add(item: headerWhen);
        }
        var sawEffect = false;

        SkipWhiteSpace(context: context);
        while (
            !cursor.Eof &&
            (cursor.Current != '}')
        ) {
            var stmt = ParseRuleBodyStatement(
                context: context,
                diagnostics: diagnostics
            );

            if (stmt is WhenStatementNode) {
                if (sawWhen) {
                    var span = new SourceSpan(
                        stmt.Offset,
                        stmt.Length,
                        stmt.Line,
                        stmt.Column
                    );

                    diagnostics?.ReportError(
                        code: PuckDiagnosticCodes.DuplicateWhen,
                        message: $"rule '{name}' has more than one 'when' clause",
                        span: span
                    );
                } else {
                    sawWhen = true;
                }
            }
            if (stmt is EffectStatementNode or ExpressionStatementNode or DecisionBlockNode) {
                sawEffect = true;
            }
            statements.Add(item: stmt);
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(
            c: '}',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: $"Expected '}}' closing body for rule '{name}'"
            );
        }

        if (!sawEffect) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.RuleWithoutEffects,
                message: $"rule '{name}' carries no effect statements",
                span: new SourceSpan(
                    startOffset,
                    (cursor.Offset - startOffset),
                    line,
                    col
                )
            );
        }

        var len = (cursor.Offset - startOffset);

        return new RuleBlockNode(
            Column: col,
            Length: len,
            Line: line,
            Name: name,
            NameExpression: nameExpression,
            Offset: startOffset,
            PoolBinding: poolBinding,
            PoolForEach: poolForEach,
            Statements: statements
        );
    }

    // A rule's own wire fields. `mode = Edge` reads exactly like a cell assignment to the effect dispatcher, so
    // these names are routed to the property path first; a state row genuinely called one of them is backquoted.
    private static readonly HashSet<string> RuleBodyPropertyNames = new(comparer: StringComparer.Ordinal) {
        "name", "gate", "mode", "forEach", "poolForEach", "zones", "locals", "effects", "decision",
    };

    // Whether the next token is one of `names` used as a property (followed by ':', '=', '{' or '[').
    private static bool AtPropertyNamed(ParseContext context, IReadOnlySet<string> names) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        try {
            if (
                !TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var name) ||
                !names.Contains(item: name)
            ) {
                return false;
            }
            SkipWhiteSpace(context: context);
            // ':'/'=' only: a '{' after one of these names opens the construct's own block, not a property value.
            return (cursor.Current is ':' or '=');
        } finally {
            cursor.ResetPosition(position: saved);
        }
    }
    private static StatementNode ParseRuleBodyStatement(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: startOffset
        );

        if (AtPropertyNamed(
            context: context,
            names: RuleBodyPropertyNames
        )) {
            var ruleProperty = TryParseGenericProperty(
                context: context,
                diagnostics: diagnostics
            );

            if (ruleProperty is not null) {
                return ruleProperty;
            }
        }

        if (TryMatchKeyword(
            context: context,
            keyword: "when"
        )) {
            return ParseWhenStatement(
                col: col,
                context: context,
                diagnostics: diagnostics,
                line: line,
                startOffset: startOffset
            );
        }
        if (TryMatchKeyword(
            context: context,
            keyword: "local"
        )) {
            return ParseLocalStatement(
                col: col,
                context: context,
                diagnostics: diagnostics,
                line: line,
                startOffset: startOffset
            );
        }
        if (TryMatchKeyword(
            context: context,
            keyword: "decision"
        )) {
            return ParseDecisionBlock(
                col: col,
                context: context,
                diagnostics: diagnostics,
                line: line,
                startOffset: startOffset
            );
        }

        var effect = ParseEffectStatement(
            context,
            diagnostics
        );

        if (effect is not null) {
            return effect;
        }

        var property = TryParseGenericProperty(
            context: context,
            diagnostics: diagnostics
        );

        if (property is not null) {
            return property;
        }

        throw CreateException(
            context: context,
            message: $"Unexpected token '{cursor.Current}' inside rule body"
        );
    }
    // A local's kind is inferred from its expression (see WorldDocumentEmitter's kind inference) rather than
    // authored, so a `: Kind`/`as Kind` clause is refused by name and consumed — the rest of the statement still
    // parses.
    private static void RefuseLocalKindAnnotation(ParseContext context, DiagnosticBag? diagnostics, string name) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;
        var matchedColon = TryConsume(
            c: ':',
            context: context
        );
        var matchedAs = (!matchedColon && TryMatchKeyword(
            context: context,
            keyword: "as"
        ));

        if (!matchedColon && !matchedAs) {
            cursor.ResetPosition(position: saved);

            return;
        }

        SkipWhiteSpace(context: context);

        var (kindLine, kindCol) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: cursor.Offset
        );

        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var word)) {
            cursor.ResetPosition(position: saved);

            return;
        }

        diagnostics?.ReportError(
            code: PuckDiagnosticCodes.LocalKindAnnotated,
            message: $"'local {name}' spells its kind explicitly as '{(matchedColon ? ":" : "as")} {word}' — the kind is inferred from its expression; write 'local {name} = ...' and drop the annotation",
            span: new SourceSpan(
                cursor.Offset,
                1,
                kindLine,
                kindCol
            )
        );
    }
    private static LocalStatementNode ParseLocalStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var name)) {
            throw CreateException(
                context: context,
                message: "Expected a name after 'local'"
            );
        }

        SkipWhiteSpace(context: context);
        RefuseLocalKindAnnotation(context: context, diagnostics: diagnostics, name: name);

        SkipWhiteSpace(context: context);
        if (!TryConsume(
            c: '=',
            context: context
        )) {
            var (eLine, eCol) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: cursor.Offset
            );
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.LocalInitializerMissing,
                message: $"'local {name}' is missing its '=' initializer",
                span: new SourceSpan(
                    cursor.Offset,
                    1,
                    eLine,
                    eCol
                )
            );
            var missingLen = (cursor.Offset - startOffset);

            return new LocalStatementNode(
                Column: col,
                Expression: CreateOperand(text: string.Empty, form: DocumentValueForm.Expression),
                Length: missingLen,
                Line: line,
                Name: name,
                Offset: startOffset
            );
        }

        var opStart = cursor.Offset;

        var (oLine, oCol) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: opStart
        );
        var text = ScanOperandSpan(
            context,
            stopKeywords: null,
            stopAtComparator: false,
            out _
        );
        var span = new SourceSpan(
            opStart,
            (cursor.Offset - opStart),
            oLine,
            oCol
        );

        var operand = CreateOperand(span: span, text: text);
        if (text.Length == 0) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.LocalInitializerMissing,
                message: $"'local {name}' initializer is empty",
                span: span
            );
        } else {
            ValidateOperand(diagnostics: diagnostics, operand: operand);
        }

        var len = (cursor.Offset - startOffset);

        return new LocalStatementNode(
            Column: col,
            Expression: operand,
            Length: len,
            Line: line,
            Name: name,
            Offset: startOffset
        );
    }
    private static DecisionBlockNode ParseDecisionBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryConsume(
            c: '{',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: "Expected '{' starting 'decision' body"
            );
        }

        var statements = new List<StatementNode>();
        var sawPeriodSeconds = false;

        SkipWhiteSpace(context: context);
        while (
            !cursor.Eof &&
            (cursor.Current != '}')
        ) {
            var stmt = ParseDecisionBodyStatement(
                context: context,
                diagnostics: diagnostics
            );

            if (stmt is PropertyNode { Name: "periodSeconds" }) {
                sawPeriodSeconds = true;
            }
            statements.Add(item: stmt);
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(
            c: '}',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: "Expected '}' closing 'decision' body"
            );
        }

        if (!sawPeriodSeconds) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.DecisionPeriodMissing,
                message: "'decision' is missing its required 'periodSeconds'",
                span: new SourceSpan(
                    startOffset,
                    (cursor.Offset - startOffset),
                    line,
                    col
                )
            );
        }

        var len = (cursor.Offset - startOffset);

        return new DecisionBlockNode(
            Column: col,
            Length: len,
            Line: line,
            Offset: startOffset,
            Statements: statements
        );
    }
    private static StatementNode ParseDecisionBodyStatement(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: startOffset
        );

        if (TryMatchKeyword(
            context: context,
            keyword: "option"
        )) {
            return ParseOptionBlock(
                col: col,
                context: context,
                diagnostics: diagnostics,
                line: line,
                startOffset: startOffset
            );
        }
        if (TryMatchKeyword(
            context: context,
            keyword: "interrupt"
        )) {
            SkipWhiteSpace(context: context);
            // `interrupt: <call-form>` is the spelling a predicate the gate grammar cannot carry decompiles to.
            if (cursor.Current is ':' or '=') {
                cursor.Advance();
                var value = ParseExpression(context: context);
                var propertyLen = (cursor.Offset - startOffset);

                return new PropertyNode(
                    Column: col,
                    Length: propertyLen,
                    Line: line,
                    Name: "interrupt",
                    Offset: startOffset,
                    Value: value
                );
            }
            var predicate = ParseGate(
                context: context,
                diagnostics: diagnostics
            );
            var len = (cursor.Offset - startOffset);

            return new InterruptStatementNode(
                Column: col,
                Length: len,
                Line: line,
                Offset: startOffset,
                Predicate: predicate
            );
        }
        if (TryMatchKeyword(
            context: context,
            keyword: "onNoChoice"
        )) {
            SkipWhiteSpace(context: context);
            if (!TryConsume(
                c: '{',
                context: context
            )) {
                throw CreateException(
                    context: context,
                    message: "Expected '{' starting 'onNoChoice' body"
                );
            }
            var effects = ParseEffectStatementList(
                context,
                diagnostics
            );

            if (!TryConsume(
                c: '}',
                context: context
            )) {
                throw CreateException(
                    context: context,
                    message: "Expected '}' closing 'onNoChoice' body"
                );
            }
            var len = (cursor.Offset - startOffset);

            return new OnNoChoiceBlockNode(
                Column: col,
                Effects: effects,
                Length: len,
                Line: line,
                Offset: startOffset
            );
        }

        var property = TryParseGenericProperty(
            context: context,
            diagnostics: diagnostics
        );

        if (property is not null) {
            return property;
        }

        throw CreateException(
            context: context,
            message: $"Unexpected token '{cursor.Current}' inside 'decision' body"
        );
    }
    private static OptionBlockNode ParseOptionBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out var name)) {
            var (nLine, nCol) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: cursor.Offset
            );
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.DecisionStructure,
                message: "Expected a quoted name after 'option'",
                span: new SourceSpan(
                    cursor.Offset,
                    1,
                    nLine,
                    nCol
                )
            );
            name = string.Empty;
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(
            c: '{',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: $"Expected '{{' starting body for option '{name}'"
            );
        }

        var statements = new List<StatementNode>();
        var sawScore = false;

        SkipWhiteSpace(context: context);
        while (
            !cursor.Eof &&
            (cursor.Current != '}')
        ) {
            var stmt = ParseOptionBodyStatement(
                context: context,
                diagnostics: diagnostics
            );

            if (stmt is ScoreStatementNode) {
                sawScore = true;
            }
            statements.Add(item: stmt);
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(
            c: '}',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: $"Expected '}}' closing body for option '{name}'"
            );
        }

        if (!sawScore) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.DecisionStructure,
                message: $"option '{name}' is missing its required 'score'",
                span: new SourceSpan(
                    startOffset,
                    (cursor.Offset - startOffset),
                    line,
                    col
                )
            );
        }

        var len = (cursor.Offset - startOffset);

        return new OptionBlockNode(
            Column: col,
            Length: len,
            Line: line,
            Name: name,
            Offset: startOffset,
            Statements: statements
        );
    }

    // An option's own wire fields, for the same reason a rule body routes its own (see RuleBodyPropertyNames).
    // `gate:` is the spelling a predicate the `when` grammar cannot carry decompiles to.
    private static readonly HashSet<string> OptionBodyPropertyNames = new(comparer: StringComparer.Ordinal) {
        "name", "gate", "neighbors", "effects",
    };

    private static StatementNode ParseOptionBodyStatement(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: startOffset
        );

        if (AtPropertyNamed(
            context: context,
            names: OptionBodyPropertyNames
        )) {
            var optionProperty = TryParseGenericProperty(
                context: context,
                diagnostics: diagnostics
            );

            if (optionProperty is not null) {
                return optionProperty;
            }
        }

        if (TryMatchKeyword(
            context: context,
            keyword: "when"
        )) {
            return ParseWhenStatement(
                col: col,
                context: context,
                diagnostics: diagnostics,
                line: line,
                startOffset: startOffset
            );
        }
        if (TryMatchKeyword(
            context: context,
            keyword: "score"
        )) {
            SkipWhiteSpace(context: context);
            TryConsume(
                c: ':',
                context: context
            );
            TryConsume(
                c: '=',
                context: context
            );
            var opStart = cursor.Offset;

            var (oLine, oCol) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: opStart
            );
            var text = ScanOperandSpan(
                context,
                stopKeywords: null,
                stopAtComparator: false,
                out _
            );
            var span = new SourceSpan(
                opStart,
                (cursor.Offset - opStart),
                oLine,
                oCol
            );

            var operand = ValidatedOperand(diagnostics: diagnostics, span: span, text: text);
            var len = (cursor.Offset - startOffset);

            return new ScoreStatementNode(
                Column: col,
                Expression: operand,
                Length: len,
                Line: line,
                Offset: startOffset
            );
        }

        var effect = ParseEffectStatement(
            context,
            diagnostics
        );

        if (effect is not null) {
            return effect;
        }

        var property = TryParseGenericProperty(
            context: context,
            diagnostics: diagnostics
        );

        if (property is not null) {
            return property;
        }

        throw CreateException(
            context: context,
            message: $"Unexpected token '{cursor.Current}' inside option body"
        );
    }
    /// <summary>Tries to parse one effect statement (§2): <c>push</c>, <c>remove</c>,
    /// <c>schedule</c>, <c>transform</c>, <c>transaction</c>, a <c>row[key] (= | +=) rhs</c> cell assignment, or a
    /// call-form statement (<c>generate(...)</c> and every <c>Puck.World.Schema</c> extension arm). Returns
    /// <see langword="null"/> — consuming nothing — when none of these match, so the caller can fall back to an
    /// ordinary property.</summary>
    private static StatementNode? ParseEffectStatement(ParseContext context, DiagnosticBag? diagnostics, bool insideTransaction = false) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;
        var effectStartPosition = cursor.Position;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: startOffset
        );

        if (TryMatchKeyword(context: context, keyword: "claim")) {
            SkipWhiteSpace(context: context);
            if (TryMatchKeyword(context: context, keyword: "pair")) {
                SkipWhiteSpace(context: context);
                if (TryMatchKeyword(context: context, keyword: "as")) {
                    SkipWhiteSpace(context: context);
                    if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var ordinaryAlias)) {
                        throw CreateException(context: context, message: "Expected an alias after 'claim pair as'");
                    }
                    SkipWhiteSpace(context: context);
                    if (!TryConsume(c: '{', context: context)) {
                        throw CreateException(context: context, message: "Expected '{' starting a claim body");
                    }
                    var ordinaryBody = ParseEffectStatementList(context: context, diagnostics: diagnostics, insideTransaction: insideTransaction);

                    if (!TryConsume(c: '}', context: context)) {
                        throw CreateException(context: context, message: "Expected '}' closing a claim body");
                    }
                    return new ClaimStatementNode("pair", ordinaryAlias, ordinaryBody, startOffset, (cursor.Offset - startOffset), line, col);
                }
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var pairPool)) {
                    throw CreateException(context: context, message: "Expected a pair-pool name after 'claim pair'");
                }
                SkipWhiteSpace(context: context);
                if (!TryMatchKeyword(context: context, keyword: "between")) {
                    throw CreateException(context: context, message: "Expected 'between <left>, <right>' after a pair-pool name");
                }
                SkipWhiteSpace(context: context);
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var left)) {
                    throw CreateException(context: context, message: "Expected the left endpoint alias after 'between'");
                }
                SkipWhiteSpace(context: context);
                if (!TryConsume(c: ',', context: context)) {
                    throw CreateException(context: context, message: "Expected ',' between pair endpoint aliases");
                }
                SkipWhiteSpace(context: context);
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var right)) {
                    throw CreateException(context: context, message: "Expected the right endpoint alias after ','");
                }
                SkipWhiteSpace(context: context);
                if (!TryMatchKeyword(context: context, keyword: "as")) {
                    throw CreateException(context: context, message: "Expected 'as <alias>' after pair endpoints");
                }
                SkipWhiteSpace(context: context);
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var pairAlias)) {
                    throw CreateException(context: context, message: "Expected a binding alias after 'claim pair ... as'");
                }
                SkipWhiteSpace(context: context);
                if (!TryConsume(c: '{', context: context)) {
                    throw CreateException(context: context, message: "Expected '{' starting a pair claim body");
                }
                var pairBody = ParseEffectStatementList(context: context, diagnostics: diagnostics, insideTransaction: insideTransaction);

                if (!TryConsume(c: '}', context: context)) {
                    throw CreateException(context: context, message: "Expected '}' closing a pair claim body");
                }
                return new ClaimPairStatementNode(pairPool, left, right, pairAlias, pairBody, startOffset, (cursor.Offset - startOffset), line, col);
            }
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var pool)) {
                throw CreateException(context: context, message: "Expected a pool name after 'claim'");
            }
            SkipWhiteSpace(context: context);
            if (!TryMatchKeyword(context: context, keyword: "as")) {
                throw CreateException(context: context, message: "Expected 'as <alias>' after a pool name");
            }
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var alias)) {
                throw CreateException(context: context, message: "Expected an alias after 'claim <pool> as'");
            }
            SkipWhiteSpace(context: context);
            if (!TryConsume(c: '{', context: context)) {
                throw CreateException(context: context, message: "Expected '{' starting a claim body");
            }
            var body = ParseEffectStatementList(context: context, diagnostics: diagnostics, insideTransaction: insideTransaction);

            if (!TryConsume(c: '}', context: context)) {
                throw CreateException(context: context, message: "Expected '}' closing a claim body");
            }
            return new ClaimStatementNode(pool, alias, body, startOffset, (cursor.Offset - startOffset), line, col);
        }

        if (TryMatchKeyword(context: context, keyword: "release")) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var alias)) {
                throw CreateException(context: context, message: "Expected an alias after 'release'");
            }
            return new ReleaseStatementNode(alias, startOffset, (cursor.Offset - startOffset), line, col);
        }

        if (TryMatchKeyword(context: context, keyword: "for")) {
            SkipWhiteSpace(context: context);
            if (!TryMatchKeyword(context: context, keyword: "each")) {
                cursor.ResetPosition(position: effectStartPosition);
            } else {
                SkipWhiteSpace(context: context);
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var alias)) {
                    throw CreateException(context: context, message: "Expected an alias after 'for each'");
                }
                SkipWhiteSpace(context: context);
                if (!TryMatchKeyword(context: context, keyword: "in")) {
                    throw CreateException(context: context, message: "Expected 'in <pool>' after a foreach alias");
                }
                SkipWhiteSpace(context: context);
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var pool)) {
                    throw CreateException(context: context, message: "Expected a pool name after 'for each <alias> in'");
                }
                SkipWhiteSpace(context: context);
                if (!TryConsume(c: '{', context: context)) {
                    throw CreateException(context: context, message: "Expected '{' starting a pool foreach body");
                }
                var body = ParseEffectStatementList(context: context, diagnostics: diagnostics, insideTransaction: insideTransaction);

                if (!TryConsume(c: '}', context: context)) {
                    throw CreateException(context: context, message: "Expected '}' closing a pool foreach body");
                }
                return new PoolForEachStatementNode(pool, alias, body, startOffset, (cursor.Offset - startOffset), line, col);
            }
        }

        if (TryMatchKeyword(
            context: context,
            keyword: "push"
        )) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var rowName)) {
                throw CreateException(
                    context: context,
                    message: "Expected a row name after 'push'"
                );
            }
            SkipWhiteSpace(context: context);
            if (!TryConsume(
                c: '=',
                context: context
            )) {
                throw CreateException(
                    context: context,
                    message: $"Expected '=' after 'push {rowName}'"
                );
            }
            var rhs = ParseRhs(
                context,
                diagnostics,
                allowText: false,
                allowSeconds: false,
                ownerForDiagnostic: "push"
            );
            var len = (cursor.Offset - startOffset);

            return new PushStatementNode(
                Column: col,
                Length: len,
                Line: line,
                Offset: startOffset,
                Rhs: rhs,
                RowName: rowName
            );
        }

        if (TryMatchKeyword(
            context: context,
            keyword: "countdown"
        )) {
            throw new PuckParseException(
                "'countdown' is refused: write a due tick with 'schedule row in Ns' and read it back by comparing '$tick' against the row",
                startOffset,
                line,
                col
            ) {
                Code = PuckDiagnosticCodes.CountdownStatementRetired,
            };
        }

        if (TryMatchKeyword(
            context: context,
            keyword: "remove"
        )) {
            if (
                !TryReadRowRefOperand(
                context,
                diagnostics,
                stopKeywords: null,
                out var target
            ) ||
                (target is null)
            ) {
                throw CreateException(
                    context: context,
                    message: "Expected a state row reference after 'remove'"
                );
            }
            var len = (cursor.Offset - startOffset);

            return new RemoveCellStatementNode(
                Column: col,
                Length: len,
                Line: line,
                Offset: startOffset,
                Target: target
            );
        }

        if (TryMatchKeyword(
            context: context,
            keyword: "schedule"
        )) {
            if (
                !TryReadRowRefOperand(
                context: context,
                diagnostics: diagnostics,
                rowRef: out var target,
                stopKeywords: ScheduleStopKeywords
            ) ||
                (target is null)
            ) {
                throw CreateException(
                    context: context,
                    message: "Expected a state row reference after 'schedule'"
                );
            }
            SkipWhiteSpace(context: context);
            if (!TryMatchKeyword(
                context: context,
                keyword: "in"
            )) {
                throw CreateException(
                    context: context,
                    message: "Expected 'in' after 'schedule <row>'"
                );
            }
            SkipWhiteSpace(context: context);
            var literal = ParseNumberWithOptionalUnit(context: context);
            var delay = DecimalValues.FromLiteral(literal: literal);

            if (literal.Unit is null) {
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.ScheduleDelayUnit,
                    message: "'schedule ... in' requires a number carrying a time unit",
                    span: new SourceSpan(
                        literal.Offset,
                        literal.Length,
                        literal.Line,
                        literal.Column
                    )
                );
            } else if (UnitConversion.TryConvert(
                UnitDimension.Seconds,
                ((double)delay),
                literal.Unit,
                out var seconds
            )) {
                delay = DecimalValues.FromDouble(value: seconds);
            } else {
                var accepted = string.Join(
                    "/",
                    UnitConversion.AcceptedUnits(dimension: UnitDimension.Seconds)
                );

                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.ScheduleDelayUnit,
                    message: $"'schedule ... in' accepts {accepted}, not '{literal.Unit}'",
                    span: new SourceSpan(
                        literal.Offset,
                        literal.Length,
                        literal.Line,
                        literal.Column
                    )
                );
            }
            var len = (cursor.Offset - startOffset);

            return new ScheduleStatementNode(
                Column: col,
                DelaySeconds: delay,
                Length: len,
                Line: line,
                Offset: startOffset,
                Target: target
            );
        }

        if (TryMatchKeyword(
            context: context,
            keyword: "transform"
        )) {
            SkipWhiteSpace(context: context);
            var callStart = cursor.Offset;

            var (callLine, callCol) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: callStart
            );
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var callName)) {
                throw CreateException(
                    context: context,
                    message: "Expected a transform call after 'transform'"
                );
            }
            SkipWhiteSpace(context: context);

            if (cursor.Current == '=') {
                throw new PuckParseException(
                    $"A transform carries no result label: write 'transform <call>(...)' and name the destination in the call's own arguments, not as 'transform {callName} = ...'",
                    callStart,
                    callLine,
                    callCol
                ) {
                    Code = PuckDiagnosticCodes.TransformResultLabel,
                };
            }

            if (cursor.Current != '(') {
                throw CreateException(
                    context: context,
                    message: $"Expected '(' after transform name '{callName}'"
                );
            }
            var call = ParseCallExpression(
                col: callCol,
                context: context,
                functionName: callName,
                line: callLine,
                startOffset: callStart
            );
            var len = (cursor.Offset - startOffset);

            return new TransformStatementNode(
                Column: col,
                Length: len,
                Line: line,
                Offset: startOffset,
                Transform: call
            );
        }

        if (TryMatchKeyword(
            context: context,
            keyword: "transaction"
        )) {
            return ParseTransactionStatement(
                col: col,
                context: context,
                diagnostics: diagnostics,
                insideTransaction: insideTransaction,
                line: line,
                startOffset: startOffset
            );
        }

        if (TryMatchKeyword(
            context: context,
            keyword: "if"
        )) {
            return ParseIfStatement(
                col: col,
                context: context,
                diagnostics: diagnostics,
                insideTransaction: insideTransaction,
                line: line,
                startOffset: startOffset
            );
        }

        if (TryMatchKeyword(
            context: context,
            keyword: "repeat"
        )) {
            return ParseRepeatStatement(
                col: col,
                context: context,
                diagnostics: diagnostics,
                insideTransaction: insideTransaction,
                line: line,
                startOffset: startOffset
            );
        }

        if (TryMatchKeyword(
            context: context,
            keyword: "break"
        )) {
            return new BreakStatementNode(
                startOffset,
                (cursor.Offset - startOffset),
                line,
                col
            );
        }

        if (TryMatchKeyword(context: context, keyword: "draw")) {
            if (!TryReadPileOperand(context: context, text: out var from)) {
                throw CreateException(context: context, message: "Expected a source pile name after 'draw'");
            }
            SkipWhiteSpace(context: context);
            _ = TryMatchKeyword(context: context, keyword: "to");
            if (!TryReadPileOperand(context: context, text: out var to)) {
                throw CreateException(context: context, message: "Expected a destination pile name after 'draw'");
            }
            return new DrawStatementNode(
                Column: col,
                From: from,
                Length: (cursor.Offset - startOffset),
                Line: line,
                Offset: startOffset,
                To: to
            );
        }

        if (TryMatchKeyword(context: context, keyword: "deal")) {
            SkipWhiteSpace(context: context);
            var numLiteral = ParseNumberWithOptionalUnit(context: context);
            var count = Convert.ToInt32(value: numLiteral.Value);

            SkipWhiteSpace(context: context);
            _ = TryMatchKeyword(context: context, keyword: "from");
            if (!TryReadPileOperand(context: context, text: out var from)) {
                throw CreateException(context: context, message: "Expected a source pile name after 'deal'");
            }
            SkipWhiteSpace(context: context);
            _ = TryMatchKeyword(context: context, keyword: "to");
            if (!TryReadPileOperand(context: context, text: out var to)) {
                throw CreateException(context: context, message: "Expected a destination pile name after 'deal'");
            }
            return new DealStatementNode(
                Column: col,
                Count: count,
                From: from,
                Length: (cursor.Offset - startOffset),
                Line: line,
                Offset: startOffset,
                To: to
            );
        }

        if (TryMatchKeyword(context: context, keyword: "shuffle")) {
            if (!TryReadPileOperand(context: context, text: out var pile)) {
                throw CreateException(context: context, message: "Expected a pile name after 'shuffle'");
            }
            SkipWhiteSpace(context: context);
            _ = TryMatchKeyword(context: context, keyword: "with");
            if (!TryReadPileOperand(context: context, text: out var draw)) {
                throw CreateException(context: context, message: "Expected a draw stream name after 'shuffle'");
            }
            return new ShuffleStatementNode(
                Column: col,
                Draw: draw,
                Length: (cursor.Offset - startOffset),
                Line: line,
                Offset: startOffset,
                Row: pile
            );
        }

        var savedPosition = cursor.Position;

        if (TryReadRowRefSpanRaw(
            context: context,
            span: out var rowRefSpan,
            text: out var rowRefText
        )) {
            SkipWhiteSpace(context: context);
            if (
                (cursor.Current == '+') &&
                (cursor.PeekNext() == '=')
            ) {
                cursor.Advance(count: 2);
                var target = ResolveRowRef(
                    diagnostics: diagnostics,
                    span: rowRefSpan,
                    text: rowRefText
                );
                var rhs = ParseRhs(
                    context,
                    diagnostics,
                    allowText: false,
                    allowSeconds: true,
                    ownerForDiagnostic: "addState"
                );
                var len = (cursor.Offset - startOffset);

                return new AddCellStatementNode(
                    Column: col,
                    Length: len,
                    Line: line,
                    Offset: startOffset,
                    Rhs: rhs,
                    Target: target
                );
            }
            if (cursor.Current == '=') {
                cursor.Advance();
                var target = ResolveRowRef(
                    diagnostics: diagnostics,
                    span: rowRefSpan,
                    text: rowRefText
                );
                var rhs = ParseRhs(
                    context,
                    diagnostics,
                    allowText: true,
                    allowSeconds: true,
                    ownerForDiagnostic: "setState"
                );
                var len = (cursor.Offset - startOffset);

                return new SetCellStatementNode(
                    Column: col,
                    Length: len,
                    Line: line,
                    Offset: startOffset,
                    Rhs: rhs,
                    Target: target
                );
            }
            if (LongestMatchingPunctuation(
                context.Scanner.Buffer,
                cursor.Offset,
                CompoundAssignmentOperators
            ) is { } compound) {
                cursor.Advance(count: compound.Length);
                var target = ResolveRowRef(
                    diagnostics: diagnostics,
                    span: rowRefSpan,
                    text: rowRefText
                );
                var rhs = ParseRhs(
                    context,
                    diagnostics,
                    allowText: false,
                    allowSeconds: false,
                    ownerForDiagnostic: "compound assignment"
                );
                var len = (cursor.Offset - startOffset);

                return new CompoundAssignStatementNode(
                    target,
                    compound[..^1],
                    rhs,
                    startOffset,
                    len,
                    line,
                    col
                );
            }
            cursor.ResetPosition(position: savedPosition);
        }

        if (TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out var callId)) {
            SkipWhiteSpace(context: context);
            if (cursor.Current == '(') {
                var call = ParseCallExpression(
                    col: col,
                    context: context,
                    functionName: callId,
                    line: line,
                    startOffset: startOffset
                );
                var len = (cursor.Offset - startOffset);

                return new ExpressionStatementNode(
                    Column: col,
                    Expression: call,
                    Length: len,
                    Line: line,
                    Offset: startOffset
                );
            }
            cursor.ResetPosition(position: savedPosition);
            return null;
        }

        return null;
    }
    private static List<StatementNode> ParseEffectStatementList(ParseContext context, DiagnosticBag? diagnostics, bool insideTransaction = false) {
        var statements = new List<StatementNode>();
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        while (
            !cursor.Eof &&
            (cursor.Current != '}')
        ) {
            var stmt = ParseEffectStatement(
                context: context,
                diagnostics: diagnostics,
                insideTransaction: insideTransaction
            );

            if (stmt is null) {
                throw CreateException(
                    context: context,
                    message: $"Expected an effect statement, found '{cursor.Current}'"
                );
            }
            statements.Add(item: stmt);
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }
        return statements;
    }
    // `if Gate { ... }`, with an optional `else { ... }` or `else if ...` tail. An `else if` is stored as an Else
    // holding one nested IfStatementNode, so a chain of any length is the same shape as one branch and no consumer
    // needs a separate else-if case.
    private static IfStatementNode ParseIfStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, bool insideTransaction) {
        var cursor = context.Scanner.Cursor;
        var condition = ParseGate(
            context: context,
            diagnostics: diagnostics
        );

        SkipWhiteSpace(context: context);

        if (!TryConsume(
            c: '{',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: "Expected '{' starting an 'if' body"
            );
        }

        var thenStatements = ParseEffectStatementList(
            context: context,
            diagnostics: diagnostics,
            insideTransaction: insideTransaction
        );

        if (!TryConsume(
            c: '}',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: "Expected '}' closing an 'if' body"
            );
        }

        IReadOnlyList<StatementNode>? elseStatements = null;
        var savedPosition = cursor.Position;

        SkipWhiteSpace(context: context);

        if (TryMatchKeyword(
            context: context,
            keyword: "else"
        )) {
            SkipWhiteSpace(context: context);

            var elseStart = cursor.Offset;

            var (elseLine, elseCol) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: elseStart
            );

            if (TryMatchKeyword(
                context: context,
                keyword: "if"
            )) {
                elseStatements = [ParseIfStatement(
                        col: elseCol,
                        context: context,
                        diagnostics: diagnostics,
                        insideTransaction: insideTransaction,
                        line: elseLine,
                        startOffset: elseStart
                    )];
            } else {
                if (!TryConsume(
                    c: '{',
                    context: context
                )) {
                    throw CreateException(
                        context: context,
                        message: "Expected '{' or 'if' after 'else'"
                    );
                }

                elseStatements = ParseEffectStatementList(
                    context: context,
                    diagnostics: diagnostics,
                    insideTransaction: insideTransaction
                );

                if (!TryConsume(
                    c: '}',
                    context: context
                )) {
                    throw CreateException(
                        context: context,
                        message: "Expected '}' closing an 'else' body"
                    );
                }
            }
        } else {
            // Nothing was consumed by the failed keyword match, but the whitespace skip before it was: rewinding
            // keeps this statement's Length honest and leaves the next statement's own leading trivia alone.
            cursor.ResetPosition(position: savedPosition);
        }

        return new IfStatementNode(
            condition,
            thenStatements,
            elseStatements,
            startOffset,
            (cursor.Offset - startOffset),
            line,
            col
        );
    }
    // `repeat Count as name { ... }`.
    private static RepeatStatementNode ParseRepeatStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, bool insideTransaction) {
        var cursor = context.Scanner.Cursor;
        var count = ParseExpression(context: context);

        SkipWhiteSpace(context: context);

        if (!TryMatchKeyword(
            context: context,
            keyword: "as"
        )) {
            throw CreateException(
                context: context,
                message: "Expected 'as <name>' after a 'repeat' count"
            );
        }

        SkipWhiteSpace(context: context);

        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var index)) {
            throw CreateException(
                context: context,
                message: "Expected an index name after 'repeat <count> as'"
            );
        }

        SkipWhiteSpace(context: context);

        if (!TryConsume(
            c: '{',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: "Expected '{' starting a 'repeat' body"
            );
        }

        var body = ParseEffectStatementList(
            context: context,
            diagnostics: diagnostics,
            insideTransaction: insideTransaction
        );

        if (!TryConsume(
            c: '}',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: "Expected '}' closing a 'repeat' body"
            );
        }

        return new RepeatStatementNode(
            count,
            index,
            body,
            startOffset,
            (cursor.Offset - startOffset),
            line,
            col
        );
    }
    private static TransactionStatementNode ParseTransactionStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, bool insideTransaction = false) {
        var cursor = context.Scanner.Cursor;

        if (insideTransaction) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.NestedTransaction,
                message: "nested 'transaction' is not supported",
                span: new SourceSpan(
                    startOffset,
                    "transaction".Length,
                    line,
                    col
                )
            );
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(
            c: '{',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: "Expected '{' starting 'transaction' body"
            );
        }
        var mainEffects = ParseEffectStatementList(
            context,
            diagnostics,
            insideTransaction: true
        );

        if (!TryConsume(
            c: '}',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: "Expected '}' closing 'transaction' body"
            );
        }

        IReadOnlyList<StatementNode>? onFailureEffects = null;
        SourceSpan? onFailureSpan = null;

        SkipWhiteSpace(context: context);

        var onFailureOffset = cursor.Offset;

        if (TryMatchKeyword(
            context: context,
            keyword: "onFailure"
        )) {
            var (failureLine, failureCol) = GetLineAndColumn(
                buffer: context.Scanner.Buffer,
                offset: onFailureOffset
            );

            onFailureSpan = new SourceSpan(
                onFailureOffset,
                "onFailure".Length,
                failureLine,
                failureCol
            );
            SkipWhiteSpace(context: context);
            if (!TryConsume(
                c: '{',
                context: context
            )) {
                throw CreateException(
                    context: context,
                    message: "Expected '{' starting 'onFailure' body"
                );
            }
            onFailureEffects = ParseEffectStatementList(
                context,
                diagnostics,
                insideTransaction: true
            );
            if (!TryConsume(
                c: '}',
                context: context
            )) {
                throw CreateException(
                    context: context,
                    message: "Expected '}' closing 'onFailure' body"
                );
            }

            SkipWhiteSpace(context: context);
            if (TryMatchKeyword(
                context: context,
                keyword: "onFailure"
            )) {
                var (dLine, dCol) = GetLineAndColumn(
                    buffer: context.Scanner.Buffer,
                    offset: cursor.Offset
                );
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.OnFailureStructure,
                    message: "a 'transaction' may carry at most one 'onFailure' block",
                    span: new SourceSpan(
                        cursor.Offset,
                        9,
                        dLine,
                        dCol
                    )
                );
                SkipWhiteSpace(context: context);
                if (TryConsume(
                    c: '{',
                    context: context
                )) {
                    ParseEffectStatementList(
                        context,
                        diagnostics,
                        insideTransaction: true
                    );
                    TryConsume(
                        c: '}',
                        context: context
                    );
                }
            }
        }

        var len = (cursor.Offset - startOffset);

        return new TransactionStatementNode(
            Column: col,
            Length: len,
            Line: line,
            MainEffects: mainEffects,
            Offset: startOffset,
            OnFailureEffects: onFailureEffects
        ) { OnFailureSpan = onFailureSpan };
    }
    /// <summary>Parses a <c>setState</c>/<c>addState</c>/<c>push</c> right-hand side (§2.3): a string literal, a
    /// number carrying the <c>s</c> unit and nothing else on the statement, or opaque operand text. Reports PUCK009
    /// when the shape reached is not one <paramref name="ownerForDiagnostic"/>'s own JSON fields can carry.</summary>
    private static RhsNode ParseRhs(ParseContext context, DiagnosticBag? diagnostics, bool allowText, bool allowSeconds, string ownerForDiagnostic) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: cursor.Offset
        );

        if (cursor.Current == '"') {
            var start = cursor.Offset;

            if (!TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out var text)) {
                throw CreateException(
                    context: context,
                    message: "Malformed string literal"
                );
            }
            var span = new SourceSpan(
                start,
                (cursor.Offset - start),
                line,
                col
            );

            if (!allowText) {
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.EffectRhsShape,
                    message: $"a string literal is not valid here — '{ownerForDiagnostic}' carries no Text field",
                    span: span
                );
            }
            return new RhsTextNode(
                text,
                start,
                span.Length,
                line,
                col
            );
        }

        if (
            char.IsDigit(c: cursor.Current) ||
            ((cursor.Current is '-' or '+') && (char.IsDigit(c: cursor.PeekNext()) || (cursor.PeekNext() == '.')))
        ) {
            var saved = cursor.Position;
            var literal = ParseNumberWithOptionalUnit(context: context);

            if (AtEndOfLogicalStatement(
                buffer: context.Scanner.Buffer,
                offset: cursor.Offset
            )) {
                if (literal.Unit == "s") {
                    if (!allowSeconds) {
                        diagnostics?.ReportError(
                            code: PuckDiagnosticCodes.EffectRhsShape,
                            message: $"a seconds literal is not valid here — '{ownerForDiagnostic}' carries no ValueSeconds field",
                            span: new SourceSpan(
                                literal.Offset,
                                literal.Length,
                                literal.Line,
                                literal.Column
                            )
                        );
                    }
                    return new RhsSecondsNode(
                        DecimalValues.FromLiteral(literal: literal),
                        literal.Offset,
                        literal.Length,
                        literal.Line,
                        literal.Column
                    );
                }
                if (literal.Unit is null) {
                    var text = context.Scanner.Buffer[literal.Offset..(literal.Offset + literal.Length)];

                    return new RhsOperandNode(
                        CreateOperand(text: text, span: literal.Span),
                        literal.Offset,
                        literal.Length,
                        literal.Line,
                        literal.Column
                    );
                }
            }
            cursor.ResetPosition(position: saved);
        }

        var opStart = cursor.Offset;

        var (oLine, oCol) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: opStart
        );
        var opText = ScanOperandSpan(
            context,
            stopKeywords: null,
            stopAtComparator: false,
            out _
        );
        var opSpan = new SourceSpan(
            opStart,
            (cursor.Offset - opStart),
            oLine,
            oCol
        );

        var operand = ValidatedOperand(diagnostics: diagnostics, span: opSpan, text: opText);
        return new RhsOperandNode(
            operand,
            opStart,
            opSpan.Length,
            oLine,
            oCol
        );
    }
    /// <summary>Reads one ordinary <c>name: value</c>/<c>name = value</c>/<c>name [ ... ]</c>/<c>name { ... }</c>
    /// property — the DSL's existing generic property grammar, plus a backquoted name (for e.g. an authored
    /// <c>`$replace`: true</c> basis-merge override, which is not itself new sugar — §0). Returns
    /// <see langword="null"/> — consuming nothing — when the next token is not a name at all.</summary>
    private static PropertyNode? TryParseGenericProperty(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(
            buffer: context.Scanner.Buffer,
            offset: startOffset
        );

        string name;

        if (cursor.Current == '`') {
            if (!TryReadBackquotedName(
                context: context,
                name: out name
            )) {
                return null;
            }
        } else if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out name)) {
            return null;
        }

        SkipWhiteSpace(context: context);
        if (cursor.Current is ':' or '=') {
            var separator = cursor.Current;

            cursor.Advance();
            SkipWhiteSpace(context: context);
            RefuseColonBeforeContainer(
                col: col,
                context: context,
                diagnostics: diagnostics,
                identifier: name,
                line: line,
                separator: separator,
                startOffset: startOffset
            );

            var value = ParseMemberValue(context: context);
            var len = (cursor.Offset - startOffset);

            return new PropertyNode(
                Column: col,
                Length: len,
                Line: line,
                Name: name,
                Offset: startOffset,
                Value: value
            );
        }
        if (cursor.Current == '[') {
            var arr = ParseArrayExpression(context: context);
            var len = (cursor.Offset - startOffset);

            return new PropertyNode(
                Column: col,
                Length: len,
                Line: line,
                Name: name,
                Offset: startOffset,
                Value: arr
            );
        }
        if (cursor.Current == '{') {
            var obj = ParseObjectExpression(context: context);
            var len = (cursor.Offset - startOffset);

            return new PropertyNode(
                Column: col,
                Length: len,
                Line: line,
                Name: name,
                Offset: startOffset,
                Value: obj
            );
        }

        cursor.ResetPosition(position: saved);
        return null;
    }
    private static bool TryReadPileOperand(ParseContext context, out string text) {
        SkipWhiteSpace(context: context);
        if (TryReadRowRefSpanRaw(context: context, span: out _, text: out text)) {
            return true;
        }
        if (TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out text)) {
            return true;
        }
        return TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out text);
    }
}
