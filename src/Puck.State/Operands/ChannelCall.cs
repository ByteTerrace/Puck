using System.Globalization;

namespace Puck.State;

/// <summary>A reserved channel as a call: the channel's name and its arguments, in authored order. What an argument
/// means (a row, a keyword, a bound) is the channel's own business, exactly as a function's parameters are.</summary>
/// <param name="Channel">The channel's name, without the reserved prefix: <c>reduce</c>, <c>board</c>,
/// <c>physics</c>.</param>
/// <param name="Arguments">The arguments, in authored order.</param>
public sealed record ChannelCall(string Channel, IReadOnlyList<ChannelArgument> Arguments) {
    /// <summary>Gets how many arguments the call carries.</summary>
    public int Count => Arguments.Count;

    /// <summary>Returns the argument at a position as the word it spells.</summary>
    /// <param name="index">The argument's position, counted from zero.</param>
    /// <returns>The argument's text; a number answers with its canonical spelling.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the call.</exception>
    public string Text(int index) => Arguments[index].Spelling;
    /// <summary>Returns the arguments from a position on, each as the word it spells.</summary>
    /// <param name="start">The first argument's position, counted from zero.</param>
    /// <returns>The arguments' texts, in order.</returns>
    public string[] Texts(int start = 0) {
        var texts = new string[Math.Max(
            val1: 0,
            val2: (Arguments.Count - start)
        )];

        for (var index = 0; (index < texts.Length); index++) {
            texts[index] = Arguments[(start + index)].Spelling;
        }

        return texts;
    }
    /// <summary>Returns the call as the channel's reserved name followed by its arguments' texts: the positions a
    /// channel's compiler addresses, with the name at zero.</summary>
    /// <returns>The reserved name, then each argument's text.</returns>
    public string[] Tokens() => [$"{StateRow.ReservedNamePrefix}{Channel}", .. Texts()];
    /// <inheritdoc/>
    public bool Equals(ChannelCall? other) => (
        (other is not null) &&
        string.Equals(
            a: Channel,
            b: other.Channel,
            comparisonType: StringComparison.Ordinal
        ) &&
        Arguments.SequenceEqual(second: other.Arguments)
    );
    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();

        hash.Add(value: Channel);

        foreach (var argument in Arguments) {
            hash.Add(value: argument);
        }

        return hash.ToHashCode();
    }
}
/// <summary>One argument of a <see cref="ChannelCall"/>.</summary>
public abstract record ChannelArgument {
    private ChannelArgument() { }

    /// <summary>Gets the argument as its authored spelling.</summary>
    public abstract string Spelling { get; }

    /// <summary>A name or keyword: a row, a pattern, a direction, an operation.</summary>
    /// <param name="Text">The word.</param>
    public sealed record Word(string Text) : ChannelArgument {
        /// <inheritdoc/>
        public override string Spelling => Text;
    }
    /// <summary>An exact number.</summary>
    /// <param name="Value">The value.</param>
    public sealed record Number(decimal Value) : ChannelArgument {
        /// <inheritdoc/>
        public override string Spelling => Value.ToString(provider: CultureInfo.InvariantCulture);
    }
    /// <summary>A row selected live from the enclosing rule's zone table (<see cref="RuleFacts.LiveZonePrefix"/>).</summary>
    /// <param name="Index">The index, an infix cell key.</param>
    public sealed record Zone(string Index) : ChannelArgument {
        /// <inheritdoc/>
        public override string Spelling => $"{RuleFacts.LiveZonePrefix}{Index}]";
    }
    /// <summary>An infix expression, evaluated per read.</summary>
    /// <param name="Infix">The expression's canonical infix spelling.</param>
    public sealed record Expression(string Infix) : ChannelArgument {
        /// <inheritdoc/>
        public override string Spelling => Infix;
    }
}
/// <summary>The one reader and writer of the colon spelling of a reserved channel.</summary>
/// <remarks>KEEP IN SYNC with <see cref="RuleFacts"/>: a channel whose last argument is an expression, and may
/// therefore carry colons of its own, is listed in <see cref="ExpressionTail"/> with how many arguments precede
/// it.</remarks>
public static class ChannelSpelling {
    // The channels whose final argument is an infix expression, by how many plain arguments come before it.
    private static readonly Dictionary<string, int> ExpressionTail = new(comparer: StringComparer.Ordinal) {
        ["expr"] = 0,
        ["history"] = 1,
    };

    /// <summary>Gets a value indicating whether a channel's argument at a position is an infix expression.</summary>
    /// <param name="channel">The channel's name, without the reserved prefix.</param>
    /// <param name="index">The argument's position, counted from zero.</param>
    /// <returns><see langword="true"/> when that argument is the channel's expression tail.</returns>
    public static bool TakesExpressionAt(string channel, int index) => (
        ExpressionTail.TryGetValue(
            key: channel,
            value: out var before
        ) &&
        (index == before)
    );
    /// <summary>Returns the argument a piece of authored text spells.</summary>
    /// <param name="text">The text.</param>
    /// <param name="expression">Whether the text is an infix expression rather than a word.</param>
    /// <returns>The argument.</returns>
    public static ChannelArgument Argument(string text, bool expression = false) {
        ArgumentNullException.ThrowIfNull(argument: text);

        return Classify(
            expression: expression,
            part: text
        );
    }
    /// <summary>Gets a value indicating whether a spelling is a reserved channel rather than a row name or a live
    /// zone.</summary>
    /// <param name="text">The spelling.</param>
    /// <returns><see langword="true"/> when the text carries the reserved prefix and is not a live zone.</returns>
    public static bool IsChannel(string? text) => (
        (text is not null) &&
        text.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: StateRow.ReservedNamePrefix
        ) &&
        !text.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: RuleFacts.LiveZonePrefix
        )
    );
    /// <summary>Reads a reserved channel's colon spelling.</summary>
    /// <param name="text">The spelling, reserved prefix included.</param>
    /// <param name="call">The call.</param>
    /// <returns><see langword="true"/> when the text is a reserved channel.</returns>
    public static bool TryParse(string? text, out ChannelCall call) {
        call = null!;

        if (!IsChannel(text: text)) {
            return false;
        }

        var body = text![StateRow.ReservedNamePrefix.Length..];
        var head = body.IndexOf(value: ':');
        var channel = ((head < 0)
            ? body
            : body[..head]
        );
        var arguments = new List<ChannelArgument>();

        if (head >= 0) {
            var rest = body[(head + 1)..];
            var plain = (ExpressionTail.TryGetValue(
                key: channel,
                value: out var before
            )
                ? before
                : int.MaxValue
            );

            var parts = Split(
                limit: plain,
                tail: out var tail,
                text: rest
            );

            foreach (var part in parts) {
                arguments.Add(item: Classify(part: part));
            }

            if (tail is not null) {
                arguments.Add(item: Classify(
                    expression: true,
                    part: tail
                ));
            }
        }

        call = new ChannelCall(
            Arguments: arguments,
            Channel: channel
        );

        return true;
    }
    /// <summary>Writes a call in the colon spelling.</summary>
    /// <param name="call">The call.</param>
    /// <returns>The spelling, reserved prefix included.</returns>
    public static string Print(ChannelCall call) {
        ArgumentNullException.ThrowIfNull(argument: call);

        return ((call.Count == 0)
            ? $"{StateRow.ReservedNamePrefix}{call.Channel}"
            : $"{StateRow.ReservedNamePrefix}{call.Channel}:{string.Join(
                separator: ':',
                values: call.Arguments.Select(selector: static argument => argument.Spelling)
            )}"
        );
    }

    // Splits on the colons outside any bracket, stopping after `limit` parts; what is left is the expression tail.
    private static List<string> Split(string text, int limit, out string? tail) {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;

        tail = null;

        for (var index = 0; (index < text.Length); index++) {
            if (parts.Count == limit) {
                break;
            }

            switch (text[index]) {
                case '[' or '(':
                    depth++;

                    break;
                case ']' or ')':
                    depth--;

                    break;
                case ':' when (depth == 0):
                    parts.Add(item: text[start..index]);
                    start = (index + 1);

                    break;
            }
        }

        if (parts.Count == limit) {
            tail = text[start..];
        } else {
            parts.Add(item: text[start..]);
        }

        return parts;
    }
    private static ChannelArgument Classify(string part, bool expression = false) {
        if (
            part.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: RuleFacts.LiveZonePrefix
            ) &&
            part.EndsWith(value: ']')
        ) {
            return new ChannelArgument.Zone(Index: part[RuleFacts.LiveZonePrefix.Length..^1]);
        }
        // A number is kept as one only when printing it back spells what was written.
        if (
            decimal.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out var number,
                s: part,
                style: NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
            ) &&
            string.Equals(
                a: number.ToString(provider: CultureInfo.InvariantCulture),
                b: part,
                comparisonType: StringComparison.Ordinal
            )
        ) {
            return new ChannelArgument.Number(Value: number);
        }

        return (expression
            ? new ChannelArgument.Expression(Infix: part)
            : new ChannelArgument.Word(Text: part)
        );
    }
}
