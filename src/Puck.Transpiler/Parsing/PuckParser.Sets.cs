using Parlot.Fluent;
using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Parsing;

public static partial class PuckParser {
    // `set <name>: <cell-set expression>` — a one-line declaration, so the expression runs to end of line and the
    // owning vocabulary hands it whole to the cell-set algebra, exactly as `match:` hands a pattern's language over.
    private static bool TryMatchSetKeyword(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (
            TryMatchKeyword(
            context: context,
            keyword: "set"
        ) &&
            SkipSpacesOnLine(context: context) &&
            TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out _)
        ) {
            SkipSpacesOnLine(context: context);

            if (cursor.Current == ':') {
                cursor.ResetPosition(position: saved);

                return TryMatchKeyword(
                    context: context,
                    keyword: "set"
                );
            }
        }

        cursor.ResetPosition(position: saved);

        return false;
    }
    private static CellSetDeclarationNode ParseSetDeclaration(ParseContext context, int startOffset, int line, int col) {
        var cursor = context.Scanner.Cursor;

        SkipSpacesOnLine(context: context);
        if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out var name)) {
            throw CreateException(
                context: context,
                message: "Expected a set name after 'set'"
            );
        }

        SkipSpacesOnLine(context: context);
        if (!TryConsume(
            c: ':',
            context: context
        )) {
            throw CreateException(
                context: context,
                message: $"Expected ':' after 'set {name}'"
            );
        }

        var expression = ReadRestOfLine(context: context).Trim();

        if (expression.Length == 0) {
            throw CreateException(
                context: context,
                message: $"Expected a cell-set expression after 'set {name}:'"
            );
        }

        return new CellSetDeclarationNode(
            Column: col,
            Expression: expression,
            Length: (cursor.Offset - startOffset),
            Line: line,
            Name: name,
            Offset: startOffset
        );
    }
}
