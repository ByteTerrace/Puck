using System.Text;
using Puck.State;
using Puck.Transpiler.Parsing;

namespace Puck.Transpiler.Formatting;

/// <summary>Opinionated, idempotent source code formatter for the Puck authoring language (.puck).</summary>
public static class PuckFormatter {
    /// <summary>Formats Puck source code according to the repository's C# and DSL style guidelines.</summary>
    /// <param name="source">The raw Puck source code.</param>
    /// <returns>Clean, idiomatic, idempotently formatted Puck code with Egyptian braces and 4-space indentation.</returns>
    public static string Format(string source) {
        ArgumentNullException.ThrowIfNull(source);

        var marker = "__puck_literal_";
        while (source.Contains(marker, StringComparison.Ordinal)) { marker += "_"; }
        var literals = new List<string>();
        var protectedSource = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length;) {
            var end = SourceLexemes.End(source, index);
            if (end > index) {
                // Comments retain their lexical role; only strings and quoted identifiers are replaced.
                if (source[index] is '"' or '$' or '`') {
                    protectedSource.Append('"').Append(marker).Append(literals.Count).Append('"');
                    literals.Add(source[index..end]);
                } else { protectedSource.Append(source.AsSpan(index, end - index)); }
                index = end;
            } else { protectedSource.Append(source[index++]); }
        }
        var formatted = FormatCore(protectedSource.ToString());
        var restored = new StringBuilder(formatted.Length);
        var prefix = "\"" + marker;
        var copied = 0;
        while (formatted.IndexOf(prefix, copied, StringComparison.Ordinal) is var start && start >= 0) {
            restored.Append(formatted.AsSpan(copied, start - copied));
            var end = formatted.IndexOf('"', start + prefix.Length);
            var ordinal = int.Parse(formatted.AsSpan(start + prefix.Length, end - start - prefix.Length), System.Globalization.CultureInfo.InvariantCulture);
            restored.Append(literals[ordinal]);
            copied = end + 1;
        }
        restored.Append(formatted.AsSpan(copied));
        return restored.ToString();
    }

    private static string FormatCore(string source) {

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
            var stringDelimiter = '\0';
            var escape = false;
            var splitIdx = -1;

            for (var i = 0; i < line.Length; i++) {
                var c = line[i];

                if (inString) {
                    // A backquoted name (ExpressionSpelling's `seat-1`/`N,S,E,W` spelling) carries no escape
                    // sequence, so only a `"` string honors a preceding backslash.
                    if (escape) {
                        escape = false;
                    } else if (c == '\\' && stringDelimiter == '"') {
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
                        // A container is written without the separator, so joining the opener to its key drops one.
                        // Formatting must not reintroduce the spelling the parser refuses (PUCK040).
                        result[prevIdx] = DropTrailingSeparator(prevTrimmed) + " " + trimmed;
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

    // Whether a line is nothing but an identifier, so a container opener on the next line belongs to it.
    private static bool IsBareKey(string prevTrimmed) {
        if (prevTrimmed.Length == 0) {
            return false;
        }

        foreach (var c in prevTrimmed) {
            if (!char.IsLetterOrDigit(c) && (c != '_')) {
                return false;
            }
        }

        return !char.IsDigit(prevTrimmed[0]);
    }

    // Drops the ':' or '=' a key was written with before a container opener joins it.
    private static string DropTrailingSeparator(string prevTrimmed) {
        if (prevTrimmed.EndsWith(':') || prevTrimmed.EndsWith('=')) {
            return prevTrimmed[..^1].TrimEnd();
        }

        return prevTrimmed;
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

        // For '[', attach to a property declaration whether or not it still carries the separator the container
        // spelling drops: `cells [` and the older `cells: [` both reach the formatter.
        if (brace == "[") {
            return (prevTrimmed.EndsWith(':') || prevTrimmed.EndsWith('=') || IsBareKey(prevTrimmed));
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
        var stringDelimiter = '\0';
        var escape = false;

        for (var i = 0; i < line.Length; i++) {
            var c = line[i];

            if (inString) {
                if (escape) {
                    escape = false;
                } else if (c == '\\' && stringDelimiter == '"') {
                    escape = true;
                } else if (c == stringDelimiter) {
                    inString = false;
                }
                continue;
            }

            // Check single line comment start
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') {
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

    private static string NormalizeLineContent(string trimmed) {
        // If line is a comment or starts with comment, keep as-is
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal)) {
            return trimmed;
        }

        var sb = new StringBuilder(trimmed.Length);
        var inString = false;
        var stringDelimiter = '\0';
        var escape = false;

        for (var i = 0; i < trimmed.Length; i++) {
            var c = trimmed[i];

            if (inString) {
                sb.Append(c);
                // A backquoted name carries no escape sequence: `N,S,E,W`'s commas are name characters, not
                // statement punctuation, and only a `"` string honors a preceding backslash.
                if (escape) {
                    escape = false;
                } else if (c == '\\' && stringDelimiter == '"') {
                    escape = true;
                } else if (c == stringDelimiter) {
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

            if (c is '"' or '`') {
                inString = true;
                stringDelimiter = c;
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

            // Normalize colon: ensure single space after (unless followed by newline or delimiter). A colon already
            // carrying one space before it — `bind name : Kind`, a `when Gate : Kind` suffix, or a ternary's
            // `? a : b` — is ExpressionSpelling's own spacing (Puck.State.ExpressionSpelling's Ternary.PrintBare),
            // not an unspaced property colon, and stripping that space splices `a : b` into `a: b`. The preceding
            // whitespace is already collapsed to at most one space by the general run below, so leaving it alone
            // here recovers both spellings without telling them apart.
            if (c == ':') {
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
    // The name grammar itself — ':' segments, signed offsets, and a folded `$zones[...]` index — is
    // ExpressionSpelling's, read through its own scanner rather than copied here (rule 8).
    private static int ReservedChannelTokenLength(string text, int start) {
        if (start >= text.Length || text[start] != '$') {
            return 0;
        }

        return Math.Max(1, ExpressionSpelling.ScanBareName(text: text, start: start));
    }
}
