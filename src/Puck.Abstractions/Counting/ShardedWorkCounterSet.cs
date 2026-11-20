using Puck.Abstractions.Memory;
namespace Puck.Abstractions.Counting;

/// <summary>
/// A named <see cref="IWorkCounterSource"/> over a fixed list of kinds for counts taken on a hot path by many threads.
/// Each thread that counts owns one shard (<see cref="ThreadShard"/>), a <see cref="WorkCounterSet"/> only that thread
/// writes, so a count is a plain store with no interlock and no cache line shared with another writer. A read publishes
/// the shards: it sums every live shard and every retired shard's carried total through one
/// <see cref="ForwardingWorkCounterSource"/>, so the reading is exact once the writers pause and never goes down while
/// they run.
/// <para>
/// A thread that exits has its shard retired by the next read or the next thread's first count: its totals are carried
/// forward and the shard is dropped, so the shards held are the live counting threads plus those that exited since the
/// last of those two events, never one per thread that ever counted. A shard is registered once per thread and kept in
/// a per-thread table indexed by this set's identity, which a set that is dropped leaves behind (one reference per
/// thread alive), so a set is long-lived or few.
/// </para>
/// </summary>
public sealed class ShardedWorkCounterSet : IWorkCounterSource {
    private static int NextId = -1;

    [ThreadStatic]
    private static WorkCounterSet?[]? Shards;

    private readonly Lock m_gate = new();
    private readonly object m_home = new();
    private readonly int m_id = Interlocked.Increment(location: ref NextId);
    private readonly WorkKind[] m_kinds;
    private readonly List<Registration> m_registered = [];
    private readonly ForwardingWorkCounterSource m_totals;

    /// <summary>Initializes a new instance of the <see cref="ShardedWorkCounterSet"/> class, every kind at zero.</summary>
    /// <param name="name">The source's name.</param>
    /// <param name="kinds">The kinds it counts, in the order a report lists them; each appears once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a dotted work name, <paramref name="kinds"/>
    /// is empty, or it holds a <see langword="null"/> kind or one kind twice.</exception>
    public ShardedWorkCounterSet(string name, ReadOnlySpan<WorkKind> kinds) {
        m_kinds = WorkCounterSet.Declare(
            kinds: kinds,
            name: name
        );
        Name = name;
        m_totals = new ForwardingWorkCounterSource(
            kinds: m_kinds,
            name: name
        );
        WorkCounterSet.ClaimHome(
            home: m_home,
            kinds: m_kinds
        );
    }

    /// <inheritdoc/>
    public string Name { get; }
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds =>
        m_kinds;
    /// <summary>Gets the number of shards held: the live counting threads and any that exited and are not yet
    /// retired.</summary>
    public int ShardCount {
        get {
            lock (m_gate) {
                return m_registered.Count;
            }
        }
    }
    /// <summary>Gets the calling thread's shard, creating it on the thread's first count. Only the calling thread may
    /// write it: handing it to another thread, or keeping it past the thread's life, loses counts. A caller that
    /// counts several kinds in one step takes it once.</summary>
    public WorkCounterSet ThreadShard {
        get {
            var shards = Shards;

            if ((shards is not null) && ((uint)m_id < (uint)shards.Length) && (shards[m_id] is { } shard)) {
                return shard;
            }

            return Enroll();
        }
    }

    /// <summary>Adds a non-negative amount of one total kind, from any thread.</summary>
    /// <param name="kind">One of this set's kinds, a total.</param>
    /// <param name="amount">The amount of work, in the kind's unit; zero leaves the count unchanged.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of this set's kinds, or is a peak.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative.</exception>
    /// <exception cref="OverflowException">The calling thread's shard would exceed <see cref="long.MaxValue"/>; the
    /// count is unchanged.</exception>
    public void Add(WorkKind kind, long amount) =>
        ThreadShard.Add(
            amount: amount,
            kind: kind
        );
    /// <summary>Adds one of a total kind, from any thread.</summary>
    /// <param name="kind">One of this set's kinds, a total.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of this set's kinds, or is a peak.</exception>
    /// <exception cref="OverflowException">The calling thread's shard is already <see cref="long.MaxValue"/>; it is
    /// unchanged.</exception>
    public void Count(WorkKind kind) =>
        ThreadShard.Count(kind: kind);
    /// <summary>Raises one peak kind to <paramref name="value"/> when it is larger than the peak held since the set's
    /// peaks were last cleared, from any thread.</summary>
    /// <param name="kind">One of this set's kinds, a peak.</param>
    /// <param name="value">The value one step of the owner's work published, in the kind's unit.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of this set's kinds, or is a total.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public void Raise(WorkKind kind, long value) =>
        ThreadShard.Raise(
            kind: kind,
            value: value
        );
    /// <summary>Reads the current total of one of this set's kinds.</summary>
    /// <param name="kind">One of this set's kinds.</param>
    /// <returns>The total counted so far, across every thread that has counted.</returns>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of this set's kinds.</exception>
    public long Read(WorkKind kind) =>
        (TryRead(kind: kind, value: out var value)
            ? value
            : throw new ArgumentException(
                message: $"Work kind '{kind?.Name}' is not a {Name} kind.",
                paramName: nameof(kind)
            )
        );
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) {
        RetireExited();

        return m_totals.TryRead(
            kind: kind,
            value: out value
        );
    }
    /// <inheritdoc/>
    /// <remarks>Returns every peak kind of every shard and of the carried peaks to zero; a total is left as it is.</remarks>
    public void ClearPeaks() =>
        m_totals.ClearPeaks();

    private WorkCounterSet Enroll() {
        var shard = WorkCounterSet.CreateShard(
            home: m_home,
            kinds: m_kinds,
            name: Name
        );

        lock (m_gate) {
            RetireExitedLocked();
            m_registered.Add(item: new(
                Owner: Thread.CurrentThread,
                Shard: shard
            ));
            m_totals.Attach(instance: shard);
        }

        var shards = Shards;

        if ((shards is null) || (shards.Length <= m_id)) {
            Array.Resize(
                array: ref shards,
                newSize: ArrayCapacity.Grown(shards?.Length ?? 0, m_id + 1)
            );
            Shards = shards;
        }

        shards[m_id] = shard;

        return shard;
    }
    private void RetireExited() {
        lock (m_gate) {
            RetireExitedLocked();
        }
    }
    // A thread that is no longer alive has written its last count, so detaching its shard carries its final totals.
    private void RetireExitedLocked() {
        for (var index = (m_registered.Count - 1); (index >= 0); index--) {
            if (!m_registered[index].Owner.IsAlive) {
                m_totals.Detach(instance: m_registered[index].Shard);
                m_registered.RemoveAt(index: index);
            }
        }
    }

    private readonly record struct Registration(Thread Owner, WorkCounterSet Shard);
}
