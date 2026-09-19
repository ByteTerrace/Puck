using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.Transpiler.Parsing;

// `table`/`slot` state-row declarations (a schema-agnostic grammar shape; which vocabulary admits them, and where,
// is the owning vocabulary's own answer — see Puck.World.Transpiler for `state.world`).
public static partial class PuckParser {
    // `table`/`slot` open a declaration only when a row name (and optional [size]) and ':' follow on the same line,
    // so an ordinary `table: ...` or `slot: ...` property, or a bare flag of that name, still parses as it always has.
    private static bool TryMatchDeclarationKeyword(ParseContext context, string keyword) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (TryMatchKeyword(context: context, keyword: keyword) && SkipSpacesOnLine(context: context)) {
            if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out _)) {
                SkipSpacesOnLine(context: context);

                var hasBracket = false;

                if (!cursor.Eof && (cursor.Current == '[')) {
                    hasBracket = TrySkipBracketedSpanOnLine(context: context);
                    SkipSpacesOnLine(context: context);
                }

                if (hasBracket || (!cursor.Eof && ((cursor.Current == ':') || (cursor.Current == '=') || (cursor.Current == '{')))) {
                    cursor.ResetPosition(position: saved);

                    return TryMatchKeyword(context: context, keyword: keyword);
                }
            }
        }

        cursor.ResetPosition(position: saved);

        return false;
    }
    // Skips bracketed span on the current line.
    private static bool TrySkipBracketedSpanOnLine(ParseContext context) {
        var cursor = context.Scanner.Cursor;

        if (cursor.Eof || (cursor.Current != '[')) {
            return false;
        }

        cursor.Advance(count: 1);
        var depth = 1;

        while (!cursor.Eof && (cursor.Current != '\n') && (cursor.Current != '\r')) {
            if (cursor.Current == '[') {
                depth++;
            } else if (cursor.Current == ']') {
                depth--;
                if (depth == 0) {
                    cursor.Advance(count: 1);
                    return true;
                }
            }
            cursor.Advance(count: 1);
        }

        return false;
    }
    // Skips horizontal whitespace (spaces and tabs) on the current line.
    private static bool SkipSpacesOnLine(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var skipped = false;

        while (!cursor.Eof && ((cursor.Current == ' ') || (cursor.Current == '\t'))) {
            cursor.Advance(count: 1);
            skipped = true;
        }

        return skipped;
    }
    private static StatementNode ParseStateTableDeclaration(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, IDocumentVocabulary? vocabulary) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var name)) {
            throw CreateException(context: context, message: "Expected a row name after 'table'");
        }

        SkipSpacesOnLine(context: context);
        ParseFamilyBracket(context: context, keyword: "table", members: out var familyMembers, name: name, size: out var familySize);

        var kind = "Int";

        SkipWhiteSpace(context: context);
        if (TryConsume(c: ':', context: context)) {
            SkipWhiteSpace(context: context);
            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out kind)) {
                throw CreateException(context: context, message: $"Expected a kind (Int, Fixed, Bool, Text, or Vector) after 'table {name} :'");
            }
        }

        var modifiers = ParseStateModifiers(context: context, keyword: "table", vocabulary: vocabulary);
        ExpressionNode? initializer = null;
        var cells = new List<StateCellEntryNode>();

        SkipWhiteSpace(context: context);
        if (TryConsume(c: '=', context: context)) {
            initializer = ParseExpression(context: context);
        } else if (TryConsume(c: '{', context: context)) {
            SkipWhiteSpace(context: context);
            while (!cursor.Eof && (cursor.Current != '}')) {
                cells.Add(item: ParseStateCellEntry(context: context, diagnostics: diagnostics, keyword: "table", vocabulary: vocabulary));
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);
            }

            if (!TryConsume(c: '}', context: context)) {
                throw CreateException(context: context, message: $"Expected '}}' closing the cell body for 'table {name}'");
            }
        }

        var len = (cursor.Offset - startOffset);

        return new StateTableDeclarationNode(Cells: cells, Column: col, FamilyMembers: familyMembers, FamilySize: familySize, Initializer: initializer, Kind: kind, Length: len, Line: line, Modifiers: modifiers, Name: name, Offset: startOffset);
    }
    // A family's bracket is either a bare count -- [8], today's spelling -- or a member list: comma-separated index
    // ranges ([0, 2..12], so a family may have gaps) and quoted member row names (["PileA", "PileB"]).
    private static void ParseFamilyBracket(ParseContext context, string keyword, string name, out ExpressionNode? size, out IReadOnlyList<FamilyMemberNode>? members) {
        var cursor = context.Scanner.Cursor;

        members = null;
        size = null;

        if (!TryConsume(c: '[', context: context)) {
            return;
        }

        var items = new List<FamilyMemberNode>();

        while (true) {
            SkipWhiteSpace(context: context);

            var startOffset = cursor.Offset;

            var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

            if ((cursor.Current == '"') && TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out var row)) {
                items.Add(item: new FamilyMemberNode(
                    Column: col,
                    Length: (cursor.Offset - startOffset),
                    Line: line,
                    Offset: startOffset,
                    Row: row
                ));
            } else {
                // `a..b` is already one expression to the core parser, so an index range arrives as that node.
                var parsed = ParseExpression(context: context);
                var first = ((parsed is RangeExpressionNode range)
                    ? range.Start
                    : parsed);
                var last = ((parsed is RangeExpressionNode bounded)
                    ? bounded.End
                    : null);

                items.Add(item: new FamilyMemberNode(
                    Column: col,
                    First: first,
                    Last: last,
                    Length: (cursor.Offset - startOffset),
                    Line: line,
                    Offset: startOffset
                ));
            }

            SkipWhiteSpace(context: context);
            if (!TryConsume(c: ',', context: context)) {
                break;
            }
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: ']', context: context)) {
            throw CreateException(context: context, message: $"Expected ']' closing the family bracket for '{keyword} {name}'");
        }

        // One bare index and nothing else is the count spelling every shipped family uses; anything else is a
        // member list, and a count is not what it means.
        if ((items.Count == 1) && (items[0] is { Last: null, Row: null, First: { } only })) {
            size = only;

            return;
        }

        members = items;
    }
    private static StateCellEntryNode ParseStateCellEntry(ParseContext context, DiagnosticBag? diagnostics, string keyword, IDocumentVocabulary? vocabulary) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out var key)) {
            throw CreateException(context: context, message: "Expected a cell key inside a 'table' body");
        }

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: '=', context: context)) {
            throw CreateException(context: context, message: $"Expected '=' after cell key '{key}'");
        }

        var value = ParseExpression(context: context);
        var modifiers = ParseStateModifiers(context: context, keyword: keyword, vocabulary: vocabulary);
        var len = (cursor.Offset - startOffset);

        return new StateCellEntryNode(Column: col, Key: key, Length: len, Line: line, Modifiers: modifiers, Offset: startOffset, Value: value);
    }
    private static StatementNode ParseStateSlotDeclaration(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, IDocumentVocabulary? vocabulary) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var name)) {
            throw CreateException(context: context, message: "Expected a row name after 'slot'");
        }

        SkipSpacesOnLine(context: context);
        ParseFamilyBracket(context: context, keyword: "slot", members: out var familyMembers, name: name, size: out var familySize);

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: ':', context: context)) {
            throw CreateException(context: context, message: $"Expected ':' and a kind after 'slot {name}'");
        }

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var kind)) {
            throw CreateException(context: context, message: $"Expected a kind (Int, Fixed, Bool, Text, or Vector) after 'slot {name} :'");
        }

        var modifiers = ParseStateModifiers(context: context, keyword: "slot", vocabulary: vocabulary);
        ExpressionNode? value = null;

        SkipWhiteSpace(context: context);
        if (TryConsume(c: '=', context: context)) {
            value = ParseExpression(context: context);
            modifiers.AddRange(collection: ParseStateModifiers(context: context, keyword: "slot", vocabulary: vocabulary));
        }

        var len = (cursor.Offset - startOffset);

        return new StateSlotDeclarationNode(Column: col, FamilyMembers: familyMembers, FamilySize: familySize, Kind: kind, Length: len, Line: line, Modifiers: modifiers, Name: name, Offset: startOffset, Value: value);
    }
    // `pile` opens a declaration only when a row name and the bare keyword 'of' follow on the same line, mirroring
    // `TryMatchDeclarationKeyword`'s own guard — an ordinary `pile: ...` property still parses as it always has.
    private static bool TryMatchPileKeyword(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var saved = cursor.Position;

        if (TryMatchKeyword(context: context, keyword: "pile") && SkipSpacesOnLine(context: context)) {
            if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out _)) {
                SkipSpacesOnLine(context: context);

                if (!cursor.Eof && (cursor.Current == '[')) {
                    TrySkipBracketedSpanOnLine(context: context);
                    SkipSpacesOnLine(context: context);
                }

                if (TryMatchKeyword(context: context, keyword: "of")) {
                    cursor.ResetPosition(position: saved);

                    return TryMatchKeyword(context: context, keyword: "pile");
                }
            }
        }

        cursor.ResetPosition(position: saved);

        return false;
    }
    private static StatementNode ParseStatePileDeclaration(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, IDocumentVocabulary? vocabulary) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var name)) {
            throw CreateException(context: context, message: "Expected a row name after 'pile'");
        }

        SkipSpacesOnLine(context: context);
        ParseFamilyBracket(context: context, keyword: "pile", members: out var familyMembers, name: name, size: out var familySize);

        SkipWhiteSpace(context: context);
        if (!TryMatchKeyword(context: context, keyword: "of")) {
            throw CreateException(context: context, message: $"Expected 'of' and a token-domain row after 'pile {name}'");
        }

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var tokenRow)) {
            throw CreateException(context: context, message: $"Expected a token-domain row name after 'pile {name} of'");
        }

        var modifiers = ParseStateModifiers(context: context, keyword: "pile", vocabulary: vocabulary);
        ExpressionNode? initializer = null;
        var tokens = new List<StatePileTokenNode>();

        SkipWhiteSpace(context: context);
        if (TryConsume(c: '=', context: context)) {
            initializer = ParseExpression(context: context);
        } else if (TryConsume(c: '{', context: context)) {
            SkipWhiteSpace(context: context);
            var savedPos = cursor.Position;

            if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out _) && (cursor.Current == '(')) {
                cursor.ResetPosition(position: savedPos);
                initializer = ParseExpression(context: context);
                SkipWhiteSpace(context: context);
            } else {
                cursor.ResetPosition(position: savedPos);
                while (!cursor.Eof && (cursor.Current != '}')) {
                    tokens.Add(item: ParseStatePileToken(context: context));
                    ConsumeSeparator(context: context);
                    SkipWhiteSpace(context: context);
                }
            }

            if (!TryConsume(c: '}', context: context)) {
                throw CreateException(context: context, message: $"Expected '}}' closing the token body for 'pile {name}'");
            }
        }

        var len = (cursor.Offset - startOffset);

        return new StatePileDeclarationNode(Column: col, FamilyMembers: familyMembers, FamilySize: familySize, Initializer: initializer, Length: len, Line: line, Modifiers: modifiers, Name: name, Offset: startOffset, TokenRow: tokenRow, Tokens: tokens);
    }
    private static StatePileTokenNode ParseStatePileToken(ParseContext context) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

        if (!TryReadName(admitted: NameForms.Identifier | NameForms.String, context: context, spelling: out _, text: out var key)) {
            throw CreateException(context: context, message: "Expected a token key inside a 'pile' body");
        }

        var len = (cursor.Offset - startOffset);

        return new StatePileTokenNode(Column: col, Key: key, Length: len, Line: line, Offset: startOffset);
    }
    private static StatementNode ParseStateGridDeclaration(ParseContext context, int startOffset, int line, int col, DiagnosticBag? diagnostics, IDocumentVocabulary? vocabulary) {
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var name)) {
            throw CreateException(context: context, message: "Expected a row name after 'grid'");
        }

        SkipSpacesOnLine(context: context);
        ParseFamilyBracket(context: context, keyword: "grid", members: out var familyMembers, name: name, size: out var familySize);

        SkipWhiteSpace(context: context);
        if (!TryConsume(c: ':', context: context)) {
            throw CreateException(context: context, message: $"Expected ':' and a kind after 'grid {name}'");
        }

        SkipWhiteSpace(context: context);
        if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var kind)) {
            throw CreateException(context: context, message: $"Expected a kind (Int or Bool) after 'grid {name} :'");
        }

        var modifiers = ParseStateModifiers(context: context, keyword: "grid", vocabulary: vocabulary);
        var cells = new List<StateCellEntryNode>();
        var hasBody = false;

        SkipWhiteSpace(context: context);
        if (TryConsume(c: '{', context: context)) {
            hasBody = true;

            SkipWhiteSpace(context: context);
            while (!cursor.Eof && (cursor.Current != '}')) {
                cells.Add(item: ParseStateCellEntry(context: context, diagnostics: diagnostics, keyword: "grid", vocabulary: vocabulary));
                ConsumeSeparator(context: context);
                SkipWhiteSpace(context: context);
            }

            if (!TryConsume(c: '}', context: context)) {
                throw CreateException(context: context, message: $"Expected '}}' closing the cell body for 'grid {name}'");
            }
        }

        var len = (cursor.Offset - startOffset);

        return new StateGridDeclarationNode(Cells: cells, Column: col, FamilyMembers: familyMembers, FamilySize: familySize, HasBody: hasBody, Kind: kind, Length: len, Line: line, Modifiers: modifiers, Name: name, Offset: startOffset);
    }
    // Zero or more `name(args)` calls, chained: `bounds(...) advance(...)`. Each candidate is read speculatively —
    // an identifier not immediately followed by '(' belongs to whatever statement comes next (the next declaration,
    // an unrelated block), so the cursor rewinds rather than consuming a token this declaration does not own,
    // unless the vocabulary says a declaration of this keyword writes that name bare.
    private static List<StateModifierNode> ParseStateModifiers(ParseContext context, string keyword, IDocumentVocabulary? vocabulary) {
        var modifiers = new List<StateModifierNode>();
        var cursor = context.Scanner.Cursor;

        while (true) {
            var saved = cursor.Position;

            SkipWhiteSpace(context: context);
            var startOffset = cursor.Offset;

            var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

            if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var modifierName)) {
                cursor.ResetPosition(position: saved);
                break;
            }

            SkipWhiteSpace(context: context);
            if (cursor.Current != '(') {
                if ((vocabulary is not null) && vocabulary.IsBareModifier(keyword: keyword, modifier: modifierName)) {
                    modifiers.Add(item: new StateModifierNode(Arguments: [], Column: col, Length: (cursor.Offset - startOffset), Line: line, Name: modifierName, Offset: startOffset));
                    continue;
                }

                cursor.ResetPosition(position: saved);
                break;
            }

            var call = ParseCallExpression(col: col, context: context, functionName: modifierName, line: line, startOffset: startOffset);

            modifiers.Add(item: new StateModifierNode(Name: modifierName, Arguments: call.Arguments, Offset: startOffset, Length: (cursor.Offset - startOffset), Line: line, Column: col));
        }

        return modifiers;
    }
}
