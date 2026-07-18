namespace Puck.Commands;

/// <summary>
/// An <see cref="IInputBindings"/> that gives each slot its own binding table — how a per-player controller
/// mapping is expressed. A slot (or a source within it) that has no entry resolves to <see langword="null"/>;
/// compose this over a default with <see cref="LayeredInputBindings"/> so unmapped slots/sources fall back.
/// </summary>
public sealed class PerSlotInputBindings : IInputBindings {
    private readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>>> m_bySlot;

    /// <summary>Initializes a new instance of the <see cref="PerSlotInputBindings"/> class.</summary>
    /// <param name="bySlot">The per-slot binding tables. Slots absent here resolve to <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bySlot"/> is <see langword="null"/>.</exception>
    public PerSlotInputBindings(IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>>> bySlot) {
        ArgumentNullException.ThrowIfNull(bySlot);

        m_bySlot = bySlot;
    }

    /// <inheritdoc/>
    public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) {
        return ((m_bySlot.TryGetValue(
            key: slot,
            value: out var table
        ) && table.TryGetValue(
            key: source,
            value: out var bindings
        ))
            ? bindings
            : null);
    }
}
