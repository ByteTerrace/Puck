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
/// One argument form of a console verb, read from the usage its description spells
/// (<c>world.state.cell.set &lt;row&gt; &lt;key&gt; &lt;value&gt; [add] | &lt;row&gt; &lt;key&gt; &lt;text...&gt;</c>):
/// the grammar <c>help</c> publishes for the verb, as data a caller can draw valid lines from. A form names its
/// placeholders; what fills them is the caller's.
/// </summary>
/// <param name="Tokens">The form's tokens after the verb, in order.</param>
public sealed record CommandUsage(IReadOnlyList<CommandUsageToken> Tokens) {
    /// <summary>Reads every usage form <paramref name="description"/> spells for <paramref name="verb"/>: each place the
    /// verb's name stands as a word, followed by its placeholders, optional groups, flags and the literals that lead to
    /// them, up to the prose that ends the form; a <c>|</c> between forms starts another form of the same verb.</summary>
    /// <param name="verb">The verb's name.</param>
    /// <param name="description">The verb's description, as <c>help --full</c> prints it.</param>
    /// <returns>The distinct forms in the order the description spells them; empty when it spells none.</returns>
    public static IReadOnlyList<CommandUsage> Parse(string verb, string description) {
        ArgumentException.ThrowIfNullOrEmpty(argument: verb);
        ArgumentNullException.ThrowIfNull(argument: description);
        var forms = new List<CommandUsage>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var start = 0;

        while ((start = description.IndexOf(value: verb, startIndex: start, comparisonType: StringComparison.Ordinal)) >= 0) {
            var after = (start + verb.Length);
            var standsAlone = (((start == 0) || IsBoundary(c: description[start - 1])) && (after < description.Length) && (description[after] == ' '));

            start = after;
            if (!standsAlone) { continue; }
            foreach (var form in ReadForms(text: description, start: (after + 1))) {
                if (seen.Add(item: Spell(tokens: form))) { forms.Add(item: new CommandUsage(Tokens: form)); }
            }
        }
        return forms;
    }

    /// <summary>Reads one declared form (<see cref="CommandForms"/>), whose every word is grammar: unlike a description,
    /// a declared form has no prose after it, so a bare word is a literal and a word that is no placeholder stays a
    /// literal spelled as written.</summary>
    /// <param name="form">The form as it is spelled after the verb.</param>
    /// <returns>The form's tokens.</returns>
    public static CommandUsage ParseForm(string form) {
        ArgumentNullException.ThrowIfNull(argument: form);

        return new CommandUsage(Tokens: [.. Split(text: form, start: 0, prose: false).Select(selector: static entry =>
            (Token(word: entry.Word) ?? new CommandUsageToken(Group: [], Kind: CommandUsageTokenKind.Word, Names: [entry.Word], Repeats: false)))]);
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

    private static bool IsBoundary(char c) => (char.IsWhiteSpace(c: c) || (c is '(' or '`' or '"' or '\''));
    private static string Spell(IReadOnlyList<CommandUsageToken> tokens) => string.Join(separator: ' ', values: tokens.Select(selector: static token => token.Kind switch {
        CommandUsageTokenKind.Word => string.Join(separator: '|', values: token.Names),
        CommandUsageTokenKind.Slot => $"<{string.Join(separator: '|', values: token.Names)}{(token.Repeats ? "..." : string.Empty)}>",
        _ => $"[{Spell(tokens: token.Group)}]",
    }));
    // The forms that start at start: tokens up to the prose that ends them, a '|' between two forms starting the next.
    private static List<List<CommandUsageToken>> ReadForms(string text, int start) {
        var raw = Split(text: text, start: start);
        var forms = new List<List<CommandUsageToken>> { new() };

        for (var index = 0; (index < raw.Count); index++) {
            var (word, ends) = raw[index];

            if (word == "|") {
                forms.Add(item: []);
                continue;
            }
            var token = Token(word: word);

            // A bare word belongs to the form only when it leads to a placeholder, a group or a flag; otherwise the
            // prose has begun.
            if ((token is null) || ((token.Kind == CommandUsageTokenKind.Word) && !word.StartsWith(value: '-') &&
                (ends || (index + 1 >= raw.Count) || (Token(word: raw[index + 1].Word) is not { Kind: not CommandUsageTokenKind.Word })))) {
                break;
            }
            forms[^1].Add(item: token);
            if (ends) { break; }
        }
        if ((forms.Count > 1) && (forms[^1].Count == 0)) { forms.RemoveAt(index: (forms.Count - 1)); }
        return forms;
    }
    // Splits the text from start into words, keeping a bracketed group whole and noting a word whose trailing
    // punctuation ends the form; stops at a line break. A declared form (prose false) has no trailing punctuation to read.
    private static List<(string Word, bool Ends)> Split(string text, int start, bool prose = true) {
        var words = new List<(string Word, bool Ends)>();
        var word = new StringBuilder();
        var depth = 0;

        for (var index = start; (index <= text.Length); index++) {
            var c = ((index < text.Length) ? text[index] : '\n');

            if ((depth == 0) && ((c == ' ') || (c == '\n'))) {
                if (word.Length > 0) {
                    var spelled = word.ToString();
                    var trimmed = (prose ? spelled.TrimEnd(trimChars: ['.', ',', ';', ':', ')', '`', '"']) : spelled);
                    var ends = (trimmed.Length != spelled.Length);

                    if (trimmed.Length > 0) { words.Add(item: (trimmed, ends)); }
                    word.Clear();
                    if (ends) { break; }
                }
                if (c == '\n') { break; }
                continue;
            }
            if (c is '<' or '[') { depth++; }
            if ((c is '>' or ']') && (depth > 0)) { depth--; }
            word.Append(value: c);
        }
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

            foreach (var (inner, _) in Split(text: word[1..^1], start: 0)) {
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
