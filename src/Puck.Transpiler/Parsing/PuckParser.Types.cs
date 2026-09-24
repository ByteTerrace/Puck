using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Parsing;

public static partial class PuckParser {
    private static bool TryMatchEnumKeyword(ParseContext context) =>
        TryMatchKeywordFollowedByName(admitted: NameForms.Identifier, context: context, keyword: "enum");
    private static bool TryMatchRecordKeyword(ParseContext context) =>
        TryMatchKeywordFollowedByName(admitted: NameForms.Identifier, context: context, keyword: "record");
    private static bool TryMatchDeriveKeyword(ParseContext context) =>
        TryMatchKeywordFollowedByName(admitted: NameForms.Identifier, context: context, keyword: "derive");
    private static EnumDeclarationNode ParseEnumDeclaration(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var name)) {
            throw CreateException(context: context, message: "Expected an enum name after 'enum'");
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: $"Expected '{{' starting enum body for '{name}'");
        }

        var members = new List<EnumMemberNode>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            SkipWhiteSpace(context: context);

            var memberStart = cursor.Offset;

            var (memberLine, memberColumn) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: memberStart);

            if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var member)) {
                if (!seen.Add(item: member)) {
                    diagnostics?.ReportError(
                        code: PuckDiagnosticCodes.DuplicateTypeMember,
                        message: $"Enum '{name}' declares duplicate member '{member}'",
                        span: new SourceSpan((cursor.Offset - member.Length), member.Length, line, col)
                    );
                }
                members.Add(item: new EnumMemberNode(
                    Column: memberColumn,
                    Length: (cursor.Offset - memberStart),
                    Line: memberLine,
                    Name: member,
                    Offset: memberStart
                ));
            }
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: $"Expected '}}' closing enum body for '{name}'");
        }

        var len = (cursor.Offset - startOffset);

        return new EnumDeclarationNode(
            Column: col,
            Length: len,
            Line: line,
            Members: members,
            Name: name,
            Offset: startOffset
        );
    }
    private static RecordDeclarationNode ParseRecordDeclaration(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var name)) {
            throw CreateException(context: context, message: "Expected a record name after 'record'");
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: $"Expected '{{' starting record body for '{name}'");
        }

        var fields = new List<RecordFieldNode>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            var fStart = cursor.Offset;

            var (fLine, fCol) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: fStart);

            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var fieldName)) {
                break;
            }

            SkipWhiteSpace(context: context);
            if (!TryConsume(c: ':', context: context)) {
                throw CreateException(context: context, message: $"Expected ':' after record field '{fieldName}'");
            }

            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var typeName)) {
                throw CreateException(context: context, message: $"Expected type name after '{fieldName}:'");
            }

            var modifiers = ParseStateModifiers(context: context, keyword: "record", vocabulary: null);
            ExpressionNode? defaultValue = null;

            SkipWhiteSpace(context: context);
            if (TryConsume(c: '=', context: context)) {
                defaultValue = ParseExpression(context: context);
                modifiers.AddRange(collection: ParseStateModifiers(context: context, keyword: "record", vocabulary: null));
            }

            if (!seen.Add(item: fieldName)) {
                diagnostics?.ReportError(
                    code: PuckDiagnosticCodes.DuplicateTypeMember,
                    message: $"Record '{name}' declares duplicate field '{fieldName}'",
                    span: new SourceSpan(fStart, (cursor.Offset - fStart), fLine, fCol)
                );
            }

            fields.Add(item: new RecordFieldNode(
                Column: fCol,
                Length: (cursor.Offset - fStart),
                Line: fLine,
                Modifiers: modifiers,
                Name: fieldName,
                Offset: fStart,
                TypeName: typeName,
                Default: defaultValue
            ));

            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: $"Expected '}}' closing record body for '{name}'");
        }

        var len = (cursor.Offset - startOffset);

        return new RecordDeclarationNode(
            Column: col,
            Fields: fields,
            Length: len,
            Line: line,
            Name: name,
            Offset: startOffset
        );
    }
    private static DerivedStateNode ParseDerivedState(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var name)) {
            throw CreateException(context: context, message: "Expected a name after 'derive'");
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: '=', context: context)) {
            throw CreateException(context: context, message: $"Expected '=' after 'derive {name}'");
        }

        SkipWhiteSpace(context: context);
        var exprStart = cursor.Offset;
        var rawExpr = ScanOperandSpan(context: context, sawComparator: out _, stopAtComparator: false, stopKeywords: null);

        var (exprLine, exprColumn) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: exprStart);
        var expr = ValidatedOperand(rawExpr, new SourceSpan(exprStart, (cursor.Offset - exprStart), exprLine, exprColumn), diagnostics);
        var len = (cursor.Offset - startOffset);

        return new DerivedStateNode(
            Column: col,
            Expression: expr,
            Length: len,
            Line: line,
            Name: name,
            Offset: startOffset
        );
    }
}
