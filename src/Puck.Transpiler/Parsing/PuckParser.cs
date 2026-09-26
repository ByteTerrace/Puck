using Parlot;
using Parlot.Fluent;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.Transpiler.Parsing;

/// <summary>High-performance recursive-descent parser for the Puck authoring language.</summary>
public static partial class PuckParser {
    private static readonly PuckWhiteSpaceParser WhiteSpace = new();

    private static bool IsEmbeddedLanguage(string identifier, IDocumentVocabulary? vocabulary) {
        return ((vocabulary is not null) && vocabulary.IsEmbeddedLanguage(identifier: identifier));
    }

    /// <summary>Reads the top-level schema directive by running the scanner/lexer only, skipping comments, strings, and nested blocks.</summary>
    /// <param name="source">The source text.</param>
    /// <param name="schema">The parsed top-level schema string, or null if omitted.</param>
    /// <returns>True if a top-level schema directive was found, false otherwise.</returns>
    public static bool TryReadDocumentSchema(string source, out string? schema) {
        schema = null;
        if (string.IsNullOrWhiteSpace(value: source)) {
            return false;
        }

        try {
            var context = CreateContext(source: source);

            SkipWhiteSpace(context: context);

            var braceDepth = 0;

            while (!context.Scanner.Cursor.Eof) {
                var cursor = context.Scanner.Cursor;
                var c = cursor.Current;

                if (c == '{') {
                    braceDepth++;
                    cursor.Advance();
                    SkipWhiteSpace(context: context);
                    continue;
                }

                if (c == '}') {
                    if (braceDepth > 0) {
                        braceDepth--;
                    }
                    cursor.Advance();
                    SkipWhiteSpace(context: context);
                    continue;
                }

                // Strings (raw, interpolated, or regular)
                if ((c == '"') || ((c == '$') && (cursor.PeekNext() == '"'))) {
                    if (TryReadName(admitted: NameForms.Interpolated | NameForms.String, context: context, spelling: out _, text: out _)) {
                        SkipWhiteSpace(context: context);
                        continue;
                    }
                }

                // Numbers: digits or negative number
                if (char.IsDigit(c: c) || ((c == '-') && char.IsDigit(c: cursor.PeekNext()))) {
                    cursor.Advance();
                    while (!cursor.Eof && (char.IsLetterOrDigit(c: cursor.Current) || (cursor.Current == '.') || (cursor.Current == '_'))) {
                        cursor.Advance();
                    }
                    SkipWhiteSpace(context: context);
                    continue;
                }

                // Identifiers / keywords
                if (IdentifierSpelling.IsStart(character: c) || (c == IdentifierSpelling.Sigil)) {
                    if ((braceDepth == 0) && TryMatchKeyword(context: context, keyword: "schema")) {
                        SkipWhiteSpace(context: context);
                        if (TryConsume(c: ':', context: context)) {
                            SkipWhiteSpace(context: context);
                            if (TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out var schemaVal) || TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out schemaVal)) {
                                schema = schemaVal;
                                return true;
                            }
                            return false;
                        }
                    }

                    // Not a matched top-level schema:, skip identifier as whole token
                    if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out _)) {
                        SkipWhiteSpace(context: context);
                        continue;
                    }
                }

                // Any other token (punctuation, operator)
                cursor.Advance();
                SkipWhiteSpace(context: context);
            }
        } catch {
            // Ignore lexer exceptions
        }

        return false;
    }
    /// <summary>Parses the entire Puck source text into a <see cref="DocumentNode"/>, collecting all diagnostics with resilient recovery.</summary>
    /// <param name="source">The source text of the Puck file.</param>
    /// <param name="defaultSchema">The default schema tag if omitted by author.</param>
    /// <param name="diagnostics">The DiagnosticBag to collect errors and warnings into.</param>
    /// <param name="vocabulary">Optional vocabulary providing schema-specific lexical rules.</param>
    /// <returns>A CompilationResult carrying the parsed DocumentNode and DiagnosticBag.</returns>
    public static CompilationResult<DocumentNode> ParseDocumentWithDiagnostics(
        string source,
        string? defaultSchema = null,
        DiagnosticBag? diagnostics = null,
        IDocumentVocabulary? vocabulary = null
    ) {
        ArgumentNullException.ThrowIfNull(source);

        diagnostics ??= new DiagnosticBag();
        try { SourceLexemes.Validate(source: source); } catch (PuckParseException error) {
            diagnostics.ReportError(code: error.Code, message: error.Message, span: new SourceSpan(error.Offset, 1, error.Line, error.Column));
            return new CompilationResult<DocumentNode>(Diagnostics: diagnostics, Value: null);
        }
        var context = CreateContext(diagnostics: diagnostics, source: source, vocabulary: vocabulary);
        var schema = defaultSchema;
        string? basis = null;
        var basisSpan = SourceSpan.None;
        var schemaSpan = SourceSpan.None;
        var statements = new List<StatementNode>();

        SkipWhiteSpace(context: context);

        while (!context.Scanner.Cursor.Eof) {
            // Peek next token to check for header schema / basis directives
            if (TryMatchKeyword(context: context, keyword: "schema")) {
                var schemaStart = (context.Scanner.Cursor.Offset - "schema".Length);

                var (schemaLine, schemaCol) = GetLineAndColumn(buffer: source, offset: schemaStart);

                SkipWhiteSpace(context: context);
                TryConsume(c: ':', context: context);
                TryConsume(c: '=', context: context);
                SkipWhiteSpace(context: context);
                if (TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out var schemaVal) || TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out schemaVal)) {
                    schema = schemaVal;
                    schemaSpan = new SourceSpan(schemaStart, (context.Scanner.Cursor.Offset - schemaStart), schemaLine, schemaCol);
                    ConsumeSeparator(context: context);
                    continue;
                }
                var (sLine, sCol) = GetLineAndColumn(buffer: source, offset: context.Scanner.Cursor.Offset);
                diagnostics.ReportError(code: PuckDiagnosticCodes.Syntax, message: "Expected schema identifier or string after 'schema'", span: new SourceSpan(context.Scanner.Cursor.Offset, 1, sLine, sCol));
                SynchronizeToStatementBoundary(context: context);
                continue;
            }

            if (TryMatchKeyword(context: context, keyword: "basis")) {
                var basisStart = (context.Scanner.Cursor.Offset - "basis".Length);

                var (basisLine, basisCol) = GetLineAndColumn(buffer: source, offset: basisStart);
                SkipWhiteSpace(context: context);
                TryConsume(c: ':', context: context);
                TryConsume(c: '=', context: context);
                SkipWhiteSpace(context: context);
                if (TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out var basisVal)) {
                    basis = basisVal;
                    basisSpan = new SourceSpan(basisStart, (context.Scanner.Cursor.Offset - basisStart), basisLine, basisCol);
                    ConsumeSeparator(context: context);
                    continue;
                }
                var (bLine, bCol) = GetLineAndColumn(buffer: source, offset: context.Scanner.Cursor.Offset);
                diagnostics.ReportError(code: PuckDiagnosticCodes.Syntax, message: "Expected string path after 'basis'", span: new SourceSpan(context.Scanner.Cursor.Offset, 1, bLine, bCol));
                SynchronizeToStatementBoundary(context: context);
                continue;
            }

            if (context.Scanner.Cursor.Current == '}') {
                var (bLine, bCol) = GetLineAndColumn(buffer: source, offset: context.Scanner.Cursor.Offset);
                diagnostics.ReportError(code: PuckDiagnosticCodes.Syntax, message: "Unexpected closing brace '}' at document level", span: new SourceSpan(context.Scanner.Cursor.Offset, 1, bLine, bCol));
                context.Scanner.Cursor.Advance();
                SkipWhiteSpace(context: context);
                continue;
            }

            var statement = ParseStatement(context: context, diagnostics: diagnostics, schema: schema, vocabulary: vocabulary);

            if (statement is not null) {
                statements.Add(item: statement);
            }

            ConsumeSeparator(context: context);
        }

        var (line, col) = GetLineAndColumn(buffer: source, offset: 0);
        var doc = new DocumentNode(
            Schema: schema,
            Basis: basis,
            Statements: statements,
            Offset: 0,
            Length: source.Length,
            Line: line,
            Column: col
        ) {
            BasisSpan = ((basisSpan.Line > 0) ? basisSpan : new SourceSpan(0, source.Length, line, col)),
            SchemaSpan = schemaSpan,
        };

        return new CompilationResult<DocumentNode>(Diagnostics: diagnostics, Value: PuckSyntaxTrivia.Attach(document: doc, source: source));
    }
    /// <summary>Parses the entire Puck source text into a <see cref="DocumentNode"/>.</summary>
    /// <param name="source">The source text of the Puck file.</param>
    /// <param name="defaultSchema">The default schema tag if omitted by author.</param>
    /// <param name="vocabulary">Optional vocabulary providing schema-specific lexical rules.</param>
    /// <returns>The root document node.</returns>
    /// <exception cref="PuckParseException">Thrown when a syntax error is encountered.</exception>
    public static DocumentNode ParseDocument(string source, string? defaultSchema = null, IDocumentVocabulary? vocabulary = null) {
        var result = ParseDocumentWithDiagnostics(source, defaultSchema, vocabulary: vocabulary);

        if (result.Diagnostics.HasErrors) {
            var firstError = result.Diagnostics.First(predicate: d => (d.Severity == DiagnosticSeverity.Error));

            throw new PuckParseException(firstError.Message, offset: firstError.Span.Offset, line: firstError.Span.Line, column: firstError.Span.Column) { Code = firstError.Code };
        }
        return result.Value!;
    }
    /// <summary>Parses an expression string into an <see cref="ExpressionNode"/>.</summary>
    /// <param name="source">The expression source text.</param>
    /// <returns>The parsed expression AST.</returns>
    public static ExpressionNode ParseExpression(string source) {
        ArgumentNullException.ThrowIfNull(source);
        SourceLexemes.Validate(source: source);

        var context = CreateContext(source: source);

        SkipWhiteSpace(context: context);
        var expr = ParseExpression(context: context);

        SkipWhiteSpace(context: context);

        if (!context.Scanner.Cursor.Eof) {
            throw CreateException(context: context, message: $"Unexpected token '{context.Scanner.Cursor.Current}' after expression");
        }

        return expr;
    }

    private static StatementNode ParseStatement(ParseContext context, DiagnosticBag? diagnostics = null, string? schema = null, IDocumentVocabulary? vocabulary = null) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        if (cursor.Eof) {
            throw CreateException(context: context, message: "Unexpected end of input while expecting statement");
        }

        try {
            return ParseStatementCore(context: context, diagnostics: diagnostics, schema: schema, vocabulary: vocabulary);
        } catch (PuckParseException ex) {
            if (diagnostics is null) {
                throw;
            }
            diagnostics.ReportError(code: ex.Code, message: ex.Message, span: new SourceSpan(ex.Offset, Math.Max(val1: 1, val2: (cursor.Offset - ex.Offset)), ex.Line, ex.Column));
            SynchronizeToStatementBoundary(context: context);
            var len = Math.Max(val1: 1, val2: (cursor.Offset - startOffset));

            return new ErrorStatementNode(ex.Message, startOffset, len, line, col);
        } catch (Exception ex) {
            if (diagnostics is null) {
                throw;
            }
            diagnostics.ReportError(code: PuckDiagnosticCodes.Syntax, message: ex.Message, span: new SourceSpan(startOffset, Math.Max(val1: 1, val2: (cursor.Offset - startOffset)), line, col));
            SynchronizeToStatementBoundary(context: context);
            var len = Math.Max(val1: 1, val2: (cursor.Offset - startOffset));

            return new ErrorStatementNode(ex.Message, startOffset, len, line, col);
        }
    }
    // Recovers a rule-body statement one level inside the rule's own '{', mirroring ParseStatement above. Recovering
    // here rather than letting a fault escape to ParseStatement's own catch matters: that catch resynchronizes
    // assuming it sits at the block it started in, so a fault surfacing there from one level inside a rule body
    // leaves the rule's own closing '}' unconsumed for the document loop to mis-report as a second, spurious
    // "unexpected closing brace" (ParseFaultReportLawTests). Defined in PuckParser.Rules.cs: ParseRuleBodyStatementCore.
    private static StatementNode ParseRuleBodyStatement(ParseContext context, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        try {
            return ParseRuleBodyStatementCore(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        } catch (Exception ex) when ((diagnostics is not null)) {
            var (code, offset, faultLine, faultCol) = (((ex as PuckParseException) is { } pex)
                ? (pex.Code, pex.Offset, pex.Line, pex.Column)
                : (PuckDiagnosticCodes.Syntax, startOffset, line, col)
            );

            diagnostics.ReportError(code: code, message: ex.Message, span: new SourceSpan(offset, Math.Max(val1: 1, val2: (cursor.Offset - offset)), faultLine, faultCol));
            SynchronizeToStatementBoundary(context: context);

            return new ErrorStatementNode(ex.Message, startOffset, Math.Max(val1: 1, val2: (cursor.Offset - startOffset)), line, col);
        }
    }
    private static StatementNode ParseStatementCore(ParseContext context, DiagnosticBag? diagnostics, string? schema = null, IDocumentVocabulary? vocabulary = null) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;
        var startPosition = cursor.Position;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        // `entry world name = module(...)` marks the world a composition boots; `entry` alone is an ordinary name.
        var entry = false;

        if (TryMatchKeyword(context: context, keyword: "entry")) {
            SkipWhiteSpace(context: context);
            var worldPosition = cursor.Position;

            if (TryMatchKeyword(context: context, keyword: "world")) {
                cursor.ResetPosition(position: worldPosition);
                entry = true;
            } else {
                cursor.ResetPosition(position: startPosition);
            }
        }

        if (TryMatchKeyword(context: context, keyword: "world")) {
            SkipWhiteSpace(context: context);
            if (!entry && (cursor.Current is ':' or '[' or '{')) {
                cursor.ResetPosition(position: startPosition);
            } else {
                var name = ParseExpression(context: context);

                SkipWhiteSpace(context: context);
                if (!TryConsume(c: '=', context: context)) {
                    throw CreateException(context: context, message: "Expected '=' after world name");
                }
                if (ParseExpression(context: context) is not CallExpressionNode module) {
                    throw CreateException(context: context, message: "Expected module invocation after '='");
                }
                return new WorldDeclarationNode(name, module, entry, startOffset, (cursor.Offset - startOffset), line, col);
            }
        }

        string? linkKind = null;

        if (TryMatchKeyword(context: context, keyword: "border")) {
            SkipWhiteSpace(context: context);
            if (cursor.Current is ':' or '[' or '{') {
                cursor.ResetPosition(position: startPosition);
            } else {
                linkKind = "border";
            }
        } else if (TryMatchKeyword(context: context, keyword: "door")) {
            SkipWhiteSpace(context: context);
            if (cursor.Current is ':' or '[' or '{') {
                cursor.ResetPosition(position: startPosition);
            } else {
                linkKind = "door";
            }
        }
        if (linkKind is not null) {
            var left = ParseExpression(context: context);

            SkipWhiteSpace(context: context);
            if (!TryConsume(c: ',', context: context)) {
                throw CreateException(context: context, message: $"Expected ',' between {linkKind} endpoints");
            }
            var right = ParseExpression(context: context);

            SkipWhiteSpace(context: context);
            if (linkKind == "door") {
                if (cursor.Current == '{') {
                    throw CreateException(context: context, message: "A door takes no property block");
                }
                return new WorldLinkNode(linkKind, left, right, [], startOffset, (cursor.Offset - startOffset), line, col);
            }
            var body = ParseBlock(context, identifier: linkKind, name: null, target: null, startOffset: startOffset, line: line, col: col, diagnostics: diagnostics, schema: schema, vocabulary: vocabulary);

            return new WorldLinkNode(linkKind, left, right, body.Statements, startOffset, (cursor.Offset - startOffset), line, col) { Trivia = body.Trivia };
        }

        // `parameter pass.member = value` binds one graph parameter; `parameter:` alone is an ordinary property.
        if (TryMatchKeyword(context: context, keyword: "parameter")) {
            SkipWhiteSpace(context: context);
            if (cursor.Current is ':' or '[' or '{') {
                cursor.ResetPosition(position: startPosition);
            } else {
                if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out var passSpelling, text: out var pass)) {
                    throw CreateException(context: context, message: "Expected a pass name after 'parameter'");
                }
                SkipWhiteSpace(context: context);
                if (!TryConsume(c: '.', context: context)) {
                    throw CreateException(context: context, message: $"Expected '.' and a member after 'parameter {pass}'");
                }
                if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out var memberSpelling, text: out var member)) {
                    throw CreateException(context: context, message: $"Expected a member name after 'parameter {pass}.'");
                }
                SkipWhiteSpace(context: context);
                if (!TryConsume(c: '=', context: context)) {
                    throw CreateException(context: context, message: $"Expected '=' after 'parameter {pass}.{member}'");
                }

                var value = ParseExpression(context: context);

                return new GraphParameterNode(pass, member, value, passSpelling.Quoted, memberSpelling.Quoted, startOffset, (cursor.Offset - startOffset), line, col);
            }
        }

        if (TryMatchKeyword(context: context, keyword: "let")) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var varName)) {
                throw CreateException(context: context, message: "Expected identifier after 'let'");
            }

            SkipWhiteSpace(context: context);
            if (!TryConsume(c: '=', context: context) && !TryConsume(c: ':', context: context)) {
                throw CreateException(context: context, message: $"Expected '=' or ':' after variable name '{varName}'");
            }

            var val = ParseExpression(context: context);
            var len = (context.Scanner.Cursor.Offset - startOffset);

            return new LetNode(Column: col, Length: len, Line: line, Name: varName, Offset: startOffset, Value: val);
        }

        if (TryMatchKeyword(context: context, keyword: "for")) {
            return ParseForStatement(col: col, context: context, diagnostics: diagnostics, line: line, schema: schema, startOffset: startOffset, vocabulary: vocabulary);
        }

        if (TryMatchKeyword(context: context, keyword: "use")) {
            SkipWhiteSpace(context: context);
            var callStart = cursor.Offset;

            var (callLine, callColumn) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: callStart);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var moduleName)) {
                throw CreateException(context: context, message: "Expected module name after 'use'");
            }
            moduleName = ReadDottedIdentifier(context: context, expectedAfterDotMessage: "Expected module name after '.'", initialName: moduleName);
            string? alias = null;

            SkipWhiteSpace(context: context);
            if (TryMatchKeyword(context: context, keyword: "as")) {
                SkipWhiteSpace(context: context);
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out alias)) {
                    throw CreateException(context: context, message: "Expected alias identifier after 'as'");
                }
            }
            SkipWhiteSpace(context: context);
            var invocation = ParseCallExpression(col: callColumn, context: context, functionName: moduleName, line: callLine, startOffset: callStart);

            return new ExpressionStatementNode(invocation, startOffset, (cursor.Offset - startOffset), line, col) {
                IsUse = true,
                UseAlias = alias,
            };
        }

        if (TryMatchKeyword(context: context, keyword: "import")) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out var importPath)) {
                throw CreateException(context: context, message: "Expected string path after 'import'");
            }

            string? alias = null;

            SkipWhiteSpace(context: context);
            if (TryMatchKeyword(context: context, keyword: "as")) {
                SkipWhiteSpace(context: context);
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out alias)) {
                    throw CreateException(context: context, message: "Expected alias identifier after 'as'");
                }
            }

            var len = (context.Scanner.Cursor.Offset - startOffset);

            return new ImportNode(Alias: alias, Column: col, Length: len, Line: line, Offset: startOffset, Path: importPath);
        }

        if (TryMatchKeyword(context: context, keyword: "export")) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var facet)) {
                throw CreateException(context: context, message: "Expected export facet ('read', 'action', 'binding') after 'export'");
            }

            var names = new List<string>();
            var facetExplicit = (facet is "read" or "reads" or "action" or "actions" or "binding" or "bindings");

            if (!facetExplicit) {
                var exportName = ReadDottedIdentifier(context: context, expectedAfterDotMessage: "Expected export name after '.'", initialName: facet);

                names.Add(item: exportName);
                facet = "read";
                SkipWhiteSpace(context: context);
                while (TryConsume(c: ',', context: context)) {
                    SkipWhiteSpace(context: context);
                    if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var nextName)) {
                        throw CreateException(context: context, message: "Expected export name after ','");
                    }
                    nextName = ReadDottedIdentifier(context: context, expectedAfterDotMessage: "Expected export name after '.'", initialName: nextName);
                    names.Add(item: nextName);
                    SkipWhiteSpace(context: context);
                }
            }

            // A facet with zero names ends its line right after the facet word (an empty exported array
            // round-trips this way), so names crossing a newline must be told apart from the next statement: a
            // continuation line is one indented deeper than the `export` keyword's own column. Without that test a
            // bare `export action` swallows the following statement's leading token as an export name.
            if (facetExplicit && (!AtEndOfLogicalStatement(buffer: context.Scanner.Buffer, offset: context.Scanner.Cursor.Offset)
                || ContinuesOnDeeperIndentedLine(context.Scanner.Buffer, context.Scanner.Cursor.Offset, col))) {
                while (true) {
                    SkipWhiteSpace(context: context);
                    if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var exportName)) {
                        while (true) {
                            var beforeDot = context.Scanner.Cursor.Position;

                            SkipWhiteSpace(context: context);
                            if (!TryConsume(c: '.', context: context)) {
                                context.Scanner.Cursor.ResetPosition(position: beforeDot);
                                break;
                            }
                            SkipWhiteSpace(context: context);
                            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var member)) {
                                throw CreateException(context: context, message: "Expected export name after '.'");
                            }
                            exportName += $".{member}";
                        }
                        names.Add(item: exportName);
                    } else if (TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out var exportStr)) {
                        names.Add(item: exportStr);
                    } else {
                        break;
                    }

                    SkipWhiteSpace(context: context);
                    if (!TryConsume(c: ',', context: context)) {
                        break;
                    }
                }
            }

            var len = (context.Scanner.Cursor.Offset - startOffset);

            return new ExportNode(Column: col, Facet: facet, Length: len, Line: line, Names: names, Offset: startOffset) { FacetExplicit = facetExplicit };
        }

        var isModule = TryMatchKeyword(context: context, keyword: "module");

        if (isModule || TryMatchKeyword(context: context, keyword: "template")) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var templateName)) {
                throw CreateException(context: context, message: "Expected identifier after 'template'");
            }

            SkipWhiteSpace(context: context);
            if (!TryConsume(c: '(', context: context)) {
                throw CreateException(context: context, message: $"Expected '(' after template name '{templateName}'");
            }

            var parameters = new List<TemplateParameterNode>();

            SkipWhiteSpace(context: context);
            while (!cursor.Eof && (cursor.Current != ')')) {
                var paramStart = cursor.Offset;

                var (pLine, pCol) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: paramStart);
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var paramName)) {
                    throw CreateException(context: context, message: "Expected parameter name in template definition");
                }

                string? kind = null;
                var requiredExports = new List<string>();
                ExpressionNode? defaultVal = null;

                SkipWhiteSpace(context: context);
                if (isModule && TryConsume(c: ':', context: context)) {
                    SkipWhiteSpace(context: context);
                    if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out kind)) {
                        throw CreateException(context: context, message: $"Expected parameter kind after '{paramName}:'");
                    }
                    SkipWhiteSpace(context: context);
                    if (string.Equals(a: kind, b: "Module", comparisonType: StringComparison.Ordinal) && TryMatchKeyword(context: context, keyword: "exporting")) {
                        while (true) {
                            SkipWhiteSpace(context: context);
                            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var required)) {
                                throw CreateException(context: context, message: "Expected export name after 'exporting'");
                            }
                            requiredExports.Add(item: required);
                            SkipWhiteSpace(context: context);
                            var beforeComma = cursor.Position;

                            if (!TryConsume(c: ',', context: context)) { break; }
                            SkipWhiteSpace(context: context);
                            var afterComma = cursor.Position;

                            if (cursor.Current == ')') {
                                cursor.ResetPosition(position: beforeComma);
                                break;
                            }
                            if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out _)) {
                                SkipWhiteSpace(context: context);
                                if (cursor.Current == ':') {
                                    cursor.ResetPosition(position: beforeComma);
                                    break;
                                }
                            }
                            cursor.ResetPosition(position: afterComma);
                        }
                    }
                }
                if (TryConsume(c: '=', context: context)) {
                    defaultVal = ParseExpression(context: context);
                }

                var pLen = (cursor.Offset - paramStart);

                parameters.Add(item: new TemplateParameterNode(Column: pCol, DefaultValue: defaultVal, Length: pLen, Line: pLine, Name: paramName, Offset: paramStart) { Kind = kind, RequiredExports = requiredExports });

                SkipWhiteSpace(context: context);
                if (TryConsume(c: ',', context: context)) {
                    SkipWhiteSpace(context: context);
                    continue;
                }
                break;
            }

            if (!TryConsume(c: ')', context: context)) {
                throw CreateException(context: context, message: "Expected ')' closing template parameters");
            }

            SkipWhiteSpace(context: context);
            if (cursor.Current != '{') {
                throw CreateException(context: context, message: $"Expected '{{' body for template '{templateName}'");
            }

            var body = ParseBlock(context, identifier: templateName, name: null, target: null, startOffset: startOffset, line: line, col: col, diagnostics: diagnostics);
            var len = (cursor.Offset - startOffset);

            return new TemplateNode(Body: body, Column: col, Length: len, Line: line, Name: templateName, Offset: startOffset, Parameters: parameters) { IsModule = isModule };
        }

        if (TryMatchRuleScopeKeyword(context: context)) {
            return ParseRuleScopeBlock(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchStabilizeKeyword(context: context)) {
            return ParseStabilizeBlock(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchWorkflowKeyword(context: context)) {
            return ParseWorkflowBlock(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchEnumKeyword(context: context)) {
            return ParseEnumDeclaration(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchRecordKeyword(context: context)) {
            return ParseRecordDeclaration(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchDeclarationKeyword(context: context, keyword: "pool")) {
            return ParseStatePoolDeclaration(col: col, context: context, line: line, startOffset: startOffset);
        }
        if (TryMatchDeclarationKeyword(context: context, keyword: "pairPool")) {
            return ParseStatePairPoolDeclaration(col: col, context: context, line: line, startOffset: startOffset);
        }

        if (TryMatchSetKeyword(context: context)) {
            return ParseSetDeclaration(col: col, context: context, line: line, startOffset: startOffset);
        }

        if (TryMatchPatternKeyword(context: context)) {
            return ParsePatternDeclaration(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchDeriveKeyword(context: context)) {
            return ParseDerivedState(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchTestKeyword(context: context)) {
            return ParseTestDeclaration(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchKeyword(context: context, keyword: "rule")) {
            return ParseRuleBlock(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchDeclarationKeyword(context: context, keyword: "table")) {
            return ParseStateTableDeclaration(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset, vocabulary: vocabulary);
        }

        if (TryMatchDeclarationKeyword(context: context, keyword: "slot")) {
            return ParseStateSlotDeclaration(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset, vocabulary: vocabulary);
        }

        if (TryMatchPileKeyword(context: context)) {
            return ParseStatePileDeclaration(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset, vocabulary: vocabulary);
        }

        if (TryMatchDeclarationKeyword(context: context, keyword: "grid")) {
            return ParseStateGridDeclaration(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset, vocabulary: vocabulary);
        }

        // Addon capability request: request Mutate "section:state"
        if (TryMatchKeyword(context: context, keyword: "request")) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var capability)) {
                throw CreateException(context: context, message: "Expected capability name (e.g. Mutate, Observe, Emit) after 'request'");
            }

            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out var subject) && !TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out subject)) {
                throw CreateException(context: context, message: $"Expected subject string or identifier after capability '{capability}'");
            }

            var len = (cursor.Offset - startOffset);

            return new AddonRequestNode(Capability: capability, Column: col, Length: len, Line: line, Offset: startOffset, Subject: subject);
        }

        // Addon machine-memory watch: watchMemory screen: 0, address: 0x02000000, length: 4
        if (TryMatchKeyword(context: context, keyword: "watchMemory")) {
            SkipWhiteSpace(context: context);
            var screen = 0;
            var address = 0L;
            var watchLength = 1;

            while (!cursor.Eof) {
                SkipWhiteSpace(context: context);
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var key)) {
                    break;
                }
                SkipWhiteSpace(context: context);
                TryConsume(c: ':', context: context);
                TryConsume(c: '=', context: context);
                SkipWhiteSpace(context: context);
                var valExpr = ParseExpression(context: context);

                if (string.Equals(a: key, b: "screen", comparisonType: StringComparison.OrdinalIgnoreCase) && (valExpr is LiteralExpressionNode { Value: long sVal })) {
                    screen = ((int)sVal);
                } else if (string.Equals(a: key, b: "address", comparisonType: StringComparison.OrdinalIgnoreCase) && (valExpr is LiteralExpressionNode { Value: long aVal })) {
                    address = aVal;
                } else if ((string.Equals(a: key, b: "length", comparisonType: StringComparison.OrdinalIgnoreCase) || string.Equals(a: key, b: "watchLength", comparisonType: StringComparison.OrdinalIgnoreCase)) && (valExpr is LiteralExpressionNode { Value: long lVal })) {
                    watchLength = ((int)lVal);
                }

                SkipWhiteSpace(context: context);
                if (!TryConsume(c: ',', context: context)) {
                    break;
                }
            }
            var len = (cursor.Offset - startOffset);

            return new AddonMemoryWatchNode(Address: address, Column: col, Length: len, Line: line, Offset: startOffset, Screen: screen, WatchLength: watchLength);
        }

        // Identifier or string starting token
        if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out var firstSpelling, text: out var firstId)) {
            throw CreateException(context: context, message: $"Unexpected token '{cursor.Current}' while parsing statement");
        }

        // Raw position right after `firstId`, before the whitespace skip below can erase the newline a bare
        // keyword statement (§4.2's `solid` flag) depends on — see the fallback at the bottom of this method.
        var afterFirstIdPosition = cursor.Position;
        var afterFirstIdOffset = cursor.Offset;

        RefuseMisplacedConstruct(col: col, context: context, identifier: firstId, line: line, nextOffset: afterFirstIdOffset, startOffset: startOffset);

        SkipWhiteSpace(context: context);

        // Check if invocation: name(...)
        if (cursor.Current == '(') {
            var callExpr = ParseCallExpression(col: col, context: context, functionName: firstId, line: line, startOffset: startOffset);
            var len = (cursor.Offset - startOffset);

            return new ExpressionStatementNode(Column: col, Expression: callExpr, Length: len, Line: line, Offset: startOffset);
        }

        // Check if property assignment: key: expr or key = expr
        if ((cursor.Current == ':') || (cursor.Current == '=')) {
            var separator = cursor.Current;

            cursor.Advance();
            SkipWhiteSpace(context: context);
            RefuseColonBeforeContainer(col: col, context: context, diagnostics: diagnostics, identifier: firstId, line: line, separator: separator, startOffset: startOffset);

            var valExpr = ParseMemberValue(context: context);
            var len = (cursor.Offset - startOffset);

            return new PropertyNode(Column: col, Length: len, Line: line, Name: firstId, Offset: startOffset, Value: valExpr);
        }

        // Check if inline array property: key [ ... ]
        if (cursor.Current == '[') {
            var arrayExpr = ParseArrayExpression(context: context);
            var len = (cursor.Offset - startOffset);

            return new PropertyNode(Column: col, Length: len, Line: line, Name: firstId, Offset: startOffset, Value: arrayExpr);
        }

        // Check if block without name: firstId { ... }
        if (cursor.Current == '{') {
            if (IsEmbeddedLanguage(identifier: firstId, vocabulary: vocabulary)) {
                return ParseEmbeddedBlock(col: col, context: context, diagnostics: diagnostics, language: firstId, line: line, startOffset: startOffset);
            }

            return (ParseBlock(context, identifier: firstId, name: null, target: null, startOffset: startOffset, line: line, col: col, diagnostics: diagnostics, schema: schema, vocabulary: vocabulary) with { IdentifierQuoted = firstSpelling.Quoted });
        }

        // Bare keyword statement: `firstId` ended its own line, so nothing can belong to it — e.g. the `solid`
        // flag inside a `placement { }` row. Tested BEFORE the two-token block form below, because that form spans
        // whitespace including newlines and would otherwise read a flag and the block statement following it as one
        // `identifier name { }` header.
        if ((context.Scanner.Buffer[startOffset] != '"') && AtEndOfLogicalStatement(buffer: context.Scanner.Buffer, offset: afterFirstIdOffset)) {
            cursor.ResetPosition(position: afterFirstIdPosition);

            return new FlagStatementNode(Column: col, Length: (afterFirstIdOffset - startOffset), Line: line, Name: firstId, Offset: startOffset);
        }

        // A block header whose name is an interpolation: `shape Superellipsoid $"braid-{i}" { … }`. The name is
        // not known until lowering, so it rides the node as an expression instead of a string.
        if ((cursor.Current == '$') && (cursor.PeekNext() == '"')) {
            if (TryReadName(admitted: NameForms.Interpolated | NameForms.String, context: context, spelling: out var nameSpelling, text: out _) && (nameSpelling.Expression is { } nameExpr)) {
                SkipWhiteSpace(context: context);

                if (cursor.Current == '{') {
                    var interpolated = ParseBlock(context, identifier: firstId, name: null, target: null, startOffset: startOffset, line: line, col: col, diagnostics: diagnostics);

                    return (interpolated with { IdentifierQuoted = firstSpelling.Quoted, NameExpression = nameExpr });
                }
            }

            throw CreateException(context: context, message: $"Expected '{{' after an interpolated name for '{firstId}'");
        }

        // Otherwise, named block or block with target:
        // Examples: layout "study" { ... }
        //           solid Prism "p1" { ... }
        //           seatRig "study" { ... }
        if (TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out var secondSpelling, text: out var secondId)) {
            SkipWhiteSpace(context: context);
            if (cursor.Current == '{') {
                return (ParseBlock(context, identifier: firstId, name: secondId, target: null, startOffset: startOffset, line: line, col: col, diagnostics: diagnostics) with { IdentifierQuoted = firstSpelling.Quoted, NameQuoted = secondSpelling.Quoted });
            }

            if ((cursor.Current == '$') && (cursor.PeekNext() == '"')) {
                if (TryReadName(admitted: NameForms.Interpolated | NameForms.String, context: context, spelling: out var targetedSpelling, text: out _) && (targetedSpelling.Expression is { } targetedName)) {
                    SkipWhiteSpace(context: context);

                    if (cursor.Current == '{') {
                        var interpolated = ParseBlock(context, identifier: firstId, name: null, target: secondId, startOffset: startOffset, line: line, col: col, diagnostics: diagnostics);

                        return (interpolated with { IdentifierQuoted = firstSpelling.Quoted, NameExpression = targetedName });
                    }
                }

                throw CreateException(context: context, message: $"Expected '{{' after an interpolated name for '{firstId} {secondId}'");
            }

            if (TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out var thirdSpelling, text: out var thirdId)) {
                SkipWhiteSpace(context: context);
                if (cursor.Current == '{') {
                    return (ParseBlock(context, identifier: firstId, name: thirdId, target: secondId, startOffset: startOffset, line: line, col: col, diagnostics: diagnostics) with { IdentifierQuoted = firstSpelling.Quoted, NameQuoted = thirdSpelling.Quoted });
                }
            }
        }

        throw CreateException(context: context, message: $"Unexpected token sequence after '{firstId}'");
    }
    // A container value is written as a block, a scalar takes a colon: `host { }` and `cameras [ ]`, never
    // `host: { }` or `cameras: [ ]`. The test is syntactic and looks only at the literal that follows, so a name
    // standing for a container (`origin: bounds`) is untouched — what is refused is the punctuation, not the value.
    // The statement still parses after the report, so one misspelling yields one diagnostic rather than a cascade.
    private static void RefuseColonBeforeContainer(ParseContext context, string identifier, char separator, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        if (cursor.Current is not ('{' or '[')) {
            return;
        }

        var opener = cursor.Current;
        var shape = ((opener == '{') ? "block" : "array");

        diagnostics?.ReportError(
            code: PuckDiagnosticCodes.ColonBeforeContainer,
            message: $"'{identifier}{separator} {opener}' - a {shape} is written without the '{separator}': use '{identifier} {opener}'",
            span: new SourceSpan(startOffset, (cursor.Offset - startOffset), line, col)
        );
    }
    // `for item in sequence { … }` and `for (item, index) in sequence { … }`.
    // `body` parses one statement of the loop's body where the loop stands somewhere other than a document block,
    // such as a rule group's body; null parses document statements.
    private static ForStatementNode ParseForStatement(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, string? schema = null, IDocumentVocabulary? vocabulary = null, Func<ParseContext, DiagnosticBag?, StatementNode?>? body = null) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);

        string item;
        string? index = null;

        if (cursor.Current == '(') {
            cursor.Advance();
            SkipWhiteSpace(context: context);

            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out item)) {
                throw CreateException(context: context, message: "Expected an item name after 'for ('");
            }

            SkipWhiteSpace(context: context);

            if (TryConsume(c: ',', context: context)) {
                SkipWhiteSpace(context: context);

                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out index)) {
                    throw CreateException(context: context, message: "Expected an index name after 'for (item,'");
                }
            }

            SkipWhiteSpace(context: context);

            if (!TryConsume(c: ')', context: context)) {
                throw CreateException(context: context, message: "Expected ')' closing a 'for' binding list");
            }
        } else if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out item)) {
            throw CreateException(context: context, message: "Expected an item name after 'for'");
        }

        SkipWhiteSpace(context: context);

        if (!TryMatchKeyword(context: context, keyword: "in")) {
            throw CreateException(context: context, message: "Expected 'in' after a 'for' binding");
        }

        var sequence = ParseExpression(context: context);

        SkipWhiteSpace(context: context);

        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: "Expected '{' starting a 'for' body");
        }

        var statements = new List<StatementNode>();

        SkipWhiteSpace(context: context);

        while (!cursor.Eof && (cursor.Current != '}')) {
            if (((body is null) ? ParseStatement(context: context, diagnostics: diagnostics, schema: schema, vocabulary: vocabulary) : body(arg1: context, arg2: diagnostics)) is { } statement) {
                statements.Add(item: statement);
            }
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: "Expected '}' closing a 'for' body");
        }

        return new ForStatementNode(item, index, sequence, statements, startOffset, (cursor.Offset - startOffset), line, col);
    }
    private static BlockNode ParseBlock(ParseContext context, string identifier, string? name, string? target, int startOffset, int line, int col, DiagnosticBag? diagnostics = null, string? schema = null, IDocumentVocabulary? vocabulary = null) {
        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: $"Expected '{{' starting block for '{identifier}'");
        }

        var statements = new List<StatementNode>();

        SkipWhiteSpace(context: context);

        while (!context.Scanner.Cursor.Eof && (context.Scanner.Cursor.Current != '}')) {
            var stmt = ParseStatement(context: context, diagnostics: diagnostics, schema: schema, vocabulary: vocabulary);

            if (stmt is not null) {
                statements.Add(item: stmt);
            }
            ConsumeSeparator(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: $"Expected '}}' closing block for '{identifier}'");
        }

        // A placement row (§4.2) authoring both the bare `solid` flag and an explicit `solid: { }` override leaves
        // the emitter no single answer for the field — diagnosed here, where both spellings are already visible.
        if (string.Equals(a: identifier, b: "placement", comparisonType: StringComparison.Ordinal)) {
            var sawBareSolid = statements.Exists(match: static s => (s is FlagStatementNode { Name: "solid" }));
            var sawSolidOverride = statements.Exists(match: static s => (s is PropertyNode { Name: "solid" } or BlockNode { Identifier: "solid" }));

            if (sawBareSolid && sawSolidOverride) {
                diagnostics?.ReportError(code: PuckDiagnosticCodes.SolidSpelledTwice, message: $"placement '{name}' authors both the bare 'solid' flag and an explicit 'solid {{ }}' override", span: new SourceSpan(startOffset, (context.Scanner.Cursor.Offset - startOffset), line, col));
            }
        }

        var len = (context.Scanner.Cursor.Offset - startOffset);

        return new BlockNode(Identifier: identifier, Name: name, Target: target, Statements: statements, Offset: startOffset, Length: len, Line: line, Column: col);
    }
    private static StatementNode ParseEmbeddedBlock(
        ParseContext context,
        string language,
        int startOffset,
        int line,
        int col,
        DiagnosticBag? diagnostics
    ) {
        var cursor = context.Scanner.Cursor;

        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: $"Expected '{{' starting embedded '{language}' block");
        }

        var bodyStartOffset = cursor.Offset;

        var (bodyLine, bodyCol) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: bodyStartOffset);
        var buffer = context.Scanner.Buffer;
        var depth = 1;

        while (!cursor.Eof) {
            var c = cursor.Current;

            // Handle string literals: single or double quote
            if (c is '"' or '\'') {
                var quote = c;

                cursor.Advance();
                while (!cursor.Eof) {
                    var ch = cursor.Current;

                    if ((ch == '\\') && (quote == '"')) {
                        cursor.Advance();
                        if (!cursor.Eof) {
                            cursor.Advance();
                        }
                        continue;
                    }
                    if (ch == quote) {
                        cursor.Advance();
                        // SQL allows '' escaping inside single-quoted strings
                        if ((quote == '\'') && !cursor.Eof && (cursor.Current == '\'')) {
                            cursor.Advance();
                            continue;
                        }
                        break;
                    }
                    cursor.Advance();
                }
                continue;
            }

            // Handle line comments: // or --
            if (((c == '/') && (cursor.PeekNext() == '/')) || ((c == '-') && (cursor.PeekNext() == '-'))) {
                cursor.Advance();
                cursor.Advance();
                while (!cursor.Eof && (cursor.Current is not '\n' and not '\r')) {
                    cursor.Advance();
                }
                continue;
            }

            // Handle block comments: /* ... */
            if ((c == '/') && (cursor.PeekNext() == '*')) {
                cursor.Advance();
                cursor.Advance();
                while (!cursor.Eof) {
                    if ((cursor.Current == '*') && (cursor.PeekNext() == '/')) {
                        cursor.Advance();
                        cursor.Advance();
                        break;
                    }
                    cursor.Advance();
                }
                continue;
            }

            if (c == '{') {
                depth++;
                cursor.Advance();
                continue;
            }

            if (c == '}') {
                depth--;
                if (depth == 0) {
                    var bodyLen = (cursor.Offset - bodyStartOffset);
                    var bodyText = buffer.Substring(length: bodyLen, startIndex: bodyStartOffset);

                    cursor.Advance();
                    var totalLen = (cursor.Offset - startOffset);

                    return new EmbeddedBlockNode(
                        Body: bodyText,
                        BodyColumn: bodyCol,
                        BodyLine: bodyLine,
                        BodyOffset: bodyStartOffset,
                        Column: col,
                        Language: language,
                        Length: totalLen,
                        Line: line,
                        Offset: startOffset
                    );
                }
                cursor.Advance();
                continue;
            }

            cursor.Advance();
        }

        throw CreateException(context: context, message: $"Expected '}}' closing embedded '{language}' block");
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
            if ((braceDepth == 0) && (parenDepth == 0) && (bracketDepth == 0)) {
                if (c == ';') {
                    cursor.Advance();
                    SkipWhiteSpace(context: context);
                    return;
                }
                if ((c == '\n') || (c == '\r')) {
                    cursor.Advance();
                    SkipWhiteSpace(context: context);
                    return;
                }
            }

            cursor.Advance();
        }
        SkipWhiteSpace(context: context);
    }
    private static ExpressionNode ParsePostfixExpression(ParseContext context) {
        var expr = ParsePrimaryExpression(context: context);

        while (true) {
            var beforeTrivia = context.Scanner.Cursor.Offset;

            SkipWhiteSpace(context: context);

            var trivia = context.Scanner.Buffer.AsSpan(beforeTrivia, (context.Scanner.Cursor.Offset - beforeTrivia));
            var separated = (trivia.IndexOf(value: '\n') >= 0);
            var cur = context.Scanner.Cursor.Current;

            if ((cur == '.') && (context.Scanner.Cursor.PeekNext() != '.')) {
                context.Scanner.Cursor.Advance();
                SkipWhiteSpace(context: context);
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var member)) {
                    throw CreateException(context: context, message: "Expected member identifier after '.'");
                }
                var len = (context.Scanner.Cursor.Offset - expr.Offset);

                expr = new MemberAccessExpressionNode(Target: expr, Member: member, Offset: expr.Offset, Length: len, Line: expr.Line, Column: expr.Column);
                continue;
            }

            // Index read: expr[index]. The '[' must be ADJACENT — an array's elements are newline-separated, so a
            // '[' that opens the next line is the next element, never an index on the previous one.
            if ((cur == '[') && !separated) {
                context.Scanner.Cursor.Advance();

                var index = ParseExpression(context: context);

                SkipWhiteSpace(context: context);

                if (!TryConsume(c: ']', context: context)) {
                    throw CreateException(context: context, message: "Expected ']' closing an index");
                }

                expr = new IndexExpressionNode(Target: expr, Index: index, Offset: expr.Offset, Length: (context.Scanner.Cursor.Offset - expr.Offset), Line: expr.Line, Column: expr.Column);

                continue;
            }

            // Function call on identifier expression: expr(arg1, arg2)
            if ((cur == '(') && (expr is IdentifierExpressionNode identExpr)) {
                expr = ParseCallExpression(context, identExpr.Name, identExpr.Offset, identExpr.Line, identExpr.Column);
                continue;
            }
            if ((cur == '(') && (expr is MemberAccessExpressionNode memberCall) && (QualifiedName.From(expression: memberCall) is { } qualifiedName)) {
                expr = ParseCallExpression(context, qualifiedName.ToString(), memberCall.Offset, memberCall.Line, memberCall.Column);
                continue;
            }

            break;
        }

        return expr;
    }
    private static ExpressionNode ParsePrimaryExpression(ParseContext context) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        if (cursor.Eof) {
            throw CreateException(context: context, message: "Unexpected end of file while expecting expression");
        }

        if (TryMatchKeyword(context: context, keyword: "asset")) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.String, context: context, spelling: out _, text: out var path)) {
                throw CreateException(context: context, message: "Expected a quoted path after 'asset'");
            }
            return new AssetExpressionNode(path, startOffset, (cursor.Offset - startOffset), line, col);
        }

        // Parentheses: ( expr )
        if (cursor.Current == '(') {
            cursor.Advance();
            var inner = ParseExpression(context: context);

            SkipWhiteSpace(context: context);
            if (!TryConsume(c: ')', context: context)) {
                throw CreateException(context: context, message: "Expected ')' closing parenthesized expression");
            }
            return inner with { Parenthesized = true };
        }

        // Array: [ ... ]
        if (cursor.Current == '[') {
            return ParseArrayExpression(context: context);
        }

        // Object: { ... }
        if (cursor.Current == '{') {
            return ParseObjectExpression(context: context);
        }

        // Hex color: #RRGGBB / #RRGGBBAA
        if (cursor.Current == '#') {
            return ParseHexColor(context: context);
        }

        // Every string form: "…", """…""", and either with a `$` prefix.
        if ((cursor.Current == '"') || ((cursor.Current == '$') && (cursor.PeekNext() == '"'))) {
            if (TryReadName(admitted: NameForms.Interpolated | NameForms.String, context: context, spelling: out var stringSpelling, text: out _) && (stringSpelling.Expression is { } stringNode)) {
                return stringNode;
            }

            throw CreateException(context: context, message: "Malformed string literal");
        }

        // Number literal: integer or decimal, optionally negative
        if (char.IsDigit(c: cursor.Current) || ((cursor.Current == '-') && (char.IsDigit(c: cursor.PeekNext()) || (cursor.PeekNext() == '.')))) {
            return ParseNumberWithOptionalUnit(context: context);
        }

        // Identifier, boolean, or null — the extended form, so a reserved-channel argument like
        // `$board:cellOf:board:placement:$each` or a `$zones[...]`-folded selector (§9-A8) reads as one token here,
        // the same way ExpressionSpelling's own lexer folds it.
        if (TryReadName(admitted: NameForms.Extended, context: context, spelling: out _, text: out var ident)) {
            var len = (cursor.Offset - startOffset);

            if (string.Equals(a: ident, b: "true", comparisonType: StringComparison.Ordinal)) {
                return new LiteralExpressionNode(Value: true, Unit: null, Offset: startOffset, Length: len, Line: line, Column: col);
            }
            if (string.Equals(a: ident, b: "false", comparisonType: StringComparison.Ordinal)) {
                return new LiteralExpressionNode(Value: false, Unit: null, Offset: startOffset, Length: len, Line: line, Column: col);
            }
            if (string.Equals(a: ident, b: "null", comparisonType: StringComparison.Ordinal)) {
                return new LiteralExpressionNode(Value: null, Unit: null, Offset: startOffset, Length: len, Line: line, Column: col);
            }

            return new IdentifierExpressionNode(Column: col, Length: len, Line: line, Name: ident, Offset: startOffset);
        }

        throw CreateException(context: context, message: $"Unexpected token '{cursor.Current}' while parsing expression");
    }
    private static LiteralExpressionNode ParseNumberWithOptionalUnit(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        // Read sign if present
        var isNegative = false;

        if (cursor.Current == '-') {
            isNegative = true;
            cursor.Advance();
        }

        // Check for hex literal: 0x...
        if ((cursor.Current == '0') && ((cursor.PeekNext() == 'x') || (cursor.PeekNext() == 'X'))) {
            cursor.Advance(count: 2);
            var hexStart = cursor.Offset;

            while (!cursor.Eof && (IsHexChar(c: cursor.Current) || (cursor.Current == '_'))) {
                cursor.Advance();
            }
            var hexLen = (cursor.Offset - hexStart);

            if (hexLen == 0) {
                throw CreateException(context: context, message: "Expected hexadecimal digits after '0x'");
            }
            var rawHex = context.Scanner.Buffer.Substring(length: hexLen, startIndex: hexStart).Replace(newValue: "", oldValue: "_");

            if (!long.TryParse(rawHex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hexVal)) {
                throw CreateException(context: context, message: $"Invalid hex integer literal '0x{rawHex}'");
            }
            var totalHexVal = (isNegative ? -hexVal : hexVal);

            return new LiteralExpressionNode(Value: totalHexVal, Unit: null, Offset: startOffset, Length: (cursor.Offset - startOffset), Line: line, Column: col, RawText: context.Scanner.Buffer[startOffset..cursor.Offset]);
        }

        var numStart = cursor.Offset;
        var hasDecimalPoint = false;

        while (!cursor.Eof) {
            if (char.IsDigit(c: cursor.Current) || (cursor.Current == '_')) {
                cursor.Advance();
            } else if ((cursor.Current == '.') && !hasDecimalPoint && char.IsDigit(c: cursor.PeekNext())) {
                hasDecimalPoint = true;
                cursor.Advance();
            } else {
                break;
            }
        }

        var numLength = (cursor.Offset - numStart);

        if (numLength == 0) {
            throw CreateException(context: context, message: "Expected number digits");
        }

        // Scientific notation is part of the number, never a unit suffix: a canonical document holds small
        // magnitudes as "8.57E-05", and reading the 'E' as a unit both loses the exponent and mis-reports the field.
        var hasExponent = false;

        if (!cursor.Eof && (cursor.Current is 'e' or 'E')) {
            var afterExponent = (cursor.Offset + 1);
            var buffer = context.Scanner.Buffer;

            if ((afterExponent < buffer.Length) && (buffer[afterExponent] is '+' or '-')) {
                afterExponent++;
            }
            if ((afterExponent < buffer.Length) && char.IsAsciiDigit(c: buffer[afterExponent])) {
                hasExponent = true;
                cursor.Advance(count: (afterExponent - cursor.Offset));
                while (!cursor.Eof && char.IsAsciiDigit(c: cursor.Current)) {
                    cursor.Advance();
                }
                numLength = (cursor.Offset - numStart);
            }
        }

        var cleanNumStr = context.Scanner.Buffer.Substring(length: numLength, startIndex: numStart).Replace(newValue: "", oldValue: "_");
        var signedNumStr = (isNegative ? ("-" + cleanNumStr) : cleanNumStr);
        object numVal;

        if (hasDecimalPoint || hasExponent) {
            if (!double.TryParse(signedNumStr, System.Globalization.CultureInfo.InvariantCulture, out var d)) {
                throw CreateException(context: context, message: $"Invalid floating point literal '{signedNumStr}'");
            }
            numVal = d;
        } else {
            if (!long.TryParse(signedNumStr, System.Globalization.CultureInfo.InvariantCulture, out var l)) {
                if (ulong.TryParse(cleanNumStr, System.Globalization.CultureInfo.InvariantCulture, out var ul) && !isNegative) {
                    numVal = ul;
                } else if (double.TryParse(signedNumStr, System.Globalization.CultureInfo.InvariantCulture, out var dbl)) {
                    numVal = dbl;
                } else {
                    throw CreateException(context: context, message: $"Invalid integer literal '{signedNumStr}'");
                }
            } else {
                numVal = l;
            }
        }

        // Check for unit suffix: s, ms, hz, rad, deg, m, mm, cm, %, pct
        string? unit = null;

        if (!cursor.Eof && (char.IsLetter(c: cursor.Current) || (cursor.Current == '%'))) {
            var unitStart = cursor.Offset;

            if (cursor.Current == '%') {
                cursor.Advance();
            } else {
                while (!cursor.Eof && char.IsLetter(c: cursor.Current)) {
                    cursor.Advance();
                }
            }
            unit = context.Scanner.Buffer.Substring(unitStart, (cursor.Offset - unitStart));
        }

        var totalLen = (cursor.Offset - startOffset);
        var rawText = ((hasDecimalPoint || hasExponent) ? signedNumStr : null);

        return new LiteralExpressionNode(Column: col, Length: totalLen, Line: line, Offset: startOffset, RawText: rawText, Unit: unit, Value: numVal);
    }
    private static ColorExpressionNode ParseHexColor(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        cursor.Advance(); // consume '#'
        var hexStart = cursor.Offset;

        while (!cursor.Eof && IsHexChar(c: cursor.Current)) {
            cursor.Advance();
        }

        var hexLen = (cursor.Offset - hexStart);

        if (hexLen is not (3 or 4 or 6 or 8)) {
            throw CreateException(context: context, message: $"Invalid hex color length #{context.Scanner.Buffer.Substring(length: hexLen, startIndex: hexStart)}");
        }

        var hex = ("#" + context.Scanner.Buffer.Substring(length: hexLen, startIndex: hexStart));

        return new ColorExpressionNode(Hex: hex, Offset: startOffset, Length: (cursor.Offset - startOffset), Line: line, Column: col);
    }
    private static ArrayExpressionNode ParseArrayExpression(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        cursor.Advance(); // consume '['
        var elements = new List<ExpressionNode>();

        SkipWhiteSpace(context: context);

        while (!cursor.Eof && (cursor.Current != ']')) {
            var elem = ParseExpression(context: context);

            elements.Add(item: elem);

            SkipWhiteSpace(context: context);
            if (cursor.Current == ',') {
                cursor.Advance();
                SkipWhiteSpace(context: context);
            }
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: ']', context: context)) {
            throw CreateException(context: context, message: "Expected ']' closing array");
        }

        return new ArrayExpressionNode(Elements: elements, Offset: startOffset, Length: (cursor.Offset - startOffset), Line: line, Column: col);
    }
    private static ObjectExpressionNode ParseObjectExpression(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        cursor.Advance(); // consume '{'
        var properties = new List<PropertyNode>();

        SkipWhiteSpace(context: context);

        while (!cursor.Eof && (cursor.Current != '}')) {
            var propStart = cursor.Offset;

            var (pLine, pCol) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: propStart);

            if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out var propKey)) {
                throw CreateException(context: context, message: $"Expected property key inside object, got '{cursor.Current}'");
            }

            SkipWhiteSpace(context: context);
            if ((cursor.Current == ':') || (cursor.Current == '=')) {
                var separator = cursor.Current;

                cursor.Advance();
                SkipWhiteSpace(context: context);

                // The same one-spelling rule a statement follows: a container is written without the separator.
                // Object literals already admit `key { }` and `key [ ]`, so this closes the second spelling here too.
                if (cursor.Current is '{' or '[') {
                    var shape = ((cursor.Current == '{') ? "block" : "array");

                    var (eLine, eCol) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: cursor.Offset);

                    throw new PuckParseException($"'{propKey}{separator} {cursor.Current}' - a {shape} is written without the '{separator}': use '{propKey} {cursor.Current}'", cursor.Offset, eLine, eCol) {
                        Code = PuckDiagnosticCodes.ColonBeforeContainer,
                    };
                }

                var valExpr = ParseMemberValue(context: context);

                properties.Add(item: new PropertyNode(Name: propKey, Value: valExpr, Offset: propStart, Length: (cursor.Offset - propStart), Line: pLine, Column: pCol));
            } else if (cursor.Current == '{') {
                var nestedObj = ParseObjectExpression(context: context);

                properties.Add(item: new PropertyNode(Name: propKey, Value: nestedObj, Offset: propStart, Length: (cursor.Offset - propStart), Line: pLine, Column: pCol));
            } else if (cursor.Current == '[') {
                var nestedArr = ParseArrayExpression(context: context);

                properties.Add(item: new PropertyNode(Name: propKey, Value: nestedArr, Offset: propStart, Length: (cursor.Offset - propStart), Line: pLine, Column: pCol));
            } else {
                throw CreateException(context: context, message: $"Expected ':', '=', '{{', or '[' after property key '{propKey}'");
            }

            ConsumeSeparator(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: "Expected '}' closing object expression");
        }

        return new ObjectExpressionNode(Properties: properties, Offset: startOffset, Length: (cursor.Offset - startOffset), Line: line, Column: col);
    }
    private static CallExpressionNode ParseCallExpression(ParseContext context, string functionName, int startOffset, int line, int col) {
        var cursor = context.Scanner.Cursor;

        if (!TryConsume(c: '(', context: context)) {
            throw CreateException(context: context, message: $"Expected '(' after function name '{functionName}'");
        }

        var arguments = new List<ArgumentNode>();

        SkipWhiteSpace(context: context);

        while (!cursor.Eof && (cursor.Current != ')')) {
            var argStart = cursor.Offset;

            var (aLine, aCol) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: argStart);
            string? argName = null;

            // Check if named argument: name: expr
            var peekPosition = cursor.Position;

            if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var candidateName)) {
                SkipWhiteSpace(context: context);
                if (cursor.Current == ':') {
                    cursor.Advance();
                    argName = candidateName;
                } else {
                    // Reset to before candidateName
                    cursor.ResetPosition(position: peekPosition);
                }
            }

            var val = ParseArgumentValue(argumentName: argName, callName: functionName, context: context, positionalIndex: arguments.Count);

            arguments.Add(item: new ArgumentNode(Name: argName, Value: val, Offset: argStart, Length: (cursor.Offset - argStart), Line: aLine, Column: aCol));

            SkipWhiteSpace(context: context);
            if (cursor.Current == ',') {
                cursor.Advance();
                SkipWhiteSpace(context: context);
            } else {
                break;
            }
        }

        if (!TryConsume(c: ')', context: context)) {
            throw CreateException(context: context, message: $"Expected ')' closing call to '{functionName}'");
        }

        var totalLen = (cursor.Offset - startOffset);

        return new CallExpressionNode(Arguments: arguments, Column: col, Length: totalLen, Line: line, Name: functionName, Offset: startOffset);
    }
    private static bool TryMatchKeyword(ParseContext context, string keyword) {
        var cursor = context.Scanner.Cursor;
        var offset = cursor.Offset;

        if ((cursor.Buffer.Length - offset) < keyword.Length) {
            return false;
        }

        if (!cursor.Buffer.AsSpan(offset, keyword.Length).SequenceEqual(other: keyword.AsSpan())) {
            return false;
        }

        var nextOffset = (offset + keyword.Length);

        if (nextOffset < cursor.Buffer.Length) {
            var nextChar = cursor.Buffer[nextOffset];

            if (IdentifierSpelling.IsPart(character: nextChar)) {
                return false;
            }
        }

        cursor.Advance(count: keyword.Length);
        return true;
    }
    private static bool TryMatchKeywordFollowedByName(ParseContext context, string keyword, NameForms admitted) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (TryMatchKeyword(context: context, keyword: keyword) && SkipSpacesOnLine(context: context)) {
            if (TryReadName(admitted: admitted, context: context, spelling: out _, text: out _)) {
                cursor.ResetPosition(position: saved);

                return TryMatchKeyword(context: context, keyword: keyword);
            }
        }

        cursor.ResetPosition(position: saved);

        return false;
    }
    private static string ReadDottedIdentifier(ParseContext context, string initialName, string expectedAfterDotMessage) {
        var result = initialName;

        while (true) {
            SkipWhiteSpace(context: context);
            if (!TryConsume(c: '.', context: context)) {
                break;
            }
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var member)) {
                throw CreateException(context: context, message: expectedAfterDotMessage);
            }
            result += $".{member}";
        }

        return result;
    }
    private static bool TryConsume(ParseContext context, char c) {
        SkipWhiteSpace(context: context);
        if (!context.Scanner.Cursor.Eof && (context.Scanner.Cursor.Current == c)) {
            context.Scanner.Cursor.Advance();
            return true;
        }
        return false;
    }
    private static void ConsumeSeparator(ParseContext context) {
        SkipWhiteSpace(context: context);
        while (!context.Scanner.Cursor.Eof && ((context.Scanner.Cursor.Current == ';') || (context.Scanner.Cursor.Current == ','))) {
            context.Scanner.Cursor.Advance();
            SkipWhiteSpace(context: context);
        }
    }
    private static void SkipWhiteSpace(ParseContext context) {
        var result = new ParseResult<TextSpan>();

        WhiteSpace.Parse(context: context, result: ref result);
    }
    private static ParseContext CreateContext(string source, IDocumentVocabulary? vocabulary = null, DiagnosticBag? diagnostics = null) {
        return new PuckParseContext(diagnostics: diagnostics, scanner: new Scanner(buffer: source), vocabulary: vocabulary) {
            WhiteSpaceParser = WhiteSpace,
        };
    }

    // The vocabulary and the bag ride the context so the expression grammar, which threads neither, can ask how a
    // call's argument is written.
    private sealed class PuckParseContext(Scanner scanner, IDocumentVocabulary? vocabulary, DiagnosticBag? diagnostics) : ParseContext(scanner) {
        public DiagnosticBag? Diagnostics { get; } = diagnostics;
        public IDocumentVocabulary? Vocabulary { get; } = vocabulary;
    }

    // An argument the vocabulary owns (a name, a cell key, a value expression) carries its parsed operand tree
    // and the author's spelling. A string literal and a container still parse as themselves, so lowering can refuse the
    // literal by name and a list of names stays a list.
    private static ExpressionNode ParseArgumentValue(ParseContext context, string callName, string? argumentName, int positionalIndex) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (
            (context is PuckParseContext { Vocabulary: { } vocabulary } owned) &&
            ((argumentName ?? vocabulary.NameCallArgument(callName: callName, positionalIndex: positionalIndex)) is { } resolved) &&
            (vocabulary.ClassifyArgument(argumentName: resolved, callName: callName) is var form and (DocumentValueForm.Name or DocumentValueForm.Key or DocumentValueForm.Expression)) &&
            !IsWholeStringOrContainer(buffer: context.Scanner.Buffer, offset: cursor.Offset)
        ) {
            var start = cursor.Offset;

            var (line, column) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: start);
            var text = ScanOperandSpan(context: context, sawComparator: out _, stopAtComparator: false, stopKeywords: null);

            if (text.Length == 0) {
                throw CreateException(context: context, message: $"Expected a value for '{resolved}' in call to '{callName}'");
            }

            // An absent member is `null` in every form; it names nothing.
            if (string.Equals(a: text, b: "null", comparisonType: StringComparison.Ordinal)) {
                return new IdentifierExpressionNode(Column: column, Length: (cursor.Offset - start), Line: line, Name: text, Offset: start);
            }

            var span = new SourceSpan(start, (cursor.Offset - start), line, column);

            var operand = CreateOperand(column: column, form: form, length: span.Length, line: line, offset: start, text: text);

            if (form == DocumentValueForm.Expression) {
                operand = ValidateOperand(operand, owned.Diagnostics);
            } else {
                var respelled = ExpressionSpelling.ToSourceDialect(text: text);

                if (!string.Equals(a: respelled, b: text, comparisonType: StringComparison.Ordinal)) {
                    owned.Diagnostics?.ReportError(
                        code: PuckDiagnosticCodes.OperandColonChannel,
                        message: $"'{text}' spells a reserved channel with colons; write '{respelled}'",
                        span: span
                    );
                }
            }

            return operand;
        }

        return ParseMemberValue(context: context);
    }
    // A member's value is read by the compile-time grammar where that grammar reads all of it. What it refuses or
    // leaves unfinished is an operand of the vocabulary's own grammar when that grammar reads it, and is held as
    // written for lowering to classify; anything else is the compile-time grammar's own error.
    private static ExpressionNode ParseMemberValue(ParseContext context) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);

        var position = cursor.Position;

        if (IsWholeStringOrContainer(buffer: context.Scanner.Buffer, offset: position.Offset)) {
            return ParseExpression(context: context);
        }

        PuckParseException? refusal = null;
        ExpressionNode? parsed = null;

        try {
            parsed = ParseExpression(context: context);

            // The compile-time grammar reads ahead for an operator, so it may already stand past a line break: it
            // read the whole member when a line ended behind it or a terminator stands in front of it.
            if (BrokeLine(buffer: context.Scanner.Buffer, offset: cursor.Offset) || EndsMemberValue(buffer: context.Scanner.Buffer, offset: cursor.Offset)) {
                return parsed;
            }
        } catch (PuckParseException error) {
            refusal = error;
        }

        var failed = cursor.Position;

        cursor.ResetPosition(position: position);

        var (line, column) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: position.Offset);
        var held = ScanOperandSpan(context: context, sawComparator: out _, stopAtComparator: false, stopKeywords: null);

        var operand = CreateOperand(column: column, form: DocumentValueForm.Unclassified, length: (cursor.Offset - position.Offset), line: line, offset: position.Offset, text: held);

        if ((held.Length > 0) && (operand.Syntax is not null)) {
            return operand;
        }
        if (refusal is not null) {
            throw refusal;
        }

        cursor.ResetPosition(position: failed);

        return parsed!;
    }
    private static bool BrokeLine(string buffer, int offset) {
        for (var index = (offset - 1); ((index >= 0) && char.IsWhiteSpace(c: buffer[index])); index--) {
            if (buffer[index] is '\n' or '\r') {
                return true;
            }
        }

        return false;
    }
    private static bool EndsMemberValue(string buffer, int offset) {
        while ((offset < buffer.Length) && (buffer[offset] is ' ' or '\t')) {
            offset++;
        }

        return (
            (offset >= buffer.Length) ||
            (buffer[offset] is ',' or '}' or ')' or ']' or ';' or '\n' or '\r') ||
            ((buffer[offset] == '/') && ((offset + 1) < buffer.Length) && (buffer[(offset + 1)] is '/' or '*'))
        );
    }
    // A string literal is the member's whole value only when the value ends where the string closes: an
    // interpolated string with more behind it is an atom that opens an operand.
    private static bool IsWholeStringOrContainer(string buffer, int offset) => (
        (offset < buffer.Length) &&
        (
            (buffer[offset] is '"' or '[' or '{') ||
            (
                (buffer[offset] == '$') && ((offset + 1) < buffer.Length) && (buffer[(offset + 1)] == '"') &&
                (StringLiteralEnd(buffer: buffer, offset: offset) is var end) &&
                ((end < 0) || EndsMemberValue(buffer: buffer, offset: end))
            )
        )
    );

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

        return (Line: (lineIdx + 1), Column: ((offset - starts[lineIdx]) + 1));
    }

    // A construct that only exists inside one enclosing block, the block it needs, and the code that names its
    // misplacement. `Quoted` marks the ones followed by a quoted name; the rest open with an operand. Both shapes
    // are distinguishable from an ordinary property or block of the same name (`when: 120` in a capture row,
    // `transform { }` as a nested object), which stay legal wherever they appear.
    private static readonly (string Keyword, string Owner, string Code, bool Quoted)[] MisplacedConstructs = [
        ("option", "decision", PuckDiagnosticCodes.DecisionStructure, true),
        ("when", "rule", PuckDiagnosticCodes.DecisionStructure, false),
        ("local", "rule", PuckDiagnosticCodes.DecisionStructure, false),
        ("interrupt", "decision", PuckDiagnosticCodes.DecisionStructure, false),
        ("push", "rule", PuckDiagnosticCodes.EffectOutsideEffectsBody, false),
        ("remove", "rule", PuckDiagnosticCodes.EffectOutsideEffectsBody, false),
        ("schedule", "rule", PuckDiagnosticCodes.EffectOutsideEffectsBody, false),
    ];

    // Refuses a rule-body-only keyword found where an ordinary statement belongs, naming the block it needs instead
    // of letting the generic statement grammar fail further along the line on an unrelated token.
    private static void RefuseMisplacedConstruct(ParseContext context, string identifier, int nextOffset, int startOffset, int line, int col) {
        var buffer = context.Scanner.Buffer;
        var probe = nextOffset;

        while ((probe < buffer.Length) && ((buffer[probe] == ' ') || (buffer[probe] == '\t'))) {
            probe++;
        }
        if (probe >= buffer.Length) {
            return;
        }
        var next = buffer[probe];

        foreach (var (keyword, owner, code, quoted) in MisplacedConstructs) {
            if (!string.Equals(a: identifier, b: keyword, comparisonType: StringComparison.Ordinal)) {
                continue;
            }
            var shapeMatches = (quoted ? (next == '"') : (IdentifierSpelling.IsStart(character: next) || (next is IdentifierSpelling.Sigil or '`')));

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

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: offset);
        return new PuckParseException(message, offset, line, col);
    }
    private static bool IsHexChar(char c) {
        return ((c is >= '0' and <= '9') || (c is >= 'a' and <= 'f') || (c is >= 'A' and <= 'F'));
    }
}
