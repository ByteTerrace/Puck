using Puck.Demo.Forge;

namespace Puck.Demo.Tracker;

/// <summary>
/// The tracker's authored-document model: a working <see cref="AudioDocument"/> plus the editor's cursor (which
/// pattern, which row) and the play/stop flag the preview reads. Every mutation verb lives here; the pad state
/// machine (<see cref="TrackerController"/>) and the console verbs both drive this one model — mirrors
/// <see cref="Creator.CreatorScene"/>'s role for the SDF editor, but this model is host-side data only: no GPU
/// program, no 3D transform, nothing that reaches the deterministic world. <see cref="Revision"/> bumps on every
/// edit so a caller can redraw the console dump only when something actually changed.
/// </summary>
internal sealed class TrackerScene {
    private const int MaxTempo = 255;
    private const int MinTempo = 1;

    private AudioDocument m_document;
    private int m_patternIndex;
    private int m_rowIndex;
    private bool m_active;
    private bool m_playing;

    /// <summary>Initializes an empty (blank) working document.</summary>
    public TrackerScene() {
        m_document = AudioDocumentStore.Blank();
    }

    /// <summary>Whether tracker mode is active (the pad takes over the creating slot).</summary>
    public bool Active => m_active;

    /// <summary>Whether the preview is (meant to be) playing — the render node reads this to drive the preview
    /// player's lifecycle; it never touches the document by itself.</summary>
    public bool Playing => m_playing;

    /// <summary>Bumps on every edit (document change, cursor move, or play/stop toggle) — the redraw-on-change poll.</summary>
    public int Revision { get; private set; }

    /// <summary>The working document (normalized — see <see cref="AudioDocumentStore"/>).</summary>
    public AudioDocument Document => m_document;

    /// <summary>The cursor's current pattern index.</summary>
    public int PatternIndex => m_patternIndex;

    /// <summary>The cursor's current row index within the current pattern.</summary>
    public int RowIndex => m_rowIndex;

    /// <summary>The current pattern's row count (the transport strip's readout denominator).</summary>
    public int RowCount => CurrentPattern.Count;

    /// <summary>Enters or leaves tracker mode. Leaving does not discard the working document — re-entering resumes
    /// exactly where editing left off (save explicitly to persist it).</summary>
    /// <param name="active">The desired state.</param>
    public void SetActive(bool active) {
        if (m_active == active) {
            return;
        }

        m_active = active;
        ++Revision;
    }

    /// <summary>Replaces the working document wholesale (a fresh blank, or a loaded one) and re-seats the cursor at
    /// pattern 0, row 0.</summary>
    /// <param name="document">The normalized document to adopt.</param>
    public void Load(AudioDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        m_document = document;
        m_patternIndex = 0;
        m_rowIndex = 0;
        ++Revision;
    }

    /// <summary>Clears the working document to one blank pattern (the <c>tracker.new</c> verb).</summary>
    public void New() {
        Load(document: AudioDocumentStore.Blank(name: m_document.Name));
    }

    /// <summary>Sets the working document's display name.</summary>
    /// <param name="name">The new name.</param>
    public void SetName(string name) {
        m_document = (m_document with { Name = (string.IsNullOrWhiteSpace(value: name) ? m_document.Name : name.Trim()) });
        ++Revision;
    }

    /// <summary>Moves the row cursor by <paramref name="direction"/>, clamped within the current pattern's row
    /// count (no wrap — the top/bottom row is a hard stop, so a repeated nudge can't silently wrap onto a
    /// different-looking row unnoticed).</summary>
    /// <param name="direction">+1 down, -1 up.</param>
    /// <returns>The new row index.</returns>
    public int MoveRow(int direction) {
        var rows = CurrentPattern.Count;

        m_rowIndex = Math.Clamp(value: (m_rowIndex + Math.Sign(value: direction)), max: (rows - 1), min: 0);
        ++Revision;

        return m_rowIndex;
    }

    /// <summary>Moves to a specific row index, clamped within the current pattern.</summary>
    /// <param name="row">The target row index.</param>
    /// <returns>The clamped row index actually landed on.</returns>
    public int SetRow(int row) {
        m_rowIndex = Math.Clamp(value: row, max: (CurrentPattern.Count - 1), min: 0);
        ++Revision;

        return m_rowIndex;
    }

    /// <summary>Moves to the previous/next pattern (wraps), re-seating the row cursor at row 0 (a different
    /// pattern's row count may not contain the old cursor).</summary>
    /// <param name="direction">+1 next, -1 previous.</param>
    /// <returns>The new pattern index.</returns>
    public int MovePattern(int direction) {
        var patterns = m_document.Patterns!;

        m_patternIndex = ((((m_patternIndex + Math.Sign(value: direction)) % patterns.Count) + patterns.Count) % patterns.Count);
        m_rowIndex = 0;
        ++Revision;

        return m_patternIndex;
    }

    /// <summary>Nudges the cursor row's note by one semitone step (see <see cref="TrackerNote"/>).</summary>
    /// <param name="direction">+1 up, -1 down.</param>
    /// <returns>The row's new note text.</returns>
    public string NudgeNote(int direction) => EditRow(edit: row => (row with { Note = TrackerNote.Nudge(note: row.Note, direction: direction) }));

    /// <summary>Nudges the cursor row's note by one octave (see <see cref="TrackerNote.NudgeOctave"/>).</summary>
    /// <param name="direction">+1 up, -1 down.</param>
    /// <returns>The row's new note text.</returns>
    public string NudgeOctave(int direction) => EditRow(edit: row => (row with { Note = TrackerNote.NudgeOctave(note: row.Note, direction: direction) }));

    /// <summary>Toggles the cursor row between a hold (<c>---</c>) and a cut (<c>OFF</c>) — the quick "silence this
    /// row" verb; a row already holding a pitched note becomes <c>OFF</c> first (cut), a second press holds, a third
    /// cuts again (a two-state toggle over the two non-pitched cycle members).</summary>
    /// <returns>The row's new note text.</returns>
    public string ToggleHoldOff() {
        return EditRow(edit: row => (row with {
            Note = (string.Equals(a: row.Note, b: AudioRowDocument.Off, comparisonType: StringComparison.OrdinalIgnoreCase)
                ? AudioRowDocument.Hold
                : AudioRowDocument.Off),
        }));
    }

    /// <summary>Nudges the tempo (frames per row), clamped 1..255.</summary>
    /// <param name="direction">+1 slower (more frames/row), -1 faster.</param>
    /// <returns>The new tempo.</returns>
    public int NudgeTempo(int direction) {
        var tempo = Math.Clamp(value: ((m_document.Tempo ?? AudioDocument.DefaultTempo) + Math.Sign(value: direction)), max: MaxTempo, min: MinTempo);

        m_document = (m_document with { Tempo = tempo });
        ++Revision;

        return tempo;
    }

    /// <summary>Sets the tempo directly (the <c>tracker.tempo</c> console verb), clamped 1..255.</summary>
    /// <param name="tempo">The requested tempo.</param>
    /// <returns>The clamped tempo actually set.</returns>
    public int SetTempo(int tempo) {
        var clamped = Math.Clamp(value: tempo, max: MaxTempo, min: MinTempo);

        m_document = (m_document with { Tempo = clamped });
        ++Revision;

        return clamped;
    }

    /// <summary>Sets an arbitrary row's note by index (the <c>tracker.note</c> console verb) — validated against
    /// <see cref="TrackerNote"/>'s vocabulary via a normalize-at-load re-run, so a bad token never corrupts the
    /// working document.</summary>
    /// <param name="row">The row index within the current pattern.</param>
    /// <param name="note">The note text (a pitch, <c>---</c>, or <c>OFF</c>).</param>
    /// <returns>Whether the row index was valid.</returns>
    public bool SetRowNote(int row, string note) {
        var pattern = CurrentPattern;

        if ((row < 0) || (row >= pattern.Count)) {
            return false;
        }

        var upper = note.Trim().ToUpperInvariant();

        if ((TrackerNote.IndexOf(note: upper) is var index) && !string.Equals(a: TrackerNote.AtIndex(index: index), b: upper, comparisonType: StringComparison.Ordinal)) {
            return false; // Not a recognized token (IndexOf's Hold fallback would silently misreport success).
        }

        ReplaceRow(row: row, edit: existing => (existing with { Note = upper }));

        return true;
    }

    /// <summary>Toggles the play/stop flag the preview lifecycle reads. Does not itself start/stop any audio — the
    /// render node owns the actual preview player (host-side output, kept out of this model).</summary>
    /// <returns>The new playing state.</returns>
    public bool TogglePlaying() {
        m_playing = !m_playing;
        ++Revision;

        return m_playing;
    }

    /// <summary>Renders the current pattern's rows as console-ready text lines, with a cursor marker on the current
    /// row plus a header line naming the document/tempo/pattern position — the console IS the tracker's display for
    /// v1 (no bespoke overlay).</summary>
    /// <returns>The lines to print (join with <c>\n</c> for a single <see cref="Commands.CommandResult"/>).</returns>
    public IReadOnlyList<string> RenderRows() {
        var pattern = CurrentPattern;
        var lines = new List<string>(capacity: (pattern.Count + 1)) {
            $"[tracker] \"{m_document.Name}\" — tempo {m_document.Tempo} — pattern {(m_patternIndex + 1)}/{m_document.Patterns!.Count} — {(m_playing ? "PLAYING" : "stopped")}",
        };

        for (var row = 0; (row < pattern.Count); row++) {
            var marker = ((row == m_rowIndex) ? ">" : " ");

            lines.Add(item: $"{marker} {row,2:D2} | {pattern[row].Note,-3} dty{(pattern[row].Duty ?? 2)}");
        }

        return lines;
    }

    private IReadOnlyList<AudioRowDocument> CurrentPattern => m_document.Patterns![m_patternIndex];

    private string EditRow(Func<AudioRowDocument, AudioRowDocument> edit) {
        var note = "";

        ReplaceRow(row: m_rowIndex, edit: row => {
            var edited = edit(row);

            note = edited.Note;

            return edited;
        });

        return note;
    }
    private void ReplaceRow(int row, Func<AudioRowDocument, AudioRowDocument> edit) {
        var patterns = new List<IReadOnlyList<AudioRowDocument>>(collection: m_document.Patterns!);
        var rows = new List<AudioRowDocument>(collection: patterns[m_patternIndex]);

        rows[row] = edit(rows[row]);
        patterns[m_patternIndex] = rows;
        m_document = (m_document with { Patterns = patterns });
        ++Revision;
    }
}
