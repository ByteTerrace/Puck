using System.Text;

namespace Puck.World.Transpiler.Formatting;

/// <summary>Opinionated, idempotent source code formatter for the Puck authoring language (.puck).</summary>
public static class PuckFormatter {
    /// <summary>Formats Puck source code according to the repository's C# and DSL style guidelines.</summary>
    /// <param name="source">The raw Puck source code.</param>
    /// <returns>Clean, idiomatic, idempotently formatted Puck code with Egyptian braces and 4-space indentation.</returns>
    public static string Format(string source) {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source)) {
            return string.Empty;
        }

        var lines = SplitLines(source);
        var splitLines = SplitCompoundDelimiters(lines);
        var preprocessed = PreprocessEgyptianBraces(splitLines);
        var formattedLines = new List<string>();

        var indentLevel = 0;
        var inMultiLineComment = false;
        var previousWasEmpty = false;

        for (var i = 0; i < preprocessed.Count; i++) {
            var rawLine = preprocessed[i];
            var trimmed = rawLine.Trim();

            if (trimmed.Length == 0) {
                // Do not allow multiple consecutive blank lines, nor blank lines right after opening brace
                if (!previousWasEmpty && formattedLines.Count > 0) {
                    var lastLine = formattedLines[^1].TrimEnd();
                    if (!lastLine.EndsWith('{') && !lastLine.EndsWith('[')) {
                        formattedLines.Add(string.Empty);
                        previousWasEmpty = true;
                    }
                }
                continue;
            }

            // Check multiline comment state
            if (inMultiLineComment) {
                var commentIndent = new string(' ', indentLevel * 4);
                formattedLines.Add(commentIndent + trimmed);
                if (trimmed.Contains("*/", StringComparison.Ordinal)) {
                    inMultiLineComment = false;
                }
                previousWasEmpty = false;
                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal) && !trimmed.Contains("*/", StringComparison.Ordinal)) {
                inMultiLineComment = true;
                var commentIndent = new string(' ', indentLevel * 4);
                formattedLines.Add(commentIndent + trimmed);
                previousWasEmpty = false;
                continue;
            }

            // Count leading closing delimiters for current line's indentation
            var leadingClosingCount = CountLeadingClosingDelimiters(trimmed);
            var effectiveIndent = Math.Max(0, indentLevel - leadingClosingCount);

            // Normalize line content (spacing around colons, commas, etc.)
            var normalizedContent = NormalizeLineContent(trimmed);

            var indent = new string(' ', effectiveIndent * 4);
            formattedLines.Add(indent + normalizedContent);
            previousWasEmpty = false;

            // Update indentLevel for next lines based on delimiters outside string literals and comments
            var delta = CalculateNestingDelta(trimmed);
            indentLevel = Math.Max(0, indentLevel + delta);
        }

        // Remove any trailing empty lines before final newline
        while (formattedLines.Count > 0 && string.IsNullOrWhiteSpace(formattedLines[^1])) {
            formattedLines.RemoveAt(formattedLines.Count - 1);
        }

        var sb = new StringBuilder();
        foreach (var line in formattedLines) {
            sb.Append(line);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static List<string> SplitLines(string source) {
        var lines = new List<string>();
        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) is not null) {
            lines.Add(line);
        }
        return lines;
    }

    private static List<string> SplitCompoundDelimiters(List<string> lines) {
        var result = new List<string>();

        foreach (var line in lines) {
            var inString = false;
            var escape = false;
            var splitIdx = -1;

            for (var i = 0; i < line.Length; i++) {
                var c = line[i];

                if (inString) {
                    if (escape) {
                        escape = false;
                    } else if (c == '\\') {
                        escape = true;
                    } else if (c == '"') {
                        inString = false;
                    }
                    continue;
                }

                if (c == '"') {
                    inString = true;
                    continue;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') {
                    break;
                }

                if ((c == '[' || c == '}') && i + 1 < line.Length) {
                    var j = i + 1;
                    while (j < line.Length && char.IsWhiteSpace(line[j])) {
                        j++;
                    }
                    if (j < line.Length && line[j] == '{') {
                        splitIdx = i + 1;
                        break;
                    }
                }
            }

            if (splitIdx > 0) {
                var first = line[..splitIdx].TrimEnd();
                var second = line[splitIdx..].TrimStart();
                var subLines = SplitCompoundDelimiters([first, second]);
                result.AddRange(subLines);
            } else {
                result.Add(line);
            }
        }

        return result;
    }

    private static List<string> PreprocessEgyptianBraces(List<string> lines) {
        var result = new List<string>();

        for (var i = 0; i < lines.Count; i++) {
            var line = lines[i];
            var trimmed = line.Trim();

            // If a line is just '{' or '[', attach it to the preceding non-empty line (Egyptian brace style)
            if ((trimmed is "{" or "[") && result.Count > 0) {
                // Find previous non-empty line
                var prevIdx = result.Count - 1;
                while (prevIdx >= 0 && string.IsNullOrWhiteSpace(result[prevIdx])) {
                    prevIdx--;
                }
                if (prevIdx >= 0) {
                    var prevTrimmed = result[prevIdx].TrimEnd();
                    if (CanAttachEgyptianBrace(prevTrimmed, trimmed)) {
                        result[prevIdx] = prevTrimmed + " " + trimmed;
                        // Clear any blank lines between them
                        for (var k = result.Count - 1; k > prevIdx; k--) {
                            result.RemoveAt(k);
                        }
                        continue;
                    }
                }
            }

            result.Add(line);
        }

        return result;
    }

    private static bool CanAttachEgyptianBrace(string prevTrimmed, string brace) {
        if (string.IsNullOrWhiteSpace(prevTrimmed)) {
            return false;
        }

        // Never attach to any delimiter or statement separator
        if (prevTrimmed.EndsWith('{') || prevTrimmed.EndsWith('}') ||
            prevTrimmed.EndsWith('[') || prevTrimmed.EndsWith(']') ||
            prevTrimmed.EndsWith(',') || prevTrimmed.EndsWith(';')) {
            return false;
        }

        // Never attach if previous line ends with a comment
        if (prevTrimmed.Contains("//", StringComparison.Ordinal) || prevTrimmed.EndsWith("*/", StringComparison.Ordinal)) {
            return false;
        }

        // For '[', only attach to property declarations ending with ':'
        if (brace == "[") {
            return prevTrimmed.EndsWith(':');
        }

        // For '{', attach to property ending with ':' or block header (identifier, string literal, or parameter ')')
        if (brace == "{") {
            return prevTrimmed.EndsWith(':') ||
                   prevTrimmed.EndsWith(')') ||
                   prevTrimmed.EndsWith('"') ||
                   char.IsLetterOrDigit(prevTrimmed[^1]) ||
                   prevTrimmed[^1] == '_';
        }

        return false;
    }

    private static int CountLeadingClosingDelimiters(string trimmedLine) {
        var count = 0;
        for (var i = 0; i < trimmedLine.Length; i++) {
            var c = trimmedLine[i];
            if (c is '}' or ']') {
                count++;
            } else if (!char.IsWhiteSpace(c) && c != ',') {
                break;
            }
        }
        return count;
    }

    private static int CalculateNestingDelta(string line) {
        var delta = 0;
        var inString = false;
        var escape = false;

        for (var i = 0; i < line.Length; i++) {
            var c = line[i];

            if (inString) {
                if (escape) {
                    escape = false;
                } else if (c == '\\') {
                    escape = true;
                } else if (c == '"') {
                    inString = false;
                }
                continue;
            }

            // Check single line comment start
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') {
                break; // rest of line is comment
            }

            if (c == '"') {
                inString = true;
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

    private static string NormalizeLineContent(string trimmed) {
        // If line is a comment or starts with comment, keep as-is
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal)) {
            return trimmed;
        }

        var sb = new StringBuilder(trimmed.Length);
        var inString = false;
        var escape = false;

        for (var i = 0; i < trimmed.Length; i++) {
            var c = trimmed[i];

            if (inString) {
                sb.Append(c);
                if (escape) {
                    escape = false;
                } else if (c == '\\') {
                    escape = true;
                } else if (c == '"') {
                    inString = false;
                }
                continue;
            }

            if (c == '/' && i + 1 < trimmed.Length && trimmed[i + 1] == '/') {
                // Rest is comment, append with single leading space if preceded by content
                if (sb.Length > 0 && sb[^1] != ' ') {
                    sb.Append(' ');
                }
                sb.Append(trimmed.AsSpan(i));
                break;
            }

            if (c == '"') {
                inString = true;
                sb.Append(c);
                continue;
            }

            // A reserved `$name:segment` channel (`$physics:quiescent`, `$zones[...]`-folded selectors) is one
            // token; its internal colons are not property/kind-annotation colons, so the per-character colon rule
            // below must not touch them. Consumed verbatim, on the same terms as
            // ExpressionSpelling.IsBareName/PuckParser.TryReadExtendedName — KEEP IN SYNC with both.
            if (c == '$') {
                var tokenLength = ReservedChannelTokenLength(trimmed, i);
                if (tokenLength > 0) {
                    sb.Append(trimmed, i, tokenLength);
                    i += tokenLength - 1;
                    continue;
                }
            }

            // Normalize colon: remove spaces before, ensure single space after (unless followed by newline or delimiter)
            if (c == ':') {
                while (sb.Length > 0 && sb[^1] == ' ') {
                    sb.Length--;
                }
                sb.Append(':');
                if (i + 1 < trimmed.Length && trimmed[i + 1] != ' ' && trimmed[i + 1] != ':' && trimmed[i + 1] != '\n') {
                    sb.Append(' ');
                }
                continue;
            }

            // Normalize comma: ensure single space after
            if (c == ',') {
                while (sb.Length > 0 && sb[^1] == ' ') {
                    sb.Length--;
                }
                sb.Append(',');
                if (i + 1 < trimmed.Length && trimmed[i + 1] != ' ') {
                    sb.Append(' ');
                }
                continue;
            }

            // Collapse multiple spaces into single space outside strings
            if (c == ' ') {
                if (sb.Length == 0 || sb[^1] != ' ') {
                    sb.Append(' ');
                }
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString().TrimEnd();
    }

    // Length of the reserved-channel token starting at a '$' in `text[start]`, or 0 when `start` is not one (an
    // isolated '$' with no following name characters at all still returns 1, so callers always advance past it).
    private static int ReservedChannelTokenLength(string text, int start) {
        if (start >= text.Length || text[start] != '$') {
            return 0;
        }

        var pos = start + 1;
        while (pos < text.Length) {
            var c = text[pos];
            if (char.IsLetterOrDigit(c) || c is '_' or '$' or '.') {
                pos++;
                continue;
            }
            if (c == ':' && pos + 1 < text.Length) {
                var next = text[pos + 1];
                if (char.IsLetterOrDigit(next) || next is '_' or '$' or '.') {
                    pos++;
                    continue;
                }
                if (next == '-' && pos + 2 < text.Length && char.IsAsciiDigit(text[pos + 2])) {
                    pos += 2;
                    continue;
                }
            }
            if (c == '[') {
                var close = LiveZoneIndexEnd(text, start, pos);
                if (close > 0) {
                    pos = close + 1;
                    continue;
                }
            }
            break;
        }

        return pos - start;
    }

    // A "$zones[" segment inside a reserved name folds its whole bracketed index — colons, nested brackets and all —
    // into the name. KEEP IN SYNC with ExpressionSpelling.LiveZoneIndexEnd and PuckParser.ReservedLiveZoneIndexEnd.
    private static int LiveZoneIndexEnd(string text, int start, int bracket) {
        const string prefix = "$zones[";
        var prefixLength = prefix.Length - 1;
        if (text[bracket] != '[' || (bracket - start) < prefixLength
            || string.CompareOrdinal(text, bracket - prefixLength, prefix, 0, prefixLength) != 0) {
            return -1;
        }
        if ((bracket - prefixLength) != start && text[bracket - prefixLength - 1] != ':') {
            return -1;
        }

        var depth = 0;
        for (var index = bracket; index < text.Length; index++) {
            if (text[index] == '[') {
                depth++;
            } else if (text[index] == ']' && --depth == 0) {
                return index;
            }
        }

        return -1;
    }
}
