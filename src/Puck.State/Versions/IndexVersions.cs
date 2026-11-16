namespace Puck.State;

/// <summary>
/// A content version per index, and the order the indices last moved in: the version half of a write door, shared by
/// every store that keys state by a dense index (the arena by row, a population by slot). A version moves only
/// through <see cref="Bump"/> and <see cref="ResetAll"/>, and both record the move here, so a reader that keeps the
/// <see cref="Sequence"/> it read to is handed exactly the indices that moved since (<see cref="TryReadMoves"/>),
/// each once however often it moved, or is told to take every index as moved.
/// </summary>
/// <remarks>
/// <para>The storage is private to this type: nothing else can change a version without the readers hearing of
/// it. The order is an intrusive list threaded through the indices, most recently moved first, so a move costs a
/// constant number of writes, a read costs the distinct indices that moved since the reader's place, and no number
/// of moves between two reads loses one.</para>
/// <para>Writers are single-threaded with one another, and a wholesale reset shares their thread or gate with every
/// reader: the arena's writers are its own, and a population's one writer is the publish of its slot notes
/// (<see cref="SlotNotes.Publish"/>). A reader that overlaps a single move may be handed an index twice, or one whose
/// move is still landing, but never loses one for good: a move publishes its sequence last, and a reader's new place
/// is the sequence it read before it walked, so whatever the walk missed moved after that place and is handed over
/// by the next read.</para>
/// </remarks>
public sealed class IndexVersions {
    private const int None = -1;

    private ulong[] m_versions;
    // The sequence each index last moved at; an index is in the list exactly while this is above the floor.
    private long[] m_movedAt;
    // The list's links by index: the index that moved next after it (toward the newest) and the one before it.
    private int[] m_newer;
    private int[] m_older;
    private int m_newest = None;
    private long m_sequence;
    // The earliest place a reader may still resume from: every version moved or restarted at once just before it.
    private long m_floor;

    /// <summary>Initializes a new instance of the <see cref="IndexVersions"/> class, every version zero and none moved.</summary>
    /// <param name="count">The number of indices.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public IndexVersions(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);
        m_versions = new ulong[count];
        m_movedAt = new long[count];
        m_newer = new int[count];
        m_older = new int[count];
    }

    /// <summary>Gets the number of indices.</summary>
    public int Count => m_versions.Length;
    /// <summary>Gets the place after every move so far: what a reader that has just read every index keeps. It moves
    /// exactly when a version does.</summary>
    public long Sequence => Volatile.Read(location: ref m_sequence);
    /// <summary>Gets one index's version.</summary>
    /// <param name="index">The index.</param>
    public ulong this[int index] => m_versions[index];

    /// <summary>Moves one index's version and makes the index the most recently moved.</summary>
    /// <param name="index">The index.</param>
    public void Bump(int index) {
        var sequence = (m_sequence + 1L);
        var linked = (m_movedAt[index] > m_floor);

        m_versions[index]++;
        m_movedAt[index] = sequence;
        if (index != m_newest) {
            if (linked) {
                var newer = m_newer[index];
                var older = m_older[index];

                // A linked index that is not the newest has a newer neighbour.
                m_older[newer] = older;
                if (older != None) { m_newer[older] = newer; }
            }

            m_older[index] = m_newest;
            m_newer[index] = None;
            if (m_newest != None) { m_newer[m_newest] = index; }
            m_newest = index;
        }

        // Published last: a reader that sees this sequence finds the move already in the list.
        Volatile.Write(location: ref m_sequence, value: sequence);
    }
    /// <summary>Sets every version at once, for a number of indices that may differ from the present one: every
    /// reader's place falls below the floor, so each takes every index as moved.</summary>
    /// <param name="count">The number of indices from now on.</param>
    /// <param name="version">The version every index takes.</param>
    public void ResetAll(int count, ulong version) {
        var sequence = (m_sequence + 1L);

        if (m_versions.Length != count) {
            m_versions = new ulong[count];
            m_movedAt = new long[count];
            m_newer = new int[count];
            m_older = new int[count];
        }

        Array.Fill(array: m_versions, value: version);
        m_newest = None;
        // Every recorded move is at or below the floor now, which is what unlinks every index.
        m_floor = sequence;
        Volatile.Write(location: ref m_sequence, value: sequence);
    }
    /// <summary>Hands a reader every index whose version moved since the place it kept, and moves its place to the
    /// sequence read before the indices were gathered.</summary>
    /// <param name="place">The place the reader kept: one this method or <see cref="Sequence"/> gave it from these
    /// versions, or a negative value for a reader that has read nothing. On return it is the place to keep, whatever
    /// the answer.</param>
    /// <param name="into">The moved indices, appended most recently moved first, each once.</param>
    /// <returns><see langword="false"/> when the reader must take every index as moved and nothing was appended: it
    /// has read nothing, or every version was set at once since its place.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="into"/> is <see langword="null"/>.</exception>
    public bool TryReadMoves(ref long place, List<int> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        var since = place;
        var sequence = Volatile.Read(location: ref m_sequence);

        place = sequence;
        if ((since < m_floor) || (since > sequence)) {
            return false;
        }

        var start = into.Count;
        var versions = m_versions;

        for (var index = m_newest; ((index != None) && (m_movedAt[index] > since)); index = m_older[index]) {
            // Each index is in the list once, so a walk longer than the indices is one a writer re-linked under: every
            // index is taken as moved, which is always a sound answer.
            if ((into.Count - start) >= versions.Length) {
                into.RemoveRange(index: start, count: (into.Count - start));

                return false;
            }

            into.Add(item: index);
        }

        return true;
    }
}
