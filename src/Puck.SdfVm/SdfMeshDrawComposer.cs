using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>A frame's host mesh draws as one list a producer hands a frame, with what changed in it
/// (<see cref="SdfFrameContent.MeshDraws"/> and the fields beside it): borrowed until the producer composes again.</summary>
/// <param name="Draws">The draws.</param>
/// <param name="Revision">The revision of <paramref name="Draws"/>' content, moved whenever the list changed.</param>
/// <param name="Changes">The journal of the rows that changed, or <see langword="null"/> when a consumer compares the
/// list whole.</param>
/// <param name="Serial">The journal endpoint captured with <paramref name="Draws"/>.</param>
public readonly record struct SdfMeshDrawList(IReadOnlyList<SdfMeshDraw> Draws, long Revision, GpuRowChanges? Changes, long Serial) {
    /// <summary>Gets an empty list that never changes.</summary>
    public static SdfMeshDrawList Empty => new(Draws: [], Changes: null, Revision: 0L, Serial: 0L);
}

/// <summary>
/// Composes several producers' mesh draws into one list in producer order, with a journal of the composed rows that
/// changed, so a frame that rewrote one producer's draws copies and owes only those rows. One producer's list passes
/// through uncopied; several are copied into one list kept and rewritten in place. A producer with no journal, or one
/// whose list is another list, owes its whole range; a change in any producer's count owes the whole composition.
/// </summary>
/// <remarks>Used on one thread. A composed list is borrowed: it is rewritten by the next <see cref="End"/> that finds a
/// producer moved.</remarks>
public sealed class SdfMeshDrawComposer {
    private sealed class Part {
        public IReadOnlyList<SdfMeshDraw> Draws = [];
        public long Revision = -1L;
        public GpuRowChanges? Changes;
        public long Serial;
        public int Offset = -1;
        public int Count = -1;
        public bool Moved;
        public readonly GpuRowCursor Cursor = new();
    }

    private readonly List<SdfMeshDraw> m_composed = [];
    private readonly GpuChangedRows m_changes = new(history: SdfMovedTransforms.History, capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_runs = new(capacity: GpuRegion.MaxCopyRuns);
    private Part[] m_parts = [];
    private int m_count = -1;
    private int m_added;

    /// <summary>Gets the composed draws as of the last <see cref="End"/>.</summary>
    public SdfMeshDrawList List { get; private set; } = SdfMeshDrawList.Empty;

    /// <summary>Starts a composition of a number of producers, each added once, in order, by <see cref="Add"/>.</summary>
    /// <param name="parts">The number of producers, none negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parts"/> is negative.</exception>
    public void Begin(int parts) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: parts);
        if (m_parts.Length != parts) {
            var grown = new Part[parts];

            for (var index = 0; (index < parts); index++) { grown[index] = ((index < m_parts.Length) ? m_parts[index] : new Part()); }
            m_parts = grown;
        }
        m_added = 0;
    }
    /// <summary>Adds the next producer's draws.</summary>
    /// <param name="draws">The producer's list, which it may rewrite in place between compositions.</param>
    /// <param name="revision">The producer's revision of <paramref name="draws"/>' content.</param>
    /// <param name="changes">The producer's journal of the rows that changed, or <see langword="null"/> for none.</param>
    /// <param name="serial">The journal endpoint captured with <paramref name="draws"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Every producer <see cref="Begin"/> named was added.</exception>
    public void Add(IReadOnlyList<SdfMeshDraw> draws, long revision, GpuRowChanges? changes, long serial) {
        ArgumentNullException.ThrowIfNull(argument: draws);
        if (m_added == m_parts.Length) { throw new InvalidOperationException(message: "Every producer the composition began with was added."); }
        var part = m_parts[m_added++];

        part.Moved = (!ReferenceEquals(objA: part.Draws, objB: draws) || (part.Revision != revision));
        (part.Draws, part.Revision, part.Changes, part.Serial) = (draws, revision, changes, serial);
    }
    /// <summary>Composes the producers added since <see cref="Begin"/> where any moved, and returns the composed draws.</summary>
    /// <returns>The composed draws (<see cref="List"/>).</returns>
    /// <exception cref="InvalidOperationException">A producer <see cref="Begin"/> named was not added.</exception>
    public SdfMeshDrawList End() {
        if (m_added != m_parts.Length) { throw new InvalidOperationException(message: "A producer the composition began with was not added."); }
        var moved = (m_count < 0);
        var layout = false;
        var total = 0;
        var drawing = 0;
        IReadOnlyList<SdfMeshDraw> only = [];

        foreach (var part in m_parts) {
            moved |= part.Moved;
            layout |= ((part.Offset != total) || (part.Count != part.Draws.Count));
            if (part.Draws.Count > 0) { drawing++; only = part.Draws; }
            total += part.Draws.Count;
        }
        if (!moved && !layout) { return List; }

        var through = (drawing <= 1);
        var whole = (layout || (m_count != total) || (!through && !ReferenceEquals(objA: List.Draws, objB: m_composed)));

        if (!through && whole) {
            m_composed.Clear();
            foreach (var part in m_parts) {
                for (var index = 0; (index < part.Draws.Count); index++) { m_composed.Add(item: part.Draws[index]); }
            }
        }
        m_changes.Begin(everything: whole, rows: total);

        var offset = 0;

        foreach (var part in m_parts) {
            if (part.Moved || (part.Offset != offset)) {
                _ = part.Cursor.Collect(into: m_runs, reader: part.Changes, revision: part.Revision, rows: part.Draws.Count, table: part.Draws, through: part.Serial);
                part.Cursor.Commit();
                for (var run = 0; (!whole && (run < m_runs.Count)); run++) {
                    var start = m_runs.Start(index: run);
                    var length = m_runs.Length(index: run);

                    m_changes.Owe(count: length, start: (offset + start));
                    for (var index = start; (!through && (index < (start + length))); index++) { m_composed[(offset + index)] = part.Draws[index]; }
                }
            }
            (part.Offset, part.Count, part.Moved) = (offset, part.Draws.Count, false);
            offset += part.Draws.Count;
        }
        m_count = total;
        List = new SdfMeshDrawList(Changes: m_changes.Reader, Draws: (through ? only : m_composed), Revision: (List.Revision + 1L), Serial: m_changes.Serial);
        return List;
    }
}
