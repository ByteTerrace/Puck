using System.Text;

namespace Puck.Commands;

/// <summary>What one token of a usage form stands for.</summary>
public enum CommandUsageTokenKind : byte {
    /// <summary>A bare word: spelled as written, or a placeholder when a caller's vocabulary names it.</summary>
    Word,

    /// <summary>An angle-bracketed placeholder (<c>&lt;row&gt;</c>), possibly naming alternatives (<c>&lt;a|b&gt;</c>).</summary>
    Slot,

    /// <summary>A bracketed group the form may leave out (<c>[add]</c>, <c>[&lt;key&gt;]</c>).</summary>
    Optional,
}

/// <summary>One token of a usage form.</summary>
/// <param name="Kind">What the token stands for.</param>
/// <param name="Names">The word's or the slot's alternatives (<c>a|b</c>; a slot's trailing <c>...</c> stripped);
/// empty for a group.</param>
/// <param name="Repeats">Whether a slot may take several words (<c>&lt;text...&gt;</c>).</param>
/// <param name="Group">An optional group's own tokens; empty otherwise.</param>
public sealed record CommandUsageToken(CommandUsageTokenKind Kind, IReadOnlyList<string> Names, bool Repeats, IReadOnlyList<CommandUsageToken> Group);

/// <summary>
/// One argument form of a console verb, read from the form its <see cref="CommandForms"/> table spells
/// (<c>&lt;row&gt; &lt;key&gt; &lt;value&gt; [add]</c>): the grammar <c>help</c> publishes for the verb, as data a
/// caller can draw valid lines from. A form names its placeholders; what fills them is the caller's.
/// </summary>
/// <param name="Tokens">The form's tokens after the verb, in order.</param>
public sealed record CommandUsage(IReadOnlyList<CommandUsageToken> Tokens) {
    /// <summary>Reads one declared form (<see cref="CommandForms"/>), whose every word is grammar: a bare word is a
    /// literal, and a word that is no placeholder stays a literal spelled as written.</summary>
    /// <param name="form">The form as it is spelled after the verb.</param>
    /// <returns>The form's tokens.</returns>
    public static CommandUsage ParseForm(string form) {
        ArgumentNullException.ThrowIfNull(argument: form);

        return new CommandUsage(Tokens: [.. Split(text: form).Select(selector: static word =>
            (Token(word: word) ?? new CommandUsageToken(Group: [], Kind: CommandUsageTokenKind.Word, Names: [word], Repeats: false)))]);
    }

    /// <summary>Gets the fewest words a line may carry after the verb to fit this form: every token but an optional
    /// group.</summary>
    public int MinimumWords => Tokens.Count(predicate: static token => (token.Kind != CommandUsageTokenKind.Optional));
    /// <summary>Gets the most words a line may carry after the verb to fit this form, or <see cref="int.MaxValue"/> when
    /// a placeholder repeats.</summary>
    public int MaximumWords => Widest(tokens: Tokens);

    /// <summary>Returns the form as its description spells it, after the verb.</summary>
    /// <returns>The spelling.</returns>
    public override string ToString() => Spell(tokens: Tokens);

    private static int Widest(IReadOnlyList<CommandUsageToken> tokens) {
        var total = 0L;

        foreach (var token in tokens) {
            if (token.Repeats) { return int.MaxValue; }
            var width = ((token.Kind == CommandUsageTokenKind.Optional) ? Widest(tokens: token.Group) : 1);

            if (width == int.MaxValue) { return int.MaxValue; }
            total += width;
        }
        return ((int)Math.Min(val1: total, val2: int.MaxValue));
    }

    private static string Spell(IReadOnlyList<CommandUsageToken> tokens) => string.Join(separator: ' ', values: tokens.Select(selector: static token => token.Kind switch {
        CommandUsageTokenKind.Word => string.Join(separator: '|', values: token.Names),
        CommandUsageTokenKind.Slot => $"<{string.Join(separator: '|', values: token.Names)}{(token.Repeats ? "..." : string.Empty)}>",
        _ => $"[{Spell(tokens: token.Group)}]",
    }));
    // Splits a form into words, keeping a bracketed group whole.
    private static List<string> Split(string text) {
        var words = new List<string>();
        var word = new StringBuilder();
        var depth = 0;

        foreach (var c in text) {
            if ((depth == 0) && (c == ' ')) {
                if (word.Length > 0) {
                    words.Add(item: word.ToString());
                    word.Clear();
                }
                continue;
            }
            if (c is '<' or '[') { depth++; }
            if ((c is '>' or ']') && (depth > 0)) { depth--; }
            word.Append(value: c);
        }
        if (word.Length > 0) { words.Add(item: word.ToString()); }
        return words;
    }
    // One word as a token, or null when it is not grammar (an unbalanced bracket, an empty placeholder).
    private static CommandUsageToken? Token(string word) {
        if ((word.Length >= 3) && (word[0] == '<') && (word[^1] == '>')) {
            var inner = word[1..^1];
            var repeats = inner.EndsWith(value: "...", comparisonType: StringComparison.Ordinal);
            var names = (repeats ? inner[..^3] : inner).Split(separator: '|', options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return ((names.Length == 0) ? null : new CommandUsageToken(Group: [], Kind: CommandUsageTokenKind.Slot, Names: names, Repeats: repeats));
        }
        if ((word.Length >= 3) && (word[0] == '[') && (word[^1] == ']')) {
            var group = new List<CommandUsageToken>();

            foreach (var inner in Split(text: word[1..^1])) {
                if (Token(word: inner) is not { } member) { return null; }
                group.Add(item: member);
            }
            return ((group.Count == 0) ? null : new CommandUsageToken(Group: group, Kind: CommandUsageTokenKind.Optional, Names: [], Repeats: false));
        }
        if ((word.IndexOfAny(anyOf: ['<', '>', '[', ']']) >= 0) || (word.Length == 0)) { return null; }
        var alternatives = word.Split(separator: '|', options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return ((alternatives.Length == 0) ? null : new CommandUsageToken(Group: [], Kind: CommandUsageTokenKind.Word, Names: alternatives, Repeats: false));
    }
}
