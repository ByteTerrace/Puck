using System.Runtime.CompilerServices;

namespace Puck.Abstractions;

/// <summary>
/// Dense, independently published entries with table-owned storage. Readers never wait or retry. The owner must
/// serialize every write, fill and retained-value visit, including against each other; readers need no gate.
/// <para>The atomic state word is the only entry door: its high 16 bits select a slot column and its low 48 bits
/// register readers. Retirement transfers that count to the column's pending count. Only an unpublished column with
/// zero pending readers can be reused. Growing columns preserves the arrays already held by readers.</para>
/// </summary>
/// <typeparam name="T">An assignment-copy value, or an image supplied by a factory.</typeparam>
public sealed class PublishArena<T> {
    private const long FoldAt = (1L << 32);
    private const int IndexShift = 48;
    private const long ReaderLimit = (ReaderMask - (1L << 32));
    private const long ReaderMask = ((1L << IndexShift) - 1);

    private readonly long[] m_states;
    private readonly Func<T>? m_create;

    private Slot[][] m_slots;
    private bool m_filling;

    /// <summary>Creates independent entries. A factory initializes a published image for every entry.</summary>
    /// <param name="capacity">Number of entries.</param>
    /// <param name="create">Optional image factory; value-copy arenas leave it absent.</param>
    public PublishArena(int capacity, Func<T>? create = null) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        m_states = new long[capacity];
        m_create = create;
        m_slots = [CreateColumn(), CreateColumn()];
        if (create is not null) { Array.Fill(array: m_states, value: (1L << IndexShift)); }
    }

    /// <summary>Gets the number of independently published entries.</summary>
    public int Capacity => m_states.Length;
    /// <summary>Gets arena machinery and inline storage bytes on Puck's x64 host, including object/array headers and
    /// all allocated columns. Objects referenced by T or by a factory are excluded; this is never their payload size.</summary>
    public long InlineStorageBytes {
        get {
            var slots = Volatile.Read(location: ref m_slots);

            return (((48L + (24L + (m_states.LongLength * sizeof(long)))) + (24L + (slots.LongLength * IntPtr.Size)))
                + (slots.LongLength * (24L + (m_states.LongLength * Unsafe.SizeOf<Slot>()))));
        }
    }

    /// <summary>Gets an entry's completed publication number.</summary>
    /// <param name="entry">Dense entry index.</param>
    public long Version(int entry) => Read(entry: entry, read: static (in T value, long version, int state) => version, state: 0);
    /// <summary>Publishes an assignment-copy value under the owner's writer gate.</summary>
    /// <param name="entry">Dense entry index.</param>
    /// <param name="value">The value.</param>
    public void Publish(int entry, in T value) {
        RefuseNested();
        var column = Reserve(entry: entry);

        m_slots[column][entry].Value = value;
        Commit(column: column, entry: entry);
    }
    /// <summary>Fills and publishes an image under the owner's writer gate. The fill must not publish recursively.</summary>
    /// <typeparam name="TState">Writer state.</typeparam>
    /// <param name="entry">Dense entry index.</param>
    /// <param name="state">Writer state.</param>
    /// <param name="fill">The fill.</param>
    public void Publish<TState>(int entry, TState state, PublishBuffer<T>.Fill<TState> fill) where TState : allows ref struct {
        ArgumentNullException.ThrowIfNull(fill);
        RefuseNested();
        var column = Reserve(entry: entry);

        m_filling = true;
        try {
            fill(ref m_slots[column][entry].Value, state);
        } catch {
            if (m_create is null) { m_slots[column][entry].Value = default!; }
            throw;
        } finally {
            m_filling = false;
        }
        Commit(column: column, entry: entry);
    }

    private void RefuseNested() {
        if (m_filling) { throw new InvalidOperationException(message: "a fill or visitor cannot publish to its own arena"); }
    }

    /// <summary>Copies a stable entry using one atomic admission and one release.</summary>
    /// <param name="entry">Dense entry index.</param>
    /// <param name="value">The latest value.</param>
    /// <returns>Whether the entry has been published.</returns>
    public bool TrySnapshot(int entry, out T value) {
        var slots = Enter(entry: entry);

        if (slots is null) { value = default!; return false; }
        try { value = slots[entry].Value; } finally { _ = Interlocked.Decrement(location: ref slots[entry].Pending); }
        return true;
    }
    /// <summary>Reads in a scoped callback. The hold ends even if the callback throws.</summary>
    /// <typeparam name="TState">Reader state.</typeparam>
    /// <typeparam name="TResult">Reader result.</typeparam>
    /// <param name="entry">Dense entry index.</param>
    /// <param name="state">Reader state.</param>
    /// <param name="read">The scoped reader.</param>
    /// <returns>The result, or default before the first publication.</returns>
    public TResult Read<TState, TResult>(int entry, TState state, PublishBuffer<T>.ReadValue<TState, TResult> read) where TState : allows ref struct {
        ArgumentNullException.ThrowIfNull(read);
        var slots = Enter(entry: entry);

        if (slots is null) { return default!; }
        try { return read(in slots[entry].Value, slots[entry].Version, state); } finally { _ = Interlocked.Decrement(location: ref slots[entry].Pending); }
    }

    private Slot[]? Enter(int entry) {
        // A fold may lose its single CAS on every attempt under an adversarial schedule. Keep the carry guard even
        // then: <2^32 host threads can be past this check, leaving room for every already admitted increment.
        if ((Volatile.Read(location: ref m_states[entry]) & ReaderMask) >= ReaderLimit) {
            throw new InvalidOperationException(message: "publication admissions exhausted after repeated lost counter folds; publish again");
        }
        var state = Interlocked.Increment(location: ref m_states[entry]);
        var column = Column(state: state);
        var slots = ((column < 0) ? null : Volatile.Read(location: ref m_slots)[column]);
        // One bounded fold, never a retry. First over-count pending, then reset admissions only if the exact word still
        // stands; on failure undo the over-count. Retirement sees either all admissions or the folded pending count.
        // FoldAt leaves ample room for further entries; the admission guard still prevents carry if every fold loses.
        if ((state & ReaderMask) >= FoldAt) {
            var count = state & ReaderMask;

            if (slots is not null) { _ = Interlocked.Add(location1: ref slots[entry].Pending, value: count); }
            if ((Interlocked.CompareExchange(ref m_states[entry], state & ~ReaderMask, state) != state) && (slots is not null)) {
                _ = Interlocked.Add(location1: ref slots[entry].Pending, value: -count);
            }
        }
        return slots;
    }
    private static int Column(long state) => (((int)(((ulong)state) >> IndexShift)) - 1);
    private Slot[] CreateColumn() {
        var column = new Slot[m_states.Length];

        if (m_create is { } create) {
            for (var entry = 0; (entry < column.Length); entry++) { column[entry].Value = create(); }
        }
        return column;
    }
    private void ClearIdle(int entry, int published) {
        if (m_create is not null) { return; }
        // Only the serialized writer clears: clearing in the last reader's release could race a writer's reuse.
        // Commit clears immediate retirements; the next write or accounting visit clears a hold that ended later.
        for (var column = 0; (column < m_slots.Length); column++) {
            if ((column != published) && (Volatile.Read(location: ref m_slots[column][entry].Pending) == 0)) {
                m_slots[column][entry].Value = default!;
            }
        }
    }
    private int Reserve(int entry) {
        var published = Column(state: Volatile.Read(location: ref m_states[entry]));

        ClearIdle(entry: entry, published: published);
        for (var offset = 1; (offset <= m_slots.Length); offset++) {
            var column = ((published + offset) % m_slots.Length);

            if ((column != published) && (Volatile.Read(location: ref m_slots[column][entry].Pending) == 0)) { return column; }
        }
        if (m_slots.Length == ushort.MaxValue) { throw new InvalidOperationException(message: "too many simultaneous held publications"); }
        var grown = new Slot[(m_slots.Length + 1)][];

        m_slots.CopyTo(array: grown, index: 0);
        grown[^1] = CreateColumn();
        Volatile.Write(location: ref m_slots, value: grown);
        return (grown.Length - 1);
    }
    private void Commit(int entry, int column) {
        var published = Column(state: Volatile.Read(location: ref m_states[entry]));

        m_slots[column][entry].Version = ((published < 0) ? 1 : (m_slots[published][entry].Version + 1));
        var previous = Interlocked.Exchange(location1: ref m_states[entry], value: (((long)(column + 1)) << IndexShift));
        var retired = Column(state: previous);

        if ((retired >= 0) && (Interlocked.Add(location1: ref m_slots[retired][entry].Pending, value: previous & ReaderMask) == 0) && (m_create is null)) {
            m_slots[retired][entry].Value = default!;
        }
    }

    /// <summary>Visits all retained values, including held retired publications, under the owner's writer gate.
    /// Clears unheld retired assignment-copy values first. A visitor must not publish to this arena.</summary>
    /// <typeparam name="TState">Visitor state.</typeparam>
    /// <param name="state">Visitor state.</param>
    /// <param name="visit">Visitor; its return value is ignored.</param>
    public void VisitRetained<TState>(TState state, PublishBuffer<T>.ReadValue<TState, int> visit) where TState : allows ref struct {
        ArgumentNullException.ThrowIfNull(visit);
        RefuseNested();
        m_filling = true;
        try {
            for (var entry = 0; (entry < m_states.Length); entry++) {
                ClearIdle(entry: entry, published: Column(state: Volatile.Read(location: ref m_states[entry])));
                foreach (var column in m_slots) { _ = visit(in column[entry].Value, column[entry].Version, state); }
            }
        } finally {
            m_filling = false;
        }
    }

    private struct Slot {
        public T Value;
        // May be negative while published: readers leave before Commit transfers their admissions.
        public long Pending;
        public long Version;
    }
}
