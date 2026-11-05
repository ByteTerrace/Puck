using Puck.Commands;
using Puck.Maths;

namespace Puck.World.Server;

/// <summary>One input the disclosure fuzzer drew: the tick it is submitted after and its words.</summary>
/// <param name="Tick">The completed tick after which the input is submitted.</param>
/// <param name="Text">The input as one line: its words joined by spaces.</param>
/// <param name="Words">The input's words, the verb first; a word drawn from a vocabulary stays one word whatever it
/// spells (a transform's JSON keeps its spaces).</param>
public sealed record WorldDisclosureInput(ulong Tick, string Text, IReadOnlyList<string> Words);

/// <summary>One verb the disclosure fuzzer draws, with the argument forms it is drawn in.</summary>
/// <param name="Name">The verb.</param>
/// <param name="Forms">Its argument forms (<see cref="CommandRegistry.Usage"/> for a console verb); empty draws loose
/// words after it, which reach its argument refusals and nothing past them.</param>
public sealed record WorldDisclosureVerb(string Name, IReadOnlyList<CommandUsage> Forms) {
    /// <summary>Creates a verb whose forms are spelled as a <see cref="CommandForms"/> table spells them after the verb.</summary>
    /// <param name="name">The verb.</param>
    /// <param name="forms">The forms, each as <see cref="CommandUsage.ParseForm"/> reads it (empty for no arguments).</param>
    /// <returns>The verb.</returns>
    public static WorldDisclosureVerb Spelled(string name, params string[] forms) => new(Forms: [.. forms.Select(selector: form =>
        ((form.Length == 0) ? new CommandUsage(Tokens: []) : CommandUsage.ParseForm(form: form)))], Name: name);
}

/// <summary>
/// Draws the noninterference harness's inputs: a seeded sequence of verbs spread over a tick window, each in one of its
/// argument forms with every placeholder filled from the fixture's vocabulary for that placeholder (<c>row</c>,
/// <c>key</c>, <c>transform-json</c>, …): the names that exist, the hidden ones, the absent ones and other players'
/// ones, never read from either world, whose names differ with the hidden state under test. A verb with no form,
/// and one draw in <see cref="LooseOneIn"/> of a verb with forms, takes up to <see cref="MaxWords"/> loose words, which
/// reach the parse refusals. The same seed, verbs, vocabulary and window give the same inputs on every run, machine
/// and backend, so both worlds of a pair are driven identically.
/// </summary>
public static class WorldDisclosureFuzzer {
    /// <summary>The stream the draws use, apart from any authored stream a world seeds.</summary>
    public const ulong Stream = 0x6469_7363_6C6F_7365UL;
    /// <summary>The most loose words one drawn input carries after its verb.</summary>
    public const int MaxWords = 3;
    /// <summary>One draw in this many of a verb with argument forms takes loose words instead.</summary>
    public const uint LooseOneIn = 8U;
    /// <summary>The most words a repeating placeholder (<c>&lt;text...&gt;</c>) takes.</summary>
    public const int MaxRepeats = 3;

    /// <summary>Draws <paramref name="count"/> inputs.</summary>
    /// <param name="seed">The seed.</param>
    /// <param name="count">The number of inputs; zero or less draws none.</param>
    /// <param name="from">The first tick an input may be submitted after.</param>
    /// <param name="until">The last tick an input may be submitted after; at least <paramref name="from"/>.</param>
    /// <param name="verbs">The verbs to draw from; must not be empty when <paramref name="count"/> is positive.</param>
    /// <param name="slots">Each placeholder's vocabulary, by name (ignoring case); a bare word of a form that names
    /// one is a placeholder too (<c>[row]</c>).</param>
    /// <param name="words">The loose words, and the vocabulary of a placeholder <paramref name="slots"/> does not
    /// name; may be empty.</param>
    /// <returns>The inputs in tick order, inputs of one tick in the order they were drawn.</returns>
    /// <exception cref="ArgumentException"><paramref name="until"/> is below <paramref name="from"/>, or
    /// <paramref name="verbs"/> is empty while <paramref name="count"/> is positive.</exception>
    public static IReadOnlyList<WorldDisclosureInput> Draw(ulong seed, int count, ulong from, ulong until, IReadOnlyList<WorldDisclosureVerb> verbs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> slots, IReadOnlyList<string> words) {
        ArgumentNullException.ThrowIfNull(argument: verbs);
        ArgumentNullException.ThrowIfNull(argument: slots);
        ArgumentNullException.ThrowIfNull(argument: words);
        if (count <= 0) { return []; }
        if (until < from) { throw new ArgumentException(message: $"the window ends at tick {until}, before it starts at {from}", paramName: nameof(until)); }
        if (verbs.Count == 0) { throw new ArgumentException(message: "no verb to draw from", paramName: nameof(verbs)); }
        var vocabulary = new Dictionary<string, IReadOnlyList<string>>(collection: slots, comparer: StringComparer.OrdinalIgnoreCase);
        var random = Pcg32XshRr.Create(state: seed, stream: Stream);
        var span = (uint)Math.Min(val1: (until - from), val2: uint.MaxValue);
        var inputs = new List<WorldDisclosureInput>(capacity: count);

        int Pick(int choices) => (int)random.NextUInt32(maximum: (uint)(choices - 1), minimum: 0U);
        string Choose(IReadOnlyList<string> candidates) => candidates[Pick(choices: candidates.Count)];
        void Fill(IReadOnlyList<CommandUsageToken> tokens, List<string> drawn) {
            foreach (var token in tokens) {
                switch (token.Kind) {
                    case CommandUsageTokenKind.Word: {
                            var word = token.Names[Pick(choices: token.Names.Count)];

                            drawn.Add(item: (vocabulary.TryGetValue(key: word, value: out var named) && (named.Count > 0)) ? Choose(candidates: named) : word);
                            break;
                        }
                    case CommandUsageTokenKind.Slot: {
                            var name = token.Names[Pick(choices: token.Names.Count)];
                            var candidates = ((vocabulary.TryGetValue(key: name, value: out var slot) && (slot.Count > 0)) ? slot : words);
                            var repeats = (token.Repeats ? (1 + Pick(choices: MaxRepeats)) : 1);

                            for (var index = 0; (index < repeats); index++) { drawn.Add(item: ((candidates.Count > 0) ? Choose(candidates: candidates) : name)); }
                            break;
                        }
                    default:
                        if (Pick(choices: 2) == 1) { Fill(tokens: token.Group, drawn: drawn); }
                        break;
                }
            }
        }

        for (var index = 0; (index < count); index++) {
            var tick = (from + random.NextUInt32(maximum: span, minimum: 0U));
            var verb = verbs[Pick(choices: verbs.Count)];
            var drawn = new List<string> { verb.Name };

            if ((verb.Forms.Count > 0) && (random.NextUInt32(maximum: (LooseOneIn - 1U), minimum: 0U) != 0U)) {
                Fill(drawn: drawn, tokens: verb.Forms[Pick(choices: verb.Forms.Count)].Tokens);
            } else {
                var loose = ((words.Count == 0) ? 0 : Pick(choices: (MaxWords + 1)));

                for (var word = 0; (word < loose); word++) { drawn.Add(item: Choose(candidates: words)); }
            }
            inputs.Add(item: new WorldDisclosureInput(Text: string.Join(separator: ' ', values: drawn), Tick: tick, Words: drawn));
        }
        return [.. inputs.OrderBy(keySelector: static input => input.Tick)];
    }
}
