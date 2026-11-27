using System.Numerics;
using System.Text;
using Puck.Maths;

namespace Puck.Text;

/// <summary>One node of parsed math markup (<see cref="MathMarkup"/>).</summary>
public abstract record MathNode {
    /// <summary>A run of nodes laid side by side on one baseline.</summary>
    /// <param name="Items">The nodes, left to right.</param>
    public sealed record Row(IReadOnlyList<MathNode> Items) : MathNode;
    /// <summary>One character drawn as its glyph.</summary>
    /// <param name="Unicode">The character's code point.</param>
    public sealed record Glyph(int Unicode) : MathNode;
    /// <summary>A fraction: the numerator over a rule over the denominator, both a size smaller.</summary>
    /// <param name="Numerator">The numerator.</param>
    /// <param name="Denominator">The denominator.</param>
    public sealed record Fraction(MathNode Numerator, MathNode Denominator) : MathNode;
    /// <summary>A square root: the radical sign before the radicand and a rule over it.</summary>
    /// <param name="Radicand">The radicand.</param>
    public sealed record Root(MathNode Radicand) : MathNode;
    /// <summary>A body between bars: one bar a side for an absolute value, two for a norm.</summary>
    /// <param name="Body">The body.</param>
    /// <param name="Bars">The bars a side, one or two.</param>
    public sealed record Fence(MathNode Body, int Bars) : MathNode;
    /// <summary>A base with an optional superscript and subscript, each a size smaller.</summary>
    /// <param name="Base">The base.</param>
    /// <param name="Superscript">The superscript, or <see langword="null"/>.</param>
    /// <param name="Subscript">The subscript, or <see langword="null"/>.</param>
    public sealed record Scripts(MathNode Base, MathNode? Superscript, MathNode? Subscript) : MathNode;
    /// <summary>A named term: a formula's parameter, drawn as its body and highlighted when the layout selects its
    /// name.</summary>
    /// <param name="Name">The term's name.</param>
    /// <param name="Body">What it draws as.</param>
    public sealed record Term(string Name, MathNode Body) : MathNode;
    /// <summary>A body drawn in one colour.</summary>
    /// <param name="Rgba">The display colour, straight alpha.</param>
    /// <param name="Body">The body.</param>
    public sealed record Tint(Vector4 Rgba, MathNode Body) : MathNode;
    /// <summary>Blank space.</summary>
    /// <param name="Ems">The width, in ems of the current size.</param>
    public sealed record Space(float Ems) : MathNode;
}

/// <summary>
/// Math markup: a small linear notation for typeset formulas, one line of markup to a line of the layout
/// (<see cref="MathLayout"/>). Characters stand for their glyphs, spaces included; <c>{…}</c> groups; <c>^</c> and
/// <c>_</c> raise and lower the next atom as a superscript or subscript of the previous one; and the commands are
/// <c>\frac{numerator}{denominator}</c>, <c>\sqrt{radicand}</c>, <c>\abs{body}</c>, <c>\norm{body}</c>,
/// <c>\term{name}{body}</c>, <c>\color{#RRGGBB[AA]}{body}</c>, <c>\,</c> (a sixth of an em) and <c>\quad</c> (an
/// em). <c>\{</c>, <c>\}</c>, <c>\\</c>, <c>\^</c> and <c>\_</c> stand for the character itself.
/// </summary>
public static class MathMarkup {
    /// <summary>The deepest nesting of groups and commands one line may hold; deeper markup is refused.</summary>
    public const int MaxDepth = 32;

    /// <summary>Parses one line of math markup.</summary>
    /// <param name="markup">The line.</param>
    /// <param name="root">The parsed row, or an empty row when the line is refused.</param>
    /// <param name="error">Why the line was refused, or empty.</param>
    /// <returns><see langword="true"/> when the line parses whole.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="markup"/> is <see langword="null"/>.</exception>
    public static bool TryParse(string markup, out MathNode.Row root, out string error) {
        ArgumentNullException.ThrowIfNull(argument: markup);

        var parser = new Parser(text: markup);

        try {
            root = parser.Row(depth: 0, closes: false);
            error = string.Empty;

            return true;
        } catch (FormatException exception) {
            root = new MathNode.Row(Items: []);
            error = exception.Message;

            return false;
        }
    }
    /// <summary>Returns the names of every term one line of markup holds, in order; a line that does not parse holds
    /// none.</summary>
    /// <param name="markup">The line.</param>
    /// <returns>The term names.</returns>
    public static IEnumerable<string> Terms(string markup) {
        if (!TryParse(error: out _, markup: markup, root: out var root)) {
            return [];
        }

        var names = new List<string>();

        Collect(node: root);

        return names;

        void Collect(MathNode node) {
            switch (node) {
                case MathNode.Row row: foreach (var item in row.Items) { Collect(node: item); } break;
                case MathNode.Fraction fraction: Collect(node: fraction.Numerator); Collect(node: fraction.Denominator); break;
                case MathNode.Root root: Collect(node: root.Radicand); break;
                case MathNode.Fence fence: Collect(node: fence.Body); break;
                case MathNode.Scripts scripts:
                    Collect(node: scripts.Base);
                    if (scripts.Superscript is { } superscript) { Collect(node: superscript); }
                    if (scripts.Subscript is { } subscript) { Collect(node: subscript); }
                    break;
                case MathNode.Term term: names.Add(item: term.Name); Collect(node: term.Body); break;
                case MathNode.Tint tint: Collect(node: tint.Body); break;
            }
        }
    }
    /// <summary>Escapes plain text so it parses as itself: every brace, backslash, caret and underscore takes its
    /// escape.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The escaped text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string Escape(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        var builder = new StringBuilder(capacity: text.Length);

        foreach (var character in text) {
            if (character is '{' or '}' or '\\' or '^' or '_') { builder.Append(value: '\\'); }
            builder.Append(value: character);
        }

        return builder.ToString();
    }

    private sealed class Parser(string text) {
        private int m_at;

        public MathNode.Row Row(int depth, bool closes) {
            if (depth > MaxDepth) { throw new FormatException(message: $"nests deeper than {MaxDepth} at position {m_at}"); }

            var items = new List<MathNode>();

            while (m_at < text.Length) {
                var character = text[m_at];

                if (character == '}') {
                    if (!closes) { throw new FormatException(message: $"closes a group no '{{' opened at position {m_at}"); }
                    return new MathNode.Row(Items: items);
                }
                if (character is '^' or '_') {
                    m_at++;
                    var script = Atom(depth: (depth + 1)) ?? throw new FormatException(message: $"has no atom after '{character}' at position {m_at}");
                    var previous = ((items.Count > 0) ? items[^1] : new MathNode.Row(Items: []));

                    if (items.Count > 0) { items.RemoveAt(index: (items.Count - 1)); }
                    items.Add(item: Attach(previous: previous, script: script, superscript: (character == '^')));
                    continue;
                }
                if (Atom(depth: (depth + 1)) is { } atom) { items.Add(item: atom); }
            }

            if (closes) { throw new FormatException(message: "leaves a '{' unclosed"); }

            return new MathNode.Row(Items: items);
        }
        private static MathNode Attach(MathNode previous, MathNode script, bool superscript) => ((previous is MathNode.Scripts scripts)
            ? (superscript
                ? ((scripts.Superscript is null) ? scripts with { Superscript = script } : new MathNode.Scripts(Base: scripts, Subscript: null, Superscript: script))
                : ((scripts.Subscript is null) ? scripts with { Subscript = script } : new MathNode.Scripts(Base: scripts, Subscript: script, Superscript: null)))
            : new MathNode.Scripts(Base: previous, Subscript: (superscript ? null : script), Superscript: (superscript ? script : null)));
        private MathNode? Atom(int depth) {
            if (m_at >= text.Length) { return null; }

            var character = text[m_at];

            if (character == '{') {
                m_at++;
                var group = Row(closes: true, depth: depth);

                m_at++;

                return group;
            }
            if (character == '\\') {
                return Command(depth: depth);
            }

            var rune = Rune.GetRuneAt(input: text, index: m_at);

            m_at += rune.Utf16SequenceLength;

            return new MathNode.Glyph(Unicode: rune.Value);
        }
        private MathNode Command(int depth) {
            m_at++;
            if (m_at >= text.Length) { throw new FormatException(message: "ends in a lone '\\'"); }

            var first = text[m_at];

            if (first is '{' or '}' or '\\' or '^' or '_') {
                m_at++;

                return new MathNode.Glyph(Unicode: first);
            }
            if (first == ',') {
                m_at++;

                return new MathNode.Space(Ems: (1f / 6f));
            }

            var start = m_at;

            while ((m_at < text.Length) && char.IsAsciiLetter(c: text[m_at])) { m_at++; }

            var name = text[start..m_at];

            // Arguments are read in the order they are written, so a command's groups are taken left to right.
            return name switch {
                "frac" => new MathNode.Fraction(Numerator: Argument(depth: depth, command: name), Denominator: Argument(depth: depth, command: name)),
                "sqrt" => new MathNode.Root(Radicand: Argument(depth: depth, command: name)),
                "abs" => new MathNode.Fence(Body: Argument(depth: depth, command: name), Bars: 1),
                "norm" => new MathNode.Fence(Body: Argument(depth: depth, command: name), Bars: 2),
                "term" => new MathNode.Term(Name: Plain(node: Argument(depth: depth, command: name)), Body: Argument(depth: depth, command: name)),
                "color" => Colored(depth: depth),
                "quad" => new MathNode.Space(Ems: 1f),
                _ => throw new FormatException(message: $"names no command '\\{name}' at position {start}"),
            };
        }
        private MathNode Argument(int depth, string command) {
            if ((m_at >= text.Length) || (text[m_at] != '{')) {
                throw new FormatException(message: $"gives '\\{command}' no '{{' group at position {m_at}");
            }

            return Atom(depth: depth)!;
        }
        private MathNode Colored(int depth) {
            var colour = Argument(depth: depth, command: "color");
            var body = Argument(depth: depth, command: "color");
            var text = Plain(node: colour);

            if (!HexColor.TryParseRgba(color: out var parsed, value: text)) {
                throw new FormatException(message: $"gives '\\color' '{text}', which is no #RGB, #RGBA, #RRGGBB or #RRGGBBAA colour");
            }

            return new MathNode.Tint(Body: body, Rgba: parsed.Rgba);
        }
        private static string Plain(MathNode node) {
            var builder = new StringBuilder();

            Append(node: node);

            return builder.ToString();

            void Append(MathNode node) {
                switch (node) {
                    case MathNode.Row row: foreach (var item in row.Items) { Append(node: item); } break;
                    case MathNode.Glyph glyph: builder.Append(value: char.ConvertFromUtf32(utf32: glyph.Unicode)); break;
                    default: throw new FormatException(message: "takes plain characters in a name or colour");
                }
            }
        }
    }
}
