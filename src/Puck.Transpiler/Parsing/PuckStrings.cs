using System.Globalization;
using System.Text;

namespace Puck.Transpiler.Parsing;

/// <summary>The one escape grammar of a plain <c>"…"</c> string: the reader and the printer are both written
/// against it, so what the printer writes is exactly what the reader reads back.</summary>
/// <remarks>
/// <para>The escapes, and nothing else:</para>
/// <list type="table">
/// <item><term><c>\\</c></term><description>a backslash</description></item>
/// <item><term><c>\"</c></term><description>a double quote</description></item>
/// <item><term><c>\n</c> <c>\r</c> <c>\t</c> <c>\0</c></term><description>line feed, carriage return, tab, nul</description></item>
/// <item><term><c>\uXXXX</c></term><description>the UTF-16 code unit those four hexadecimal digits spell</description></item>
/// </list>
/// <para>A backslash in front of anything else is refused rather than guessed at. A raw <c>"""…"""</c> string has
/// no escapes at all and does not come through here.</para>
/// </remarks>
public static class PuckStrings {
    // U+007F has no short escape and is invisible in a source file, so it prints as a code unit like every other
    // control character.
    private const char Delete = '\u007F';

    /// <summary>Reads the plain string literal that starts at <paramref name="offset"/>.</summary>
    /// <param name="source">The source text.</param>
    /// <param name="offset">The index of the opening quote.</param>
    /// <param name="value">The decoded value.</param>
    /// <param name="end">The index just past the closing quote.</param>
    /// <param name="error">Why the literal could not be read, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a complete literal was read.</returns>
    public static bool TryRead(string source, int offset, out string value, out int end, out string? error) {
        ArgumentNullException.ThrowIfNull(argument: source);

        value = string.Empty;
        end = offset;
        error = null;

        if (
            (offset < 0) ||
            (offset >= source.Length) ||
            (source[offset] != '"')
        ) {
            error = "expected a '\"' opening a string";

            return false;
        }

        var builder = new StringBuilder();
        var index = (offset + 1);

        while (index < source.Length) {
            var character = source[index];

            if (character == '"') {
                value = builder.ToString();
                end = (index + 1);

                return true;
            }
            if (character != '\\') {
                if (character is '\n' or '\r') {
                    error = "a string runs past the end of its line; write '\\n' or use a \"\"\" fence";

                    return false;
                }
                builder.Append(value: character);
                index++;

                continue;
            }
            if ((index + 1) >= source.Length) {
                break;
            }

            var escape = source[(index + 1)];

            switch (escape) {
                case '\\': { builder.Append(value: '\\'); index += 2; continue; }
                case '"': { builder.Append(value: '"'); index += 2; continue; }
                case 'n': { builder.Append(value: '\n'); index += 2; continue; }
                case 'r': { builder.Append(value: '\r'); index += 2; continue; }
                case 't': { builder.Append(value: '\t'); index += 2; continue; }
                case '0': { builder.Append(value: '\0'); index += 2; continue; }
                case 'u': {
                        if (
                            ((index + 6) <= source.Length) &&
                            ushort.TryParse(
                            source.AsSpan(
                                length: 4,
                                start: (index + 2)
                            ),
                            NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture,
                            out var unit
                        )
                        ) {
                            builder.Append(value: ((char)unit));
                            index += 6;

                            continue;
                        }
                        error = "'\\u' takes exactly four hexadecimal digits";

                        return false;
                    }
                default: {
                        error = $"'\\{escape}' is not an escape; write '\\\\' for a backslash";

                        return false;
                    }
            }
        }
        error = "a string runs to the end of the source with no closing '\"'";

        return false;
    }
    /// <summary>Returns <paramref name="value"/> as a plain string literal, quotes included.</summary>
    /// <param name="value">The value to spell.</param>
    /// <returns>The literal, which <see cref="TryRead"/> reads back as <paramref name="value"/>.</returns>
    public static string Write(string value) {
        ArgumentNullException.ThrowIfNull(argument: value);

        var builder = new StringBuilder(capacity: (value.Length + 2));

        builder.Append(value: '"');
        Escape(
            into: builder,
            text: value
        );
        builder.Append(value: '"');

        return builder.ToString();
    }
    /// <summary>Appends <paramref name="text"/> to <paramref name="into"/> with every character that needs an escape
    /// escaped, and no quotes around it.</summary>
    /// <param name="into">The builder to append to.</param>
    /// <param name="text">The text to escape.</param>
    public static void Escape(StringBuilder into, string text) {
        ArgumentNullException.ThrowIfNull(argument: into);
        ArgumentNullException.ThrowIfNull(argument: text);

        foreach (var character in text) {
            switch (character) {
                case '\\': { into.Append(value: "\\\\"); break; }
                case '"': { into.Append(value: "\\\""); break; }
                case '\n': { into.Append(value: "\\n"); break; }
                case '\r': { into.Append(value: "\\r"); break; }
                case '\t': { into.Append(value: "\\t"); break; }
                case '\0': { into.Append(value: "\\0"); break; }
                default: {
                        if (
                            (character < ' ') ||
                            (character == Delete)
                        ) {
                            into.Append(value: "\\u").Append(value: ((int)character).ToString(
                                format: "X4",
                                provider: CultureInfo.InvariantCulture
                            ));
                        } else { into.Append(value: character); }

                        break;
                    }
            }
        }
    }
    /// <summary>Returns a value indicating whether <paramref name="text"/> can stand inside a <c>"""</c> fence and
    /// read back unchanged: a fence carries no escapes, so it cannot hold its own delimiter, a quote against either
    /// delimiter, or a line break the fence's indent-stripping form would rewrite.</summary>
    /// <param name="text">The text to fence.</param>
    /// <returns><see langword="true"/> when a fence spells <paramref name="text"/> exactly.</returns>
    public static bool CanFence(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        return (
            (text.Length > 0) &&
            (text[0] != '"') &&
            (text[^1] != '"') &&
            (text.IndexOf(value: '\n') < 0) &&
            (text.IndexOf(value: '\r') < 0) &&
            !text.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "\"\"\""
            )
        );
    }
}
