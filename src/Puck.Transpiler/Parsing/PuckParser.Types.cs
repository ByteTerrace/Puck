using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Parsing;

public static partial class PuckParser {
    private static bool TryMatchEnumKeyword(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (TryMatchKeyword(context: context, keyword: "enum") && SkipSpacesOnLine(context: context)) {
            if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out _)) {
                cursor.ResetPosition(position: saved);
                return TryMatchKeyword(context: context, keyword: "enum");
            }
        }

        cursor.ResetPosition(position: saved);
        return false;
    }
    private static bool TryMatchRecordKeyword(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (TryMatchKeyword(context: context, keyword: "record") && SkipSpacesOnLine(context: context)) {
            if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out _)) {
                cursor.ResetPosition(position: saved);
                return TryMatchKeyword(context: context, keyword: "record");
            }
        }

        cursor.ResetPosition(position: saved);
        return false;
    }
    private static bool TryMatchDeriveKeyword(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (TryMatchKeyword(context: context, keyword: "derive") && SkipSpacesOnLine(context: context)) {
            if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out _)) {
                cursor.ResetPosition(position: saved);
                return TryMatchKeyword(context: context, keyword: "derive");
            }
        }

        cursor.ResetPosition(position: saved);
        return false;
    }
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
                Name: fieldName,
                Offset: fStart,
                TypeName: typeName
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
        var expr = ParseExpression(context: context);
        var rawExpr = context.Scanner.Buffer.Substring(exprStart, (cursor.Offset - exprStart)).Trim();
        var len = (cursor.Offset - startOffset);

        return new DerivedStateNode(
            Column: col,
            Expression: expr,
            Length: len,
            Line: line,
            Name: name,
            Offset: startOffset,
            RawExpression: rawExpr
        );
    }
}
