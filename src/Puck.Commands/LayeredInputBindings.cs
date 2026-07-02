namespace Puck.Commands;

/// <summary>
/// Composes two <see cref="IInputBindings"/> into one: a slot's <paramref name="Primary"/> binding for a source
/// wins, and anything it leaves unmapped falls through to <paramref name="Fallback"/>. This is how optional,
/// per-player overrides layer onto an engine-provided default — a player who remaps one control keeps the
/// default for every other.
/// </summary>
/// <param name="Primary">The higher-priority bindings (e.g. a player's optional overrides).</param>
/// <param name="Fallback">The bindings consulted when <paramref name="Primary"/> resolves nothing (the default).</param>
public sealed record LayeredInputBindings(IInputBindings Primary, IInputBindings Fallback) : IInputBindings {
    /// <inheritdoc/>
    public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) {
        return (Primary.Resolve(slot: slot, source: source) ?? Fallback.Resolve(slot: slot, source: source));
    }
}
