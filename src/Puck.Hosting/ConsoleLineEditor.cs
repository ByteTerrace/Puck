using System.Text;

using Puck.Abstractions.Windowing;
using Puck.Commands;

namespace Puck.Hosting;

/// <summary>
/// The console panel's line editor: a caret-addressed buffer plus a bounded command history, driven by the
/// terminal's window-input sink from captured window text/edit events and republished to the
/// <see cref="ConsoleTape"/> after every edit — the state behind the prompt row's <c>"&gt; "</c> + input a renderer
/// draws. Selection is all-or-nothing (<see cref="SelectAll"/> selects the whole line; there is no partial
/// drag/shift-arrow selection).
/// </summary>
/// <remarks>Single-threaded by contract: every method here runs on the window-pump thread, the same thread the
/// window-input sink observes from, so there is no locking.</remarks>
public sealed class ConsoleLineEditor {
    // A block caret so a reopened panel shows the insertion point without a blink timer.
    private const char CaretGlyph = '█';
    private const int HistoryCapacity = 64;

    private readonly IClipboardService m_clipboard;
    private readonly ITextCommandSink m_source;
    private readonly ConsoleTape m_tape;

    private bool m_allSelected;
    private int m_caret;
    // Ranges [0, m_history.Count]; m_history.Count means "not browsing" (the live, uncommitted line).
    private int m_historyCursor;

    private readonly StringBuilder m_buffer = new();
    private readonly List<string> m_history = new();

    /// <summary>Initializes a new instance of the <see cref="ConsoleLineEditor"/> class.</summary>
    /// <param name="source">The command source a submitted line enqueues into.</param>
    /// <param name="tape">The tape republished after every edit.</param>
    /// <param name="clipboard">The clipboard <see cref="Copy"/>/<see cref="Cut"/> write through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/>, <paramref name="tape"/>, or
    /// <paramref name="clipboard"/> is <see langword="null"/>.</exception>
    public ConsoleLineEditor(ITextCommandSink source, ConsoleTape tape, IClipboardService clipboard) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: tape);
        ArgumentNullException.ThrowIfNull(argument: clipboard);

        m_clipboard = clipboard;
        m_source = source;
        m_tape = tape;
    }

    // The shared tail of Backspace/DeleteForward/Cut when the whole line is selected: the selection IS the edit,
    // so it clears the buffer outright rather than deleting one character.
    private void ClearSelectionEdit() {
        m_buffer.Clear();
        m_caret = 0;
        m_allSelected = false;
        Republish();
    }
    private void LoadHistoryEntry() {
        var entry = ((m_historyCursor < m_history.Count)
            ? m_history[m_historyCursor]
            : string.Empty
        );

        m_buffer.Clear();
        m_buffer.Append(value: entry);
        m_caret = m_buffer.Length;
        Republish();
    }
    private void Republish() {
        if (m_allSelected) {
            // The raw buffer, no caret glyph — the highlight rect shows the whole line as selected instead.
            m_tape.SetInput(
                input: m_buffer.ToString(),
                selected: true
            );
            return;
        }

        var text = m_buffer.ToString();
        var display = ((text[..m_caret] + CaretGlyph) + text[m_caret..]);

        m_tape.SetInput(
            input: display,
            selected: false
        );
    }

    /// <summary>Inserts printable text at the caret, control characters filtered out (a WM_CHAR for Enter/Tab must
    /// not also land as a literal character — those arrive as their own named-key edges instead). When the whole
    /// line is selected, the insertion replaces it (paste-over-selection), matching a normal text field.</summary>
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

            m_buffer.Insert(
                index: m_caret,
                value: character
            );
            m_caret++;
            inserted = true;
        }

        if (inserted) {
            Republish();
        }
    }
    /// <summary>Deletes the character before the caret, if any — or, when the whole line is selected, the
    /// selection itself (the selection deletion is the edit).</summary>
    public void Backspace() {
        if (m_allSelected) {
            ClearSelectionEdit();
            return;
        }

        if (m_caret == 0) {
            return;
        }

        m_buffer.Remove(
            length: 1,
            startIndex: (m_caret - 1)
        );
        m_caret--;
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
    /// <summary>Moves the caret to the start of the buffer.</summary>
    public void CaretHome() {
        m_allSelected = false;

        if (m_caret == 0) {
            return;
        }

        m_caret = 0;
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
    /// <summary>Clears the buffer and caret without submitting.</summary>
    public void Clear() {
        m_historyCursor = m_history.Count;
        m_buffer.Clear();
        m_caret = 0;
        m_allSelected = false;
        Republish();
    }
    /// <summary>Copies the whole line to the clipboard, when the buffer is non-empty. All-or-nothing: a bare
    /// Ctrl+C with nothing selected still copies the line — a convenience, since the whole line is always what a
    /// selection would have covered. Leaves the buffer and selection state untouched.</summary>
    public void Copy() {
        if (m_buffer.Length == 0) {
            return;
        }

        m_clipboard.SetText(text: m_buffer.ToString());
    }
    /// <summary>Copies the whole line to the clipboard, then clears the buffer (the selection deletion is the
    /// edit). A no-op on an empty buffer.</summary>
    public void Cut() {
        Copy();

        if (m_buffer.Length == 0) {
            return;
        }

        ClearSelectionEdit();
    }
    /// <summary>Deletes the character at the caret, if any — or, when the whole line is selected, the selection
    /// itself (the selection deletion is the edit).</summary>
    public void DeleteForward() {
        if (m_allSelected) {
            ClearSelectionEdit();
            return;
        }

        if (m_caret >= m_buffer.Length) {
            return;
        }

        m_buffer.Remove(
            length: 1,
            startIndex: m_caret
        );
        Republish();
    }
    /// <summary>Replaces the buffer with the next (newer) history entry, or clears it once history is exhausted.</summary>
    public void HistoryNext() {
        m_allSelected = false;

        if (m_history.Count == 0) {
            return;
        }

        m_historyCursor = Math.Min(
            val1: m_history.Count,
            val2: (m_historyCursor + 1)
        );
        LoadHistoryEntry();
    }
    /// <summary>Replaces the buffer with the previous (older) history entry, if any.</summary>
    public void HistoryPrev() {
        m_allSelected = false;

        if (m_history.Count == 0) {
            return;
        }

        m_historyCursor = Math.Max(
            val1: 0,
            val2: (m_historyCursor - 1)
        );
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
    /// <summary>Submits the buffered line to the editor's principal-bound <see cref="ITextCommandSink"/> — the same
    /// tick-aligned command pump stdin uses, under this session's own identity — then records it in history and
    /// clears the buffer. A no-op on an empty buffer.</summary>
    public void Submit() {
        if (m_buffer.Length == 0) {
            return;
        }

        var line = m_buffer.ToString();

        m_source.Enqueue(line: line);

        // Dedup consecutive repeats only — a repeated older command still deserves its own history slot.
        if (
            (m_history.Count == 0) ||
            (m_history[^1] != line)
        ) {
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
}
