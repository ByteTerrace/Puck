namespace Puck.Abstractions;

/// <summary>
/// A latest-value handoff: writers serialize with each other, readers never wait or retry and skip publications they
/// were too slow to see. Values and factory-created images share the <see cref="PublishArena{T}"/> Enter/Commit mechanism.
/// Entry is one interlocked increment, a copy or scoped callback and one leaving decrement. At the admission threshold
/// an additional bounded fold attempts to reset the count without retry. A carry guard still refuses admission if
/// every fold loses until the 48-bit count is exhausted. Retired values clear on retirement or on the
/// next write after their last reader leaves; factory images remain for reuse. Steady reads and writes allocate nothing.
/// </summary>
/// <typeparam name="T">An assignment-copy value or an image filled in place.</typeparam>
public sealed class PublishBuffer<T> {
    /// <summary>Fills the slot a writer publishes next.</summary>
    /// <typeparam name="TState">Writer state.</typeparam>
    /// <param name="slot">The slot to overwrite whole.</param>
    /// <param name="state">Writer state.</param>
    public delegate void Fill<TState>(ref T slot, scoped TState state) where TState : allows ref struct;
    /// <summary>Reads a published value inside its hold; references must not escape.</summary>
    /// <typeparam name="TState">Reader state.</typeparam>
    /// <typeparam name="TResult">Reader result.</typeparam>
    /// <param name="value">The stable value.</param>
    /// <param name="version">Its publication number.</param>
    /// <param name="state">Reader state.</param>
    /// <returns>The result.</returns>
    public delegate TResult ReadValue<TState, TResult>(scoped in T value, long version, scoped TState state) where TState : allows ref struct;

    private readonly PublishArena<T> m_arena;
    private readonly Lock m_writer = new();

    /// <summary>Creates an empty buffer.</summary>
    public PublishBuffer() => m_arena = new(capacity: 1);
    /// <summary>Creates a buffer with a complete published initial image.</summary>
    /// <param name="create">Creates each image in the same initial state.</param>
    public PublishBuffer(Func<T> create) {
        ArgumentNullException.ThrowIfNull(create);
        m_arena = new(capacity: 1, create: create);
    }

    /// <summary>Gets the latest completed publication number.</summary>
    public long Version => m_arena.Version(entry: 0);

    /// <summary>Publishes an assignment-copy value.</summary>
    /// <param name="value">The value.</param>
    public void Publish(in T value) {
        lock (m_writer) {
            m_arena.Publish(entry: 0, value: value);
        }
    }
    /// <summary>Publishes an image filled in place, without delaying readers.</summary>
    /// <typeparam name="TState">Writer state.</typeparam>
    /// <param name="state">Writer state.</param>
    /// <param name="fill">Overwrites the slot whole; must not publish recursively to this buffer.</param>
    public void Publish<TState>(TState state, Fill<TState> fill) where TState : allows ref struct {
        ArgumentNullException.ThrowIfNull(fill);
        lock (m_writer) {
            m_arena.Publish(entry: 0, fill: fill, state: state);
        }
    }
    /// <summary>Reads inside a scoped callback. Returns default without calling it before publication.</summary>
    /// <typeparam name="TState">Reader state.</typeparam>
    /// <typeparam name="TResult">Reader result.</typeparam>
    /// <param name="state">Reader state.</param>
    /// <param name="read">Scoped callback; its hold ends even on throw.</param>
    /// <returns>The result.</returns>
    public TResult Read<TState, TResult>(TState state, ReadValue<TState, TResult> read) where TState : allows ref struct =>
        m_arena.Read(entry: 0, read: read, state: state);
    /// <summary>Copies the latest complete value.</summary>
    /// <param name="value">The value, or default before publication.</param>
    /// <returns>Whether a value has been published.</returns>
    public bool TrySnapshot(out T value) => m_arena.TrySnapshot(entry: 0, value: out value);
}
