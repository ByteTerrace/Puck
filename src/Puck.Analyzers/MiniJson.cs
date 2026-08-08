using System.Globalization;
using System.Text;

namespace Puck.Analyzers;

/// <summary>
/// A minimal, dependency-free JSON reader. <c>System.Text.Json</c> is not referenced here on purpose: an analyzer
/// loads into the compiler's own process under an isolated assembly-load context, and a netstandard2.0 analyzer
/// that references a package with its own runtime dependencies has to ship and resolve those dependencies itself.
/// <c>VerifiedCode.json</c>'s shape is small and fixed (an object of string-keyed objects, string, and string-array
/// values), so a few dozen lines of hand-rolled parsing is both simpler and more robust than carrying that weight.
/// </summary>
internal static class MiniJson {
    /// <summary>Parses <paramref name="json"/> into an object graph of <see cref="Dictionary{TKey, TValue}"/>, <see cref="List{T}"/>, <see cref="string"/>, <see cref="double"/>, <see cref="bool"/>, and <see langword="null"/>.</summary>
    /// <param name="json">The complete JSON document text.</param>
    /// <returns>The root value.</returns>
    /// <exception cref="FormatException">The text is not well-formed JSON.</exception>
    public static object? Parse(string json) {
        var position = 0;

        var value = ParseValue(json: json, position: ref position);

        SkipWhitespace(json: json, position: ref position);

        if (position != json.Length) {
            throw new FormatException(message: $"Unexpected trailing content at position {position}.");
        }

        return value;
    }

    private static object? ParseValue(string json, ref int position) {
        SkipWhitespace(json: json, position: ref position);

        if (position >= json.Length) {
            throw new FormatException(message: "Unexpected end of JSON.");
        }

        return json[position] switch {
            '{' => ParseObject(json: json, position: ref position),
            '[' => ParseArray(json: json, position: ref position),
            '"' => ParseString(json: json, position: ref position),
            't' => ParseLiteral(json: json, position: ref position, literal: "true", value: true),
            'f' => ParseLiteral(json: json, position: ref position, literal: "false", value: false),
            'n' => ParseLiteral(json: json, position: ref position, literal: "null", value: null),
            _ => ParseNumber(json: json, position: ref position),
        };
    }
    private static Dictionary<string, object?> ParseObject(string json, ref int position) {
        var result = new Dictionary<string, object?>(comparer: StringComparer.Ordinal);

        Expect(json: json, position: ref position, expected: '{');
        SkipWhitespace(json: json, position: ref position);

        if (Peek(json: json, position: position) == '}') {
            position++;

            return result;
        }

        while (true) {
            SkipWhitespace(json: json, position: ref position);

            var key = ParseString(json: json, position: ref position);

            SkipWhitespace(json: json, position: ref position);
            Expect(json: json, position: ref position, expected: ':');

            var value = ParseValue(json: json, position: ref position);

            // Last-write-wins on a repeated key would let a second copy of an object silently decide what the
            // first one recorded, so a duplicate is refused rather than resolved.
            if (result.ContainsKey(key: key)) {
                throw new FormatException(message: $"Duplicate object member '{key}'.");
            }

            result[key] = value;

            SkipWhitespace(json: json, position: ref position);

            var next = Peek(json: json, position: position);

            if (next == ',') {
                position++;

                continue;
            }

            Expect(json: json, position: ref position, expected: '}');

            break;
        }

        return result;
    }
    private static List<object?> ParseArray(string json, ref int position) {
        var result = new List<object?>();

        Expect(json: json, position: ref position, expected: '[');
        SkipWhitespace(json: json, position: ref position);

        if (Peek(json: json, position: position) == ']') {
            position++;

            return result;
        }

        while (true) {
            var value = ParseValue(json: json, position: ref position);

            result.Add(item: value);

            SkipWhitespace(json: json, position: ref position);

            var next = Peek(json: json, position: position);

            if (next == ',') {
                position++;

                continue;
            }

            Expect(json: json, position: ref position, expected: ']');

            break;
        }

        return result;
    }
    private static string ParseString(string json, ref int position) {
        Expect(json: json, position: ref position, expected: '"');

        var builder = new StringBuilder();

        while (true) {
            if (position >= json.Length) {
                throw new FormatException(message: "Unterminated JSON string.");
            }

            var c = json[position++];

            if (c == '"') {
                break;
            }

            if (c != '\\') {
                builder.Append(value: c);

                continue;
            }

            if (position >= json.Length) {
                throw new FormatException(message: "Unterminated JSON string escape.");
            }

            var escape = json[position++];

            if (JsonEscape.TryDecode(escape: escape, value: out var decoded)) {
                builder.Append(value: decoded);

                continue;
            }

            if (escape != 'u') {
                throw new FormatException(message: $"Unrecognized JSON escape '\\{escape}'.");
            }

            if ((position + 4) > json.Length) {
                throw new FormatException(message: "Truncated \\u escape.");
            }

            var codeUnit = ushort.Parse(s: json.Substring(startIndex: position, length: 4), style: NumberStyles.AllowHexSpecifier, provider: CultureInfo.InvariantCulture);

            builder.Append(value: (char)codeUnit);
            position += 4;
        }

        return builder.ToString();
    }
    private static object? ParseNumber(string json, ref int position) {
        var start = position;

        while ((position < json.Length) && ("-+.0123456789eE".IndexOf(value: json[position]) >= 0)) {
            position++;
        }

        if (position == start) {
            throw new FormatException(message: $"Expected a JSON value at position {position}.");
        }

        return double.Parse(s: json.Substring(startIndex: start, length: (position - start)), style: NumberStyles.Float, provider: CultureInfo.InvariantCulture);
    }
    private static object? ParseLiteral(string json, ref int position, string literal, object? value) {
        if (((position + literal.Length) > json.Length) || (string.CompareOrdinal(strA: json, indexA: position, strB: literal, indexB: 0, length: literal.Length) != 0)) {
            throw new FormatException(message: $"Expected literal '{literal}' at position {position}.");
        }

        position += literal.Length;

        return value;
    }
    private static void SkipWhitespace(string json, ref int position) {
        while ((position < json.Length) && char.IsWhiteSpace(c: json[position])) {
            position++;
        }
    }
    private static char Peek(string json, int position) =>
        ((position < json.Length) ? json[position] : '\0');
    private static void Expect(string json, ref int position, char expected) {
        if ((position >= json.Length) || (json[position] != expected)) {
            throw new FormatException(message: $"Expected '{expected}' at position {position}.");
        }

        position++;
    }
}
