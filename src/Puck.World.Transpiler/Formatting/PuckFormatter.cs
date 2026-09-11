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
        var preprocessed = PreprocessEgyptianBraces(lines);
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
                    if (!prevTrimmed.EndsWith(trimmed[0])) {
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
}
