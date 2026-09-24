using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Syntax = Puck.State.ExpressionSpelling;

namespace Puck.Transpiler.Parsing;

public static partial class PuckParser {
    private static OperandExpressionNode CreateOperand(string text, Diagnostics.SourceSpan span) => CreateOperand(
        text: text, form: DocumentValueForm.Expression, offset: span.Offset, length: span.Length,
        line: span.Line, column: span.Column);

    /// <summary>Creates an operand from its text, with the interpolated strings the text holds parsed as its
    /// atoms.</summary>
    /// <param name="text">The operand text, trimmed.</param>
    /// <param name="form">Which grammar the vocabulary reads the text in.</param>
    /// <param name="offset">The character offset within the source text.</param>
    /// <param name="length">The character length of the node span.</param>
    /// <param name="line">The 1-based line number in source text.</param>
    /// <param name="column">The 1-based column number in source text.</param>
    /// <returns>The operand.</returns>
    /// <exception cref="PuckParseException">An interpolated string inside <paramref name="text"/> is
    /// malformed.</exception>
    public static OperandExpressionNode CreateOperand(string text, DocumentValueForm form, int offset = 0, int length = 0, int line = 1, int column = 1) {
        ArgumentNullException.ThrowIfNull(argument: text);
        var atoms = ScanAtoms(text: text);

        Syntax.TryParseSyntax(
            text: text,
            atoms: [.. atoms.Select(selector: static atom => new Syntax.SourceAtomSpan(atom.Start, atom.Length))],
            syntax: out var syntax,
            error: out var error
        );

        return new OperandExpressionNode(
            Atoms: atoms,
            Column: column,
            Form: form,
            Length: length,
            Line: line,
            Offset: offset,
            Text: text
        ) { Syntax = syntax, SyntaxError = error };
    }
    /// <summary>Returns operand text with every atom replaced by <paramref name="replace"/>'s result.</summary>
    /// <param name="operand">The operand.</param>
    /// <param name="replace">Returns the text that stands where an atom stood.</param>
    /// <returns>The text with no atom left in it.</returns>
    public static string ReplaceAtoms(OperandExpressionNode operand, Func<OperandAtom, string> replace) {
        ArgumentNullException.ThrowIfNull(argument: operand);
        ArgumentNullException.ThrowIfNull(argument: replace);

        if (operand.Atoms.Count == 0) {
            return operand.Text;
        }

        var into = new System.Text.StringBuilder(capacity: operand.Text.Length);
        var from = 0;

        foreach (var atom in operand.Atoms) {
            into.Append(value: operand.Text, startIndex: from, count: (atom.Start - from));
            into.Append(value: replace(arg: atom));
            from = (atom.Start + atom.Length);
        }

        return into.Append(value: operand.Text, startIndex: from, count: (operand.Text.Length - from)).ToString();
    }

    private static IReadOnlyList<OperandAtom> ScanAtoms(string text) {
        var spans = ScanAtomSpans(text: text);

        if (spans.Count == 0) {
            return [];
        }

        var atoms = new List<OperandAtom>(capacity: spans.Count);

        foreach (var (start, length) in spans) {
            if (ParseExpression(source: text.Substring(length: length, startIndex: start)) is not InterpolatedStringNode value) {
                throw new PuckParseException($"'{text.Substring(length: length, startIndex: start)}' is not an interpolated string", start, 1, (start + 1));
            }

            atoms.Add(item: new OperandAtom(Length: length, Start: start, Value: value));
        }

        return atoms;
    }
    // An atom is an interpolated string outside a backquoted name. Plain strings are left to the operand grammar,
    // which admits them only where a call accepts string arguments.
    private static List<(int Start, int Length)> ScanAtomSpans(string text) {
        var spans = new List<(int Start, int Length)>();

        for (var index = 0; (index < text.Length); index++) {
            var character = text[index];

            if (character == '`') {
                var close = text.IndexOf(startIndex: (index + 1), value: '`');

                if (close < 0) {
                    break;
                }

                index = close;
            } else if (StringLiteralEnd(buffer: text, offset: index) is var end and > 0) {
                if (character == '$') {
                    spans.Add(item: (index, (end - index)));
                }

                index = (end - 1);
            }
        }

        return spans;
    }
    // Where the string literal that opens at the offset closes, or -1 when none opens there or it never closes.
    private static int StringLiteralEnd(string buffer, int offset) {
        var quote = (((buffer[offset] == '$') && ((offset + 1) < buffer.Length)) ? (offset + 1) : offset);

        if (buffer[quote] != '"') {
            return -1;
        }
        if (Matches(buffer: buffer, offset: quote, token: RawFence)) {
            var close = buffer.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: (quote + RawFence.Length), value: RawFence);

            return ((close < 0) ? -1 : (close + RawFence.Length));
        }

        return (PuckStrings.TryRead(end: out var end, error: out _, offset: quote, source: buffer, value: out _) ? end : -1);
    }
}
