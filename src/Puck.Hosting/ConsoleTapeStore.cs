namespace Puck.Hosting;

/// <summary>One tape row: the text plus the verdict that decides how it is painted.</summary>
/// <param name="Text">The row's text.</param>
/// <param name="Refused">Whether the row belongs to a refused result (rendered in the danger hue, matching the toast
/// channel) rather than an accepted one.</param>
public readonly record struct ConsoleTapeLine(
    string Text,
    bool Refused
);
/// <summary>The published console-tape snapshot a renderer draws.</summary>
/// <param name="Visible">Whether the console panel is shown (hidden = a renderer draws nothing).</param>
/// <param name="Lines">The output history, oldest first; a renderer shows the trailing lines that fit.</param>
/// <param name="Input">The in-progress input line (rendered on the bottom row after the prompt).</param>
/// <param name="Selected">Whether the whole <see cref="Input"/> line is selected (Ctrl+A) — a renderer paints a
/// highlight rect behind it instead of the insertion caret.</param>
public readonly record struct ConsoleTapeFrame(
    bool Visible,
    IReadOnlyList<ConsoleTapeLine> Lines,
    string Input,
    bool Selected
);
/// <summary>The read seam a console renderer consumes (the unified overlay's console-panel writer); the terminal's
/// <see cref="ConsoleTape"/> is the writer.</summary>
public interface IConsoleTapeSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <param name="includeHiddenHistory">Whether a hidden panel's history is needed. Renderers may skip it;
    /// a diagnostic reader still receives the complete current history by default.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out ConsoleTapeFrame frame, bool includeHiddenHistory = true);
}
/// <summary>
/// The console-tape state store: edits update bounded staging storage under a short lock, and a reader materializes
/// one immutable frame for all edits since its previous read. Hidden rendering can skip history, while ordinary
/// snapshot reads retain the complete history and older snapshots remain immutable.
/// </summary>
public sealed class ConsoleTapeStore : IConsoleTapeSource {
    private readonly Lock m_gate = new();

    private ConsoleTapeFrame m_frame;

    private ConsoleTapeLine[] m_pending = [];

    private int m_count;
    private bool m_dirty;
    private bool m_published;

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in ConsoleTapeFrame frame) {
        lock (m_gate) {
            m_frame = frame;
            m_dirty = false;
            m_published = true;
        }
    }

    internal void Publish(ConsoleTapeLine[] ring, int head, int count, string input, bool selected, bool visible) {
        lock (m_gate) {
            if (m_pending.Length < ring.Length) { m_pending = new ConsoleTapeLine[ring.Length]; }
            for (var index = 0; (index < count); index++) { m_pending[index] = ring[((head + index) % ring.Length)]; }
            m_count = count;
            m_frame = new ConsoleTapeFrame(visible, (m_frame.Lines ?? []), input, selected);
            m_dirty = true;
            m_published = true;
        }
    }

    /// <inheritdoc/>
    public bool TrySnapshot(out ConsoleTapeFrame frame, bool includeHiddenHistory = true) {
        lock (m_gate) {
            if (m_dirty && (m_frame.Visible || includeHiddenHistory)) {
                m_frame = m_frame with { Lines = m_pending.AsSpan(length: m_count, start: 0).ToArray() };
                m_dirty = false;
            }
            frame = m_frame;
            return m_published;
        }
    }
}
