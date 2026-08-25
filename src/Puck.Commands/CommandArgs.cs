using System.Globalization;

namespace Puck.Commands;

/// <summary>
/// The console verbs' shared argument-parsing helpers — invariant-culture <see cref="float"/>/<see cref="int"/>
/// parsing for the trailing token-list arguments every command-module/logic handler receives. Lives here
/// (<c>Puck.Commands</c>) because shared invariant-culture argument parsing is command-dispatch substrate every
/// console consumer needs, not a demo-only convenience — the earlier "engine stays lean" fence that kept this
/// Demo-side was lifted 2026-07. Every command module/logic class that parses a numeric argument should call these
/// instead of re-declaring <c>float.TryParse</c>/<c>int.TryParse</c> locally, so the parsing rule (invariant
/// culture, no thousands separators) can never drift between verbs.
/// </summary>
public static class CommandArgs {
    /// <summary>The exception set a document-LOAD or file-capture verb treats as unreadable/corrupt INPUT — a JSON
    /// parse failure, a schema/shape mismatch, a bad base64/number, or a filesystem fault — so a malformed file or a
    /// hostile path echoes a friendly error instead of escaping the command pump (which catches only
    /// <c>DeviceLostException</c>) and tearing the single-session host down. A genuine logic bug (a
    /// <see cref="NullReferenceException"/>, an <see cref="InvalidOperationException"/>, …) is deliberately NOT in the
    /// set, so it still surfaces rather than being masked.</summary>
    /// <param name="exception">The caught exception.</param>
    /// <returns>Whether it is a malformed-input or I/O fault safe to narrate rather than rethrow.</returns>
    public static bool IsMalformedInput(Exception exception) =>
        (exception is System.Text.Json.JsonException
            or System.IO.InvalidDataException
            or System.IO.IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or FormatException);
    /// <summary>The single float-parse rule for every console argument — finite, invariant-culture decimal or exponent
    /// notation with optional leading/trailing whitespace (no thousands separators, NaN, or infinities). Both the
    /// <see cref="string"/> overload and <see cref="WireArgs.TryFloat"/> route through this span core, so the rule has
    /// exactly one definition site and cannot drift between the array and zero-copy argument paths.</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseFloat(ReadOnlySpan<char> text, out float value) =>
        (float.TryParse(
            s: text,
            result: out value,
            provider: CultureInfo.InvariantCulture,
            style: NumberStyles.Float
        ) &&
        float.IsFinite(f: value));
    /// <summary>Parses one finite float argument, invariant-culture (see the span overload for the rule).</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseFloat(string text, out float value) => TryParseFloat(
        text: text.AsSpan(),
        value: out value
    );
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
            if (!TryParseFloat(
                text: args[(start + index)],
                value: out values[index]
            )) {
                return false;
            }
        }

        return true;
    }
    /// <summary>The single integer-parse rule for every console argument — invariant-culture plain digit strings
    /// (console args are typed, not formatted numbers). Both the <see cref="string"/> overload and
    /// <see cref="WireArgs.TryInt"/> route through this span core.</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseInt(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(
            s: text,
            result: out value,
            provider: CultureInfo.InvariantCulture,
            style: NumberStyles.Integer
        );
    /// <summary>Parses one integer argument, invariant-culture (see the span overload for the rule).</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseInt(string text, out int value) => TryParseInt(
        text: text.AsSpan(),
        value: out value
    );
    /// <summary>The single <see cref="long"/>-parse rule for every console argument — invariant-culture, the same
    /// style <see cref="TryParseInt(ReadOnlySpan{char}, out int)"/> uses.</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseLong(ReadOnlySpan<char> text, out long value) =>
        long.TryParse(
            s: text,
            result: out value,
            provider: CultureInfo.InvariantCulture,
            style: NumberStyles.Integer
        );
    /// <summary>Parses one invariant-culture <see cref="long"/> argument (see the span overload for the rule).</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseLong(string text, out long value) => TryParseLong(
        text: text.AsSpan(),
        value: out value
    );
    /// <summary>The single <see cref="ulong"/>-parse rule for every console argument — invariant-culture, the same
    /// style <see cref="TryParseInt(ReadOnlySpan{char}, out int)"/> uses.</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseULong(ReadOnlySpan<char> text, out ulong value) =>
        ulong.TryParse(
            s: text,
            result: out value,
            provider: CultureInfo.InvariantCulture,
            style: NumberStyles.Integer
        );
    /// <summary>Parses one invariant-culture <see cref="ulong"/> argument (see the span overload for the rule).</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseULong(string text, out ulong value) => TryParseULong(
        text: text.AsSpan(),
        value: out value
    );
    /// <summary>The digits-only <see cref="ulong"/>-parse rule for a tick/count console grammar — <see cref="NumberStyles.None"/>:
    /// a plain run of ASCII digits, no leading sign, no surrounding whitespace, no thousands separators. Distinct
    /// from <see cref="TryParseULong(ReadOnlySpan{char}, out ulong)"/> (<see cref="NumberStyles.Integer"/>, which also
    /// admits a leading '+' and surrounding whitespace): a tick/count argument names a literal digit string, not a
    /// formatted number, so <c>+1</c> and <c> 1 </c> are refused rather than silently accepted.</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseUnsignedDigits(ReadOnlySpan<char> text, out ulong value) =>
        ulong.TryParse(
            s: text,
            result: out value,
            provider: CultureInfo.InvariantCulture,
            style: NumberStyles.None
        );
    /// <summary>Parses one digits-only <see cref="ulong"/> argument (see the span overload for the rule).</summary>
    /// <param name="text">The argument token.</param>
    /// <param name="value">The parsed value, or 0 on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseUnsignedDigits(string text, out ulong value) => TryParseUnsignedDigits(
        text: text.AsSpan(),
        value: out value
    );
}
