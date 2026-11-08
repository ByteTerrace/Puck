namespace Puck.Cli;

/// <summary>Replaces the content between a pair of generated-section delimiters, preserving the surrounding text.</summary>
public static class CliGeneratedSection {
    /// <summary>Replaces the first delimited section at or after <paramref name="startIndex"/>. The renderer receives
    /// the opening marker's indentation; delimiters themselves remain in the document.</summary>
    public static bool TryReplace(string text, string begin, string end, Func<string, string> render, out string generated, int startIndex = 0) {
        generated = text;
        var open = text.IndexOf(value: begin, startIndex: startIndex, comparisonType: StringComparison.Ordinal);
        if (open < 0) { return false; }
        var content = open + begin.Length;
        var close = text.IndexOf(value: end, startIndex: content, comparisonType: StringComparison.Ordinal);
        if (close < 0) { return false; }
        var indent = open;
        while (indent > 0 && text[indent - 1] is ' ' or '\t') { indent--; }
        generated = string.Concat(text.AsSpan(0, content), render(text[indent..open]).ReplaceLineEndings(replacementText: "\n"), text.AsSpan(close));
        return true;
    }
}
