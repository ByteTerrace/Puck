using Parlot;
using Parlot.Fluent;
using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Parsing;

/// <summary>High-performance recursive-descent parser for the Puck authoring language.</summary>
public static partial class PuckParser {
    private static readonly PuckWhiteSpaceParser WhiteSpace = new();

    /// <summary>Parses the entire Puck source text into a <see cref="DocumentNode"/>, collecting all diagnostics with resilient recovery.</summary>
    /// <param name="source">The source text of the Puck file.</param>
    /// <param name="defaultSchema">The default schema tag if omitted by author.</param>
    /// <param name="diagnostics">The DiagnosticBag to collect errors and warnings into.</param>
    /// <returns>A CompilationResult carrying the parsed DocumentNode and DiagnosticBag.</returns>
    public static CompilationResult<DocumentNode> ParseDocumentWithDiagnostics(string source, string? defaultSchema = null, DiagnosticBag? diagnostics = null) {
        ArgumentNullException.ThrowIfNull(source);

        diagnostics ??= new DiagnosticBag();
        var context = CreateContext(source);
        var schema = defaultSchema;
        string? basis = null;
        var basisSpan = SourceSpan.None;
        var statements = new List<StatementNode>();

        SkipWhiteSpace(context);

        while (!context.Scanner.Cursor.Eof) {
            // Peek next token to check for header schema / basis directives
            if (TryMatchKeyword(context, "schema")) {
                SkipWhiteSpace(context);
                TryConsume(context, ':');
                TryConsume(context, '=');
                SkipWhiteSpace(context);
                if (TryReadString(context, out var schemaVal) || TryReadIdentifier(context, out schemaVal)) {
                    schema = schemaVal;
                    ConsumeSeparator(context);
                    continue;
                }
                var (sLine, sCol) = GetLineAndColumn(source, context.Scanner.Cursor.Offset);
                diagnostics.ReportError(PuckDiagnosticCodes.Syntax, "Expected schema identifier or string after 'schema'", new SourceSpan(context.Scanner.Cursor.Offset, 1, sLine, sCol));
                SynchronizeToStatementBoundary(context);
                continue;
            }

            if (TryMatchKeyword(context, "basis")) {
                var basisStart = context.Scanner.Cursor.Offset - "basis".Length;
                var (basisLine, basisCol) = GetLineAndColumn(source, basisStart);
                SkipWhiteSpace(context);
                TryConsume(context, ':');
                TryConsume(context, '=');
                SkipWhiteSpace(context);
                if (TryReadString(context, out var basisVal)) {
                    basis = basisVal;
                    basisSpan = new SourceSpan(basisStart, context.Scanner.Cursor.Offset - basisStart, basisLine, basisCol);
                    ConsumeSeparator(context);
                    continue;
                }
                var (bLine, bCol) = GetLineAndColumn(source, context.Scanner.Cursor.Offset);
                diagnostics.ReportError(PuckDiagnosticCodes.Syntax, "Expected string path after 'basis'", new SourceSpan(context.Scanner.Cursor.Offset, 1, bLine, bCol));
                SynchronizeToStatementBoundary(context);
                continue;
            }

            if (context.Scanner.Cursor.Current == '}') {
                var (bLine, bCol) = GetLineAndColumn(source, context.Scanner.Cursor.Offset);
                diagnostics.ReportError(PuckDiagnosticCodes.Syntax, "Unexpected closing brace '}' at document level", new SourceSpan(context.Scanner.Cursor.Offset, 1, bLine, bCol));
                context.Scanner.Cursor.Advance();
                SkipWhiteSpace(context);
                continue;
            }

            var statement = ParseStatement(context, diagnostics);
            if (statement is not null) {
                statements.Add(statement);
            }

            ConsumeSeparator(context);
        }

        var (line, col) = GetLineAndColumn(source, 0);
        var doc = new DocumentNode(
            Schema: schema,
            Basis: basis,
            Statements: statements,
            Offset: 0,
            Length: source.Length,
            Line: line,
            Column: col
        ) {
            BasisSpan = (basisSpan.Line > 0) ? basisSpan : new SourceSpan(0, source.Length, line, col),
        };

        return new CompilationResult<DocumentNode>(doc, diagnostics);
    }

    /// <summary>Parses the entire Puck source text into a <see cref="DocumentNode"/>.</summary>
    /// <param name="source">The source text of the Puck file.</param>
    /// <param name="defaultSchema">The default schema tag if omitted by author.</param>
    /// <returns>The root document node.</returns>
    /// <exception cref="PuckParseException">Thrown when a syntax error is encountered.</exception>
    public static DocumentNode ParseDocument(string source, string? defaultSchema = null) {
        var result = ParseDocumentWithDiagnostics(source, defaultSchema);
        if (result.Diagnostics.HasErrors) {
            var firstError = result.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
            throw new PuckParseException(firstError.Message, firstError.Span.Line, firstError.Span.Column, firstError.Span.Offset);
        }
        return result.Value!;
    }

    /// <summary>Parses an expression string into an <see cref="ExpressionNode"/>.</summary>
    /// <param name="source">The expression source text.</param>
    /// <returns>The parsed expression AST.</returns>
    public static ExpressionNode ParseExpression(string source) {
        ArgumentNullException.ThrowIfNull(source);

        var context = CreateContext(source);
        SkipWhiteSpace(context);
        var expr = ParseExpression(context);
        SkipWhiteSpace(context);

        if (!context.Scanner.Cursor.Eof) {
            throw CreateException(context, $"Unexpected token '{context.Scanner.Cursor.Current}' after expression");
        }

        return expr;
    }

    private static StatementNode ParseStatement(ParseContext context, DiagnosticBag? diagnostics = null) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        if (cursor.Eof) {
            throw CreateException(context, "Unexpected end of input while expecting statement");
        }

        try {
            return ParseStatementCore(context, diagnostics);
        } catch (PuckParseException ex) {
            if (diagnostics is null) {
                throw;
            }
            diagnostics.ReportError(ex.Code, ex.Message, new SourceSpan(ex.Offset, Math.Max(1, cursor.Offset - ex.Offset), ex.Line, ex.Column));
            SynchronizeToStatementBoundary(context);
            var len = Math.Max(1, cursor.Offset - startOffset);
            return new ErrorStatementNode(ex.Message, startOffset, len, line, col);
        } catch (Exception ex) {
            if (diagnostics is null) {
                throw;
            }
            diagnostics.ReportError(PuckDiagnosticCodes.Syntax, ex.Message, new SourceSpan(startOffset, Math.Max(1, cursor.Offset - startOffset), line, col));
            SynchronizeToStatementBoundary(context);
            var len = Math.Max(1, cursor.Offset - startOffset);
            return new ErrorStatementNode(ex.Message, startOffset, len, line, col);
        }
    }

    private static StatementNode ParseStatementCore(ParseContext context, DiagnosticBag? diagnostics) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        if (TryMatchKeyword(context, "let")) {
            SkipWhiteSpace(context);
            if (!TryReadIdentifier(context, out var varName)) {
                throw CreateException(context, "Expected identifier after 'let'");
            }

            SkipWhiteSpace(context);
            if (!TryConsume(context, '=') && !TryConsume(context, ':')) {
                throw CreateException(context, $"Expected '=' or ':' after variable name '{varName}'");
            }

            var val = ParseExpression(context);
            var len = context.Scanner.Cursor.Offset - startOffset;
            return new LetNode(Name: varName, Value: val, Offset: startOffset, Length: len, Line: line, Column: col);
        }

        if (TryMatchKeyword(context, "import")) {
            SkipWhiteSpace(context);
            if (!TryReadString(context, out var importPath)) {
                throw CreateException(context, "Expected string path after 'import'");
            }

            string? alias = null;
            SkipWhiteSpace(context);
            if (TryMatchKeyword(context, "as")) {
                SkipWhiteSpace(context);
                if (!TryReadIdentifier(context, out alias)) {
                    throw CreateException(context, "Expected alias identifier after 'as'");
                }
            }

            var len = context.Scanner.Cursor.Offset - startOffset;
            return new ImportNode(Path: importPath, Alias: alias, Offset: startOffset, Length: len, Line: line, Column: col);
        }

        if (TryMatchKeyword(context, "export")) {
            SkipWhiteSpace(context);
            if (!TryReadIdentifier(context, out var facet)) {
                throw CreateException(context, "Expected export facet ('read', 'action', 'binding') after 'export'");
            }

            var names = new List<string>();

            // A facet with zero names ends its line right after the facet word (an empty exported array
            // round-trips this way), so names crossing a newline must be told apart from the next statement: a
            // continuation line is one indented deeper than the `export` keyword's own column. Without that test a
            // bare `export action` swallows the following statement's leading token as an export name.
            if (!AtEndOfLogicalStatement(context.Scanner.Buffer, context.Scanner.Cursor.Offset)
                || ContinuesOnDeeperIndentedLine(context.Scanner.Buffer, context.Scanner.Cursor.Offset, col)) {
                while (true) {
                    SkipWhiteSpace(context);
                    if (TryReadIdentifier(context, out var exportName)) {
                        names.Add(exportName);
                    } else if (TryReadString(context, out var exportStr)) {
                        names.Add(exportStr);
                    } else {
                        break;
                    }

                    SkipWhiteSpace(context);
                    if (!TryConsume(context, ',')) {
                        break;
                    }
                }
            }

            var len = context.Scanner.Cursor.Offset - startOffset;
            return new ExportNode(Facet: facet, Names: names, Offset: startOffset, Length: len, Line: line, Column: col);
        }

        if (TryMatchKeyword(context, "template")) {
            SkipWhiteSpace(context);
            if (!TryReadIdentifier(context, out var templateName)) {
                throw CreateException(context, "Expected identifier after 'template'");
            }

            SkipWhiteSpace(context);
            if (!TryConsume(context, '(')) {
                throw CreateException(context, $"Expected '(' after template name '{templateName}'");
            }

            var parameters = new List<TemplateParameterNode>();
            SkipWhiteSpace(context);
            while (!cursor.Eof && cursor.Current != ')') {
                var paramStart = cursor.Offset;
                var (pLine, pCol) = GetLineAndColumn(context.Scanner.Buffer, paramStart);
                if (!TryReadIdentifier(context, out var paramName)) {
                    throw CreateException(context, "Expected parameter name in template definition");
                }

                ExpressionNode? defaultVal = null;
                SkipWhiteSpace(context);
                if (TryConsume(context, '=')) {
                    defaultVal = ParseExpression(context);
                }

                var pLen = cursor.Offset - paramStart;
                parameters.Add(new TemplateParameterNode(paramName, defaultVal, paramStart, pLen, pLine, pCol));

                SkipWhiteSpace(context);
                if (TryConsume(context, ',')) {
                    SkipWhiteSpace(context);
                    continue;
                }
                break;
            }

            if (!TryConsume(context, ')')) {
                throw CreateException(context, "Expected ')' closing template parameters");
            }

            SkipWhiteSpace(context);
            if (cursor.Current != '{') {
                throw CreateException(context, $"Expected '{{' body for template '{templateName}'");
            }

            var body = ParseBlock(context, identifier: templateName, name: null, target: null, startOffset: startOffset, line: line, col: col, diagnostics: diagnostics);
            var len = cursor.Offset - startOffset;
            return new TemplateNode(Name: templateName, Parameters: parameters, Body: body, Offset: startOffset, Length: len, Line: line, Column: col);
        }

        if (TryMatchKeyword(context, "rule")) {
            return ParseRuleBlock(context, startOffset, line, col, diagnostics);
        }

        // Addon capability request: request Mutate "section:state"
        if (TryMatchKeyword(context, "request")) {
            SkipWhiteSpace(context);
            if (!TryReadIdentifier(context, out var capability)) {
                throw CreateException(context, "Expected capability name (e.g. Mutate, Observe, Emit) after 'request'");
            }

            SkipWhiteSpace(context);
            if (!TryReadString(context, out var subject) && !TryReadIdentifier(context, out subject)) {
                throw CreateException(context, $"Expected subject string or identifier after capability '{capability}'");
            }

            var len = cursor.Offset - startOffset;
            return new AddonRequestNode(Capability: capability, Subject: subject, Offset: startOffset, Length: len, Line: line, Column: col);
        }

        // Addon machine-memory watch: watchMemory screen: 0, address: 0x02000000, length: 4
        if (TryMatchKeyword(context, "watchMemory")) {
            SkipWhiteSpace(context);
            var screen = 0;
            long address = 0;
            var watchLength = 1;

            while (!cursor.Eof) {
                SkipWhiteSpace(context);
                if (!TryReadIdentifier(context, out var key)) {
                    break;
                }
                SkipWhiteSpace(context);
                TryConsume(context, ':');
                TryConsume(context, '=');
                SkipWhiteSpace(context);
                var valExpr = ParseExpression(context);
                if (string.Equals(key, "screen", StringComparison.OrdinalIgnoreCase) && valExpr is LiteralExpressionNode { Value: long sVal }) {
                    screen = (int)sVal;
                } else if (string.Equals(key, "address", StringComparison.OrdinalIgnoreCase) && valExpr is LiteralExpressionNode { Value: long aVal }) {
                    address = aVal;
                } else if ((string.Equals(key, "length", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "watchLength", StringComparison.OrdinalIgnoreCase)) && valExpr is LiteralExpressionNode { Value: long lVal }) {
                    watchLength = (int)lVal;
                }

                SkipWhiteSpace(context);
                if (!TryConsume(context, ',')) {
                    break;
                }
            }
            var len = cursor.Offset - startOffset;
            return new AddonMemoryWatchNode(Screen: screen, Address: address, WatchLength: watchLength, Offset: startOffset, Length: len, Line: line, Column: col);
        }

        // Identifier or string starting token
        if (!TryReadIdentifierOrString(context, out var firstId)) {
            throw CreateException(context, $"Unexpected token '{cursor.Current}' while parsing statement");
        }

        // Raw position right after `firstId`, before the whitespace skip below can erase the newline a bare
        // keyword statement (§4.2's `solid` flag) depends on — see the fallback at the bottom of this method.
        var afterFirstIdPosition = cursor.Position;
        var afterFirstIdOffset = cursor.Offset;

        RefuseMisplacedConstruct(context, firstId, afterFirstIdOffset, startOffset, line, col);

        SkipWhiteSpace(context);

        // Check if invocation: name(...)
        if (cursor.Current == '(') {
            var callExpr = ParseCallExpression(context, firstId, startOffset, line, col);
            var len = cursor.Offset - startOffset;
            return new ExpressionStatementNode(Expression: callExpr, Offset: startOffset, Length: len, Line: line, Column: col);
        }

        // Check if property assignment: key: expr or key = expr
        if (cursor.Current == ':' || cursor.Current == '=') {
            cursor.Advance();
            var valExpr = ParseExpression(context);
            var len = cursor.Offset - startOffset;
            return new PropertyNode(Name: firstId, Value: valExpr, Offset: startOffset, Length: len, Line: line, Column: col);
        }

        // Check if inline array property: key [ ... ]
        if (cursor.Current == '[') {
            var arrayExpr = ParseArrayExpression(context);
            var len = cursor.Offset - startOffset;
            return new PropertyNode(Name: firstId, Value: arrayExpr, Offset: startOffset, Length: len, Line: line, Column: col);
        }

        // Check if block without name: firstId { ... }
        if (cursor.Current == '{') {
            return ParseBlock(context, identifier: firstId, name: null, target: null, startOffset: startOffset, line: line, col: col, diagnostics: diagnostics);
        }

        // Otherwise, named block or block with target:
        // Examples: layout "study" { ... }
        //           solid Prism "p1" { ... }
        //           seatRig "study" { ... }
        if (TryReadIdentifierOrString(context, out var secondId)) {
            SkipWhiteSpace(context);
            if (cursor.Current == '{') {
                return ParseBlock(context, identifier: firstId, name: secondId, target: null, startOffset: startOffset, line: line, col: col, diagnostics: diagnostics);
            }

            if (TryReadIdentifierOrString(context, out var thirdId)) {
                SkipWhiteSpace(context);
                if (cursor.Current == '{') {
                    return ParseBlock(context, identifier: firstId, name: thirdId, target: secondId, startOffset: startOffset, line: line, col: col, diagnostics: diagnostics);
                }
            }
        }

        // Bare keyword statement: nothing else was found on `firstId`'s own line, and no block/property/call shape
        // matched — e.g. the `solid` flag inside a `placement { }` row (§4.2). A last resort, tried only once every
        // other statement shape above has failed, so it can never shadow a multi-line block header.
        if (context.Scanner.Buffer[startOffset] != '"' && AtEndOfLogicalStatement(context.Scanner.Buffer, afterFirstIdOffset)) {
            cursor.ResetPosition(afterFirstIdPosition);
            var flagLen = afterFirstIdOffset - startOffset;
            return new FlagStatementNode(Name: firstId, Offset: startOffset, Length: flagLen, Line: line, Column: col);
        }

        throw CreateException(context, $"Unexpected token sequence after '{firstId}'");
    }

    private static BlockNode ParseBlock(ParseContext context, string identifier, string? name, string? target, int startOffset, int line, int col, DiagnosticBag? diagnostics = null) {
        if (!TryConsume(context, '{')) {
            throw CreateException(context, $"Expected '{{' starting block for '{identifier}'");
        }

        var statements = new List<StatementNode>();
        SkipWhiteSpace(context);

        while (!context.Scanner.Cursor.Eof && context.Scanner.Cursor.Current != '}') {
            var stmt = ParseStatement(context, diagnostics);
            if (stmt is not null) {
                statements.Add(stmt);
            }
            ConsumeSeparator(context);
        }

        if (!TryConsume(context, '}')) {
            throw CreateException(context, $"Expected '}}' closing block for '{identifier}'");
        }

        // A placement row (§4.2) authoring both the bare `solid` flag and an explicit `solid: { }` override leaves
        // the emitter no single answer for the field — diagnosed here, where both spellings are already visible.
        if (string.Equals(identifier, "placement", StringComparison.Ordinal)) {
            var sawBareSolid = statements.Exists(static s => s is FlagStatementNode { Name: "solid" });
            var sawSolidProperty = statements.Exists(static s => s is PropertyNode { Name: "solid" });
            if (sawBareSolid && sawSolidProperty) {
                diagnostics?.ReportError(PuckDiagnosticCodes.SolidSpelledTwice, $"placement '{name}' authors both the bare 'solid' flag and an explicit 'solid: {{ }}' override", new SourceSpan(startOffset, context.Scanner.Cursor.Offset - startOffset, line, col));
            }
        }

        var len = context.Scanner.Cursor.Offset - startOffset;
        return new BlockNode(Identifier: identifier, Name: name, Target: target, Statements: statements, Offset: startOffset, Length: len, Line: line, Column: col);
    }

    private static void SynchronizeToStatementBoundary(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var braceDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        while (!cursor.Eof) {
            var c = cursor.Current;

            if (c == '{') {
                braceDepth++;
                cursor.Advance();
                continue;
            }
            if (c == '}') {
                if (braceDepth > 0) {
                    braceDepth--;
                    cursor.Advance();
                    continue;
                }
                // Stop at closing brace of the current block without consuming it,
                // but if we started right on it, consume it to prevent infinite loop
                if (cursor.Offset == startOffset) {
                    cursor.Advance();
                }
                break;
            }
            if (c == '(') {
                parenDepth++;
                cursor.Advance();
                continue;
            }
            if (c == ')') {
                if (parenDepth > 0) {
                    parenDepth--;
                }
                cursor.Advance();
                continue;
            }
            if (c == '[') {
                bracketDepth++;
                cursor.Advance();
                continue;
            }
            if (c == ']') {
                if (bracketDepth > 0) {
                    bracketDepth--;
                }
                cursor.Advance();
                continue;
            }

            // Check for statement delimiters when not inside nested parens/brackets
            if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0) {
                if (c == ';') {
                    cursor.Advance();
                    SkipWhiteSpace(context);
                    return;
                }
                if (c == '\n' || c == '\r') {
                    cursor.Advance();
                    SkipWhiteSpace(context);
                    return;
                }
            }

            cursor.Advance();
        }
        SkipWhiteSpace(context);
    }

    private static ExpressionNode ParseExpression(ParseContext context) {
        return ParseRangeExpression(context);
    }

    private static ExpressionNode ParseRangeExpression(ParseContext context) {
        var left = ParseAdditiveExpression(context);
        SkipWhiteSpace(context);

        if (context.Scanner.Cursor.Current == '.' && context.Scanner.Cursor.PeekNext() == '.') {
            context.Scanner.Cursor.Advance(2);
            var right = ParseAdditiveExpression(context);
            var len = context.Scanner.Cursor.Offset - left.Offset;
            return new RangeExpressionNode(Start: left, End: right, Offset: left.Offset, Length: len, Line: left.Line, Column: left.Column);
        }

        return left;
    }

    private static ExpressionNode ParseAdditiveExpression(ParseContext context) {
        var left = ParseMultiplicativeExpression(context);

        while (true) {
            SkipWhiteSpace(context);
            var cur = context.Scanner.Cursor.Current;
            if (cur != '+' && cur != '-') {
                break;
            }

            var op = cur.ToString();
            context.Scanner.Cursor.Advance();
            var right = ParseMultiplicativeExpression(context);
            var len = context.Scanner.Cursor.Offset - left.Offset;
            left = new BinaryExpressionNode(Left: left, Operator: op, Right: right, Offset: left.Offset, Length: len, Line: left.Line, Column: left.Column);
        }

        return left;
    }

    private static ExpressionNode ParseMultiplicativeExpression(ParseContext context) {
        var left = ParsePostfixExpression(context);

        while (true) {
            SkipWhiteSpace(context);
            var cur = context.Scanner.Cursor.Current;
            if (cur != '*' && cur != '/') {
                break;
            }

            var op = cur.ToString();
            context.Scanner.Cursor.Advance();
            var right = ParsePostfixExpression(context);
            var len = context.Scanner.Cursor.Offset - left.Offset;
            left = new BinaryExpressionNode(Left: left, Operator: op, Right: right, Offset: left.Offset, Length: len, Line: left.Line, Column: left.Column);
        }

        return left;
    }

    private static ExpressionNode ParsePostfixExpression(ParseContext context) {
        var expr = ParsePrimaryExpression(context);

        while (true) {
            SkipWhiteSpace(context);
            var cur = context.Scanner.Cursor.Current;

            // Member access: expr.member
            if (cur == '.' && context.Scanner.Cursor.PeekNext() != '.') {
                context.Scanner.Cursor.Advance();
                SkipWhiteSpace(context);
                if (!TryReadIdentifier(context, out var member)) {
                    throw CreateException(context, "Expected member identifier after '.'");
                }
                var len = context.Scanner.Cursor.Offset - expr.Offset;
                expr = new MemberAccessExpressionNode(Target: expr, Member: member, Offset: expr.Offset, Length: len, Line: expr.Line, Column: expr.Column);
                continue;
            }

            // Function call on identifier expression: expr(arg1, arg2)
            if (cur == '(' && expr is IdentifierExpressionNode identExpr) {
                expr = ParseCallExpression(context, identExpr.Name, identExpr.Offset, identExpr.Line, identExpr.Column);
                continue;
            }

            break;
        }

        return expr;
    }

    private static ExpressionNode ParsePrimaryExpression(ParseContext context) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        if (cursor.Eof) {
            throw CreateException(context, "Unexpected end of file while expecting expression");
        }

        // Parentheses: ( expr )
        if (cursor.Current == '(') {
            cursor.Advance();
            var inner = ParseExpression(context);
            SkipWhiteSpace(context);
            if (!TryConsume(context, ')')) {
                throw CreateException(context, "Expected ')' closing parenthesized expression");
            }
            return inner;
        }

        // Array: [ ... ]
        if (cursor.Current == '[') {
            return ParseArrayExpression(context);
        }

        // Object: { ... }
        if (cursor.Current == '{') {
            return ParseObjectExpression(context);
        }

        // Hex color: #RRGGBB / #RRGGBBAA
        if (cursor.Current == '#') {
            return ParseHexColor(context);
        }

        // String literal: "..."
        if (cursor.Current == '"') {
            if (TryReadString(context, out var strVal)) {
                var len = cursor.Offset - startOffset;
                return new LiteralExpressionNode(Value: strVal, Unit: null, Offset: startOffset, Length: len, Line: line, Column: col);
            }
            throw CreateException(context, "Malformed string literal");
        }

        // Number literal: integer or decimal, optionally negative
        if (char.IsDigit(cursor.Current) || ((cursor.Current == '-' || cursor.Current == '+') && (char.IsDigit(cursor.PeekNext()) || cursor.PeekNext() == '.'))) {
            return ParseNumberWithOptionalUnit(context);
        }

        // Identifier, boolean, or null — extended (TryReadExtendedName) so a reserved-channel argument like
        // `$board:cellOf:board:placement:$each` or a `$zones[...]`-folded selector (§9-A8) reads as one token here,
        // the same way ExpressionSpelling's own lexer folds it.
        if (TryReadExtendedName(context, out var ident)) {
            var len = cursor.Offset - startOffset;

            if (string.Equals(ident, "true", StringComparison.Ordinal)) {
                return new LiteralExpressionNode(Value: true, Unit: null, Offset: startOffset, Length: len, Line: line, Column: col);
            }
            if (string.Equals(ident, "false", StringComparison.Ordinal)) {
                return new LiteralExpressionNode(Value: false, Unit: null, Offset: startOffset, Length: len, Line: line, Column: col);
            }
            if (string.Equals(ident, "null", StringComparison.Ordinal)) {
                return new LiteralExpressionNode(Value: null, Unit: null, Offset: startOffset, Length: len, Line: line, Column: col);
            }

            return new IdentifierExpressionNode(Name: ident, Offset: startOffset, Length: len, Line: line, Column: col);
        }

        throw CreateException(context, $"Unexpected token '{cursor.Current}' while parsing expression");
    }

    private static LiteralExpressionNode ParseNumberWithOptionalUnit(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        // Read sign if present
        var isNegative = false;
        if (cursor.Current == '-') {
            isNegative = true;
            cursor.Advance();
        } else if (cursor.Current == '+') {
            cursor.Advance();
        }

        // Check for hex literal: 0x...
        if (cursor.Current == '0' && (cursor.PeekNext() == 'x' || cursor.PeekNext() == 'X')) {
            cursor.Advance(2);
            var hexStart = cursor.Offset;
            while (!cursor.Eof && (IsHexChar(cursor.Current) || cursor.Current == '_')) {
                cursor.Advance();
            }
            var hexLen = cursor.Offset - hexStart;
            if (hexLen == 0) {
                throw CreateException(context, "Expected hexadecimal digits after '0x'");
            }
            var rawHex = context.Scanner.Buffer.Substring(hexStart, hexLen).Replace("_", "");
            if (!long.TryParse(rawHex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hexVal)) {
                throw CreateException(context, $"Invalid hex integer literal '0x{rawHex}'");
            }
            var totalHexVal = isNegative ? -hexVal : hexVal;
            return new LiteralExpressionNode(Value: totalHexVal, Unit: null, Offset: startOffset, Length: cursor.Offset - startOffset, Line: line, Column: col);
        }

        var numStart = cursor.Offset;
        var hasDecimalPoint = false;

        while (!cursor.Eof) {
            if (char.IsDigit(cursor.Current) || cursor.Current == '_') {
                cursor.Advance();
            } else if (cursor.Current == '.' && !hasDecimalPoint && char.IsDigit(cursor.PeekNext())) {
                hasDecimalPoint = true;
                cursor.Advance();
            } else {
                break;
            }
        }

        var numLength = cursor.Offset - numStart;
        if (numLength == 0) {
            throw CreateException(context, "Expected number digits");
        }

        // Scientific notation is part of the number, never a unit suffix: a canonical document holds small
        // magnitudes as "8.57E-05", and reading the 'E' as a unit both loses the exponent and mis-reports the field.
        var hasExponent = false;
        if (!cursor.Eof && (cursor.Current is 'e' or 'E')) {
            var afterExponent = cursor.Offset + 1;
            var buffer = context.Scanner.Buffer;
            if (afterExponent < buffer.Length && (buffer[afterExponent] is '+' or '-')) {
                afterExponent++;
            }
            if (afterExponent < buffer.Length && char.IsAsciiDigit(buffer[afterExponent])) {
                hasExponent = true;
                cursor.Advance(afterExponent - cursor.Offset);
                while (!cursor.Eof && char.IsAsciiDigit(cursor.Current)) {
                    cursor.Advance();
                }
                numLength = cursor.Offset - numStart;
            }
        }

        var cleanNumStr = context.Scanner.Buffer.Substring(numStart, numLength).Replace("_", "");
        var signedNumStr = isNegative ? "-" + cleanNumStr : cleanNumStr;
        object numVal;
        if (hasDecimalPoint || hasExponent) {
            if (!double.TryParse(signedNumStr, System.Globalization.CultureInfo.InvariantCulture, out var d)) {
                throw CreateException(context, $"Invalid floating point literal '{signedNumStr}'");
            }
            numVal = d;
        } else {
            if (!long.TryParse(signedNumStr, System.Globalization.CultureInfo.InvariantCulture, out var l)) {
                if (ulong.TryParse(cleanNumStr, System.Globalization.CultureInfo.InvariantCulture, out var ul) && !isNegative) {
                    numVal = ul;
                } else if (double.TryParse(signedNumStr, System.Globalization.CultureInfo.InvariantCulture, out var dbl)) {
                    numVal = dbl;
                } else {
                    throw CreateException(context, $"Invalid integer literal '{signedNumStr}'");
                }
            } else {
                numVal = l;
            }
        }

        // Check for unit suffix: s, ms, hz, rad, deg, m, mm, cm, %, pct
        string? unit = null;
        if (!cursor.Eof && (char.IsLetter(cursor.Current) || cursor.Current == '%')) {
            var unitStart = cursor.Offset;
            if (cursor.Current == '%') {
                cursor.Advance();
            } else {
                while (!cursor.Eof && char.IsLetter(cursor.Current)) {
                    cursor.Advance();
                }
            }
            unit = context.Scanner.Buffer.Substring(unitStart, cursor.Offset - unitStart);
        }

        var totalLen = cursor.Offset - startOffset;
        return new LiteralExpressionNode(Value: numVal, Unit: unit, Offset: startOffset, Length: totalLen, Line: line, Column: col);
    }

    private static ColorExpressionNode ParseHexColor(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        cursor.Advance(); // consume '#'
        var hexStart = cursor.Offset;

        while (!cursor.Eof && IsHexChar(cursor.Current)) {
            cursor.Advance();
        }

        var hexLen = cursor.Offset - hexStart;
        if (hexLen is not (3 or 4 or 6 or 8)) {
            throw CreateException(context, $"Invalid hex color length #{context.Scanner.Buffer.Substring(hexStart, hexLen)}");
        }

        var hex = "#" + context.Scanner.Buffer.Substring(hexStart, hexLen);
        return new ColorExpressionNode(Hex: hex, Offset: startOffset, Length: cursor.Offset - startOffset, Line: line, Column: col);
    }

    private static ArrayExpressionNode ParseArrayExpression(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        cursor.Advance(); // consume '['
        var elements = new List<ExpressionNode>();
        SkipWhiteSpace(context);

        while (!cursor.Eof && cursor.Current != ']') {
            var elem = ParseExpression(context);
            elements.Add(elem);

            SkipWhiteSpace(context);
            if (cursor.Current == ',') {
                cursor.Advance();
                SkipWhiteSpace(context);
            }
        }

        SkipWhiteSpace(context);
        if (!TryConsume(context, ']')) {
            throw CreateException(context, "Expected ']' closing array");
        }

        return new ArrayExpressionNode(Elements: elements, Offset: startOffset, Length: cursor.Offset - startOffset, Line: line, Column: col);
    }

    private static ObjectExpressionNode ParseObjectExpression(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, startOffset);

        cursor.Advance(); // consume '{'
        var properties = new List<PropertyNode>();
        SkipWhiteSpace(context);

        while (!cursor.Eof && cursor.Current != '}') {
            var propStart = cursor.Offset;
            var (pLine, pCol) = GetLineAndColumn(context.Scanner.Buffer, propStart);

            if (!TryReadIdentifierOrString(context, out var propKey)) {
                throw CreateException(context, $"Expected property key inside object, got '{cursor.Current}'");
            }

            SkipWhiteSpace(context);
            if (cursor.Current == ':' || cursor.Current == '=') {
                cursor.Advance();
                var valExpr = ParseExpression(context);
                properties.Add(new PropertyNode(Name: propKey, Value: valExpr, Offset: propStart, Length: cursor.Offset - propStart, Line: pLine, Column: pCol));
            } else if (cursor.Current == '{') {
                var nestedObj = ParseObjectExpression(context);
                properties.Add(new PropertyNode(Name: propKey, Value: nestedObj, Offset: propStart, Length: cursor.Offset - propStart, Line: pLine, Column: pCol));
            } else if (cursor.Current == '[') {
                var nestedArr = ParseArrayExpression(context);
                properties.Add(new PropertyNode(Name: propKey, Value: nestedArr, Offset: propStart, Length: cursor.Offset - propStart, Line: pLine, Column: pCol));
            } else {
                throw CreateException(context, $"Expected ':', '=', '{{', or '[' after property key '{propKey}'");
            }

            ConsumeSeparator(context);
        }

        if (!TryConsume(context, '}')) {
            throw CreateException(context, "Expected '}' closing object expression");
        }

        return new ObjectExpressionNode(Properties: properties, Offset: startOffset, Length: cursor.Offset - startOffset, Line: line, Column: col);
    }

    private static CallExpressionNode ParseCallExpression(ParseContext context, string functionName, int startOffset, int line, int col) {
        var cursor = context.Scanner.Cursor;
        if (!TryConsume(context, '(')) {
            throw CreateException(context, $"Expected '(' after function name '{functionName}'");
        }

        var arguments = new List<ArgumentNode>();
        SkipWhiteSpace(context);

        while (!cursor.Eof && cursor.Current != ')') {
            var argStart = cursor.Offset;
            var (aLine, aCol) = GetLineAndColumn(context.Scanner.Buffer, argStart);
            string? argName = null;

            // Check if named argument: name: expr
            var peekPosition = cursor.Position;
            if (TryReadIdentifier(context, out var candidateName)) {
                SkipWhiteSpace(context);
                if (cursor.Current == ':') {
                    cursor.Advance();
                    argName = candidateName;
                } else {
                    // Reset to before candidateName
                    cursor.ResetPosition(peekPosition);
                }
            }

            var val = ParseExpression(context);
            arguments.Add(new ArgumentNode(Name: argName, Value: val, Offset: argStart, Length: cursor.Offset - argStart, Line: aLine, Column: aCol));

            SkipWhiteSpace(context);
            if (cursor.Current == ',') {
                cursor.Advance();
                SkipWhiteSpace(context);
            } else {
                break;
            }
        }

        if (!TryConsume(context, ')')) {
            throw CreateException(context, $"Expected ')' closing call to '{functionName}'");
        }

        var totalLen = cursor.Offset - startOffset;
        return new CallExpressionNode(Name: functionName, Arguments: arguments, Offset: startOffset, Length: totalLen, Line: line, Column: col);
    }

    private static bool TryMatchKeyword(ParseContext context, string keyword) {
        var cursor = context.Scanner.Cursor;
        var offset = cursor.Offset;

        if (cursor.Buffer.Length - offset < keyword.Length) {
            return false;
        }

        if (!cursor.Buffer.AsSpan(offset, keyword.Length).SequenceEqual(keyword.AsSpan())) {
            return false;
        }

        var nextOffset = offset + keyword.Length;
        if (nextOffset < cursor.Buffer.Length) {
            var nextChar = cursor.Buffer[nextOffset];
            if (char.IsLetterOrDigit(nextChar) || nextChar == '_' || nextChar == '$') {
                return false;
            }
        }

        cursor.Advance(keyword.Length);
        return true;
    }

    private static bool TryReadIdentifierOrString(ParseContext context, out string value) {
        SkipWhiteSpace(context);
        if (!context.Scanner.Cursor.Eof && context.Scanner.Cursor.Current == '"') {
            return TryReadString(context, out value);
        }
        return TryReadIdentifier(context, out value);
    }

    private static bool TryReadIdentifier(ParseContext context, out string identifier) {
        SkipWhiteSpace(context);
        var cursor = context.Scanner.Cursor;
        var start = cursor.Offset;

        if (cursor.Eof) {
            identifier = string.Empty;
            return false;
        }

        var first = cursor.Current;
        if (!char.IsLetter(first) && first != '_' && first != '$') {
            identifier = string.Empty;
            return false;
        }

        cursor.Advance();
        while (!cursor.Eof) {
            var cur = cursor.Current;
            if (char.IsLetterOrDigit(cur) || cur == '_' || cur == '$') {
                cursor.Advance();
            } else {
                break;
            }
        }

        identifier = context.Scanner.Buffer.Substring(start, cursor.Offset - start);
        return true;
    }

    private static bool TryReadString(ParseContext context, out string text) {
        SkipWhiteSpace(context);
        var strParser = Terms.String();
        var result = new ParseResult<TextSpan>();
        if (strParser.Parse(context, ref result)) {
            text = Character.DecodeString(result.Value).ToString();
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static bool TryConsume(ParseContext context, char c) {
        SkipWhiteSpace(context);
        if (!context.Scanner.Cursor.Eof && context.Scanner.Cursor.Current == c) {
            context.Scanner.Cursor.Advance();
            return true;
        }
        return false;
    }

    private static void ConsumeSeparator(ParseContext context) {
        SkipWhiteSpace(context);
        while (!context.Scanner.Cursor.Eof && (context.Scanner.Cursor.Current == ';' || context.Scanner.Cursor.Current == ',')) {
            context.Scanner.Cursor.Advance();
            SkipWhiteSpace(context);
        }
    }

    private static void SkipWhiteSpace(ParseContext context) {
        var result = new ParseResult<TextSpan>();
        WhiteSpace.Parse(context, ref result);
    }

    private static ParseContext CreateContext(string source) {
        return new ParseContext(new Scanner(source)) {
            WhiteSpaceParser = WhiteSpace
        };
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, int[]> LineStartsCache = new();

    private static int[] GetLineStarts(string buffer) =>
        LineStartsCache.GetValue(key: buffer, createValueCallback: static b => {
            var starts = new List<int> { 0 };

            for (var i = 0; (i < b.Length); i++) {
                if (b[i] == '\n') {
                    starts.Add(item: (i + 1));
                }
            }

            return starts.ToArray();
        });

    private static (int Line, int Column) GetLineAndColumn(string buffer, int offset) {
        var starts = GetLineStarts(buffer: buffer);
        var idx = Array.BinarySearch(array: starts, value: offset);

        if (idx >= 0) {
            return (Line: (idx + 1), Column: 1);
        }

        var lineIdx = (~idx - 1);

        if (lineIdx < 0) {
            lineIdx = 0;
        }

        return (Line: (lineIdx + 1), Column: (offset - starts[lineIdx] + 1));
    }

    // A construct that only exists inside one enclosing block, the block it needs, and the code that names its
    // misplacement. `Quoted` marks the ones followed by a quoted name; the rest open with an operand. Both shapes
    // are distinguishable from an ordinary property or block of the same name (`when: 120` in a capture row,
    // `transform { }` as a nested object), which stay legal wherever they appear.
    private static readonly (string Keyword, string Owner, string Code, bool Quoted)[] MisplacedConstructs = [
        ("option", "decision", PuckDiagnosticCodes.DecisionStructure, true),
        ("when", "rule", PuckDiagnosticCodes.DecisionStructure, false),
        ("bind", "rule", PuckDiagnosticCodes.DecisionStructure, false),
        ("interrupt", "decision", PuckDiagnosticCodes.DecisionStructure, false),
        ("push", "rule", PuckDiagnosticCodes.EffectOutsideEffectsBody, false),
        ("countdown", "rule", PuckDiagnosticCodes.EffectOutsideEffectsBody, false),
        ("remove", "rule", PuckDiagnosticCodes.EffectOutsideEffectsBody, false),
        ("schedule", "rule", PuckDiagnosticCodes.EffectOutsideEffectsBody, false),
    ];

    // Refuses a rule-body-only keyword found where an ordinary statement belongs, naming the block it needs instead
    // of letting the generic statement grammar fail further along the line on an unrelated token.
    private static void RefuseMisplacedConstruct(ParseContext context, string identifier, int nextOffset, int startOffset, int line, int col) {
        var buffer = context.Scanner.Buffer;
        var probe = nextOffset;
        while (probe < buffer.Length && (buffer[probe] == ' ' || buffer[probe] == '\t')) {
            probe++;
        }
        if (probe >= buffer.Length) {
            return;
        }
        var next = buffer[probe];

        foreach (var (keyword, owner, code, quoted) in MisplacedConstructs) {
            if (!string.Equals(identifier, keyword, StringComparison.Ordinal)) {
                continue;
            }
            var shapeMatches = quoted ? (next == '"') : (char.IsLetter(next) || next is '_' or '$' or '`');
            if (!shapeMatches) {
                return;
            }
            throw new PuckParseException($"'{keyword}' is only valid inside a '{owner}' body", startOffset, line, col) {
                Code = code,
            };
        }
    }

    private static PuckParseException CreateException(ParseContext context, string message) {
        var offset = context.Scanner.Cursor.Offset;
        var (line, col) = GetLineAndColumn(context.Scanner.Buffer, offset);
        return new PuckParseException(message, offset, line, col);
    }

    private static bool IsHexChar(char c) {
        return (c is >= '0' and <= '9') || (c is >= 'a' and <= 'f') || (c is >= 'A' and <= 'F');
    }
}
