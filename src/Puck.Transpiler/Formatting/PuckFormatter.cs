using System.Text;
using Puck.State;
using Puck.Transpiler.Parsing;

namespace Puck.Transpiler.Formatting;

/// <summary>Opinionated, idempotent source code formatter for the Puck authoring language (.puck).</summary>
public static class PuckFormatter {
    private static int CalculateNestingDelta(string line) {
        var delta = 0;
        var inString = false;
        var stringDelimiter = '\0';
        var escape = false;

        for (var i = 0; (i < line.Length); i++) {
            var c = line[i];

            if (inString) {
                if (escape) {
                    escape = false;
                } else if (
                    (c == '\\') &&
                    (stringDelimiter == '"')
                ) {
                    escape = true;
                } else if (c == stringDelimiter) {
                    inString = false;
                }
                continue;
            }

            // Check single line comment start
            if (
                (c == '/') &&
                ((i + 1) < line.Length) &&
                (line[(i + 1)] == '/')
            ) {
                break; // rest of line is comment
            }

            if (c is '"' or '`') {
                inString = true;
                stringDelimiter = c;
                continue;
            }

            if (c is '{' or '[') {
                delta++;
            } else if (c is '}' or ']') {
                delta--;
            }
        }

        return delta;
    }
    private static bool CanAttachEgyptianBrace(string prevTrimmed, string brace) {
        if (string.IsNullOrWhiteSpace(value: prevTrimmed)) {
            return false;
        }

        // Never attach to any delimiter or statement separator
        if (
            prevTrimmed.EndsWith(value: '{') ||
            prevTrimmed.EndsWith(value: '}') ||
            prevTrimmed.EndsWith(value: '[') ||
            prevTrimmed.EndsWith(value: ']') ||
            prevTrimmed.EndsWith(value: ',') ||
            prevTrimmed.EndsWith(value: ';')
        ) {
            return false;
        }

        // Never attach if previous line ends with a comment
        if (
            prevTrimmed.Contains(
            comparisonType: StringComparison.Ordinal,
            value: "//"
        ) ||
            prevTrimmed.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "*/"
        )
        ) {
            return false;
        }

        // For '[', attach to a property declaration whether or not it still carries the separator the container
        // spelling drops: `cells [` and the older `cells: [` both reach the formatter.
        if (brace == "[") {
            return (
                prevTrimmed.EndsWith(value: ':') ||
                prevTrimmed.EndsWith(value: '=') ||
                IsBareKey(prevTrimmed: prevTrimmed)
            );
        }

        // For '{', attach to property ending with ':' or block header (identifier, string literal, or parameter ')')
        if (brace == "{") {
            return (
                prevTrimmed.EndsWith(value: ':') ||
                prevTrimmed.EndsWith(value: ')') ||
                prevTrimmed.EndsWith(value: '"') ||
                char.IsLetterOrDigit(c: prevTrimmed[^1]) ||
                (prevTrimmed[^1] == '_')
            );
        }

        return false;
    }
    private static int CountLeadingClosingDelimiters(string trimmedLine) {
        var count = 0;

        for (var i = 0; (i < trimmedLine.Length); i++) {
            var c = trimmedLine[i];

            if (c is '}' or ']') {
                count++;
            } else if (
                !char.IsWhiteSpace(c: c) &&
                (c != ',')
            ) {
                break;
            }
        }
        return count;
    }
    // Drops the ':' or '=' a key was written with before a container opener joins it.
    private static string DropTrailingSeparator(string prevTrimmed) {
        if (
            prevTrimmed.EndsWith(value: ':') ||
            prevTrimmed.EndsWith(value: '=')
        ) {
            return prevTrimmed[..^1].TrimEnd();
        }

        return prevTrimmed;
    }
    // Object members occupy separate lines; scalar arrays and vectors remain compact.
    private static string ExpandObjects(string source) {
        var stack = new Stack<(char Kind, int Open, List<int> Commas)>();
        var breaks = new SortedSet<int>();

        for (var index = 0; (index < source.Length); index++) {
            var end = SourceLexemes.End(
                offset: index,
                source: source
            );

            if (end > index) {
                index = (end - 1);
                continue;
            }
            var character = source[index];

            if (character is '{' or '[' or '(') {
                stack.Push(item: (character, index, []));
            } else if (
                (character == ',') &&
                stack.TryPeek(result: out var owner) &&
                (owner.Kind == '{')
            ) {
                owner.Commas.Add(item: (index + 1));
            } else if (
                (character is '}' or ']' or ')') &&
                stack.TryPop(result: out var opening)
            ) {
                if (
                    (opening.Kind == '{') &&
                    (character == '}') &&
                    !source.AsSpan(
                    length: ((index - opening.Open) - 1),
                    start: (opening.Open + 1)
                ).Trim().IsEmpty
                ) {
                    breaks.Add(item: (opening.Open + 1));
                    breaks.Add(item: index);
                    var next = (index + 1);

                    while (
                        (next < source.Length) &&
                        (source[next] is ' ' or '\t')
                    ) { next++; }
                    if (
                        (next < source.Length) &&
                        (source[next] is ']' or '}')
                    ) { breaks.Add(item: (index + 1)); }
                    foreach (var comma in opening.Commas) { breaks.Add(item: comma); }
                }
            }
        }
        var result = new StringBuilder(capacity: source.Length);
        var copied = 0;

        foreach (var position in breaks) {
            result.Append(value: source.AsSpan(
                length: (position - copied),
                start: copied
            ));
            var before = (position - 1);
            var after = position;

            while (
                (before >= 0) &&
                (source[before] is ' ' or '\t' or '\r')
            ) { before--; }
            while (
                (after < source.Length) &&
                (source[after] is ' ' or '\t' or '\r')
            ) { after++; }
            if (
                (before >= 0) &&
                (source[before] != '\n') &&
                (after < source.Length) &&
                (source[after] != '\n')
            ) {
                result.Append(value: '\n');
            }
            copied = position;
        }
        result.Append(value: source.AsSpan(start: copied));
        return result.ToString();
    }
    private static string FormatCore(string source, int tabSize, bool insertSpaces) {

        if (string.IsNullOrWhiteSpace(value: source)) {
            return string.Empty;
        }

        var lines = SplitLines(source: source);
        var splitLines = SplitCompoundDelimiters(lines: lines);
        var preprocessed = PreprocessEgyptianBraces(lines: SplitLines(source: ExpandObjects(source: string.Join(
            separator: "\n",
            values: splitLines
        ))));
        var formattedLines = new List<string>();

        var indentLevel = 0;
        var inMultiLineComment = false;
        var previousWasEmpty = false;

        for (var i = 0; (i < preprocessed.Count); i++) {
            var rawLine = preprocessed[i];
            var trimmed = rawLine.Trim();

            if (trimmed.Length == 0) {
                // Do not allow multiple consecutive blank lines, nor blank lines right after opening brace
                if (
                    !previousWasEmpty &&
                    (formattedLines.Count > 0)
                ) {
                    var lastLine = formattedLines[^1].TrimEnd();

                    if (
                        !lastLine.EndsWith(value: '{') &&
                        !lastLine.EndsWith(value: '[')
                    ) {
                        formattedLines.Add(item: string.Empty);
                        previousWasEmpty = true;
                    }
                }
                continue;
            }

            // Check multiline comment state
            if (inMultiLineComment) {
                var commentIndent = new string(
                    c: (insertSpaces
                    ? ' '
                    : '\t'),
                    count: (indentLevel * (insertSpaces
                    ? tabSize
                    : 1))
                );

                formattedLines.Add(item: (commentIndent + trimmed));
                if (trimmed.Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: "*/"
                )) {
                    inMultiLineComment = false;
                }
                previousWasEmpty = false;
                continue;
            }

            if (
                trimmed.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "/*"
            ) &&
                !trimmed.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "*/"
            )
            ) {
                inMultiLineComment = true;
                var commentIndent = new string(
                    c: (insertSpaces
                    ? ' '
                    : '\t'),
                    count: (indentLevel * (insertSpaces
                    ? tabSize
                    : 1))
                );

                formattedLines.Add(item: (commentIndent + trimmed));
                previousWasEmpty = false;
                continue;
            }

            // Count leading closing delimiters for current line's indentation
            var leadingClosingCount = CountLeadingClosingDelimiters(trimmedLine: trimmed);
            var effectiveIndent = Math.Max(
                val1: 0,
                val2: (indentLevel - leadingClosingCount)
            );

            // Normalize line content (spacing around colons, commas, etc.)
            var normalizedContent = NormalizeLineContent(trimmed: trimmed);

            var indent = new string(
                c: (insertSpaces
                ? ' '
                : '\t'),
                count: (effectiveIndent * (insertSpaces
                ? tabSize
                : 1))
            );

            formattedLines.Add(item: (indent + normalizedContent));
            previousWasEmpty = false;

            // Update indentLevel for next lines based on delimiters outside string literals and comments
            var delta = CalculateNestingDelta(line: trimmed);

            indentLevel = Math.Max(
                val1: 0,
                val2: (indentLevel + delta)
            );
        }

        // Remove any trailing empty lines before final newline
        while (
            (formattedLines.Count > 0) &&
            string.IsNullOrWhiteSpace(value: formattedLines[^1])
        ) {
            formattedLines.RemoveAt(index: (formattedLines.Count - 1));
        }

        var sb = new StringBuilder();

        foreach (var line in formattedLines) {
            sb.Append(value: line);
            sb.Append(value: '\n');
        }

        return sb.ToString();
    }
    // Whether a line is nothing but an identifier, so a container opener on the next line belongs to it.
    private static bool IsBareKey(string prevTrimmed) {
        if (prevTrimmed.Length == 0) {
            return false;
        }

        foreach (var c in prevTrimmed) {
            if (
                !char.IsLetterOrDigit(c: c) &&
                (c != '_')
            ) {
                return false;
            }
        }

        return !char.IsDigit(c: prevTrimmed[0]);
    }
    private static string LiteralMarker(string source) {
        const string Prefix = "__puck_literal_";
        var occupied = new HashSet<int>();
        var offset = 0;

        while (
            (source.IndexOf(
            comparisonType: StringComparison.Ordinal,
            startIndex: offset,
            value: Prefix
        ) is var start) &&
            (start >= 0)
        ) {
            var digits = (start + Prefix.Length);
            var end = digits;

            while (
                (end < source.Length) &&
                (source[end] is >= '0' and <= '9')
            ) { end++; }
            if (
                (end < source.Length) &&
                (source[end] == '_') &&
                int.TryParse(
                source.AsSpan(
                    length: (end - digits),
                    start: digits
                ),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value
            )
            ) {
                occupied.Add(item: value);
            }
            // The final underscore can also start the next prefix when no digits followed this one.
            offset = ((end > digits)
                ? end
                : (digits - 1)
            );
        }
        // A single source scan keeps collision selection linear; a numeric suffix also keeps every
        // protected literal short even when authored text contains a very long underscore run.
        var ordinal = 0;

        while (occupied.Contains(item: ordinal)) { ordinal++; }
        return ((Prefix + ordinal.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)) + "_");
    }
    private static string NormalizeLineContent(string trimmed) {
        // If line is a comment or starts with comment, keep as-is
        if (
            trimmed.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "//"
        ) ||
            trimmed.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "/*"
        )
        ) {
            return trimmed;
        }

        var sb = new StringBuilder(capacity: trimmed.Length);
        var inString = false;
        var stringDelimiter = '\0';
        var escape = false;

        for (var i = 0; (i < trimmed.Length); i++) {
            var c = trimmed[i];

            if (inString) {
                sb.Append(value: c);
                // A backquoted name carries no escape sequence: `N,S,E,W`'s commas are name characters, not
                // statement punctuation, and only a `"` string honors a preceding backslash.
                if (escape) {
                    escape = false;
                } else if (
                    (c == '\\') &&
                    (stringDelimiter == '"')
                ) {
                    escape = true;
                } else if (c == stringDelimiter) {
                    inString = false;
                }
                continue;
            }

            if (
                (c == '/') &&
                ((i + 1) < trimmed.Length) &&
                (trimmed[(i + 1)] == '/')
            ) {
                // Rest is comment, append with single leading space if preceded by content
                if (
                    (sb.Length > 0) &&
                    (sb[^1] != ' ')
                ) {
                    sb.Append(value: ' ');
                }
                sb.Append(value: trimmed.AsSpan(start: i));
                break;
            }

            if (c is '"' or '`') {
                inString = true;
                stringDelimiter = c;
                sb.Append(value: c);
                continue;
            }

            // A reserved `$name:segment` channel (`$physics:quiescent`, `$zones[...]`-folded selectors) is one
            // token; its internal colons are not property/kind-annotation colons, so the per-character colon rule
            // below must not touch them. Consumed verbatim, on the same terms as
            // ExpressionSpelling.IsBareName/PuckParser.TryReadExtendedName — KEEP IN SYNC with both.
            if (c == '$') {
                var tokenLength = ReservedChannelTokenLength(
                    start: i,
                    text: trimmed
                );

                if (tokenLength > 0) {
                    sb.Append(
                        count: tokenLength,
                        startIndex: i,
                        value: trimmed
                    );
                    i += (tokenLength - 1);
                    continue;
                }
            }

            // Normalize colon: ensure single space after (unless followed by newline or delimiter). A colon already
            // carrying one space before it — `bind name : Kind`, a `when Gate : Kind` suffix, or a ternary's
            // `? a : b` — is ExpressionSpelling's own spacing (Puck.State.ExpressionSpelling's Ternary.PrintBare),
            // not an unspaced property colon, and stripping that space splices `a : b` into `a: b`. The preceding
            // whitespace is already collapsed to at most one space by the general run below, so leaving it alone
            // here recovers both spellings without telling them apart.
            if (c == ':') {
                sb.Append(value: ':');
                if (
                    ((i + 1) < trimmed.Length) &&
                    (trimmed[(i + 1)] != ' ') &&
                    (trimmed[(i + 1)] != ':') &&
                    (trimmed[(i + 1)] != '\n')
                ) {
                    sb.Append(value: ' ');
                }
                continue;
            }

            // Normalize comma: ensure single space after
            if (c == ',') {
                while (
                    (sb.Length > 0) &&
                    (sb[^1] == ' ')
                ) {
                    sb.Length--;
                }
                sb.Append(value: ',');
                if (
                    ((i + 1) < trimmed.Length) &&
                    (trimmed[(i + 1)] != ' ')
                ) {
                    sb.Append(value: ' ');
                }
                continue;
            }

            // Collapse multiple spaces into single space outside strings
            if (c == ' ') {
                if (
                    (sb.Length == 0) ||
                    (sb[^1] != ' ')
                ) {
                    sb.Append(value: ' ');
                }
                continue;
            }

            sb.Append(value: c);
        }

        return sb.ToString().TrimEnd();
    }
    private static List<string> PreprocessEgyptianBraces(List<string> lines) {
        var result = new List<string>();

        for (var i = 0; (i < lines.Count); i++) {
            var line = lines[i];
            var trimmed = line.Trim();

            // If a line is just '{' or '[', attach it to the preceding non-empty line (Egyptian brace style)
            if (
                (trimmed is "{" or "[") &&
                (result.Count > 0)
            ) {
                // Find previous non-empty line
                var prevIdx = (result.Count - 1);

                while (
                    (prevIdx >= 0) &&
                    string.IsNullOrWhiteSpace(value: result[prevIdx])
                ) {
                    prevIdx--;
                }
                if (prevIdx >= 0) {
                    var prevTrimmed = result[prevIdx].TrimEnd();

                    if (CanAttachEgyptianBrace(
                        brace: trimmed,
                        prevTrimmed: prevTrimmed
                    )) {
                        // A container is written without the separator, so joining the opener to its key drops one.
                        // Formatting must not reintroduce the spelling the parser refuses (PUCK040).
                        result[prevIdx] = ((DropTrailingSeparator(prevTrimmed: prevTrimmed) + " ") + trimmed);
                        // Clear any blank lines between them
                        for (var k = (result.Count - 1); (k > prevIdx); k--) {
                            result.RemoveAt(index: k);
                        }
                        continue;
                    }
                }
            }

            result.Add(item: line);
        }

        return result;
    }
    // Length of the reserved-channel token starting at a '$' in `text[start]`, or 0 when `start` is not one (an
    // isolated '$' with no following name characters at all still returns 1, so callers always advance past it).
    // The name grammar itself — ':' segments, signed offsets, and a folded `$zones[...]` index — is
    // ExpressionSpelling's, read through its own scanner rather than copied here (rule 8).
    private static int ReservedChannelTokenLength(string text, int start) {
        if (
            (start >= text.Length) ||
            (text[start] != '$')
        ) {
            return 0;
        }

        return Math.Max(
            val1: 1,
            val2: ExpressionSpelling.ScanBareName(
                start: start,
                text: text
            )
        );
    }
    private static List<string> SplitCompoundDelimiters(List<string> lines) {
        var result = new List<string>();

        foreach (var line in lines) {
            var inString = false;
            var stringDelimiter = '\0';
            var escape = false;
            var splitIdx = -1;

            for (var i = 0; (i < line.Length); i++) {
                var c = line[i];

                if (inString) {
                    // A backquoted name (ExpressionSpelling's `seat-1`/`N,S,E,W` spelling) carries no escape
                    // sequence, so only a `"` string honors a preceding backslash.
                    if (escape) {
                        escape = false;
                    } else if (
                        (c == '\\') &&
                        (stringDelimiter == '"')
                    ) {
                        escape = true;
                    } else if (c == stringDelimiter) {
                        inString = false;
                    }
                    continue;
                }

                if (c is '"' or '`') {
                    inString = true;
                    stringDelimiter = c;
                    continue;
                }

                if (
                    (c == '/') &&
                    ((i + 1) < line.Length) &&
                    (line[(i + 1)] == '/')
                ) {
                    break;
                }

                if (
                    ((c == '[') || (c == '}')) &&
                    ((i + 1) < line.Length)
                ) {
                    var j = (i + 1);

                    while (
                        (j < line.Length) &&
                        char.IsWhiteSpace(c: line[j])
                    ) {
                        j++;
                    }
                    if (
                        (j < line.Length) &&
                        (line[j] == '{')
                    ) {
                        splitIdx = (i + 1);
                        break;
                    }
                }
            }

            if (splitIdx > 0) {
                var first = line[..splitIdx].TrimEnd();
                var second = line[splitIdx..].TrimStart();
                var subLines = SplitCompoundDelimiters(lines: [first, second]);

                result.AddRange(collection: subLines);
            } else {
                result.Add(item: line);
            }
        }

        return result;
    }
    private static List<string> SplitLines(string source) {
        var lines = new List<string>();
        using var reader = new StringReader(s: source);
        string? line;

        while ((line = reader.ReadLine()) is not null) {
            lines.Add(item: line);
        }
        return lines;
    }

    /// <summary>Formats Puck source code using the DSL layout rules and requested indentation.</summary>
    /// <param name="source">The raw Puck source code.</param>
    /// <param name="tabSize">Spaces per indentation level; defaults to two.</param>
    /// <param name="insertSpaces">Use spaces when true, or one tab per indentation level when false.</param>
    /// <returns>Idempotently formatted Puck code with expanded object members and the requested indentation.</returns>
    public static string Format(string source, int tabSize = 2, bool insertSpaces = true) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tabSize);

        var marker = LiteralMarker(source: source);
        var literals = new List<string>();
        var protectedSource = new StringBuilder(capacity: source.Length);

        for (var index = 0; (index < source.Length);) {
            var end = SourceLexemes.End(
                offset: index,
                source: source
            );

            if (end > index) {
                // Comments retain their lexical role; only strings and quoted identifiers are replaced.
                if (source[index] is '"' or '$' or '`') {
                    protectedSource.Append(value: '"').Append(value: marker).Append(value: literals.Count).Append(value: '"');
                    literals.Add(item: source[index..end]);
                } else { protectedSource.Append(value: source.AsSpan(
                    length: (end - index),
                    start: index
                )); }
                index = end;
            } else { protectedSource.Append(value: source[index++]); }
        }
        var formatted = FormatCore(
            protectedSource.ToString(),
            tabSize,
            insertSpaces
        );
        var restored = new StringBuilder(capacity: formatted.Length);
        var prefix = ("\"" + marker);
        var copied = 0;

        while (
            (formatted.IndexOf(
            comparisonType: StringComparison.Ordinal,
            startIndex: copied,
            value: prefix
        ) is var start) &&
            (start >= 0)
        ) {
            restored.Append(value: formatted.AsSpan(
                length: (start - copied),
                start: copied
            ));
            var end = formatted.IndexOf(
                '"',
                (start + prefix.Length)
            );
            var ordinal = int.Parse(
                formatted.AsSpan(
                    (start + prefix.Length),
                    ((end - start) - prefix.Length)
                ),
                System.Globalization.CultureInfo.InvariantCulture
            );

            restored.Append(value: literals[ordinal]);
            copied = (end + 1);
        }
        restored.Append(value: formatted.AsSpan(start: copied));
        return restored.ToString();
    }
}
