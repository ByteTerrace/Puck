using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Parsing;

// `table`/`slot` state-row declarations (a schema-agnostic grammar shape; which vocabulary admits them, and where,
// is the owning vocabulary's own answer — see Puck.World.Transpiler for `state.world`).
public static partial class PuckParser {
    // `table`/`slot` open a declaration only when a row name and ':' follow on the same line, so an ordinary
    // `table: ...` or `slot: ...` property, or a bare flag of that name, still parses as it always has.
    private static bool TryMatchDeclarationKeyword(ParseContext context, string keyword) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (TryMatchKeyword(context: context, keyword: keyword) && SkipSpacesOnLine(context: context)) {
            if (TryReadIdentifier(context: context, identifier: out _)) {
                SkipSpacesOnLine(context: context);

                if (!cursor.Eof && (cursor.Current == ':')) {
                    cursor.ResetPosition(position: saved);

                    return TryMatchKeyword(context: context, keyword: keyword);
                }
            }
        }

        cursor.ResetPosition(position: saved);

        return false;
    }
    // Skips spaces and tabs without crossing a line; answers whether any were skipped.
    private static bool SkipSpacesOnLine(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var skipped = false;

        while (!cursor.Eof && ((cursor.Current == ' ') || (cursor.Current == '\t'))) {
            cursor.Advance(count: 1);
            skipped = true;
        }

        return skipped;
    }
    private static StatementNode ParseStateTableDeclaration(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadIdentifier(context: context, identifier: out var name)) {
            throw CreateException(context: context, message: "Expected a row name after 'table'");
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: ':', context: context)) {
            throw CreateException(context: context, message: $"Expected ':' and a kind after 'table {name}'");
        }

        SkipWhiteSpace(context: context);
        if (!TryReadIdentifier(context: context, identifier: out var kind)) {
            throw CreateException(context: context, message: $"Expected a kind (Int, Fixed, Bool, or Text) after 'table {name} :'");
        }

        var modifiers = ParseStateModifiers(context: context);

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: '{', context: context)) {
            throw CreateException(context: context, message: $"Expected '{{' starting the cell body for 'table {name}'");
        }

        var cells = new List<StateCellEntryNode>();

        SkipWhiteSpace(context: context);
        while (!cursor.Eof && (cursor.Current != '}')) {
            cells.Add(item: ParseStateCellEntry(context: context, diagnostics: diagnostics));
            ConsumeSeparator(context: context);
            SkipWhiteSpace(context: context);
        }

        if (!TryConsume(c: '}', context: context)) {
            throw CreateException(context: context, message: $"Expected '}}' closing the cell body for 'table {name}'");
        }

        var len = (cursor.Offset - startOffset);

        return new StateTableDeclarationNode(Cells: cells, Column: col, Kind: kind, Length: len, Line: line, Modifiers: modifiers, Name: name, Offset: startOffset);
    }
    private static StateCellEntryNode ParseStateCellEntry(ParseContext context, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        if (!TryReadIdentifierOrString(context: context, value: out var key)) {
            throw CreateException(context: context, message: "Expected a cell key inside a 'table' body");
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: '=', context: context)) {
            throw CreateException(context: context, message: $"Expected '=' after cell key '{key}'");
        }

        var value = ParseExpression(context: context);
        var modifiers = ParseStateModifiers(context: context);
        var len = (cursor.Offset - startOffset);

        return new StateCellEntryNode(Column: col, Key: key, Length: len, Line: line, Modifiers: modifiers, Offset: startOffset, Value: value);
    }
    private static StatementNode ParseStateSlotDeclaration(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadIdentifier(context: context, identifier: out var name)) {
            throw CreateException(context: context, message: "Expected a row name after 'slot'");
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: ':', context: context)) {
            throw CreateException(context: context, message: $"Expected ':' and a kind after 'slot {name}'");
        }

        SkipWhiteSpace(context: context);
        if (!TryReadIdentifier(context: context, identifier: out var kind)) {
            throw CreateException(context: context, message: $"Expected a kind (Int, Fixed, Bool, or Text) after 'slot {name} :'");
        }

        ExpressionNode? value = null;

        SkipWhiteSpace(context: context);
        if (TryConsume(c: '=', context: context)) {
            value = ParseExpression(context: context);
        }

        var modifiers = ParseStateModifiers(context: context);
        var len = (cursor.Offset - startOffset);

        return new StateSlotDeclarationNode(Column: col, Kind: kind, Length: len, Line: line, Modifiers: modifiers, Name: name, Offset: startOffset, Value: value);
    }
    // Zero or more `name(args)` calls, chained: `bounds(...) advance(...)`. Each candidate is read speculatively —
    // an identifier not immediately followed by '(' belongs to whatever statement comes next (the next declaration,
    // an unrelated block), so the cursor rewinds rather than consuming a token this declaration does not own.
    private static List<StateModifierNode> ParseStateModifiers(ParseContext context) {
        var modifiers = new List<StateModifierNode>();
        var cursor = context.Scanner.Cursor;

        while (true) {
            var saved = cursor.Position;

            SkipWhiteSpace(context: context);
            var startOffset = cursor.Offset;

            var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

            if (!TryReadIdentifier(context: context, identifier: out var modifierName)) {
                cursor.ResetPosition(position: saved);
                break;
            }

            SkipWhiteSpace(context: context);
            if (cursor.Current != '(') {
                cursor.ResetPosition(position: saved);
                break;
            }

            var call = ParseCallExpression(col: col, context: context, functionName: modifierName, line: line, startOffset: startOffset);

            modifiers.Add(item: new StateModifierNode(Name: modifierName, Arguments: call.Arguments, Offset: startOffset, Length: (cursor.Offset - startOffset), Line: line, Column: col));
        }

        return modifiers;
    }
}
