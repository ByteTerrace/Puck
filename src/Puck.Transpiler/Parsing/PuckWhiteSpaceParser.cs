using Parlot;
using Parlot.Fluent;

namespace Puck.Transpiler.Parsing;

/// <summary>Whitespace parser for Puck that transparently skips horizontal whitespace, newlines, single-line (//) and multi-line (/* */) comments.</summary>
public sealed class PuckWhiteSpaceParser : Parser<TextSpan> {
    /// <inheritdoc/>
    public override bool Parse(ParseContext context, ref ParseResult<TextSpan> result) {
        var cursor = context.Scanner.Cursor;
        var start = cursor.Offset;

        while (!cursor.Eof) {
            if (char.IsWhiteSpace(cursor.Current)) {
                cursor.Advance();
            } else if ((cursor.Current == '/') && (cursor.PeekNext() == '/')) {
                cursor.Advance(2);
                while (!cursor.Eof && (cursor.Current != '\n') && (cursor.Current != '\r')) {
                    cursor.Advance();
                }
            } else if ((cursor.Current == '/') && (cursor.PeekNext() == '*')) {
                cursor.Advance(2);
                while (!cursor.Eof && !((cursor.Current == '*') && (cursor.PeekNext() == '/'))) {
                    cursor.Advance();
                }
                if (!cursor.Eof) {
                    cursor.Advance(2);
                }
            } else {
                break;
            }
        }

        if (cursor.Offset > start) {
            result.Set(start, cursor.Offset, new TextSpan(context.Scanner.Buffer, start, cursor.Offset - start));
            return true;
        }

        return false;
    }
}
