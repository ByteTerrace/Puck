namespace Puck.Commands;

/// <summary>
/// An <see cref="IInputBindings"/> backed by a single table shared by every slot — the common case where all
/// players use the same mapping (and the drop-in for a single-player game).
/// </summary>
public sealed class SharedInputBindings : IInputBindings {
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> m_bindings;

    /// <summary>Initializes a new instance of the <see cref="SharedInputBindings"/> class.</summary>
    /// <param name="bindings">The table mapping each input source id to the commands it activates, for every slot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is <see langword="null"/>.</exception>
    public SharedInputBindings(IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> bindings) {
        ArgumentNullException.ThrowIfNull(bindings);

        m_bindings = bindings;
    }

    /// <inheritdoc/>
    public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) {
        return (m_bindings.TryGetValue(
            key: source,
            value: out var bindings
        )
            ? bindings
            : null);
    }
}
