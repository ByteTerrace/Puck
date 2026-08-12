using System.Globalization;
using System.Text;

namespace Puck.Analyzers;

/// <summary>
/// Locates one member's value inside a JSON document's raw text, addressing it structurally — by walking the
/// document's own nesting — rather than by searching for a key that could match anywhere. Used by the code fix so
/// a repair edits exactly the bytes it named and leaves the manifest's authored formatting alone; a full
/// re-serialization would reproduce the file's key order and indentation only by luck.
/// </summary>
internal static class JsonText {
    /// <summary>The span of a located JSON value, as raw text including any surrounding quotes.</summary>
    /// <param name="Start">The index of the value's first character.</param>
    /// <param name="Length">The number of characters the value occupies.</param>
    internal readonly record struct ValueSpan(int Start, int Length);

    /// <summary>
    /// Locates the string value at <c>entries[<paramref name="id"/>].sha256</c>.
    /// </summary>
    /// <param name="json">The manifest text.</param>
    /// <param name="id">The manifest entry id, matched exactly against a key of the <c>entries</c> object.</param>
    /// <returns>The recorded hash's span including its quotes, or <see langword="null"/> when that path does not exist.</returns>
    public static ValueSpan? FindRecordedHash(string json, string id) {
        var root = SkipWhitespace(
            json: json,
            index: 0
        );

        if (
            (root >= json.Length) ||
            (json[root] != '{')
        ) {
            return null;
        }

        if (FindMember(
            json: json,
            objectStart: root,
            name: "entries"
        ) is not int entries) {
            return null;
        }

        if (
            (entries >= json.Length) ||
            (json[entries] != '{') ||
            (FindMember(
            json: json,
            objectStart: entries,
            name: id
        ) is not int entry)
        ) {
            return null;
        }

        if (
            (entry >= json.Length) ||
            (json[entry] != '{') ||
            (FindMember(
            json: json,
            objectStart: entry,
            name: "sha256"
        ) is not int hash)
        ) {
            return null;
        }

        if (
            (hash >= json.Length) ||
            (json[hash] != '"')
        ) {
            return null;
        }

        var end = SkipValue(
            json: json,
            index: hash
        );

        return ((end < 0)
            ? null
            : new ValueSpan(
            Start: hash,
            Length: (end - hash)
        ));
    }

    /// <summary>Replaces the value at <paramref name="span"/> with <paramref name="replacement"/>, leaving every other character untouched.</summary>
    public static string Replace(string json, ValueSpan span, string replacement) =>
        ((json.Substring(
        startIndex: 0,
        length: span.Start
    ) + replacement) + json.Substring(startIndex: (span.Start + span.Length)));

    /// <summary>Finds the value of the member named <paramref name="name"/> directly inside the object opening at <paramref name="objectStart"/>.</summary>
    /// <returns>The index of the value's first character, or <see langword="null"/> when the object has no such member.</returns>
    private static int? FindMember(string json, int objectStart, string name) {
        if (
            (objectStart >= json.Length) ||
            (json[objectStart] != '{')
        ) {
            return null;
        }

        var index = SkipWhitespace(
            json: json,
            index: (objectStart + 1)
        );

        if (
            (index < json.Length) &&
            (json[index] == '}')
        ) {
            return null;
        }

        while (index < json.Length) {
            index = SkipWhitespace(
                json: json,
                index: index
            );

            if (!TryReadString(
                json: json,
                index: index,
                value: out var key,
                next: out var afterKey
            )) {
                return null;
            }

            index = SkipWhitespace(
                json: json,
                index: afterKey
            );

            if (
                (index >= json.Length) ||
                (json[index] != ':')
            ) {
                return null;
            }

            index = SkipWhitespace(
                json: json,
                index: (index + 1)
            );

            if (index >= json.Length) {
                return null;
            }

            if (string.Equals(
                a: key,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return index;
            }

            var afterValue = SkipValue(
                json: json,
                index: index
            );

            if (afterValue < 0) {
                return null;
            }

            index = SkipWhitespace(
                json: json,
                index: afterValue
            );

            if (
                (index >= json.Length) ||
                (json[index] != ',')
            ) {
                return null;
            }

            index++;
        }

        return null;
    }

    /// <summary>The index just past the value starting at <paramref name="index"/>, or -1 when the text is malformed.</summary>
    private static int SkipValue(string json, int index) {
        if (index >= json.Length) {
            return -1;
        }

        var start = json[index];

        if (start == '"') {
            return (TryReadString(
                json: json,
                index: index,
                value: out _,
                next: out var afterString
            )
                ? afterString
                : -1);
        }

        if (
            (start == '{') ||
            (start == '[')
        ) {
            var depth = 0;

            while (index < json.Length) {
                var character = json[index];

                if (character == '"') {
                    if (!TryReadString(
                        json: json,
                        index: index,
                        value: out _,
                        next: out var afterString
                    )) {
                        return -1;
                    }

                    index = afterString;

                    continue;
                }

                if (
                    (character == '{') ||
                    (character == '[')
                ) {
                    depth++;
                } else if (
                    (character == '}') ||
                    (character == ']')
                ) {
                    depth--;

                    if (depth == 0) {
                        return (index + 1);
                    }
                }

                index++;
            }

            return -1;
        }

        while (
            (index < json.Length) &&
            (json[index] != ',') &&
            (json[index] != '}') &&
            (json[index] != ']') &&
            !char.IsWhiteSpace(c: json[index])
        ) {
            index++;
        }

        return index;
    }
    private static bool TryReadString(string json, int index, out string value, out int next) {
        value = string.Empty;
        next = index;

        if (
            (index >= json.Length) ||
            (json[index] != '"')
        ) {
            return false;
        }

        var builder = new StringBuilder();

        index++;

        while (index < json.Length) {
            var character = json[index++];

            if (character == '"') {
                value = builder.ToString();
                next = index;

                return true;
            }

            if (character != '\\') {
                builder.Append(value: character);

                continue;
            }

            if (index >= json.Length) {
                return false;
            }

            var escape = json[index++];

            if (JsonEscape.TryDecode(
                escape: escape,
                value: out var decoded
            )) {
                builder.Append(value: decoded);

                continue;
            }

            if (
                (escape != 'u') ||
                ((index + 4) > json.Length)
            ) {
                return false;
            }

            if (!ushort.TryParse(
                s: json.Substring(
                    startIndex: index,
                    length: 4
                ),
                style: NumberStyles.AllowHexSpecifier,
                provider: CultureInfo.InvariantCulture,
                result: out var codeUnit
            )) {
                return false;
            }

            builder.Append(value: (char)codeUnit);
            index += 4;
        }

        return false;
    }
    private static int SkipWhitespace(string json, int index) {
        while (
            (index < json.Length) &&
            char.IsWhiteSpace(c: json[index])
        ) {
            index++;
        }

        return index;
    }
}
