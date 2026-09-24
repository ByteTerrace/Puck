namespace Puck.Abstractions.Counting;

/// <summary>
/// A named <see cref="IWorkCounterSource"/> over a fixed list of kinds, one <see cref="WorkCount"/> per kind, that any
/// number of threads count into. An owner whose counts need no more than that — a compiler counting requests, a loader
/// counting the files it read — declares its kinds and holds one set instead of writing its own source. Every write is
/// interlocked (<see cref="WorkCount.AddShared"/>), so a count taken on a build thread and one taken on the frame thread
/// land in the same total. A read allocates nothing.
/// </summary>
public sealed class WorkCounterSet : IWorkCounterSource {
    private readonly WorkCount[] m_counts;
    private readonly WorkKind[] m_kinds;

    /// <summary>Initializes a new instance of the <see cref="WorkCounterSet"/> class, every kind at zero.</summary>
    /// <param name="name">The source's name.</param>
    /// <param name="kinds">The kinds it counts, in the order a report lists them; each appears once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a dotted work name, <paramref name="kinds"/>
    /// is empty, or it holds a <see langword="null"/> kind or one kind twice.</exception>
    public WorkCounterSet(string name, ReadOnlySpan<WorkKind> kinds) {
        Name = WorkKind.RequireSourceName(
            name: name,
            paramName: nameof(name)
        );

        if (kinds.IsEmpty) {
            throw new ArgumentException(
                message: $"Work source '{name}' must count at least one kind.",
                paramName: nameof(kinds)
            );
        }

        for (var index = 0; (index < kinds.Length); index++) {
            if (kinds[index] is null) {
                throw new ArgumentException(
                    message: $"Work source '{name}' lists a null kind at {index}.",
                    paramName: nameof(kinds)
                );
            }

            for (var earlier = 0; (earlier < index); earlier++) {
                if (ReferenceEquals(objA: kinds[earlier], objB: kinds[index])) {
                    throw new ArgumentException(
                        message: $"Work source '{name}' lists kind '{kinds[index].Name}' twice.",
                        paramName: nameof(kinds)
                    );
                }
            }
        }

        m_kinds = kinds.ToArray();
        m_counts = new WorkCount[m_kinds.Length];
    }

    /// <inheritdoc/>
    public string Name { get; }
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds =>
        m_kinds;

    /// <summary>Adds a non-negative amount of one kind, from any thread.</summary>
    /// <param name="kind">One of this set's kinds.</param>
    /// <param name="amount">The amount of work, in the kind's unit; zero leaves the count unchanged.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of this set's kinds.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative.</exception>
    /// <exception cref="OverflowException">The total would exceed <see cref="long.MaxValue"/>; the count is unchanged.</exception>
    public void Add(WorkKind kind, long amount) =>
        m_counts[IndexOf(kind: kind)].AddShared(amount: amount);
    /// <summary>Adds one of a kind, from any thread.</summary>
    /// <param name="kind">One of this set's kinds.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of this set's kinds.</exception>
    /// <exception cref="OverflowException">The count is already <see cref="long.MaxValue"/>; it is unchanged.</exception>
    public void Count(WorkKind kind) =>
        Add(
            amount: 1L,
            kind: kind
        );
    /// <summary>Reads the current total of one of this set's kinds.</summary>
    /// <param name="kind">One of this set's kinds.</param>
    /// <returns>The total counted so far.</returns>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of this set's kinds.</exception>
    public long Read(WorkKind kind) =>
        m_counts[IndexOf(kind: kind)].Value;
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) {
        var index = Find(kind: kind);

        if (index < 0) {
            value = 0L;

            return false;
        }

        value = m_counts[index].Value;

        return true;
    }

    private int Find(WorkKind kind) {
        for (var index = 0; (index < m_kinds.Length); index++) {
            if (ReferenceEquals(objA: m_kinds[index], objB: kind)) {
                return index;
            }
        }

        return -1;
    }
    private int IndexOf(WorkKind kind) {
        var index = Find(kind: kind);

        if (index < 0) {
            throw new ArgumentException(
                message: $"Work kind '{kind?.Name}' is not a {Name} kind.",
                paramName: nameof(kind)
            );
        }

        return index;
    }
}
