using Parlot.Fluent;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Parsing;

public static partial class PuckParser {
    // `rules` opens a scope only when followed by a name (and not ':' or '['), so `rules:` or `rules [` parses as property.
    private static bool TryMatchRuleScopeKeyword(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (TryMatchKeyword(context: context, keyword: "rules") && SkipSpacesOnLine(context: context)) {
            if (TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out _)) {
                SkipSpacesOnLine(context: context);

                if (!cursor.Eof && (cursor.Current != ':') && (cursor.Current != '[')) {
                    cursor.ResetPosition(position: saved);

                    return TryMatchKeyword(context: context, keyword: "rules");
                }
            }
        }

        cursor.ResetPosition(position: saved);

        return false;
    }
    private static RuleScopeNode ParseRuleScopeBlock(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        using var locals = ExpressionSpelling.WithLocals(locals: DeclaredLocals(
            buffer: context.Scanner.Buffer,
            offset: context.Scanner.Cursor.Offset
        ));
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out var name)) {
            var (nLine, nCol) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: cursor.Offset);
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.RuleNameMissing,
                message: "Expected an identifier or quoted name after 'rules'",
                span: new SourceSpan(cursor.Offset, 1, nLine, nCol)
            );
            name = string.Empty;
        }

        WhenStatementNode? headerWhen = null;

        SkipWhiteSpace(context: context);

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
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: $"Expected '{{' starting body for rules scope '{name}'");
        }

        var statements = new List<StatementNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            var stmt = ParseRuleScopeBodyStatement(context: context, diagnostics: diagnostics);

            if (stmt is not null) {
                statements.Add(item: stmt);
            }
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: $"Expected '}}' closing body for rules scope '{name}'");
        }

        var len = (cursor.Offset - startOffset);

        return new RuleScopeNode(
            Column: col,
            HeaderWhen: headerWhen,
            Length: len,
            Line: line,
            Name: name,
            Offset: startOffset,
            Statements: statements
        );
    }
    private static StatementNode? ParseRuleScopeBodyStatement(ParseContext context, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        if (TryMatchRuleScopeKeyword(context: context)) {
            return ParseRuleScopeBlock(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchStabilizeKeyword(context: context)) {
            return ParseStabilizeBlock(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchWorkflowKeyword(context: context)) {
            return ParseWorkflowBlock(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchDeriveKeyword(context: context)) {
            return ParseDerivedState(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchKeyword(context: context, keyword: "rule")) {
            return ParseRuleBlock(col: col, context: context, diagnostics: diagnostics, line: line, startOffset: startOffset);
        }

        if (TryMatchKeyword(context: context, keyword: "when")) {
            return ParseWhenStatement(
                col: col,
                context: context,
                diagnostics: diagnostics,
                line: line,
                startOffset: startOffset
            );
        }

        if (TryMatchKeyword(context: context, keyword: "local")) {
            return ParseLocalStatement(
                col: col,
                context: context,
                diagnostics: diagnostics,
                line: line,
                startOffset: startOffset
            );
        }

        if (ParseEffectStatement(context: context, diagnostics: diagnostics) is { } eff) {
            return eff;
        }

        if (TryParseGenericProperty(context: context, diagnostics: diagnostics) is { } prop) {
            return prop;
        }

        throw CreateException(context: context, message: $"Unexpected statement inside rules scope at offset {cursor.Offset}");
    }
}
