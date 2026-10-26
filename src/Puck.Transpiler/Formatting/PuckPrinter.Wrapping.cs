using System.Text;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;

namespace Puck.Transpiler.Formatting;

// Width. The printer keeps every line break its author wrote and adds its own only where a line would pass
// LineWidth: a call's arguments, an array's elements and an object's properties each take a line of their own, an
// operator chain and a conditional chain break after their operators, and a gate breaks after `and` or `or`. Each
// added break stands where the reader skips the line break as white space, so the reparsed tree is the printed one.
// A list (arguments, elements, properties) the printer breaks reads back as one its author broke, and a list with a
// line break anywhere inside reads back the same way, so a list stays whole only when all of it fits on its one line.
// An operator chain carries no such mark, so it stays whole when its first and last lines fit with every group inside
// it laid out as it will be. Either way a second print decides what the first did: printing is a fixed point.
public static partial class PuckPrinter {
    private sealed partial class Writer {
        // Above zero inside a text that must stay one line (an interpolation's hole, a test's lines) and for a
        // printer whose output is one line of a document (PrintExpression, PrintGate), which never adds a break.
        private int m_flat;
        // Each group's decision at the column it was decided at, so a group measured inside an enclosing group's
        // measure is laid out once.
        private readonly Dictionary<(object Node, int Column), bool> m_fits = new(comparer: NodeAtColumn.Instance);

        // `wraps`: whether the writer adds its own line breaks past LineWidth.
        public Writer(bool wraps) {
            m_flat = (wraps ? 0 : 1);
        }

        private bool Wraps => (m_flat == 0);

        private int Column() {
            var column = 0;

            for (var index = (m_builder.Length - 1); ((index >= 0) && (m_builder[index] != '\n')); index--) {
                column++;
            }

            return column;
        }
        // Whether `print` writes one line, ending within the width, from the current column, when nothing inside
        // it breaks. Nothing it writes is kept.
        private bool FitsOnOneLine(Action print) {
            if (!Wraps) {
                return true;
            }

            var column = Column();
            var start = m_builder.Length;

            Flat(print: print);

            var length = (m_builder.Length - start);
            var fits = (((column + length) <= LineWidth) && (m_builder.ToString(length: length, startIndex: start).IndexOf(value: '\n') < 0));

            m_builder.Length = start;

            return fits;
        }
        // Whether `node`, which `print` writes whole from the current column, keeps its first and last lines within
        // the width while every group inside it decides for itself. Nothing `print` writes is kept.
        private bool Fits(object node, Action print) {
            if (!Wraps) {
                return true;
            }

            var key = (node, Column());

            if (m_fits.TryGetValue(key: key, value: out var known)) {
                return known;
            }
            m_fits[key] = true;

            var start = m_builder.Length;

            print();

            var firstEnd = start;

            while ((firstEnd < m_builder.Length) && (m_builder[firstEnd] != '\n')) {
                firstEnd++;
            }

            var lastStart = m_builder.Length;

            while ((lastStart > firstEnd) && (m_builder[(lastStart - 1)] != '\n')) {
                lastStart--;
            }

            var fits = (((key.Item2 + (firstEnd - start)) <= LineWidth) && ((m_builder.Length - lastStart) <= LineWidth));

            m_builder.Length = start;
            m_fits[key] = fits;

            return fits;
        }
        private void Flat(Action print) {
            m_flat++;
            try {
                print();
            } finally {
                m_flat--;
            }
        }
        private void Break(int level) {
            EndLine();
            Indent(level: level);
        }

        // A list of scalars, `[1, 2, 3]`, as rows of elements filling the width, each row ending in a comma.
        private void FilledArray(ArrayExpressionNode array, int level) {
            m_builder.Append(value: '[');
            Break(level: (level + 1));
            for (var index = 0; (index < array.Elements.Count); index++) {
                var text = Capture(expression: array.Elements[index], level: (level + 1));

                if (index > 0) {
                    // The element and the comma after it, unless it is the last.
                    if ((Column() + 2 + text.Length + (((index + 1) < array.Elements.Count) ? 1 : 0)) > LineWidth) {
                        m_builder.Append(value: ',');
                        Break(level: (level + 1));
                    } else {
                        m_builder.Append(value: ", ");
                    }
                }
                m_builder.Append(value: text);
            }
            Break(level: level);
            m_builder.Append(value: ']');
        }
        private static bool IsScalar(ExpressionNode element) => (element is LiteralExpressionNode or ColorExpressionNode or IdentifierExpressionNode or UnaryExpressionNode { Operand: LiteralExpressionNode });
        // `a + b + c`, one operator level read left to right, as one operand a line with each operator ending the
        // line before the operand it joins. The parentheses are the flat printer's, operand by operand.
        private void BrokenChain(BinaryExpressionNode binary, int level) {
            var precedence = Precedence(@operator: binary);
            var operands = new List<ExpressionNode> { binary.Right };
            var operators = new List<string> { binary.Operator };
            var current = binary.Left;

            while ((current is BinaryExpressionNode link) && !link.Parenthesized && (Precedence(@operator: link) == precedence)) {
                operands.Add(item: link.Right);
                operators.Add(item: link.Operator);
                current = link.Left;
            }
            operands.Add(item: current);
            operands.Reverse();
            operators.Reverse();
            Operand(
                level: level,
                operand: operands[0],
                parenthesize: (Precedence(@operator: operands[0]) < precedence)
            );
            for (var index = 1; (index < operands.Count); index++) {
                m_builder.Append(value: ' ').Append(value: operators[(index - 1)]);
                Break(level: (level + 1));
                Operand(
                    level: (level + 1),
                    operand: operands[index],
                    parenthesize: (Precedence(@operator: operands[index]) <= precedence)
                );
            }
        }
        // `c ? a : d ? b : e` as one arm a line, `c ? a :`, with the last line the final otherwise.
        private void BrokenConditional(ConditionalExpressionNode conditional, int level) {
            var arm = conditional;
            var armLevel = level;

            while (true) {
                Operand(
                    level: armLevel,
                    operand: arm.Condition,
                    parenthesize: (Precedence(@operator: arm.Condition) <= PuckParser.ConditionalLevel)
                );
                m_builder.Append(value: " ? ");
                Operand(
                    level: (level + 1),
                    operand: arm.WhenTrue,
                    parenthesize: (Precedence(@operator: arm.WhenTrue) <= PuckParser.ConditionalLevel)
                );
                m_builder.Append(value: " :");
                Break(level: (level + 1));
                armLevel = (level + 1);
                if ((arm.WhenFalse is ConditionalExpressionNode next) && !next.Parenthesized) {
                    arm = next;

                    continue;
                }
                Operand(
                    level: (level + 1),
                    operand: arm.WhenFalse,
                    parenthesize: (Precedence(@operator: arm.WhenFalse) < PuckParser.ConditionalLevel)
                );

                return;
            }
        }

        // An operand's text. The operand grammar ends at a line break outside parentheses and brackets, so a break
        // goes only inside parentheses, and never inside a key's brackets. A position that takes a whole expression
        // (`enclose`) wraps an operator chain at the text's own top level in parentheses first, which a program reads
        // the same. The author's own breaks are joined first, so the layout is the printer's.
        private void OperandText(string text, int level, bool enclose) {
            var line = OperandKeys(text: JoinedOperand(text: text));

            if (!Wraps || ((Column() + line.Length) <= LineWidth)) {
                m_builder.Append(value: line);

                return;
            }
            var top = TopLevelBreaks(text: line, start: 0, end: line.Length);

            if (Hugs(breaks: top, end: line.Length, level: level, start: 0, text: line)) {
                return;
            }
            if (enclose && (top.Count > 0)) {
                m_builder.Append(value: '(');
                Break(level: (level + 1));
                OperandChain(continuation: (level + 1), end: line.Length, level: (level + 1), start: 0, text: line);
                Break(level: level);
                m_builder.Append(value: ')');

                return;
            }
            OperandRun(end: line.Length, level: level, start: 0, text: line);
        }
        // text[start..end), whitespace inside a pair of parentheses: one piece a line, split after the operators the
        // loosest operator level writes there, each piece broken further when it still does not fit.
        private void OperandChain(string text, int start, int end, int level, int continuation) {
            (start, end) = Trimmed(text: text, start: start, end: end);
            if ((Column() + (end - start)) <= LineWidth) {
                m_builder.Append(value: text, startIndex: start, count: (end - start));

                return;
            }

            var breaks = TopLevelBreaks(text: text, start: start, end: end);

            if (breaks.Count == 0) {
                OperandRun(end: end, level: level, start: start, text: text);

                return;
            }
            if (Hugs(breaks: breaks, end: end, level: level, start: start, text: text)) {
                return;
            }

            var piece = start;

            for (var index = 0; (index <= breaks.Count); index++) {
                var pieceEnd = ((index < breaks.Count) ? breaks[index].Start : end);

                OperandChain(continuation: (continuation + 1), end: pieceEnd, level: ((index == 0) ? level : continuation), start: piece, text: text);
                if (index == breaks.Count) {
                    break;
                }
                m_builder.Append(value: ' ').Append(value: text, startIndex: breaks[index].Start, count: (breaks[index].End - breaks[index].Start));
                Break(level: continuation);
                piece = breaks[index].End;
            }
        }
        // `(…) op b`, one operator joining one parenthesized group or call to an operand with no parentheses of its
        // own, breaks inside the group and keeps `) op b` on the closing line.
        private bool Hugs(string text, int start, int end, List<(int Start, int End)> breaks, int level) {
            if (breaks.Count != 1) {
                return false;
            }

            var (first, last) = Trimmed(end: breaks[0].Start, start: start, text: text);
            var tail = text[breaks[0].Start..end].Trim();
            var open = first;

            while ((open < last) && IdentifierSpelling.IsNameCharacter(character: text[open])) {
                open++;
            }
            if ((open >= last) || (text[open] != '(') || !TryMatch(close: out var close, end: last, open: open, text: text) || (close != (last - 1)) ||
                tail.Contains(value: '(') || (((level * IndentWidth) + 2 + tail.Length) > LineWidth)) {
                return false;
            }
            OperandRun(end: last, level: level, reserve: (tail.Length + 1), start: first, text: text);
            m_builder.Append(value: ' ').Append(value: tail);

            return true;
        }
        // text[start..end) as written, breaking a parenthesized group that does not fit: a call's arguments one a
        // line, a grouping's chain as OperandChain lays it out. Strings and keys pass through whole. `reserve` is what
        // will follow a group that closes the text, on the same line.
        private void OperandRun(string text, int start, int end, int level, int reserve = 0) {
            var index = start;

            while (index < end) {
                var lexeme = SourceLexemes.End(offset: index, source: text);

                if (lexeme > index) {
                    m_builder.Append(value: text, startIndex: index, count: (lexeme - index));
                    index = lexeme;

                    continue;
                }

                var character = text[index];

                if ((character is '(' or '[') && TryMatch(close: out var close, end: end, open: index, text: text)) {
                    if ((character == '[') || (close == (index + 1)) || ((Column() + ((close + 1) - index) + (((close + 1) == end) ? reserve : 0)) <= LineWidth)) {
                        m_builder.Append(value: text, startIndex: index, count: ((close + 1) - index));
                        index = (close + 1);

                        continue;
                    }
                    m_builder.Append(value: '(');
                    if (IsIndexTarget(before: index, text: text)) {
                        var arguments = TopLevelCommas(end: close, start: (index + 1), text: text);
                        var argument = (index + 1);

                        for (var item = 0; (item <= arguments.Count); item++) {
                            var argumentEnd = ((item < arguments.Count) ? arguments[item] : close);
                            var (from, to) = Trimmed(end: argumentEnd, start: argument, text: text);

                            Break(level: (level + 1));
                            OperandChain(continuation: (level + 2), end: to, level: (level + 1), start: from, text: text);
                            if (item < arguments.Count) { m_builder.Append(value: ','); }
                            argument = (argumentEnd + 1);
                        }
                    } else {
                        Break(level: (level + 1));
                        OperandChain(continuation: (level + 1), end: close, level: (level + 1), start: (index + 1), text: text);
                    }
                    Break(level: level);
                    m_builder.Append(value: ')');
                    index = (close + 1);

                    continue;
                }

                m_builder.Append(value: character);
                index++;
            }
        }
    }

    private sealed class NodeAtColumn : IEqualityComparer<(object Node, int Column)> {
        public static NodeAtColumn Instance { get; } = new();

        public bool Equals((object Node, int Column) x, (object Node, int Column) y) => (ReferenceEquals(objA: x.Node, objB: y.Node) && (x.Column == y.Column));
        public int GetHashCode((object Node, int Column) obj) => HashCode.Combine(value1: System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o: obj.Node), value2: obj.Column);
    }
    // An operand's text on one line: a run of white space holding a line break becomes one space, or nothing just
    // inside a parenthesis or bracket, which is the text the printer's own breaks were taken from.
    private static string JoinedOperand(string text) {
        if (!text.Contains(value: '\n')) {
            return text;
        }

        var into = new StringBuilder(capacity: text.Length);
        var index = 0;

        while (index < text.Length) {
            var lexeme = SourceLexemes.End(offset: index, source: text);

            if (lexeme > index) {
                into.Append(value: text, startIndex: index, count: (lexeme - index));
                index = lexeme;

                continue;
            }
            if (!char.IsWhiteSpace(c: text[index])) {
                into.Append(value: text[index]);
                index++;

                continue;
            }

            var run = index;

            while ((index < text.Length) && char.IsWhiteSpace(c: text[index])) {
                index++;
            }

            var breaks = text.AsSpan(start: run, length: (index - run)).Contains(value: '\n');

            if (!breaks) {
                into.Append(value: text, startIndex: run, count: (index - run));
            } else if (!((into.Length > 0) && (into[^1] is '(' or '[')) && !((index < text.Length) && (text[index] is ')' or ']'))) {
                into.Append(value: ' ');
            }
        }

        return into.ToString();
    }
    private static (int Start, int End) Trimmed(string text, int start, int end) {
        while ((start < end) && char.IsWhiteSpace(c: text[start])) {
            start++;
        }
        while ((end > start) && char.IsWhiteSpace(c: text[(end - 1)])) {
            end--;
        }

        return (start, end);
    }
    // The commas of text[start..end) outside any nested pair and any string.
    private static List<int> TopLevelCommas(string text, int start, int end) {
        var commas = new List<int>();

        Scan(end: end, start: start, text: text, visit: (index, _) => {
            if (text[index] == ',') {
                commas.Add(item: index);
            }

            return 1;
        });

        return commas;
    }
    // The operators of text[start..end), outside any nested pair, that the loosest operator level written there
    // spells: a conditional's `?` and its `:`, or one infix level's symbols. A symbol is an infix operator only after
    // an operand, so a sign stays with what it negates.
    private static List<(int Start, int End)> TopLevelBreaks(string text, int start, int end) {
        var found = new List<(int Start, int End, int Level)>();
        var pendingQuestions = 0;

        Scan(end: end, start: start, text: text, visit: (index, previous) => {
            var character = text[index];

            if ((character == '?') && !(((index + 1) < end) && (text[(index + 1)] == '?')) && EndsOperand(previous: previous)) {
                pendingQuestions++;
                found.Add(item: (index, (index + 1), PuckParser.ConditionalLevel));

                return 1;
            }
            if ((character == ':') && (pendingQuestions > 0) && (index > start) && (text[(index - 1)] == ' ')) {
                pendingQuestions--;
                found.Add(item: (index, (index + 1), PuckParser.ConditionalLevel));

                return 1;
            }
            if (!EndsOperand(previous: previous)) {
                return 1;
            }
            foreach (var row in ExpressionOperators.Infix) {
                var symbol = row.Symbol!;

                if (((index + symbol.Length) <= end) && (string.CompareOrdinal(indexA: index, indexB: 0, length: symbol.Length, strA: text, strB: symbol) == 0)) {
                    found.Add(item: (index, (index + symbol.Length), PuckParser.InfixLevel(symbol: symbol)));

                    return symbol.Length;
                }
            }

            return 1;
        });

        if (found.Count == 0) {
            return [];
        }

        var loosest = found.Min(selector: static item => item.Level);
        // A conditional chain breaks after each arm's `:`, so an arm reads `condition ? value :` on its own line;
        // an arm that still does not fit breaks after its `?`.
        var arms = ((loosest == PuckParser.ConditionalLevel)
            ? found.Where(predicate: item => ((item.Level == loosest) && (text[item.Start] == ':'))).ToList()
            : []);

        return [.. ((arms.Count > 0) ? arms : found.Where(predicate: item => (item.Level == loosest))).Select(selector: static item => (item.Start, item.End))];
    }
    private static bool EndsOperand(char previous) => (IdentifierSpelling.IsNameCharacter(character: previous) || (previous is ')' or ']' or '"' or '`'));
    // Calls `visit` with each character of text[start..end) that stands outside every nested pair and every string or
    // backquoted name, and the last character before it that is not white space; `visit` answers how far to step.
    private static void Scan(string text, int start, int end, Func<int, char, int> visit) {
        var depth = 0;
        var previous = '\0';
        var index = start;

        while (index < end) {
            var lexeme = SourceLexemes.End(offset: index, source: text);

            if (lexeme > index) {
                index = lexeme;
                previous = text[(lexeme - 1)];

                continue;
            }

            var character = text[index];

            if (character is '(' or '[') {
                depth++;
                previous = character;
                index++;

                continue;
            }
            if (character is ')' or ']') {
                depth = Math.Max(val1: 0, val2: (depth - 1));
                previous = character;
                index++;

                continue;
            }
            if (char.IsWhiteSpace(c: character)) {
                index++;

                continue;
            }
            if (depth > 0) {
                previous = character;
                index++;

                continue;
            }

            var step = Math.Max(val1: 1, val2: visit(arg1: index, arg2: previous));

            previous = text[((index + step) - 1)];
            index += step;
        }
    }
}
