using System.Text;

using Puck.Abstractions.Windowing;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The console panel's line editor: a caret-addressed buffer plus a bounded command history, driven by
/// <see cref="WorldConsoleTextSink"/> from captured window text/edit events and republished to
/// <see cref="WorldConsoleMirror"/> after every edit — the state behind the prompt row's <c>"&gt; "</c> + input the
/// overlay already draws. Selection is all-or-nothing (<see cref="SelectAll"/> selects the whole line; there is no
/// partial drag/shift-arrow selection).
/// </summary>
/// <remarks>Single-threaded by contract: every method here runs on the window-pump thread, the same thread
/// <see cref="WorldConsoleTextSink.Observe"/> is called from, so there is no locking.</remarks>
internal sealed class WorldConsoleInput {
    private const int HistoryCapacity = 64;
    // A block caret so a reopened panel shows the insertion point without a blink timer.
    private const char CaretGlyph = '█';

    private readonly StringBuilder m_buffer = new();
    private bool m_allSelected;
    private int m_caret;
    private readonly IClipboardService m_clipboard;
    private readonly List<string> m_history = new();
    // Ranges [0, m_history.Count]; m_history.Count means "not browsing" (the live, uncommitted line).
    private int m_historyCursor;
    private readonly WorldConsoleMirror m_mirror;
    private readonly TextCommandSource m_source;

    /// <summary>Initializes a new instance of the <see cref="WorldConsoleInput"/> class.</summary>
    /// <param name="source">The command source a submitted line enqueues into.</param>
    /// <param name="mirror">The mirror republished after every edit.</param>
    /// <param name="clipboard">The clipboard <see cref="Copy"/>/<see cref="Cut"/> write through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/>, <paramref name="mirror"/>, or
    /// <paramref name="clipboard"/> is <see langword="null"/>.</exception>
    public WorldConsoleInput(TextCommandSource source, WorldConsoleMirror mirror, IClipboardService clipboard) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentNullException.ThrowIfNull(argument: clipboard);

        m_clipboard = clipboard;
        m_mirror = mirror;
        m_source = source;
    }

    /// <summary>Inserts printable text at the caret, control characters filtered out (a WM_CHAR for Enter/Tab must
    /// not also land as a literal character — those arrive as their own named-key edges instead). When the whole
    /// line is selected, the insertion REPLACES it (paste-over-selection), matching a normal text field.</summary>
    /// <param name="text">The typed or pasted text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public void AppendText(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        if (m_allSelected) {
            m_buffer.Clear();
            m_caret = 0;
            m_allSelected = false;
        }

        var inserted = false;

        foreach (var character in text) {
            if (char.IsControl(c: character)) {
                continue;
            }

            m_buffer.Insert(index: m_caret, value: character);
            m_caret++;
            inserted = true;
        }

        if (inserted) {
            Republish();
        }
    }

    /// <summary>Deletes the character before the caret, if any — or, when the whole line is selected, the
    /// selection itself (the selection deletion IS the edit).</summary>
    public void Backspace() {
        if (m_allSelected) {
            ClearSelectionEdit();
            return;
        }

        if (m_caret == 0) {
            return;
        }

        m_buffer.Remove(startIndex: (m_caret - 1), length: 1);
        m_caret--;
        Republish();
    }

    /// <summary>Deletes the character at the caret, if any — or, when the whole line is selected, the selection
    /// itself (the selection deletion IS the edit).</summary>
    public void DeleteForward() {
        if (m_allSelected) {
            ClearSelectionEdit();
            return;
        }

        if (m_caret >= m_buffer.Length) {
            return;
        }

        m_buffer.Remove(startIndex: m_caret, length: 1);
        Republish();
    }

    /// <summary>Moves the caret one character left, clamped to the start of the buffer.</summary>
    public void CaretLeft() {
        m_allSelected = false;

        if (m_caret == 0) {
            return;
        }

        m_caret--;
        Republish();
    }

    /// <summary>Moves the caret one character right, clamped to the end of the buffer.</summary>
    public void CaretRight() {
        m_allSelected = false;

        if (m_caret >= m_buffer.Length) {
            return;
        }

        m_caret++;
        Republish();
    }

    /// <summary>Moves the caret to the start of the buffer.</summary>
    public void CaretHome() {
        m_allSelected = false;

        if (m_caret == 0) {
            return;
        }

        m_caret = 0;
        Republish();
    }

    /// <summary>Moves the caret to the end of the buffer.</summary>
    public void CaretEnd() {
        m_allSelected = false;

        if (m_caret >= m_buffer.Length) {
            return;
        }

        m_caret = m_buffer.Length;
        Republish();
    }

    /// <summary>Replaces the buffer with the previous (older) history entry, if any.</summary>
    public void HistoryPrev() {
        m_allSelected = false;

        if (m_history.Count == 0) {
            return;
        }

        m_historyCursor = Math.Max(val1: 0, val2: (m_historyCursor - 1));
        LoadHistoryEntry();
    }

    /// <summary>Replaces the buffer with the next (newer) history entry, or clears it once history is exhausted.</summary>
    public void HistoryNext() {
        m_allSelected = false;

        if (m_history.Count == 0) {
            return;
        }

        m_historyCursor = Math.Min(val1: m_history.Count, val2: (m_historyCursor + 1));
        LoadHistoryEntry();
    }

    /// <summary>Selects the whole line, when the buffer is non-empty. A no-op on an empty buffer (there is nothing
    /// to show as selected).</summary>
    public void SelectAll() {
        if (m_buffer.Length == 0) {
            return;
        }

        m_allSelected = true;
        Republish();
    }

    /// <summary>Copies the whole line to the clipboard, when the buffer is non-empty. All-or-nothing: a bare
    /// Ctrl+C with nothing selected still copies the line — a convenience, since the WHOLE line is always what a
    /// selection would have covered. Leaves the buffer and selection state untouched.</summary>
    public void Copy() {
        if (m_buffer.Length == 0) {
            return;
        }

        m_clipboard.SetText(text: m_buffer.ToString());
    }

    /// <summary>Copies the whole line to the clipboard, then clears the buffer (the selection deletion IS the
    /// edit). A no-op on an empty buffer.</summary>
    public void Cut() {
        Copy();

        if (m_buffer.Length == 0) {
            return;
        }

        ClearSelectionEdit();
    }

    /// <summary>Submits the buffered line to <see cref="TextCommandSource.Enqueue"/> — the same tick-aligned dispatch
    /// path stdin uses — then records it in history and clears the buffer. A no-op on an empty buffer.</summary>
    public void Submit() {
        if (m_buffer.Length == 0) {
            return;
        }

        var line = m_buffer.ToString();

        m_source.Enqueue(line: line);

        // Dedup consecutive repeats only — a repeated older command still deserves its own history slot.
        if ((m_history.Count == 0) || (m_history[^1] != line)) {
            m_history.Add(item: line);

            if (m_history.Count > HistoryCapacity) {
                m_history.RemoveAt(index: 0);
            }
        }

        m_historyCursor = m_history.Count;
        m_buffer.Clear();
        m_caret = 0;
        m_allSelected = false;
        Republish();
    }

    /// <summary>Clears the buffer and caret without submitting.</summary>
    public void Clear() {
        m_historyCursor = m_history.Count;
        m_buffer.Clear();
        m_caret = 0;
        m_allSelected = false;
        Republish();
    }

    private void LoadHistoryEntry() {
        var entry = ((m_historyCursor < m_history.Count) ? m_history[m_historyCursor] : string.Empty);

        m_buffer.Clear();
        m_buffer.Append(value: entry);
        m_caret = m_buffer.Length;
        Republish();
    }

    // The shared tail of Backspace/DeleteForward/Cut when the whole line is selected: the selection IS the edit,
    // so it clears the buffer outright rather than deleting one character.
    private void ClearSelectionEdit() {
        m_buffer.Clear();
        m_caret = 0;
        m_allSelected = false;
        Republish();
    }
    private void Republish() {
        if (m_allSelected) {
            // The raw buffer, no caret glyph — the highlight rect shows the whole line as selected instead.
            m_mirror.SetInput(input: m_buffer.ToString(), selected: true);
            return;
        }

        var text = m_buffer.ToString();
        var display = ((text[..m_caret] + CaretGlyph) + text[m_caret..]);

        m_mirror.SetInput(input: display, selected: false);
    }
}
