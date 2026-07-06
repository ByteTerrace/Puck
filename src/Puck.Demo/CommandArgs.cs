using System.Globalization;

namespace Puck.Demo;

/// <summary>
/// The console verbs' shared argument-parsing helpers — invariant-culture <see cref="float"/>/<see cref="int"/>
/// parsing for the trailing token-list arguments every <c>*CommandModule</c>/<c>*Commands</c> handler receives.
/// Lives here (Demo-side, not <c>Puck.Commands</c>) because it is a console-verb convenience, not part of the
/// engine's command-dispatch contract — the engine library stays lean. Every command module/logic class that parses
/// a numeric argument should call these instead of re-declaring <c>float.TryParse</c>/<c>int.TryParse</c> locally, so
/// the parsing rule (invariant culture, no thousands separators) can never drift between verbs.
/// </summary>
internal static class CommandArgs {
    /// <summary>Parses one float argument, invariant-culture, allowing a leading sign and a decimal point only (no
    /// thousands separators — console args are typed, not formatted numbers).</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseFloat(string text, out float value) =>
        float.TryParse(s: text, result: out value, provider: CultureInfo.InvariantCulture, style: NumberStyles.Float);

    /// <summary>Parses <paramref name="count"/> consecutive float arguments starting at <paramref name="start"/>
    /// (e.g. an <c>&lt;x&gt; &lt;y&gt; &lt;z&gt;</c> triple) — fails as a unit if any token is missing or unparsable,
    /// so callers can offer one usage string rather than partial-parse errors.</summary>
    /// <param name="args">The full argument array.</param>
    /// <param name="count">How many consecutive floats to parse.</param>
    /// <param name="start">The starting index into <paramref name="args"/>.</param>
    /// <param name="values">The parsed values (length <paramref name="count"/>), zeroed on failure.</param>
    /// <returns>Whether every token in the range parsed.</returns>
    public static bool TryParseFloats(string[] args, int count, int start, out float[] values) {
        values = new float[count];

        if (args.Length < (start + count)) {
            return false;
        }

        for (var index = 0; (index < count); index++) {
            if (!TryParseFloat(text: args[start + index], value: out values[index])) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Parses one integer argument, invariant-culture (plain digit strings — console args are typed, not
    /// formatted numbers).</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseInt(string text, out int value) =>
        int.TryParse(s: text, result: out value, provider: CultureInfo.InvariantCulture, style: NumberStyles.Integer);
}
