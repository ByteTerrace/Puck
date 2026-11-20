namespace Puck.Cli;

/// <summary>Text helpers the verbs share.</summary>
public static class CliText {
    /// <summary>Splits process output into its non-empty lines, whatever line ending each carries.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The lines in order, without line endings and without blank lines.</returns>
    public static IReadOnlyList<string> Lines(string text) => [.. AllLines(text: text).Where(predicate: static line => (line.Length > 0))];
    /// <summary>Joins the last <paramref name="count"/> lines of process output that hold more than whitespace: a failing
    /// process's refusal sits at its end, and the whole transcript would bury it.</summary>
    /// <param name="text">The output.</param>
    /// <param name="count">The number of lines to keep.</param>
    /// <param name="separator">The text that joins them; the default keeps them on one line, for a message printed on one.</param>
    /// <returns>The kept lines, or an empty string when the output holds none.</returns>
    public static string Tail(string text, int count, string separator = " | ") => string.Join(
        separator: separator,
        values: Lines(text: text).Where(predicate: static line => !string.IsNullOrWhiteSpace(value: line)).TakeLast(count: count)
    );
    /// <summary>Reads source lines, preserving interior blank lines and omitting the phantom line after a final newline.</summary>
    /// <param name="text">The source text.</param>
    /// <param name="emptyIsLine">Whether an empty input counts as one empty line, as source searches require.</param>
    /// <returns>The lines, without their terminators.</returns>
    public static string[] AllLines(string text, bool emptyIsLine = false) {
        var lines = new List<string>();

        foreach (var line in EnumerateLines(text: text.AsSpan(), emptyIsLine: emptyIsLine)) { lines.Add(item: line.ToString()); }
        return [.. lines];
    }
    /// <summary>Enumerates the same lines without materializing strings, for count-only source searches.</summary>
    public static LineEnumerator EnumerateLines(ReadOnlySpan<char> text, bool emptyIsLine = false) => new(emptyIsLine: emptyIsLine, text: text);

    /// <summary>A span-based line reader. Empty input is optionally one empty line; a terminal separator adds none.</summary>
    public ref struct LineEnumerator {
        private ReadOnlySpan<char> m_remaining;
        private bool m_available;
        private int m_next;

        public ReadOnlySpan<char> Current { get; private set; }
        public int Start { get; private set; }

        internal LineEnumerator(ReadOnlySpan<char> text, bool emptyIsLine) {
            m_remaining = text;
            m_available = (!text.IsEmpty || emptyIsLine);
        }

        public LineEnumerator GetEnumerator() => this;
        public bool MoveNext() {
            if (!m_available) { return false; }
            Start = m_next;
            var end = m_remaining.IndexOfAny(value0: '\r', value1: '\n');

            if (end < 0) {
                Current = m_remaining;
                m_available = false;
            } else {
                Current = m_remaining[..end];
                var skip = (end + (((m_remaining[end] == '\r') && ((end + 1) < m_remaining.Length) && (m_remaining[(end + 1)] == '\n')) ? 2 : 1));

                m_next += skip;
                m_remaining = m_remaining[skip..];
                m_available = !m_remaining.IsEmpty;
            }
            return true;
        }
    }
}
