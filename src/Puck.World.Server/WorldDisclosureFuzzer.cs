using System.Text;
using Puck.Maths;

namespace Puck.World.Server;

/// <summary>One input the disclosure fuzzer drew: the tick it is submitted after and its text.</summary>
/// <param name="Tick">The completed tick after which the input is submitted.</param>
/// <param name="Text">The input: a console line, or a session action.</param>
public readonly record struct WorldDisclosureInput(ulong Tick, string Text);

/// <summary>
/// Draws the noninterference harness's random inputs: a seeded sequence of verbs, each with up to three words, spread
/// over a tick window. The same seed, vocabulary and window give the same inputs on every run, machine and backend, so
/// both worlds of a pair are driven identically. The vocabulary is the fixture's own (the names a principal can spell,
/// existing or not), never read from either world, since a vocabulary drawn from state would differ with the hidden
/// state under test.
/// </summary>
public static class WorldDisclosureFuzzer {
    /// <summary>The stream the draws use, apart from any authored stream a world seeds.</summary>
    public const ulong Stream = 0x6469_7363_6C6F_7365UL;
    /// <summary>The most words one drawn input carries after its verb.</summary>
    public const int MaxWords = 3;

    /// <summary>Draws <paramref name="count"/> inputs.</summary>
    /// <param name="seed">The seed.</param>
    /// <param name="count">The number of inputs; zero or less draws none.</param>
    /// <param name="from">The first tick an input may be submitted after.</param>
    /// <param name="until">The last tick an input may be submitted after; at least <paramref name="from"/>.</param>
    /// <param name="verbs">The verbs to draw from; must not be empty when <paramref name="count"/> is positive.</param>
    /// <param name="words">The words to draw arguments from; may be empty.</param>
    /// <returns>The inputs in tick order, inputs of one tick in the order they were drawn.</returns>
    /// <exception cref="ArgumentException"><paramref name="until"/> is below <paramref name="from"/>, or
    /// <paramref name="verbs"/> is empty while <paramref name="count"/> is positive.</exception>
    public static IReadOnlyList<WorldDisclosureInput> Draw(ulong seed, int count, ulong from, ulong until, IReadOnlyList<string> verbs, IReadOnlyList<string> words) {
        ArgumentNullException.ThrowIfNull(argument: verbs);
        ArgumentNullException.ThrowIfNull(argument: words);
        if (count <= 0) { return []; }
        if (until < from) { throw new ArgumentException(message: $"the window ends at tick {until}, before it starts at {from}", paramName: nameof(until)); }
        if (verbs.Count == 0) { throw new ArgumentException(message: "no verb to draw from", paramName: nameof(verbs)); }
        var random = Pcg32XshRr.Create(state: seed, stream: Stream);
        var span = (uint)Math.Min(val1: (until - from), val2: uint.MaxValue);
        var inputs = new List<WorldDisclosureInput>(capacity: count);
        var text = new StringBuilder();

        for (var index = 0; (index < count); index++) {
            var tick = (from + random.NextUInt32(maximum: span, minimum: 0U));

            text.Clear().Append(value: verbs[(int)random.NextUInt32(maximum: (uint)(verbs.Count - 1), minimum: 0U)]);
            var arguments = ((words.Count == 0) ? 0 : (int)random.NextUInt32(maximum: MaxWords, minimum: 0U));

            for (var word = 0; (word < arguments); word++) {
                text.Append(value: ' ').Append(value: words[(int)random.NextUInt32(maximum: (uint)(words.Count - 1), minimum: 0U)]);
            }
            inputs.Add(item: new WorldDisclosureInput(Text: text.ToString(), Tick: tick));
        }
        return [.. inputs.OrderBy(keySelector: static input => input.Tick)];
    }
}
