using System.Runtime.CompilerServices;
using Puck.Abstractions.Memory;

namespace Puck.Abstractions;

/// <summary>
/// Dense, independently published entries with table-owned storage. Readers never wait or retry. The owner must
/// serialize every write, fill and retained-value visit, including against each other; readers need no gate.
/// <para>The atomic state word is the only entry door: its high 16 bits select a slot column and its low 48 bits
/// register readers. Retirement transfers that count to the column's pending count. Only an unpublished column with
/// zero pending readers can be reused. Growing columns preserves the columns already held by readers.</para>
/// <para>An arena made with an allocator keeps its state words and columns natively (a value of no references only),
/// so a large arena of plain values leaves nothing for the collector; its owner disposes it once no reader can reach it
/// (an epoch reclamation, <see cref="ReaderEpochs{T}"/>), and a reader inside it after that reads freed storage. A
/// managed arena needs no disposal.</para>
/// </summary>
/// <typeparam name="T">An assignment-copy value, or an image supplied by a factory.</typeparam>
public sealed unsafe class PublishArena<T> : IDisposable {
    private const long FoldAt = (1L << 32);
    private const int IndexShift = 48;
    private const long ReaderLimit = (ReaderMask - (1L << 32));
    private const long ReaderMask = ((1L << IndexShift) - 1);

    private readonly int m_capacity;
    private readonly Func<T>? m_create;
    private readonly IAllocator? m_allocator;
    private readonly IAllocationObserver? m_observer;
    // The state words: an array, or a native block (m_nativeStates) at m_statesPointer.
    private readonly long[]? m_states;
    private readonly OwnedBuffer<long>? m_nativeStates;
    private readonly long* m_statesPointer;

    private Column[] m_columns;
    private bool m_filling;
    private bool m_disposed;

    /// <summary>Creates independent entries. A factory initializes a published image for every entry.</summary>
    /// <param name="capacity">Number of entries.</param>
    /// <param name="create">Optional image factory; value-copy arenas leave it absent.</param>
    /// <param name="allocator">The native allocator the state words and columns come from, or <see langword="null"/> for
    /// managed arrays; a native arena holds values of no references, publishes no images and is disposed by its
    /// owner.</param>
    /// <param name="observer">The owner a native arena's storage is charged to, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="allocator"/> is given for a value holding references or with a
    /// factory.</exception>
    public PublishArena(int capacity, Func<T>? create = null, IAllocator? allocator = null, IAllocationObserver? observer = null) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if ((allocator is not null) && ((create is not null) || RuntimeHelpers.IsReferenceOrContainsReferences<Slot>())) {
            throw new ArgumentException(message: "A native arena holds values of no references and publishes no images.", paramName: nameof(allocator));
        }
        m_capacity = capacity;
        m_create = create;
        m_allocator = allocator;
        m_observer = observer;
        if (allocator is null) {
            m_states = new long[capacity];
        } else {
            m_nativeStates = OwnedBuffer<long>.OfLength(allocator: allocator, clear: true, length: capacity, observer: observer);
            m_statesPointer = (long*)m_nativeStates.Pin().Pointer;
        }
        m_columns = [CreateColumn(), CreateColumn()];
        if (create is not null) { m_states!.AsSpan().Fill(value: (1L << IndexShift)); }
    }

    /// <summary>Gets the number of independently published entries.</summary>
    public int Capacity => m_capacity;
    /// <summary>Gets arena machinery and inline storage bytes on Puck's x64 host, including object/array headers and
    /// all allocated columns, managed or native. Objects referenced by T or by a factory are excluded; this is never their
    /// payload size.</summary>
    public long InlineStorageBytes {
        get {
            var columns = Volatile.Read(location: ref m_columns);

            return (((48L + (24L + (((long)m_capacity) * sizeof(long)))) + (24L + (columns.LongLength * Unsafe.SizeOf<Column>())))
                + (columns.LongLength * (24L + (((long)m_capacity) * Unsafe.SizeOf<Slot>()))));
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

        m_columns[column].At(entry: entry).Value = value;
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
            fill(ref m_columns[column].At(entry: entry).Value, state);
        } catch {
            if (m_create is null) { m_columns[column].At(entry: entry).Value = default!; }
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
        if (!Enter(entry: entry, column: out var column)) { value = default!; return false; }
        ref var slot = ref column.At(entry: entry);

        try { value = slot.Value; } finally { _ = Interlocked.Decrement(location: ref slot.Pending); }
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
        if (!Enter(entry: entry, column: out var column)) { return default!; }
        ref var slot = ref column.At(entry: entry);

        try { return read(in slot.Value, slot.Version, state); } finally { _ = Interlocked.Decrement(location: ref slot.Pending); }
    }

    // An entry's state word, in the array or the native block.
    private ref long StateAt(int entry) {
        if (m_states is { } states) { return ref states[entry]; }
        if (((uint)entry) >= ((uint)m_capacity)) { throw new ArgumentOutOfRangeException(paramName: nameof(entry)); }
        return ref m_statesPointer[entry];
    }
    private bool Enter(int entry, out Column column) {
        ref var word = ref StateAt(entry: entry);

        // A fold may lose its single CAS on every attempt under an adversarial schedule. Keep the carry guard even
        // then: <2^32 host threads can be past this check, leaving room for every already admitted increment.
        if ((Volatile.Read(location: ref word) & ReaderMask) >= ReaderLimit) {
            throw new InvalidOperationException(message: "publication admissions exhausted after repeated lost counter folds; publish again");
        }
        var state = Interlocked.Increment(location: ref word);
        var index = ColumnOf(state: state);
        var published = (index >= 0);

        column = (published ? Volatile.Read(location: ref m_columns)[index] : default);
        // One bounded fold, never a retry. First over-count pending, then reset admissions only if the exact word still
        // stands; on failure undo the over-count. Retirement sees either all admissions or the folded pending count.
        // FoldAt leaves ample room for further entries; the admission guard still prevents carry if every fold loses.
        if ((state & ReaderMask) >= FoldAt) {
            var count = state & ReaderMask;

            if (published) { _ = Interlocked.Add(location1: ref column.At(entry: entry).Pending, value: count); }
            if ((Interlocked.CompareExchange(ref word, state & ~ReaderMask, state) != state) && published) {
                _ = Interlocked.Add(location1: ref column.At(entry: entry).Pending, value: -count);
            }
        }
        return published;
    }
    private static int ColumnOf(long state) => (((int)(((ulong)state) >> IndexShift)) - 1);
    private Column CreateColumn() {
        if (m_allocator is not null) {
            var block = OwnedBuffer<byte>.OfLength(allocator: m_allocator, clear: true, length: checked(m_capacity * Unsafe.SizeOf<Slot>()), observer: m_observer);

            return new Column(array: null, native: block, pointer: (nint)block.Pin().Pointer);
        }

        var column = new Slot[m_capacity];

        if (m_create is { } create) {
            for (var entry = 0; (entry < column.Length); entry++) { column[entry].Value = create(); }
        }
        return new Column(array: column, native: null, pointer: 0);
    }
    private void ClearIdle(int entry, int published) {
        if (m_create is not null) { return; }
        // Only the serialized writer clears: clearing in the last reader's release could race a writer's reuse.
        // Commit clears immediate retirements; the next write or accounting visit clears a hold that ended later.
        for (var column = 0; (column < m_columns.Length); column++) {
            ref var slot = ref m_columns[column].At(entry: entry);

            if ((column != published) && (Volatile.Read(location: ref slot.Pending) == 0)) { slot.Value = default!; }
        }
    }
    private int Reserve(int entry) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        var published = ColumnOf(state: Volatile.Read(location: ref StateAt(entry: entry)));

        ClearIdle(entry: entry, published: published);
        for (var offset = 1; (offset <= m_columns.Length); offset++) {
            var column = ((published + offset) % m_columns.Length);

            if ((column != published) && (Volatile.Read(location: ref m_columns[column].At(entry: entry).Pending) == 0)) { return column; }
        }
        if (m_columns.Length == ushort.MaxValue) { throw new InvalidOperationException(message: "too many simultaneous held publications"); }
        var grown = new Column[(m_columns.Length + 1)];

        m_columns.CopyTo(array: grown, index: 0);
        grown[^1] = CreateColumn();
        Volatile.Write(location: ref m_columns, value: grown);
        return (grown.Length - 1);
    }
    private void Commit(int entry, int column) {
        ref var word = ref StateAt(entry: entry);
        var published = ColumnOf(state: Volatile.Read(location: ref word));

        m_columns[column].At(entry: entry).Version = ((published < 0) ? 1 : (m_columns[published].At(entry: entry).Version + 1));
        var previous = Interlocked.Exchange(location1: ref word, value: (((long)(column + 1)) << IndexShift));
        var retired = ColumnOf(state: previous);

        if (retired >= 0) {
            ref var slot = ref m_columns[retired].At(entry: entry);

            if ((Interlocked.Add(location1: ref slot.Pending, value: previous & ReaderMask) == 0) && (m_create is null)) { slot.Value = default!; }
        }
    }

    /// <summary>Returns every entry to unpublished and drops every value, keeping the storage, so the owner can publish
    /// into the arena afresh. The owner calls it only once no reader can reach the arena any more (an epoch reclamation,
    /// <see cref="ReaderEpochs{T}"/>); a reader still inside it would read the cleared entries.</summary>
    /// <exception cref="InvalidOperationException">The arena publishes images from a factory, which a reset cannot
    /// restore.</exception>
    public void Reset() {
        if (m_create is not null) { throw new InvalidOperationException(message: "an image arena cannot be reset"); }
        RefuseNested();
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        if (m_states is { } states) { Array.Clear(array: states); } else { m_nativeStates!.GetSpan().Clear(); }
        foreach (var column in m_columns) { column.Clear(capacity: m_capacity); }
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
            for (var entry = 0; (entry < m_capacity); entry++) {
                ClearIdle(entry: entry, published: ColumnOf(state: Volatile.Read(location: ref StateAt(entry: entry))));
                foreach (var column in m_columns) {
                    ref var slot = ref column.At(entry: entry);

                    _ = visit(in slot.Value, slot.Version, state);
                }
            }
        } finally {
            m_filling = false;
        }
    }
    /// <summary>Frees a native arena's state words and columns, once no reader can reach the arena (an epoch
    /// reclamation); a managed arena holds nothing to free.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        m_nativeStates?.Dispose();
        foreach (var column in m_columns) { column.Native?.Dispose(); }
    }

    // One column of slots: an array, or a native block at a pointer, which never moves while the arena lives.
    private readonly struct Column(Slot[]? array, OwnedBuffer<byte>? native, nint pointer) {
        public OwnedBuffer<byte>? Native { get; } = native;

        public ref Slot At(int entry) => ref ((array is not null) ? ref array[entry] : ref Unsafe.Add(source: ref Unsafe.AsRef<Slot>(source: (void*)pointer), elementOffset: entry));
        public void Clear(int capacity) {
            if (array is not null) { Array.Clear(array: array); } else { new Span<Slot>(pointer: (void*)pointer, length: capacity).Clear(); }
        }
    }

    private struct Slot {
        public T Value;
        // May be negative while published: readers leave before Commit transfers their admissions.
        public long Pending;
        public long Version;
    }
}
