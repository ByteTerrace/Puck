using System.Text;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;

namespace Puck.Transpiler.Lowering;

public static partial class DocumentLowering {
    /// <summary>Chooses a marker prefix absent from the source and all replacement spellings.</summary>
    /// <param name="text">The source whose literal text must survive replacement.</param>
    /// <param name="replacements">The final spellings, which must never be replaced a second time.</param>
    /// <param name="prefix">The initial marker; defaults to a non-identifier control character. Grammar-only rewrites may request an identifier.</param>
    /// <returns>A marker prefix absent from the source and replacement spellings.</returns>
    public static string AtomMarkerPrefix(string text, IReadOnlyList<string> replacements, string prefix = "\u0001") {
        ArgumentNullException.ThrowIfNull(argument: text);
        ArgumentNullException.ThrowIfNull(argument: replacements);
        ArgumentException.ThrowIfNullOrEmpty(argument: prefix);
        while (text.Contains(comparisonType: StringComparison.Ordinal, value: prefix) || replacements.Any(predicate: value => value.Contains(comparisonType: StringComparison.Ordinal, value: prefix))) {
            prefix += prefix;
        }
        return prefix;
    }
    /// <summary>Evaluates every atom of an operand and returns what the operand grammar reads where each
    /// stood.</summary>
    /// <param name="operand">The operand.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>One spelling per atom, in written order: a number as written, a bare name as written, and any
    /// other name backquoted.</returns>
    /// <remarks>An atom's result is one token, so it cannot add syntax to the text around it. A result that holds a
    /// backquote has no spelling and is reported.</remarks>
    public static IReadOnlyList<string> AtomSpellings(OperandExpressionNode operand, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(argument: operand);
        ArgumentNullException.ThrowIfNull(argument: scope);

        if (operand.Atoms.Count == 0) {
            return [];
        }

        var spellings = new string[operand.Atoms.Count];

        for (var index = 0; (index < spellings.Length); index++) {
            var computed = (KeyText(node: At(context: null, lower: () => LowerValue(expr: operand.Atoms[index].Value, scope: scope), scope: scope)) ?? string.Empty);

            if ((computed.Length == 0) || computed.Contains(value: '`')) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.InvalidValue,
                    message: $"'{operand.Text.Substring(length: operand.Atoms[index].Length, startIndex: operand.Atoms[index].Start)}' computes '{computed}', which is neither a name nor a number",
                    span: operand.Span
                );
            }

            spellings[index] = ((Puck.State.ExpressionSpelling.IsBareName(name: computed) || decimal.TryParse(provider: System.Globalization.CultureInfo.InvariantCulture, result: out _, s: computed, style: System.Globalization.NumberStyles.Float))
                ? computed
                : $"`{computed}`"
            );
        }

        return spellings;
    }
    /// <summary>Returns operand text with every atom replaced by its computed spelling.</summary>
    /// <param name="operand">The operand.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>The text the operand grammar reads.</returns>
    public static string SpliceAtoms(OperandExpressionNode operand, DocumentScope scope) {
        var spellings = AtomSpellings(operand: operand, scope: scope);
        var index = 0;

        return PuckParser.ReplaceAtoms(operand: operand, replace: _ => spellings[index++]);
    }
    /// <summary>Returns the bare spelling of an expression an interpolated string builds as text.</summary>
    /// <param name="interpolated">The interpolated string.</param>
    /// <returns>The expression written bare: a hole pasted onto name characters becomes an atom with them, a hole
    /// that holds a number becomes the number, a hole that holds one binding's name where a bare word reads a
    /// binding becomes that name, any other hole becomes an atom of its own, and a reserved colon spelling becomes
    /// the call an author writes.</returns>
    public static string BareInterpolation(InterpolatedStringNode interpolated) {
        ArgumentNullException.ThrowIfNull(argument: interpolated);

        // One element per literal character or hole, so a run of name characters and holes is found across segments.
        var elements = new List<(char Character, ExpressionNode? Hole)>();

        foreach (var segment in interpolated.Segments) {
            switch (segment) {
                case InterpolationSegment.Literal literal:
                    foreach (var character in literal.Text) {
                        elements.Add(item: (character, null));
                    }

                    break;

                case InterpolationSegment.Hole hole:
                    elements.Add(item: ('\0', hole.Expression));

                    break;
            }
        }

        var atoms = new List<string>();
        var markerPrefix = AtomMarkerPrefix(PuckPrinter.PrintExpression(interpolated), [], "__atom");
        var into = new StringBuilder();

        for (var index = 0; (index < elements.Count);) {
            if (!IsNameElement(element: elements[index])) {
                into.Append(value: elements[index].Character);
                index++;

                continue;
            }

            var end = index;
            var holes = 0;

            while ((end < elements.Count) && IsNameElement(element: elements[end])) {
                holes += ((elements[end].Hole is null) ? 0 : 1);
                end++;
            }

            if (holes == 0) {
                for (; (index < end); index++) {
                    into.Append(value: elements[index].Character);
                }

                continue;
            }

            if (
                ((end - index) == 1) &&
                (elements[index].Hole is IdentifierExpressionNode binding) &&
                ReadsAsBinding(after: ((end < elements.Count) ? elements[end].Character : ' '), before: ((index > 0) ? elements[(index - 1)].Character : ' '))
            ) {
                into.Append(value: binding.Name);
            } else if (((end - index) == 1) && (elements[index].Hole is LiteralExpressionNode { Value: not string } number)) {
                into.Append(value: PuckPrinter.PrintExpression(expression: number));
            } else {
                var segments = new List<InterpolationSegment>();
                var literal = new StringBuilder();

                for (var at = index; (at < end); at++) {
                    if (elements[at].Hole is { } expression) {
                        if (literal.Length > 0) {
                            segments.Add(item: new InterpolationSegment.Literal(Text: literal.ToString()));
                            literal.Clear();
                        }

                        segments.Add(item: new InterpolationSegment.Hole(Expression: expression));
                    } else {
                        literal.Append(value: elements[at].Character);
                    }
                }
                if (literal.Length > 0) {
                    segments.Add(item: new InterpolationSegment.Literal(Text: literal.ToString()));
                }

                into.Append(value: AtomPlaceholder(markerPrefix, atoms.Count));
                atoms.Add(item: PuckPrinter.PrintExpression(expression: new InterpolatedStringNode(Segments: segments)));
            }

            index = end;
        }

        var text = Puck.State.ExpressionSpelling.ToSourceDialect(text: into.ToString());

        for (var index = 0; (index < atoms.Count); index++) {
            text = text.Replace(comparisonType: StringComparison.Ordinal, newValue: atoms[index], oldValue: AtomPlaceholder(index: index, prefix: markerPrefix));
        }

        return text;
    }

    private static string AtomPlaceholder(string prefix, int index) => $"{prefix}{index}__";
    private static bool IsNameElement((char Character, ExpressionNode? Hole) element) => (
        (element.Hole is not null) ||
        char.IsAsciiLetterOrDigit(c: element.Character) ||
        (element.Character == '_')
    );
    // A bare word reads a compile-time binding only where no sigil, bracket or call makes it a read of live state
    // or a literal key. KEEP IN SYNC with the world emitter's BareIdentifier.
    private static bool ReadsAsBinding(char before, char after) => (
        (before is not ('$' or '`' or '.' or '[')) &&
        (after is not ('(' or '[' or ':' or '`'))
    );
}
