namespace Puck.Abstractions;

/// <summary>
/// The one latest-value handoff between threads: writers publish a value, readers read the newest one, and nothing is
/// queued, so a reader skips every publication it was too slow to see. A value is either copied by assignment
/// (<see cref="Publish(in T)"/>, <see cref="TrySnapshot"/>) or, for an image too large to copy as one value, filled in
/// place (<see cref="Publish{TState}(TState, Fill{TState})"/>) and read in place through a scoped callback or a <see cref="Lease"/>.
/// <para><b>Invariant.</b> A reader never sees a torn value (one whose words come from two publications), never waits
/// on a writer and never spins or retries: <see cref="Read"/> is one interlocked add, a copy or an in-place read, and
/// one interlocked decrement when the lease ends, with no loop. Writers serialize with each other on one lock and never
/// wait on a reader.</para>
/// <para><b>How.</b> Values live in slots. One 64-bit state word holds the published slot (its index plus one in the
/// top 16 bits, zero before the first publication) and, in its low 48 bits, the number of readers that entered that
/// slot since it was published. A reader enters with one <see cref="Interlocked.Add(ref long, long)"/> on the word,
/// which names the slot and registers the reader on it in the same atomic step, so there is nothing to validate and
/// nothing to retry. A writer fills a slot that is neither published nor held, then swaps it into the word with one
/// <see cref="Interlocked.Exchange(ref long, long)"/>; the old word's count is every reader that entered the retired
/// slot, and the writer adds it to that slot's own pending count, which each leaving reader decrements. A retired slot
/// whose pending count is zero holds no reader and no reader can newly enter it, so the next writer may overwrite it.
/// When every slot is published or held, the writer adds a slot rather than wait, so the slot count is bounded by two
/// plus the number of reads in flight at once and settles at three for one reader. The interlocked operations are full
/// fences: everything a writer wrote into a slot is visible to a reader that enters it, and a reader's reads of a slot
/// finish before its leaving decrement lets a writer reuse it.</para>
/// <para>Snapshots and scoped callback reads allocate nothing once slots are available. An explicit lease allocates one
/// release identity shared by every alias, so disposing it twice cannot release another reader's hold.</para>
/// <para>Entry refuses before the 48-bit reader counter can carry into the index. It leaves 2^32 counter values for
/// calls already past the admission check: Windows and Linux thread identifiers bound simultaneous synchronous entry
/// calls below 2^32. Publishing resets the counter, including after admission has refused.</para>
/// </summary>
/// <typeparam name="T">The published value: a record or record struct copied by assignment, or an image a writer
/// fills in place.</typeparam>
public sealed class PublishBuffer<T> {
    /// <summary>Fills the slot a writer publishes next.</summary>
    /// <typeparam name="TState">The writer's state.</typeparam>
    /// <param name="slot">The slot: an older publication's value (or the factory's image) for the writer to overwrite
    /// whole, since it is never the latest one.</param>
    /// <param name="state">The writer's state.</param>
    public delegate void Fill<TState>(ref T slot, scoped TState state) where TState : allows ref struct;
    /// <summary>Reads a published value within the buffer's hold; references must not escape the callback.</summary>
    /// <typeparam name="TState">The reader's state.</typeparam>
    /// <typeparam name="TResult">The callback's result.</typeparam>
    /// <param name="value">The stable published value.</param>
    /// <param name="version">Its publication number.</param>
    /// <param name="state">The reader's state.</param>
    /// <returns>The reader's result.</returns>
    public delegate TResult ReadValue<TState, TResult>(scoped in T value, long version, scoped TState state) where TState : allows ref struct;

    private const int IndexShift = 48;
    private const int MaxSlots = ushort.MaxValue;
    private const long ReaderLimit = (ReaderMask - (1L << 32));
    private const long ReaderMask = ((1L << IndexShift) - 1);

    private readonly Func<T>? m_create;
    private readonly Lock m_writer = new();

    // The slots, grown only by a writer under m_writer and replaced whole, so a reader holding an older array still finds
    // every slot it can enter at the same index.
    private Slot[] m_slots;
    // The published slot's index plus one in the top 16 bits, and the readers that entered it in the low 48 bits.
    private long m_state;
    // The published slot, or -1 before the first publication; read and written only under m_writer.
    private int m_published;
    private bool m_filling;
    private long m_version;

    /// <summary>Initializes an empty buffer: <see cref="TrySnapshot"/> reports nothing until the first publication.</summary>
    public PublishBuffer() {
        m_slots = [new(), new(), new()];
        m_published = -1;
    }
    /// <summary>Initializes a buffer of images filled in place, already holding a published first image, so a reader's
    /// first read is complete.</summary>
    /// <param name="create">The factory for each slot's image; every image it returns must describe the same initial
    /// state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="create"/> is <see langword="null"/>.</exception>
    public PublishBuffer(Func<T> create) {
        ArgumentNullException.ThrowIfNull(argument: create);

        m_create = create;
        m_slots = [new() { Value = create() }, new() { Value = create() }, new() { Value = create() }];
        m_published = 0;
        m_state = (1L << IndexShift);
    }

    /// <summary>Gets the number of completed publications.</summary>
    public long Version => Volatile.Read(location: ref m_version);
    /// <summary>Gets the logical bytes in the slot array and slot payloads, excluding object headers and buffer
    /// machinery. Includes retired slots still held by readers.</summary>
    public long StorageBytes => (((long)Volatile.Read(location: ref m_slots).Length) *
        ((IntPtr.Size + System.Runtime.CompilerServices.Unsafe.SizeOf<T>()) + (2 * sizeof(long))));

    /// <summary>Publishes a value from any thread, copying it into a slot.</summary>
    /// <param name="value">The value to publish.</param>
    /// <exception cref="InvalidOperationException">The call comes from inside a fill of this buffer.</exception>
    public void Publish(in T value) {
        lock (m_writer) {
            var index = Reserve();

            m_slots[index].Value = value;
            Commit(index: index);
        }
    }
    /// <summary>Publishes an image filled in place, from any thread. The fill runs under the writers' lock, so it must
    /// not publish to this buffer itself; a reader never waits for it, however long it takes.</summary>
    /// <typeparam name="TState">The writer's state.</typeparam>
    /// <param name="state">The writer's state, handed to <paramref name="fill"/>.</param>
    /// <param name="fill">Overwrites the slot whole; a static lambda allocates nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fill"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The call comes from inside a fill of this buffer.</exception>
    public void Publish<TState>(TState state, Fill<TState> fill) where TState : allows ref struct {
        ArgumentNullException.ThrowIfNull(argument: fill);

        lock (m_writer) {
            var index = Reserve();

            m_filling = true;
            try {
                fill(slot: ref m_slots[index].Value, state: state);
            } finally {
                m_filling = false;
            }
            Commit(index: index);
        }
    }
    /// <summary>Enters the latest published slot for an in-place read. The value stays unchanged by every writer until
    /// the lease is disposed. Repeated disposal, including through an alias, releases only once.</summary>
    /// <returns>The lease; <see cref="Lease.HasValue"/> is <see langword="false"/> before the first publication.</returns>
    /// <exception cref="InvalidOperationException">This publication's reader count reached its admission limit.</exception>
    public Lease Read() => new(owner: this);

    private Slot? Enter() {
        if ((Volatile.Read(location: ref m_state) & ReaderMask) >= ReaderLimit) {
            throw new InvalidOperationException(message: "the publication's reader count is exhausted; publish again before reading");
        }
        var state = Interlocked.Increment(location: ref m_state);
        var published = ((int)(((ulong)state) >> IndexShift));

        return ((published == 0)
            ? null
            : Volatile.Read(location: ref m_slots)[(published - 1)]);
    }

    /// <summary>Reads the latest value in a scoped callback, without allocating a lease. The hold ends even if the
    /// callback throws. Before the first publication, returns <see langword="default"/> without calling the reader.</summary>
    /// <typeparam name="TState">The reader's state.</typeparam>
    /// <typeparam name="TResult">The callback's result.</typeparam>
    /// <param name="state">The reader's state.</param>
    /// <param name="read">The scoped reader.</param>
    /// <returns>The callback result, or <see langword="default"/> before publication.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">This publication's reader count reached its admission limit.</exception>
    public TResult Read<TState, TResult>(TState state, ReadValue<TState, TResult> read) where TState : allows ref struct {
        ArgumentNullException.ThrowIfNull(argument: read);
        var slot = Enter();

        if (slot is null) { return default!; }
        try {
            return read(value: in slot.Value, version: slot.Version, state: state);
        } finally {
            _ = Interlocked.Decrement(location: ref slot.Pending);
        }
    }
    /// <summary>Copies the latest published value, when one exists.</summary>
    /// <param name="value">The latest value, when published; otherwise <see langword="default"/>.</param>
    /// <returns><see langword="true"/> when a value has been published.</returns>
    /// <exception cref="InvalidOperationException">This publication's reader count reached its admission limit.</exception>
    public bool TrySnapshot(out T value) {
        var slot = Enter();

        if (slot is null) {
            value = default!;

            return false;
        }
        try {
            value = slot.Value;
        } finally {
            _ = Interlocked.Decrement(location: ref slot.Pending);
        }

        return true;
    }

    // A slot neither published nor held by a reader, under m_writer; grows the slots when every one is taken.
    private int Reserve() {
        if (m_filling) { throw new InvalidOperationException(message: "a fill cannot publish to the buffer it is filling"); }
        var slots = m_slots;
        var published = m_published;

        for (var offset = 1; (offset <= slots.Length); offset++) {
            var index = ((published + offset) % slots.Length);

            if ((index != published) && (Volatile.Read(location: ref slots[index].Pending) == 0L)) {
                return index;
            }
        }
        if (slots.Length >= MaxSlots) {
            throw new InvalidOperationException(message: $"{MaxSlots} reads of a publish buffer are in flight at once; a lease was not disposed");
        }
        var grown = new Slot[(slots.Length + 1)];

        slots.CopyTo(array: grown, index: 0);
        grown[slots.Length] = new Slot { Value = ((m_create is { } create) ? create() : default!) };
        // Released before the exchange that can name the new slot, so a reader that enters it finds it in the array.
        Volatile.Write(location: ref m_slots, value: grown);

        return slots.Length;
    }
    // Publishes a filled slot under m_writer and hands the retired slot its readers.
    private void Commit(int index) {
        var slots = m_slots;
        var version = (m_version + 1);

        slots[index].Version = version;
        var previous = Interlocked.Exchange(location1: ref m_state, value: (((long)(index + 1)) << IndexShift));
        var retired = (((int)(((ulong)previous) >> IndexShift)) - 1);

        if (retired >= 0) {
            _ = Interlocked.Add(location1: ref slots[retired].Pending, value: previous & ReaderMask);
        }
        m_published = index;
        Volatile.Write(location: ref m_version, value: version);
    }

    private sealed class Slot {
        public T Value = default!;
        // Readers that entered this slot and have not left: the count handed over when it retired, less every leave. It
        // can dip below zero while the slot is published, when a reader leaves before the retirement hands its entry over.
        public long Pending;
        public long Version;
    }

    /// <summary>One reader's hold on a published slot: the value cannot change until it is disposed.</summary>
    public sealed class Lease : IDisposable {
        private Slot? m_slot;

        // The allocation precedes entry; a failed allocation cannot strand a registered reader.
        internal Lease(PublishBuffer<T> owner) => m_slot = owner.Enter();

        /// <summary>Gets whether a value had been published when the lease was taken.</summary>
        public bool HasValue => (m_slot is not null);
        /// <summary>Gets the held value; only valid while <see cref="HasValue"/>.</summary>
        public ref readonly T Value => ref m_slot!.Value;
        /// <summary>Gets the held value's publication number: zero for a factory's first image.</summary>
        public long Version => (m_slot?.Version ?? 0L);

        /// <summary>Leaves the slot, letting a writer reuse it once every other reader has left.</summary>
        public void Dispose() {
            if (Interlocked.Exchange(location1: ref m_slot, value: null) is { } slot) {
                _ = Interlocked.Decrement(location: ref slot.Pending);
            }
        }
    }
}
