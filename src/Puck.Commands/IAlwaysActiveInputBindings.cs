namespace Puck.Commands;

/// <summary>
/// Resolves host-owned bindings that remain active independently of a game's current page or chord state. This is
/// the small terminal/navigation plane beside, rather than inside, authored gameplay bindings.
/// </summary>
public interface IAlwaysActiveInputBindings {
    /// <summary>Returns the host bindings for a slot and provider-neutral source, or <see langword="null"/>.</summary>
    IReadOnlyList<CommandBinding>? Resolve(int slot, string source);
}
