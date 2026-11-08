namespace Puck.Cli.Docs;

// Documentation permits list-indented fences. Both citation and link scans consume this same state machine.
internal sealed class MarkdownFence {
    private char m_marker;
    private int m_length;

    public int Indent { get; private set; }
    public bool IsOpen => (m_length != 0);

    // Opening and closing lines are code too. A closer has only whitespace after a sufficiently long matching run.
    public bool InCode(string line) {
        var indent = (line.Length - line.TrimStart(' ', '\t').Length);
        var span = line.AsSpan(start: indent);
        var length = 0;
        var marker = (span.IsEmpty ? '\0' : span[0]);

        if (marker is '`' or '~') {
            while ((length < span.Length) && (span[length] == marker)) { length++; }
        }
        if (IsOpen) {
            if ((marker == m_marker) && (length >= m_length) && span[length..].Trim().IsEmpty) { m_length = 0; }
            return true;
        }
        if ((length < 3) || ((marker == '`') && span[length..].Contains(value: '`'))) { return false; }
        m_marker = marker;
        m_length = length;
        Indent = indent;
        return true;
    }
}
